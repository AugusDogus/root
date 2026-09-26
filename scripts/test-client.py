#!/usr/bin/env python3
"""Run the native client and native host in separate, muted Xvfb labs."""
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import time
import argparse
import random
import tempfile
from contextlib import ExitStack
from ssh_test_tunnel import local_ssh_forward
from selection_messages import latest_selection
from gameplay_driver import choose
from gameplay_checks import child, PLAYER_ENTITIES, check_private_snapshots

PROJECT = Path(__file__).resolve().parents[1]
HOST = PROJECT / '.lab/results/server'
CLIENT = PROJECT / '.lab/client'


def exchange(endpoint, payload):
    with socket.create_connection(('127.0.0.1', endpoint['port']), timeout=10) as client:
        client.sendall(json.dumps(payload).encode() + b'\n')
        with client.makefile('rb') as stream:
            return json.loads(stream.readline(4 * 1024 * 1024))


def stop(process):
    if process is None:
        return
    try:
        process.wait(timeout=15)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGTERM)
        process.wait(timeout=15)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--seat', type=int, choices=[1, 6], default=1)
    parser.add_argument('--scenario', choices=['setup', 'ambush'], default='setup', help='Ambush selects the defending seat automatically')
    parser.add_argument('--ssh-tunnel-test', action='store_true', help='Route the native client through a disposable local SSH tunnel')
    args = parser.parse_args()
    seat_number = args.seat
    HOST.mkdir(parents=True, exist_ok=True)
    endpoint = None
    client_process = None
    connection = None
    started = time.time()
    with ExitStack() as resources, (HOST / 'client-test-launch.log').open('w') as host_log, (CLIENT / 'launch.log').open('w') as client_log:
        host_process = subprocess.Popen(['bash', 'scripts/run-lab.sh', 'server'], cwd=PROJECT,
                                        env={**os.environ, 'ROOT_LAB_TEST_SEED': '12345'},
                                        stdout=host_log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 70
            while time.monotonic() < deadline:
                if host_process.poll() is not None:
                    raise RuntimeError('Private host exited before readiness. Inspect .lab/results/server/client-test-launch.log.')
                try:
                    endpoint_file = HOST / 'endpoint.json'
                    if endpoint_file.stat().st_mtime >= started:
                        endpoint = json.loads(endpoint_file.read_text())
                        break
                except (FileNotFoundError, json.JSONDecodeError):
                    pass
                time.sleep(0.2)
            if endpoint is None:
                raise TimeoutError('Private host did not become ready within 70 seconds.')
            if args.scenario == 'ambush':
                rng = random.Random(12345)
                undo_ids = set()
                cursors = [0] * 6
                selections = [None] * 6
                for _ in range(300):
                    for seat in range(6):
                        reply = exchange(endpoint, {'op': 'poll', 'token': endpoint['tokens'][seat], 'after': cursors[seat]})
                        assert reply['ok'], reply
                        cursors[seat] = reply['next']
                        selections[seat] = latest_selection(reply, selections[seat])
                    active = [(seat, selection) for seat, selection in enumerate(selections) if selection is not None]
                    assert active, 'Match stopped before an ambush prompt'
                    seat, selection = active[0]
                    if selection['value']['prompt']['id'].endswith('.ChooseAmbushCards'):
                        seat_number = seat + 1
                        break
                    request = choose(selection, rng, undo_ids)
                    assert exchange(endpoint, {**request, 'token': endpoint['tokens'][seat]}) == {'ok': True}, selection
                else:
                    raise AssertionError('No ambush prompt within 300 decisions')
            elif seat_number == 6:
                # Advance other factions' setup so the sixth client has a real pending placement.
                for _ in range(20):
                    active = []
                    for seat in range(6):
                        response = exchange(endpoint, {'op': 'poll', 'token': endpoint['tokens'][seat], 'after': 0})
                        selection = latest_selection(response)
                        if selection is not None:
                            active.append((seat, selection))
                    assert len(active) == 1, 'Expected one setup decision'
                    seat, selection = active[0]
                    if seat == 5:
                        break
                    value = selection['value']
                    request = {'token': endpoint['tokens'][seat], 'counter': value['counter']}
                    if selection['name'] == 'ArchetypeCustomChoiceRequired':
                        request.update(op='custom', choice=0)
                    elif value['sourceID'] in value['targetMap']:
                        request.update(op='choose', source=value['sourceID'], target=value['targetMap'][value['sourceID']][0]['validTargets'][0])
                    else:
                        request.update(op='pass')
                    assert exchange(endpoint, request) == {'ok': True}, selection['name']
                else:
                    raise AssertionError('Did not reach Riverfolk setup')
            client_port = endpoint['port']
            if args.ssh_tunnel_test:
                client_port = resources.enter_context(local_ssh_forward(client_port, CLIENT / 'ssh-test.log'))
            with tempfile.NamedTemporaryFile(mode='w', prefix='test-invite-', suffix='.json', dir=CLIENT, delete=False) as config:
                connection = Path(config.name)
                json.dump({'port': client_port, 'token': endpoint['tokens'][seat_number - 1]}, config)
            client_process = subprocess.Popen(['bash', 'scripts/run-lab.sh', 'client-probe'], cwd=PROJECT,
                                              env={**os.environ, 'ROOT_LAB_DIR': str(CLIENT), 'ROOT_CLIENT_CONNECTION': str(connection)}, stdout=client_log,
                                              stderr=subprocess.STDOUT, start_new_session=True)
            code = client_process.wait(timeout=260)
            if code != 0:
                raise RuntimeError(f'Client exited with code {code}. Inspect .lab/client/launch.log.')
            log = (CLIENT / 'game/BepInEx/LogOutput.log').read_text()
            if 'Private client failed:' in log or 'Board launch failed:' in log:
                raise RuntimeError('Native client integration failed. Inspect .lab/client/game/BepInEx/LogOutput.log.')
            if 'Native match relay connected' not in log:
                raise AssertionError('Native match relay was not connected.')
            result = json.loads((CLIENT / 'results/client-probe/test-results.json').read_text())
            assert result['status'] == 'passed', result
            assert result['players'] == 6 and result['submitted'] and result['accepted'] == 1, result
            assert 'Local authority disabled' in log
            remote = exchange(endpoint, {'op': 'join', 'token': endpoint['tokens'][seat_number - 1]})
            assert remote['ok'] and (remote['offer']['Counter'] if remote['offer'] else None) == result['counter']
            assert result['localAccount'] == remote['roster'][seat_number - 1]['account']
            if args.scenario == 'ambush':
                assert result['submittedPrompt'].endswith('.ChooseAmbushCards'), result
                state = remote['messages'][0]['value']['msg']['value']
                hand = child(child(state['entities'], PLAYER_ENTITIES[seat_number - 1]), 'Hand')
                assert result['submittedTarget'] not in [card['entityID'] for card in hand['children']], 'Played ambush must leave the defender hand'
                check_private_snapshots(endpoint)
            print(f'PASS: native client in seat {seat_number} renders six seats and submits {args.scenario} through Root controls.')
            if args.ssh_tunnel_test:
                (CLIENT / 'results/ssh-tunnel-test.json').write_text(json.dumps({
                    'status': 'passed', 'seat': seat_number, 'transport': 'SSH public-key authentication with pinned host key',
                    'scope': 'loopback tunnel; separate-machine networking not tested', 'accepted': result['accepted']
                }, indent=2) + '\n')
        finally:
            stop(client_process)
            if endpoint is not None and host_process.poll() is None:
                try:
                    exchange(endpoint, {'op': 'shutdown', 'token': endpoint['controlToken']})
                except (OSError, ValueError):
                    pass  # stop() still terminates only this test process group.
            stop(host_process)
            if connection is not None:
                connection.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
