#!/usr/bin/env python3
"""Smoke-test the manual launcher, isolated displays, invitations, and cleanup."""
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import time

PROJECT = Path(__file__).resolve().parents[1]
OUTPUT = PROJECT / '.lab/results/launcher'


def await_ready(process, predicate, description, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(f'{description}: launcher exited with code {process.returncode}')
        result = predicate()
        if result:
            return result
        time.sleep(0.2)
    raise TimeoutError(f'{description}: timed out after {seconds} seconds')


def stop(process):
    if process is not None and process.poll() is None:
        process.send_signal(signal.SIGINT)
        process.wait(timeout=30)


def main():
    OUTPUT.mkdir(parents=True, exist_ok=True)
    client = None
    invitations = None
    with (OUTPUT / 'host.log').open('w') as host_log, (OUTPUT / 'client.log').open('w') as client_log:
        host = subprocess.Popen(['python3', 'scripts/private-match.py', 'host', '--minutes', '5'],
                                cwd=PROJECT, stdout=host_log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            def ready_invitations():
                text = (OUTPUT / 'host.log').read_text()
                for line in text.splitlines():
                    if 'Seat invitations: ' in line:
                        return Path(line.split('Seat invitations: ', 1)[1])
                return None

            invitations = await_ready(host, ready_invitations, 'Host readiness')
            seats = sorted(invitations.glob('seat-*.json'))
            assert len(seats) == 6
            assert all(file.stat().st_mode & 0o777 == 0o600 for file in seats)
            client_started = time.time()
            client = subprocess.Popen(['python3', 'scripts/private-match.py', 'client', '--invite', str(seats[0]), '--minutes', '5'],
                                      cwd=PROJECT, stdout=client_log, stderr=subprocess.STDOUT, start_new_session=True)
            client_plugin_log = PROJECT / '.lab/client/game/BepInEx/LogOutput.log'

            def connected():
                if not client_plugin_log.exists() or client_plugin_log.stat().st_mtime < client_started:
                    return False
                text = client_plugin_log.read_text()
                if 'Private connection stopped:' in text:
                    raise AssertionError('Manual client failed; inspect .lab/client/game/BepInEx/LogOutput.log')
                return 'Native match relay connected to the private host' in text

            await_ready(client, connected, 'Manual client connection', seconds=120)
            host_display_file = PROJECT / '.lab/results/server/display.json'
            client_display_file = PROJECT / '.lab/client/results/client/display.json'
            host_display = json.loads(host_display_file.read_text())
            client_display = json.loads(client_display_file.read_text())
            assert host_display['display'] != client_display['display']
            assert all(Path(display['xauthority']).is_file() for display in [host_display, client_display])
            assert all(display['display'] != os.environ.get('DISPLAY') for display in [host_display, client_display])
            config = json.loads(seats[0].read_text())
            with socket.create_connection(('127.0.0.1', config['port']), timeout=10) as connection:
                connection.sendall(json.dumps({'op': 'join', 'token': config['token']}).encode() + b'\n')
                with connection.makefile('rb') as stream:
                    response = json.loads(stream.readline(4 * 1024 * 1024))
            assert response['ok'] and response['players'] == 6
            assert response['offer']['Prompt'].endswith('MarquiseDeCatSetup.ChooseStarting')
        finally:
            try:
                stop(client)
            finally:
                stop(host)
        assert invitations is not None and not invitations.exists()
        assert not (PROJECT / '.lab/client/connection.json').exists()
        assert not host_display_file.exists() and not client_display_file.exists()
        assert not (PROJECT / '.lab/results/server/endpoint.json').exists()
        result = {'status': 'passed', 'checks': [
            'Six invitations have mode 0600', 'Manual native client connects without automated moves',
            'Host and client use distinct Xvfb displays, separate from the desktop',
            'Ctrl+C removes invitations, endpoint, client credentials, and display metadata',
        ]}
        (OUTPUT / 'test-results.json').write_text(json.dumps(result, indent=2) + '\n')
        print('PASS: manual launcher starts isolated sessions and cleans up after Ctrl+C')


if __name__ == '__main__':
    main()
