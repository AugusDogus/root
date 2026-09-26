#!/usr/bin/env python3
"""Run a private host or one manual-play client, always on an isolated display."""
import argparse
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import tempfile
import time

PROJECT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['host', 'client'])
    parser.add_argument('--invite', type=Path, help='Seat invitation file, required for a client')
    parser.add_argument('--minutes', type=int, default=180, help='Session lifetime, 1 to 720 minutes (default: 180)')
    saves = parser.add_mutually_exclusive_group()
    saves.add_argument('--save', type=Path, help='Autosave a new host match to a new file')
    saves.add_argument('--resume', type=Path, help='Resume a host checkpoint and continue autosaving it')
    parser.add_argument('--seat-lab', type=int, choices=range(1, 7), help='Use a separately prepared graphical client lab')
    args = parser.parse_args()
    if not 1 <= args.minutes <= 720:
        parser.error('--minutes must be between 1 and 720')
    if args.mode == 'client' and (args.invite is None or not args.invite.is_file()):
        parser.error('client requires --invite pointing to an existing seat invitation')
    if args.mode != 'host' and (args.save or args.resume):
        parser.error('--save and --resume apply only to a host')
    if args.mode != 'client' and args.seat_lab:
        parser.error('--seat-lab applies only to a client')
    if args.save and args.save.exists():
        parser.error('Save file already exists. Use --resume or choose a new path; the file was not changed')
    if args.resume and not args.resume.is_file():
        parser.error('Resume file does not exist')
    env = {**os.environ, 'ROOT_LAB_LIFETIME_SECONDS': str(args.minutes * 60)}
    checkpoint = args.save or args.resume
    if checkpoint:
        env['ROOT_LAB_SAVE_FILE'] = str(checkpoint.resolve())
        env['ROOT_LAB_RESUME'] = '1' if args.resume else '0'
    mode = 'server' if args.mode == 'host' else 'client'
    if args.mode == 'client':
        client_lab = PROJECT / (f'.lab/clients/seat-{args.seat_lab}' if args.seat_lab else '.lab/client')
        env.update(ROOT_LAB_DIR=str(client_lab), ROOT_CLIENT_CONNECTION=str(args.invite.resolve()))
    lab = Path(env.get('ROOT_LAB_DIR', PROJECT / '.lab'))
    output = lab / 'results' / mode
    output.mkdir(parents=True, exist_ok=True)
    invitations = None
    with (output / 'launch.log').open('w') as log:
        started = time.time()
        process = subprocess.Popen(['bash', 'scripts/run-lab.sh', mode], cwd=PROJECT, env=env,
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            if args.mode == 'host':
                endpoint_file = output / 'endpoint.json'
                deadline = time.monotonic() + 90
                endpoint = None
                while time.monotonic() < deadline:
                    if process.poll() is not None:
                        raise RuntimeError(f'Host exited before readiness. Inspect {output / "launch.log"}')
                    try:
                        if endpoint_file.stat().st_mtime >= started:
                            endpoint = json.loads(endpoint_file.read_text())
                            break
                    except (FileNotFoundError, json.JSONDecodeError):
                        pass
                    time.sleep(0.2)
                if endpoint is None:
                    raise TimeoutError(f'Host did not become ready within 90 seconds. Inspect {output / "launch.log"}')
                invitations = Path(tempfile.mkdtemp(prefix='invitations-', dir=PROJECT / '.lab'))
                for seat, token in enumerate(endpoint['tokens'], 1):
                    with os.fdopen(os.open(invitations / f'seat-{seat}.json', os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), 'w') as file:
                        json.dump({'port': endpoint['port'], 'token': token}, file)
                print(f'Private host ready on 127.0.0.1:{endpoint["port"]}. Seat invitations: {invitations}', flush=True)
                print('Stop with Ctrl+C. Invitations expire when this host stops.', flush=True)
                if checkpoint:
                    print(f'Autosaving accepted moves to {checkpoint.resolve()}. Resume with --resume after a restart.', flush=True)
            else:
                print(f'Manual client starting on its own muted Xvfb display. Log: {output / "player.log"}', flush=True)
            return process.wait()
        except KeyboardInterrupt:
            return 130
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
            if invitations is not None:
                shutil.rmtree(invitations)


if __name__ == '__main__':
    raise SystemExit(main())
