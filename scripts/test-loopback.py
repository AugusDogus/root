#!/usr/bin/env python3
"""Exercise the real native rules engine through six separate local clients."""
import json
import os
from pathlib import Path
import signal
import socket
import subprocess
import time
import uuid
from selection_messages import latest_selection

PROJECT = Path(__file__).resolve().parents[1]
OUTPUT = PROJECT / '.lab/results/server'
OUTPUT.mkdir(parents=True, exist_ok=True)
endpoint_file = OUTPUT / 'endpoint.json'
checks = []


def check(condition, description):
    if not condition:
        raise AssertionError(description)
    checks.append(description)


def exchange(endpoint, payload):
    with socket.create_connection(('127.0.0.1', endpoint['port']), timeout=10) as client:
        client.sendall(json.dumps(payload).encode() + b'\n')
        with client.makefile('rb') as stream:
            reply = stream.readline(4 * 1024 * 1024)
        return json.loads(reply)


def poll(endpoint, seat, after=0):
    response = exchange(endpoint, {'op': 'poll', 'token': endpoint['tokens'][seat], 'after': after})
    check(response['ok'], f'Seat {seat + 1} receives its message stream')
    return response


def snapshot(response):
    for message in response['messages']:
        if message['name'] == 'SequenceMessage':
            inner = message['value']['msg']
            if inner['name'] == 'SerializedGameState':
                return inner['value']
    raise AssertionError('Initial game snapshot is missing')


def child(entity, name):
    return next(value for value in entity['children'] if value['entityName'] == name)




endpoint = None
with (OUTPUT / 'launch.log').open('w') as log:
    started = time.time()
    process = subprocess.Popen(['bash', 'scripts/run-lab.sh', 'server'], cwd=PROJECT,
                               env={**os.environ, 'ROOT_LAB_TEST_SEED': '12345'},
                               stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f'Host exited before readiness, code {process.returncode}; inspect {OUTPUT / "launch.log"}')
            if endpoint_file.exists() and endpoint_file.stat().st_mtime >= started:
                try:
                    endpoint = json.loads(endpoint_file.read_text())
                    break
                except json.JSONDecodeError:
                    pass  # The host is still writing its readiness file.
            time.sleep(0.2)
        if endpoint is None:
            raise TimeoutError('Host did not become ready within 60 seconds')

        clients = [poll(endpoint, seat) for seat in range(6)]
        check(all(client['players'] == 6 for client in clients), 'All six clients see a six-player match')
        states = [snapshot(client) for client in clients]
        check(all(len(state['playerAccounts']) == 6 for state in states), 'Native snapshots contain six player accounts')
        check(all(child(state['entities'], 'Deck')['children'] == [] for state in states), 'Shared deck contents are hidden from all six clients')
        owner_deck = child(child(states[1]['entities'], 'EyrieDynastiesPlayer'), 'Deck')
        other_deck = child(child(states[0]['entities'], 'EyrieDynastiesPlayer'), 'Deck')
        check(len(owner_deck['children']) == 4 and other_deck['children'] == [], 'Eyrie private deck is visible to its owner and omitted for another player')

        offer = clients[0]['offer']
        check(offer['Prompt'].endswith('MarquiseDeCatSetup.ChooseStarting'), 'Marquise receives the legal keep-placement prompt')
        check(all(client['offer'] is None for client in clients[1:]), 'Other seats do not receive Marquise selection offers')
        choice = {'op': 'choose', 'token': endpoint['tokens'][0], 'counter': offer['Counter'],
                  'source': offer['Source'], 'target': offer['Targets'][0]}
        wrong_player = exchange(endpoint, {**choice, 'token': endpoint['tokens'][1]})
        check(wrong_player == {'ok': False, 'error': 'WrongPlayer'}, 'Another player cannot answer the Marquise prompt')
        invalid_target = exchange(endpoint, {**choice, 'target': str(uuid.uuid4())})
        check(invalid_target == {'ok': False, 'error': 'InvalidTarget'}, 'An unoffered target is rejected')
        target_choice = {'op': 'targets', 'token': endpoint['tokens'][0], 'counter': offer['Counter'],
                         'source': offer['Source'], 'targets': [{'kind': 'entities', 'values': [offer['Targets'][0]]}]}
        duplicate = exchange(endpoint, {**target_choice, 'targets': [{'kind': 'entities', 'values': [offer['Targets'][0]] * 2}]})
        check(duplicate == {'ok': False, 'error': 'InvalidTarget'}, 'Duplicate and excess entity selections are rejected')
        missing = exchange(endpoint, {**target_choice, 'targets': [{'kind': 'entities', 'values': []}]})
        check(missing == {'ok': False, 'error': 'InvalidTarget'}, 'Mandatory entity selections cannot be empty')
        malformed = exchange(endpoint, {**target_choice, 'targets': [{'kind': 'entities', 'values': [None]}]})
        check(malformed == {'ok': False, 'error': 'InvalidSelection'}, 'Malformed target lists are rejected')
        premature_pass = exchange(endpoint, {'op': 'pass', 'token': endpoint['tokens'][0], 'counter': offer['Counter']})
        check(premature_pass == {'ok': False, 'error': 'UnsupportedSelection'}, 'Required setup placements cannot be skipped')
        check(poll(endpoint, 0, clients[0]['next'])['offer']['Counter'] == offer['Counter'], 'Rejected choices preserve the current selection')
        check(exchange(endpoint, target_choice) == {'ok': True}, 'Legal keep placement is accepted through the private transport')
        after = poll(endpoint, 0, clients[0]['next'])
        check(after['offer']['Counter'] != offer['Counter'] and after['offer']['Prompt'].endswith('.Sawmill'), 'Native engine advances to starting sawmill placement')
        check(exchange(endpoint, choice) == {'ok': False, 'error': 'NoSuchSelection'}, 'Replaying a consumed selection is rejected')
        check(exchange(endpoint, {'op': 'poll', 'token': 'invalid', 'after': 0}) == {'ok': False, 'error': 'Unauthorized'}, 'Unknown seat credentials are rejected')
        check(exchange(endpoint, {**choice, 'counter': 'bad'}) == {'ok': False, 'error': 'InvalidSelection'}, 'Malformed selection input is rejected')
        check(exchange(endpoint, {'op': 'poll', 'token': endpoint['tokens'][0], 'after': -1}) == {'ok': False, 'error': 'InvalidCursor'}, 'Invalid message cursors are rejected')

        setup_choices = []
        finished_setup = False
        for _ in range(30):
            pending = [(seat, latest_selection(exchange(endpoint, {'op': 'poll', 'token': endpoint['tokens'][seat], 'after': 0})))
                       for seat in range(6)]
            active = [(seat, message) for seat, message in pending if message is not None]
            check(len(active) == 1, 'Exactly one seat has the current setup selection')
            seat, message = active[0]
            value = message['value']
            prompt = value['prompt']['id']
            if prompt.endswith('.BuyRiverfolkServices'):
                check(seat == 0, 'Completed six-faction setup reaches the first Marquise turn')
                finished_setup = True
                break
            request = {'token': endpoint['tokens'][seat], 'counter': value['counter']}
            if message['name'] == 'ArchetypeCustomChoiceRequired':
                request.update(op='custom', choice=0)
                check(exchange(endpoint, {**request, 'choice': len(value['buttons'])}) == {'ok': False, 'error': 'InvalidTarget'},
                      'Out-of-range faction choices preserve the selection')
            elif message['name'] == 'RiverfolkPricesRequired':
                request.update(op='prices', handCard=1, riverboats=2, mercenaries=4)
                check(exchange(endpoint, {**request, 'handCard': 5}) == {'ok': False, 'error': 'InvalidTarget'},
                      'Riverfolk service prices outside 1 to 4 are rejected')
            elif message['name'] == 'SelectionWithTargetsRequired':
                source = value['sourceID']
                if source in value['targetMap']:
                    target = value['targetMap'][source][0]['validTargets'][0]
                    request.update(op='targets', source=source, targets=[{'kind': 'entities', 'values': [target]}])
                else:
                    request.update(op='pass')
            else:
                raise AssertionError(f'Unsupported setup selection: {message["name"]}')
            check(exchange(endpoint, {**request, 'token': endpoint['tokens'][(seat + 1) % 6]}) == {'ok': False, 'error': 'WrongPlayer'},
                  'Another seat cannot submit the current setup response')
            check(exchange(endpoint, request) == {'ok': True}, f'Seat {seat + 1}: {prompt}')
            setup_choices.append({'seat': seat + 1, 'prompt': prompt, 'operation': request['op']})
        check(finished_setup, 'All six factions finish setup through authenticated client connections')
        joined = [exchange(endpoint, {'op': 'join', 'token': endpoint['tokens'][seat]}) for seat in range(6)]
        check(all(reply['ok'] and len(reply['messages']) <= 2 for reply in joined), 'Late joins receive a current snapshot and their pending selection without replaying history')
        joined_states = [snapshot(reply) for reply in joined]
        names = ['MarquiseDeCatPlayer', 'EyrieDynastiesPlayer', 'WoodlandAlliancePlayer', 'VagabondPlayer', 'LizardCultPlayer', 'RiverfolkCompanyPlayer']
        for seat, state in enumerate(joined_states):
            check(state['playerAccounts'] == [joined[seat]['roster'][seat]['account']], f'Joined seat {seat + 1} controls only its authenticated account')
            check(child(state['entities'], 'Deck')['children'] == [], f'Joined seat {seat + 1} cannot see the shared deck')
            check(len(child(child(state['entities'], names[seat]), 'Hand')['children']) > 0, f'Joined seat {seat + 1} receives its own dealt hand')
            for other, name in enumerate(names):
                if other == seat or other == 5:
                    continue  # Riverfolk deliberately has a public hand.
                check(child(child(state['entities'], name), 'Hand')['children'] == [], f'Joined seat {seat + 1} cannot see seat {other + 1}\'s private hand')
        current = latest_selection(joined[0])
        check(current is not None and current['value']['prompt']['id'].endswith('.BuyRiverfolkServices'), 'Joining preserves the current turn and pending selection')
        counter = current['value']['counter']
        check(exchange(endpoint, {'op': 'custom', 'token': endpoint['tokens'][0], 'counter': counter, 'choice': None}) == {'ok': True},
              'The current player can continue after every seat joins')
        continued = poll(endpoint, 0, joined[0]['next'])
        next_selection = latest_selection(continued)
        check(next_selection is not None and next_selection['value']['counter'] != counter,
              'Snapshot creation preserves authoritative state and subsequent message delivery')
        result = {'status': 'passed', 'checks': checks, 'setupChoices': setup_choices}
        (OUTPUT / 'test-results.json').write_text(json.dumps(result, indent=2) + '\n')
        print(f'PASS: {len(checks)} checks against the six-player native engine')
    finally:
        if endpoint is not None and process.poll() is None:
            try:
                exchange(endpoint, {'op': 'shutdown', 'token': endpoint['controlToken']})
            except (OSError, ValueError):
                print('Host shutdown request failed; terminating only this test process group.')
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGTERM)
            process.wait(timeout=10)
