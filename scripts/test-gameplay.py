#!/usr/bin/env python3
"""Play six authenticated seats against the native host, stopping on unsupported actions."""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import random
import subprocess
import time
import tempfile
from recovery_checks import crash_and_resume
from selection_messages import latest_selection
from test_client_support import exchange, stop
from gameplay_driver import choose, choose_undo
from gameplay_checks import check_private_snapshots

PROJECT = Path(__file__).resolve().parents[1]



def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=int, default=12345)
    parser.add_argument('--steps', type=int, default=1500)
    parser.add_argument('--restart-at', type=int, help='Autosave, crash the native host at this decision, and resume')
    parser.add_argument('--resume-checkpoint', type=Path, help='Continue an existing test checkpoint')
    parser.add_argument('--exercise-undo', action='store_true', help='Submit each offered Undo prompt once per seat, then continue to victory')
    args = parser.parse_args()
    output = PROJECT / ('.lab/results/recovery-gameplay' if args.restart_at is not None or args.resume_checkpoint else '.lab/results/gameplay')
    output.mkdir(parents=True, exist_ok=True)
    endpoint_file = PROJECT / '.lab/results/server/endpoint.json'
    endpoint = None
    decisions = Counter()
    prompts = Counter()
    privacy_checks = []
    undone_prompts = set()
    result = {'status': 'failed', 'seed': args.seed}
    (output / 'test-results.json').write_text(json.dumps({'status': 'running', 'seed': args.seed}) + '\n')
    started = time.time()
    checkpoint = None
    extra = {}
    if args.restart_at is not None:
        assert 0 < args.restart_at < args.steps
    if args.resume_checkpoint:
        checkpoint = args.resume_checkpoint.resolve()
        assert checkpoint.is_file()
        extra = {'ROOT_LAB_SAVE_FILE': str(checkpoint), 'ROOT_LAB_RESUME': '1'}
    elif args.restart_at is not None:
        # Reserve a unique name, then let the new host create its private save.
        with tempfile.NamedTemporaryFile(dir=output, prefix='checkpoint-', suffix='.json', delete=False) as file:
            checkpoint = Path(file.name)
        checkpoint.unlink()
        extra = {'ROOT_LAB_SAVE_FILE': str(checkpoint), 'ROOT_LAB_RESUME': '0'}
    with (output / 'launch.log').open('w') as log, (output / 'trace.jsonl').open('w') as trace:
        process = subprocess.Popen(['bash', 'scripts/run-lab.sh', 'server'], cwd=PROJECT,
                                   env={**os.environ, 'ROOT_LAB_TEST_SEED': str(args.seed), 'ROOT_LAB_LIFETIME_SECONDS': '1800', **extra},
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 70
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError('Host exited before readiness')
                try:
                    if endpoint_file.stat().st_mtime >= started:
                        endpoint = json.loads(endpoint_file.read_text())
                        break
                except (FileNotFoundError, json.JSONDecodeError):
                    pass
                time.sleep(0.2)
            if endpoint is None:
                raise TimeoutError('Host did not become ready')
            rng = random.Random(args.seed)
            undo_ids = set()
            cursors = [0] * 6
            selections = [None] * 6
            def join_seats():
                for seat, token in enumerate(endpoint['tokens']):
                    joined = exchange(endpoint, {'op': 'join', 'token': token})
                    assert joined['ok']
                    cursors[seat] = joined['next']
                    selections[seat] = latest_selection(joined)
            join_seats()
            previous_prompt = None
            repeated = 0
            for step in range(args.steps):
                if step == args.restart_at:
                    process, endpoint = crash_and_resume(PROJECT, process, endpoint, checkpoint, log)
                    join_seats()
                    result['recovery'] = {'decision': step, 'sixSnapshotsAndPendingDecisionsMatch': True, 'oldInvitationsRejected': True}
                    print(f'Recovered after killing the native host at decision {step}; all six seat snapshots match', flush=True)
                replies = []
                for seat in range(6):
                    reply = exchange(endpoint, {'op': 'poll', 'token': endpoint['tokens'][seat], 'after': cursors[seat]})
                    assert reply['ok'], reply
                    replies.append(reply)
                    cursors[seat] = reply['next']
                    selections[seat] = latest_selection(reply, selections[seat])
                    if reply.get('gameOver'):
                        assert reply['winner'] is not None
                        result.update(status='passed', winnerSeat=next(i + 1 for i, p in enumerate(reply['roster']) if p['account'] == reply['winner']))
                if result['status'] == 'passed':
                    assert len(replies) == 6 and all(reply['gameOver'] for reply in replies)
                    final_results = []
                    for reply in replies:
                        game_results = next(message['value']['msg']['value'] for message in reply['messages']
                                            if message['name'] == 'SequenceMessage' and message['value']['msg']['name'] == 'GameResults')
                        final_results.append(game_results['results'])
                    assert all(players == final_results[0] for players in final_results), 'All six seats must receive the same final standings'
                    assert len(final_results[0]) == 6
                    assert any(player['didWin'] and (player['score'] >= 30 or player['wasDominanceWin']) for player in final_results[0]), 'Native winner must satisfy a victory condition'
                    log_counts = Counter(attribute['value']['id'] for entry in game_results['actionLog']
                                         for attribute in entry['attributes']
                                         if isinstance(attribute.get('value'), dict) and 'id' in attribute['value'])
                    for expected in ['Log.initiate.battle', 'Log.Roll', 'Log.Casualties']:
                        assert log_counts[expected] > 0, f'Native game log did not demonstrate {expected}'
                    if args.resume_checkpoint is None:
                        assert any(name.startswith('log.buy.service.') and count > 0 for name, count in log_counts.items()), \
                            'Native game log did not demonstrate a completed Riverfolk service purchase'
                    assert all(decisions[seat] > 0 for seat in range(1, 7))
                    if args.exercise_undo:
                        assert undone_prompts, 'Match completed without exercising an offered Undo action'
                    result['nativeLogCounts'] = dict(log_counts)
                    result['standings'] = [{key: player[key] for key in ['faction', 'score', 'didWin', 'wasDominanceWin']} for player in final_results[0]]
                    for token in endpoint['tokens']:
                        joined = exchange(endpoint, {'op': 'join', 'token': token})
                        assert joined['gameOver'] and len(joined['messages']) == 2
                        assert joined['messages'][1]['value']['msg']['name'] == 'GameResults'
                        assert joined['messages'][1]['value']['msg']['value']['results'] == final_results[0], 'A late join must receive the completed match standings'
                    if checkpoint is not None:
                        process, endpoint = crash_and_resume(PROJECT, process, endpoint, checkpoint, log)
                        assert all(exchange(endpoint, {'op': 'join', 'token': token})['gameOver'] for token in endpoint['tokens'])
                        result.setdefault('recovery', {})['finishedMatchRestored'] = True
                    break
                if step % 100 == 0:
                    privacy_checks.append({'step': step, **check_private_snapshots(endpoint)})
                active = [(seat, selection) for seat, selection in enumerate(selections) if selection is not None]
                if not active:
                    (output / 'terminal-replies.json').write_text(json.dumps(replies, indent=2))
                assert active, 'No pending selection before game over'
                seat, selection = active[0]
                prompt = (seat, selection['value']['prompt']['id'])
                repeated = repeated + 1 if prompt == previous_prompt else 1
                previous_prompt = prompt
                assert repeated < 20, f'Bot made no progress past {prompt} for 20 decisions'
                trace.write(json.dumps({'step': step, 'seat': seat + 1, 'selection': selection}) + '\n')
                trace.flush()
                request = choose_undo(selection) if args.exercise_undo and prompt not in undone_prompts else None
                if request is not None:
                    undone_prompts.add(prompt)
                else:
                    request = choose(selection, rng, undo_ids)
                reply = exchange(endpoint, {**request, 'token': endpoint['tokens'][seat]})
                trace.write(json.dumps({'request': request, 'response': reply}) + '\n')
                trace.flush()
                assert reply == {'ok': True}, (seat + 1, selection['value']['prompt']['id'], request, reply)
                decisions[seat + 1] += 1
                prompts[selection['value']['prompt']['id']] += 1
            assert result['status'] == 'passed', f'No victory after {args.steps} decisions'
        except Exception as error:
            result.update(status='failed', error=str(error))
            raise
        finally:
            result.update(decisions=dict(decisions), prompts=dict(prompts), privacyChecks=privacy_checks)
            result['undoChecks'] = [{'seat': seat + 1, 'prompt': prompt} for seat, prompt in sorted(undone_prompts)]
            (output / 'test-results.json').write_text(json.dumps(result, indent=2) + '\n')
            if endpoint is not None and process.poll() is None:
                try:
                    exchange(endpoint, {'op': 'shutdown', 'token': endpoint['controlToken']})
                except (OSError, ValueError):
                    pass
            stop(process)
    print(f'PASS: six-player match reaches victory after {sum(decisions.values())} decisions')


if __name__ == '__main__':
    main()
