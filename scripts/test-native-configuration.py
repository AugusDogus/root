#!/usr/bin/env python3
"""Exercise configuration captured from Root's native setup in the private host."""
import argparse
import json
import os
from pathlib import Path
import random
import shutil
import sys
import tempfile
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from runtime import GameProcess
from steam import discover
from network import exchange
from selection_messages import latest_selection
from gameplay_driver import choose
from recovery_checks import canonical


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('configuration', type=Path)
    parser.add_argument('--restart-at', type=int, default=20)
    parser.add_argument('--export-checkpoint', type=Path, help='Preserve the verified match as a local graphical-test fixture')
    args = parser.parse_args()
    if args.export_checkpoint:
        args.export_checkpoint = args.export_checkpoint.resolve()
        if not args.export_checkpoint.is_relative_to(PROJECT / '.lab'):
            raise ValueError('UI fixtures must stay in the private .lab directory.')
        if args.export_checkpoint.exists():
            raise FileExistsError('The requested fixture already exists. Choose a new name to preserve it.')
    initialization = json.loads(args.configuration.read_text())
    assert len(initialization['TuberPlayers']) == 6
    lab = PROJECT / '.lab/steam-game-probe'
    with lock_data(lab), tempfile.TemporaryDirectory(prefix='native-configuration-', dir=lab) as temporary:
        shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', lab / 'host/game/BepInEx/plugins/EngineProbe.dll')
        config_file = lab / 'host/native-setup.json'
        config_file.write_text(json.dumps(initialization))
        checkpoint = Path(temporary) / 'checkpoint.json'
        game = GameProcess(discover(), lab / 'host', 'server', headless=True, save=checkpoint)
        report = {'status': 'failed', 'decisions': 0, 'prompts': []}
        try:
            endpoint = game.endpoint()
            def request(seat, body):
                return json.loads(exchange(endpoint['port'], json.dumps({**body, 'token': endpoint['tokens'][seat]}).encode()))
            humans = [index for index, player in enumerate(initialization['TuberPlayers']) if player['isHuman']]
            cursors = {seat: 0 for seat in humans}
            selections = {seat: None for seat in humans}
            identities = None
            rng = random.Random(12345)
            undo_ids = set()
            deadline = time.monotonic() + 180
            while report['decisions'] < 120:
                replies = {seat: request(seat, {'op': 'join'} if identities is None else {'op': 'poll', 'after': cursors[seat]}) for seat in humans}
                assert all(reply['ok'] for reply in replies.values()), replies
                reply = replies[humans[0]]
                accounts = [player['account'] for player in reply['roster']]
                if identities is None:
                    identities = accounts
                    public = reply['initialization']
                    assert public.get('gameState') is None and public.get('saveData') is None
                    assert not public.get('DeckStacking') and not public.get('SpecialClearings')
                    for player in public['TuberPlayers']:
                        assert not player.get('CardsInHand') and not player.get('CardsInPlay')
                        assert not player.get('metadata') or set(player['metadata']) == {'rootSixPlayer.aiSeat'}
                    for field in ('AdvancedSetup', 'AdsetDisableDraft', 'ChosenMap', 'ChosenDeck', 'EnableBluff'):
                        assert public[field] == initialization[field], field
                    assert reply['setup']['CooperativeMode'] == (initialization['options'].get('CooperativeMode') == 'true')
                    chosen = [player['Faction'] for player in initialization['TuberPlayers']]
                    if 'Invalid' not in chosen:
                        actual = [player['Faction'] for player in public['TuberPlayers']]
                        vagabonds = {'Vagabond', 'SecondVagabond'}
                        # Native setup can swap the internal Vagabond IDs when
                        # shuffling. Characters and controllers belong to seats.
                        assert sorted(actual) == sorted(chosen) and all(
                            expected == received or {expected, received} == vagabonds
                            for expected, received in zip(chosen, actual)
                        ), f'Native startup changed chosen factions: expected {chosen}, got {actual}'
                        for expected, received in zip(initialization['TuberPlayers'], public['TuberPlayers']):
                            assert received['isHuman'] == expected['isHuman'], 'Native startup changed a seat controller'
                            if expected['Faction'] in vagabonds and expected['StartingCharacter'] != 'Unknown':
                                assert received['StartingCharacter'] == expected['StartingCharacter'], 'Native startup changed a Vagabond character'
                assert accounts == identities, 'Faction selection changed private seat identities'
                for seat, response in replies.items():
                    cursors[seat] = response['next']
                    selections[seat] = latest_selection(response, selections[seat])
                if reply['gameOver']:
                    break
                if report['decisions'] >= args.restart_at and 'recovery' not in report:
                    before = {seat: request(seat, {'op': 'join'}) for seat in humans}
                    document = json.loads(checkpoint.read_text())
                    assert document['Version'] == 4
                    saved = document['Initialization']
                    assert saved['DoNotShufflePlayers'] == initialization['DoNotShufflePlayers']
                    assert saved['options'].get('RandomSuits') == initialization['options'].get('RandomSuits')
                    game.close()
                    game = GameProcess(discover(), lab / 'host', 'server', headless=True, save=checkpoint, resume=True)
                    endpoint = game.endpoint()
                    for seat in humans:
                        after = request(seat, {'op': 'join'})
                        assert after['roster'] == before[seat]['roster']
                        if canonical(after['messages']) != canonical(before[seat]['messages']):
                            evidence = PROJECT / 'results/native-recovery-mismatch.json'
                            evidence.write_text(json.dumps({'seat': seat, 'before': before[seat], 'after': after}, indent=2))
                            raise AssertionError(f'Seat {seat + 1} changed on recovery; see {evidence}')
                        cursors[seat] = after['next']
                        selections[seat] = latest_selection(after)
                    report['recovery'] = 'passed'
                    report['recoveredAt'] = report['decisions']
                    report['unassignedAtRecovery'] = sum(player['faction'] == 4 for player in reply['roster'])
                active = next(((seat, selection) for seat, selection in selections.items() if selection is not None), None)
                if active is None:
                    if time.monotonic() > deadline:
                        raise RuntimeError('Native configuration stopped producing decisions')
                    time.sleep(.1)
                    continue
                seat, selection = active
                prompt = selection['value']['prompt']['id']
                if prompt not in report['prompts']:
                    report['prompts'].append(prompt)
                response = request(seat, choose(selection, rng, undo_ids))
                assert response == {'ok': True}, (prompt, response)
                report['decisions'] += 1
                if report['decisions'] % 20 == 0:
                    print(f"Native setup: {report['decisions']} decisions accepted", flush=True)
                deadline = time.monotonic() + 30
            report['roster'] = [player['faction'] for player in reply['roster']]
            assert len(set(report['roster'])) == 6 and 4 not in report['roster'], 'Draft did not assign six different factions'
            report['status'] = 'passed'
            print(json.dumps(report), flush=True)
        finally:
            game.close()
            config_file.unlink(missing_ok=True)
            if args.export_checkpoint and report['status'] == 'passed':
                args.export_checkpoint.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                with checkpoint.open('rb') as source, args.export_checkpoint.open('xb') as destination:
                    shutil.copyfileobj(source, destination)
            (PROJECT / 'results/native-configuration-test.json').write_text(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
