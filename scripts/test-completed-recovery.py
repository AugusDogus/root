#!/usr/bin/env python3
"""Verify a completed checkpoint against snapshots captured before the host crash."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
from gameplay_checks import check_private_snapshots
from recovery_checks import canonical
from test_client_support import exchange, stop

PROJECT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('checkpoint', type=Path)
    parser.add_argument('snapshots', type=Path)
    args = parser.parse_args()
    checkpoint = args.checkpoint.resolve()
    before = json.loads(args.snapshots.read_text())
    assert len(before) == 6
    expected_results = before[0][1]['value']['msg']['value']['results']
    assert any(player['didWin'] for player in expected_results)
    original_hash = hashlib.sha256(checkpoint.read_bytes()).digest()
    output = PROJECT / '.lab/results/completed-recovery'
    output.mkdir(parents=True, exist_ok=True)
    result = {'status': 'running'}
    endpoint = None
    started = time.time()
    with (output / 'launch.log').open('w') as log:
        process = subprocess.Popen(['bash', 'scripts/run-lab.sh', 'server'], cwd=PROJECT,
            env={**os.environ, 'ROOT_LAB_SAVE_FILE': str(checkpoint), 'ROOT_LAB_RESUME': '1'},
            stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            endpoint_file = PROJECT / '.lab/results/server/endpoint.json'
            deadline = time.monotonic() + 90
            while time.monotonic() < deadline:
                assert process.poll() is None, 'Restored host exited before readiness'
                try:
                    if endpoint_file.stat().st_mtime >= started:
                        endpoint = json.loads(endpoint_file.read_text())
                        break
                except (FileNotFoundError, json.JSONDecodeError):
                    pass
                time.sleep(0.2)
            assert endpoint is not None, 'Restored host did not become ready'
            for seat, token in enumerate(endpoint['tokens']):
                after = exchange(endpoint, {'op': 'join', 'token': token})
                assert after['ok'] and after['gameOver'], f'Seat {seat + 1} lacks completed status'
                assert canonical(after['messages']) == canonical(before[seat]), f'Seat {seat + 1} changed during recovery'
                assert after['messages'][1]['value']['msg']['value']['results'] == expected_results
            privacy = check_private_snapshots(endpoint)
            assert hashlib.sha256(checkpoint.read_bytes()).digest() == original_hash, 'Read-only resume must preserve the checkpoint file'
            result = {'status': 'passed', 'matchingSeats': 6, 'checkpointUnchanged': True, 'privacy': privacy,
                      'standings': [{key: row[key] for key in ['faction', 'score', 'didWin']} for row in expected_results]}
            print('PASS: all six finished snapshots, private hands, and standings survive host recovery')
        except Exception as error:
            result = {'status': 'failed', 'error': str(error)}
            raise
        finally:
            if endpoint and process.poll() is None:
                try:
                    exchange(endpoint, {'op': 'shutdown', 'token': endpoint['controlToken']})
                except (OSError, ValueError):
                    pass
            stop(process)
            (output / 'test-results.json').write_text(json.dumps(result, indent=2) + '\n')


if __name__ == '__main__':
    main()
