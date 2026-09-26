#!/usr/bin/env python3
"""Native six-seat board and move over Steam P2P, isolated and muted."""
from contextlib import ExitStack
import json
import os
import random
from pathlib import Path
import shutil
import sys

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from network import exchange
from runtime import GameProcess
from steam import discover
from selection_messages import latest_selection
from gameplay_driver import choose


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-game-probe'
    with lock_data(lab), ExitStack() as resources:
        for role in ('host', 'client'):
            game = lab / role / 'game'
            if not game.exists():
                shutil.copytree(PROJECT / '.lab/friends-launcher-test' / role / 'game', game)
            shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', game / 'BepInEx/plugins/EngineProbe.dll')
            shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.pdb', game / 'BepInEx/plugins/EngineProbe.pdb')
        installation = discover()
        host = GameProcess(installation, lab / 'host', 'server', headless=True)
        resources.callback(host.close)
        endpoint = host.endpoint()

        def request(seat, body):
            return json.loads(exchange(endpoint['port'], json.dumps({**body, 'token': endpoint['tokens'][seat]}).encode()))

        # Seat 1 remains local to the host. Reach Riverfolk's native placement control.
        rng = random.Random(12345)
        undo_ids = set()
        for _ in range(80):
            remote = request(5, {'op': 'poll', 'after': 0})
            if remote['offer'] and remote['offer']['Targets']:
                break
            active = [(seat, latest_selection(request(seat, {'op': 'poll', 'after': 0}))) for seat in range(6)]
            active = [(seat, selection) for seat, selection in active if selection is not None]
            if len(active) != 1:
                raise AssertionError('Expected one setup decision before Riverfolk placement.')
            seat, selection = active[0]
            print(f'Setup: seat {seat + 1}, {selection["name"]}, {selection["value"].get("prompt", {}).get("id", "")}', flush=True)
            offer = request(seat, {'op': 'poll', 'after': 0})['offer']
            if offer and offer['Targets']:
                choice = {'op': 'choose', 'counter': offer['Counter'], 'source': offer['Source'], 'target': offer['Targets'][0]}
            else:
                choice = choose(selection, rng, undo_ids)
            assert request(seat, choice) == {'ok': True}
        else:
            raise AssertionError('Riverfolk setup not reached.')
        config = lab / 'client/steam-config.json'
        resources.callback(lambda: config.unlink(missing_ok=True))
        config.write_text(json.dumps({'port': endpoint['port'], 'tokens': endpoint['tokens']}))
        evidence = lab / 'client/results/client-probe/test-results.json'
        evidence.unlink(missing_ok=True)
        client = GameProcess(installation, lab / 'client', 'steam-client-probe', headless=True)
        resources.callback(client.close)
        code = client.process.wait(timeout=420)
        result = json.loads(evidence.read_text())
        assert code == 0 and result['status'] == 'passed' and result['players'] == 6 and result['accepted'] == 1, {'exitCode': code, **result}
        remote = request(5, {'op': 'join'})
        assert result['localAccount'] == remote['roster'][5]['account']
        assert (remote['offer']['Counter'] if remote['offer'] else None) == result['counter']
        result.update(transport='SteamNetworkingSockets P2P self-connection', remotePeerTested=False,
                      dedicatedProtonPrefixes=True, invitationsSent=0)
        (PROJECT / 'results/native-steam-test.json').write_text(json.dumps(result, indent=2) + '\n')
        print('PASS: native six-seat board and Riverfolk setup move over Steam P2P.')


if __name__ == '__main__':
    main()
