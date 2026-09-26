#!/usr/bin/env python3
"""Run one development job with enforced limits and a desktop memory reserve."""
import fcntl
import os
from pathlib import Path
import signal
import subprocess
import sys

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import LIMIT, RESERVE, UNIT, available_memory, require_test_budget


def supervise(command):
    require_test_budget()
    child = subprocess.Popen(command, cwd=PROJECT, start_new_session=True)
    try:
        while child.poll() is None:
            if available_memory() < RESERVE:
                print('Stopping this test: available RAM fell below the 8 GiB desktop reserve.', file=sys.stderr, flush=True)
                return 1
            try:
                return child.wait(timeout=1)
            except subprocess.TimeoutExpired:
                pass
        return child.returncode
    finally:
        if child.poll() is None:
            os.killpg(child.pid, signal.SIGTERM)
            try:
                child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait(timeout=5)
        # systemd removes every remaining descendant when this supervisor exits,
        # including Wine and Xvfb processes in separate process groups.


def main():
    command = sys.argv[1:]
    if command[:1] == ['--inside']:
        return supervise(command[1:])
    if not command:
        raise ValueError('Usage: python3 scripts/safe-test.py COMMAND [ARGS...]')
    lock_path = PROJECT / '.lab/native-test.lock'
    lock_path.parent.mkdir(exist_ok=True)
    with lock_path.open('a') as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise RuntimeError('Another constrained development job is running. Wait for it to finish.') from None
        if available_memory() < LIMIT + RESERVE:
            raise MemoryError('At least 12 GiB available RAM is required: 4 GiB for testing and 8 GiB for the desktop.')
        args = ['systemd-run', '--user', f'--unit={UNIT}', '--collect', '--wait', '--pipe',
                '-p', 'MemoryMax=4G', '-p', 'MemoryHigh=3G', '-p', 'MemorySwapMax=0',
                '-p', 'CPUQuota=150%', '-p', 'Nice=15', '-p', 'IOWeight=10',
                '-p', 'OOMPolicy=kill', '-p', 'KillMode=control-group', '-p', 'TimeoutStopSec=10',
                f'--working-directory={PROJECT}', sys.executable, str(Path(__file__).resolve()), '--inside', *command]
        runner = subprocess.Popen(args)
        try:
            return runner.wait()
        except KeyboardInterrupt:
            subprocess.run(['systemctl', '--user', 'stop', UNIT], check=False, timeout=20)
            return runner.wait(timeout=20)


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (RuntimeError, MemoryError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
