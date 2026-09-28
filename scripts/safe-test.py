#!/usr/bin/env python3
"""Run one development job with enforced limits and a desktop memory reserve."""
import fcntl
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from test_diagnostics import atomic_json, create_run, finish_run, progress

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import LIMIT, RESERVE, UNIT, available_memory, external_game_running, require_test_budget


def supervise(command):
    require_test_budget()
    if external_game_running():
        raise RuntimeError('Root is running outside the test session. Close it before starting development tests.')
    child = subprocess.Popen(command, cwd=PROJECT, start_new_session=True)
    def completed(code):
        atomic_json(Path(os.environ['ROOT_TEST_RUN_DIR']) / 'command-exit.json', {'exit_code': code})
        return code
    try:
        while child.poll() is None:
            if available_memory() < RESERVE:
                print('Stopping this test: available RAM fell below the 8 GiB desktop reserve.', file=sys.stderr, flush=True)
                return 1
            if external_game_running():
                print('Stopping this test: Root started outside the test session. Your game will keep running.', file=sys.stderr, flush=True)
                return 1
            try:
                return completed(child.wait(timeout=1))
            except subprocess.TimeoutExpired:
                pass
        return completed(child.returncode)
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
            raise MemoryError('At least 16 GiB available RAM is required: 8 GiB for testing and 8 GiB for the desktop.')
        if external_game_running():
            raise RuntimeError('Root is running outside the test session. Close it before starting development tests.')
        run = create_run(PROJECT / '.lab/test-runs')
        log = run / 'output.log'
        progress(f'Test log: {log}')
        started = time.monotonic()
        args = ['systemd-run', '--user', f'--unit={UNIT}', '--collect', '--wait',
                '-p', f'StandardOutput=append:{log}', '-p', 'StandardError=inherit',
                f'--setenv=ROOT_TEST_RUN_DIR={run}',
                '-p', 'MemoryMax=8G', '-p', 'MemoryHigh=6G', '-p', 'MemorySwapMax=0',
                '-p', 'CPUQuota=150%', '-p', 'Nice=15', '-p', 'IOWeight=10',
                '-p', 'OOMPolicy=kill', '-p', 'KillMode=control-group', '-p', 'TimeoutStopSec=10',
                f'--working-directory={PROJECT}', sys.executable, str(Path(__file__).resolve()), '--inside', *command]
        exit_code = 1
        try:
            # Both the service and systemd-run write to a file, independent of
            # the caller's output pipe. No command arguments enter the report.
            with log.open('ab') as output:
                runner = subprocess.Popen(args, stdout=output, stderr=subprocess.STDOUT)
                try:
                    exit_code = runner.wait()
                    # systemd considers an externally stopped service successful
                    # even when SIGTERM killed the test. Require the child's own
                    # completion record before reporting a pass.
                    if exit_code == 0:
                        completed = run / 'command-exit.json'
                        exit_code = json.loads(completed.read_text())['exit_code'] if completed.exists() else 130
                except KeyboardInterrupt:
                    subprocess.run(['systemctl', '--user', 'stop', UNIT], check=False,
                                   timeout=20, stdout=output, stderr=subprocess.STDOUT)
                    runner.wait(timeout=20)
                    exit_code = 130
            return exit_code
        finally:
            elapsed = time.monotonic() - started
            finish_run(run, exit_code, elapsed)
            progress(f'Test {"passed" if exit_code == 0 else "failed"}: exit {exit_code}, {elapsed:.1f}s. Log: {log}')


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (RuntimeError, MemoryError, ValueError) as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
