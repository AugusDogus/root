#!/usr/bin/env python3
"""Exercise DLC setup and turns in muted, isolated native six-seat matches."""
from collections import Counter
import argparse
import json
import os
from pathlib import Path
import random
import shutil
import sys
import subprocess
import tempfile
import traceback
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

BASE = [0, 1, 2, 3, 6, 7]
MARAUDER = [14, 15, 8, 9, 6, 7]
CASES = [
    ('ai-base', {'Factions': [14, 15, 0, 1, 2, 3], 'AI': [None, None, 0, 1, 2, 1]}),
    ('ai-expansions', {'Factions': [0, 1, 6, 7, 8, 9], 'AI': [None, None, 1, 2, 0, 1], 'Map': 2, 'Deck': 1}),
    ('ai-marauder', {'Factions': [2, 3, 14, 15, 0, 1], 'AI': [None, None, 2, 1, 1, 0], 'Map': 3}),
    ('ai-mixed', {'Factions': [14, 15, 10, 11, 2, 3], 'AI': [None, None, None, None, 0, 2]}),
    ('ai-vagabonds', {'Factions': [0, 1, 2, 3, 5, 8], 'AI': [None, 0, 1, None, 2, 1],
        'Characters': [0, 0, 0, 7, 8, 0], 'AdvancedSetup': True}),
    ('ai-solo', {'Factions': BASE, 'AI': [None, 0, 1, 2, 1, 0]}),
    ('ai-vagabond-host', {'Factions': [5, 0, 1, 2, 3, 8], 'AI': [None, 0, 1, 2, 2, 1],
        'Characters': [8, 0, 0, 0, 7, 0]}),
    ('marauder', {'Factions': MARAUDER, 'Map': 2, 'Deck': 1}),
    ('clockwork', {'Factions': [14, 15, 10, 11, 12, 13], 'Map': 3}),
    *[(f'clockwork-traits-{character}', {'Factions': [14, 15, 10, 11, 12, 13], 'Map': 3,
        'BotTraits': [[], [], [0, 1, 2, 3], [0, 1, 2, 3], [0, 1, 2, 3], [0, 1, 2, 3]],
        'VagabotCharacter': character}) for character in (1, 2, 3)],
    ('vagabonds', {'Factions': [0, 1, 2, 3, 5, 8], 'Characters': [0, 0, 0, 7, 8, 0], 'Map': 1, 'Deck': 1, 'Landmarks': [2, 3], 'Hirelings': [20, 21, 22]}),
    ('hirelings-a', {'Factions': BASE, 'Hirelings': [23, 26, 27], 'Landmarks': [0, 1]}),
    ('hirelings-b', {'Factions': BASE, 'Hirelings': [28, 20, 23], 'Landmarks': [4, 5]}),
    ('hirelings-c', {'Factions': MARAUDER, 'Hirelings': [16, 17, 18], 'AdvancedSetup': True}),
    ('hirelings-d', {'Factions': MARAUDER, 'Hirelings': [19, 20, 26], 'Map': 3, 'Deck': 1}),
    ('hirelings-e', {'Factions': [0, 1, 2, 3, 8, 9], 'Characters': [0, 0, 0, 9, 0, 0], 'Hirelings': [24, 25, 23], 'Map': 2}),
    *[(f'vagabond-pair-{first}', {'Factions': [0, 1, 2, 3, 5, 8], 'Characters': [0, 0, 0, first, first + 1, 0]}) for first in (1, 3, 5)],
]


def run_case(lab, name, setup, resume_checkpoint=None):
    seed = int(os.environ.get('ROOT_DLC_TEST_SEED', '12345'))
    (lab / 'host/results/server/pending.json').unlink(missing_ok=True)
    (lab / 'host/match-setup.json').write_text(json.dumps(setup))
    scratch = tempfile.TemporaryDirectory(prefix='dlc-recovery-', dir=lab)
    checkpoint = Path(scratch.name) / 'checkpoint.json' if resume_checkpoint or name.startswith(('clockwork', 'ai-')) or name == 'vagabond-pair-1' else None
    if resume_checkpoint:
        shutil.copy2(resume_checkpoint, checkpoint)
    game = GameProcess(discover(), lab / 'host', 'server', headless=True, save=checkpoint,
                       resume=resume_checkpoint is not None, test_seed=seed)
    endpoint = None
    report = {'name': name, 'status': 'failed', 'setup': setup, 'seed': seed}
    prompts = Counter()
    decisions_by_seat = Counter()
    latest = None
    try:
        endpoint = game.endpoint()
        def request(seat, body):
            return json.loads(exchange(endpoint['port'], json.dumps({**body, 'token': endpoint['tokens'][seat]}).encode()))
        humans = [seat for seat, faction in enumerate(setup['Factions']) if not 10 <= faction <= 13 and setup.get('AI', [None] * 6)[seat] is None]
        for seat in set(range(6)) - set(humans):
            response = request(seat, {'op': 'join'})
            assert response == {'ok': False, 'error': 'BotSeat'}, f"AI seat {seat + 1}: ok={response.get('ok')}, error={response.get('error')}"
        rng = random.Random(seed)
        undo_ids = set()
        cursors = {seat: 0 for seat in humans}
        selections = {seat: None for seat in humans}
        for step in range(400):
            if step and step % 40 == 0:
                print(f'{name}: {step} human decisions accepted', flush=True)
            replies = {seat: request(seat, {'op': 'join'} if step == 0 else {'op': 'poll', 'after': cursors[seat]}) for seat in humans}
            assert all(reply['ok'] for reply in replies.values()), replies
            for seat, reply in replies.items():
                cursors[seat] = reply['next']
                selections[seat] = latest_selection(reply, selections[seat])
            latest = replies[humans[0]]
            assert [item['faction'] for item in latest['roster']] == setup['Factions']
            assert latest['setup']['Map'] == setup.get('Map', 0)
            assert latest['setup']['Deck'] == setup.get('Deck', 0)
            assert set(latest['setup']['Hirelings']) == set(setup.get('Hirelings', []))
            assert set(latest['setup']['Landmarks']) == set(setup.get('Landmarks', []))
            assert latest['setup']['Characters'] == setup.get('Characters', [0] * 6)
            assert latest['setup']['AI'] == setup.get('AI', [None] * 6)
            for seat, difficulty in enumerate(setup.get('AI', [None] * 6)):
                if difficulty is not None:
                    assert latest['lobby'][seat]['State'] == 'AI · ' + ['Easy', 'Medium', 'Hard'][difficulty]
            assert latest['setup']['BotTraits'] == setup.get('BotTraits', [[] for _ in range(6)])
            assert latest['setup']['VagabotCharacter'] == setup.get('VagabotCharacter', 1)
            if (name != 'clockwork' and step >= 120 and all(decisions_by_seat[seat] >= 3 for seat in humans)) or latest['gameOver']:
                report.update(status='passed', decisions=step, decisionsBySeat=dict(decisions_by_seat), gameOver=latest['gameOver'])
                break
            active = [(seat, selection) for seat, selection in selections.items() if selection is not None]
            deadline = time.monotonic() + 30
            while not active and not latest['gameOver'] and time.monotonic() < deadline:
                # Some native AI decisions now finish on subsequent Unity frames.
                time.sleep(.1)
                replies = {seat: request(seat, {'op': 'poll', 'after': cursors[seat]}) for seat in humans}
                assert all(reply['ok'] for reply in replies.values()), replies
                for seat, reply in replies.items():
                    cursors[seat] = reply['next']
                    selections[seat] = latest_selection(reply, selections[seat])
                latest = replies[humans[0]]
                active = [(seat, selection) for seat, selection in selections.items() if selection is not None]
            if latest['gameOver']:
                report.update(status='passed', decisions=step, decisionsBySeat=dict(decisions_by_seat), gameOver=True)
                break
            if not active:
                raise RuntimeError('No human selection or game end after waiting 30 seconds for native AI')
            if step >= (15 if name.startswith('ai-') else 60) and checkpoint is not None and 'recovery' not in report:
                assert json.loads(checkpoint.read_text())['Version'] == 4, 'New saves must reject older mods without AI seat ownership'
                snapshots = {seat: request(seat, {'op': 'join'}) for seat in humans}
                # Snapshot only at a pending human choice, after native AI has settled.
                old_endpoint = endpoint
                game.close()
                game = GameProcess(discover(), lab / 'host', 'server', headless=True, save=checkpoint, resume=True)
                endpoint = game.endpoint()
                for seat in humans:
                    restored = request(seat, {'op': 'join'})
                    assert restored['setup'] == replies[seat]['setup'], 'Saved DLC settings changed'
                    assert restored['roster'] == replies[seat]['roster'], 'Saved factions changed'
                    assert canonical(restored['messages']) == canonical(snapshots[seat]['messages']), f'Seat {seat + 1} changed after recovery'
                    cursors[seat] = restored['next']
                    selections[seat] = latest_selection(restored)
                    assert json.loads(exchange(endpoint['port'], json.dumps({'op': 'join', 'token': old_endpoint['tokens'][seat]}).encode())) == {'ok': False, 'error': 'Unauthorized'}
                for seat in set(range(6)) - set(humans):
                    assert request(seat, {'op': 'join'}) == {'ok': False, 'error': 'BotSeat'}
                report['recovery'] = 'Settings, bot seats, human snapshots and pending decisions preserved; old tokens rejected'
                active = [(seat, selection) for seat, selection in selections.items() if selection is not None]
            seat, selection = active[0]
            prompt = selection['value']['prompt']['id']
            prompts[prompt] += 1
            decisions_by_seat[seat] += 1
            action = choose(selection, rng, undo_ids)
            response = request(seat, action)
            if response != {'ok': True}:
                (lab / 'dlc-failure.json').write_text(json.dumps({'selection': selection, 'action': action, 'response': response}, indent=2))
                raise RuntimeError(f'{prompt}: {response}')
        else:
            raise RuntimeError(f'Insufficient seat coverage after 400 decisions: {dict(decisions_by_seat)}')
        return report
    except Exception as error:
        failure_name = name + ('-replay' if resume_checkpoint else '')
        report['error'] = traceback.format_exc()
        report['decisions'] = sum(decisions_by_seat.values())
        if latest is not None:
            report['actualSetup'] = latest['setup']
            (lab / f'{failure_name}-failed-replies.json').write_text(json.dumps(replies))
        if checkpoint is not None and checkpoint.exists():
            shutil.copy2(checkpoint, lab / f'{failure_name}-failed-checkpoint.json')
        for filename in ('player.log',):
            diagnostic = lab / 'host/results/server' / filename
            if diagnostic.exists():
                shutil.copy2(diagnostic, lab / f'{failure_name}-{filename}')
        shutil.copy2(lab / 'host/game/BepInEx/LogOutput.log', PROJECT / 'results' / f'dlc-{failure_name}-error.log')
        return report
    finally:
        report['prompts'] = dict(prompts)
        if endpoint is not None and game.process.poll() is None:
            try:
                exchange(endpoint['port'], json.dumps({'op': 'shutdown', 'token': endpoint['controlToken']}).encode())
                game.process.wait(timeout=15)
            except (OSError, TimeoutError, subprocess.TimeoutExpired):
                pass
        game.close()
        (lab / 'host/match-setup.json').unlink(missing_ok=True)
        scratch.cleanup()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('cases', nargs='*', help='Case names; omit to test all configurations')
    parser.add_argument('--resume-checkpoint', type=Path, help='Replay one case from a disposable copy of a failed checkpoint')
    args = parser.parse_args()
    if set(args.cases) - {name for name, _ in CASES}:
        parser.error('Unknown DLC case name')
    if args.resume_checkpoint and (len(args.cases) != 1 or not args.resume_checkpoint.is_file()):
        parser.error('--resume-checkpoint requires one case and an existing checkpoint')
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-game-probe'
    reports = []
    summary = PROJECT / 'results/dlc-tests.json'
    previous = json.loads(summary.read_text()) if summary.exists() else []
    by_name = {report['name']: report for report in previous}
    with lock_data(lab):
        shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', lab / 'host/game/BepInEx/plugins/EngineProbe.dll')
        for name, setup in CASES:
            if args.cases and name not in args.cases:
                continue
            print(f'Testing {name}…', flush=True)
            report = run_case(lab, name, setup, args.resume_checkpoint)
            reports.append(report)
            by_name[name] = report
            (PROJECT / 'results' / f'dlc-{name}-test.json').write_text(json.dumps(report, indent=2) + '\n')
            summary.write_text(json.dumps([by_name[name] for name, _ in CASES if name in by_name], indent=2) + '\n')
            print(json.dumps({key: value for key, value in report.items() if key != 'prompts'}), flush=True)
    if any(report['status'] != 'passed' for report in reports):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
