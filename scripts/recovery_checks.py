"""Crash a dedicated lab host and compare all six recovered native snapshots."""
import json
import os
from pathlib import Path
import signal
import subprocess
import time
from test_client_support import exchange, stop


def canonical(value, owner=None):
    # Native loading materializes inherited ownership and reorders attribute
    # dictionaries. Normalize only those representations, preserving card order.
    if isinstance(value, dict):
        if 'entityID' in value:
            value = dict(value)
            value['owningPlayerID'] = value.get('owningPlayerID', owner)
            owner = value['owningPlayerID']
        return {key: canonical(item, owner) for key, item in value.items()}
    if isinstance(value, list):
        result = [canonical(item, owner) for item in value]
        if result and all(isinstance(item, dict) and isinstance(item.get('name'), int) for item in result):
            result.sort(key=lambda item: item['name'])
        return result
    return value


def crash_and_resume(project, process, endpoint, checkpoint, log):
    before = [exchange(endpoint, {'op': 'join', 'token': token}) for token in endpoint['tokens']]
    evidence = checkpoint.parent / 'recovery-before.json'
    evidence.write_text(json.dumps([reply['messages'] for reply in before], indent=2))
    evidence.chmod(0o600)
    assert checkpoint.is_file() and checkpoint.stat().st_mode & 0o777 == 0o600
    assert json.loads(checkpoint.read_text())['Version'] == 2
    pids = []
    for proc in Path('/proc').iterdir():
        if not proc.name.isdigit():
            continue
        try:
            if (proc / 'comm').read_text().strip() != 'Root.exe':
                continue
            env = dict(item.split('=', 1) for item in (proc / 'environ').read_bytes().decode().split('\0') if '=' in item)
            if env.get('WINEPREFIX', '').rstrip('/') == str(project / '.lab/compatdata/pfx'):
                pids.append(int(proc.name))
        except (FileNotFoundError, PermissionError, ProcessLookupError):
            pass
    assert len(pids) == 1, 'Expected exactly one Root process in the test host prefix'
    os.kill(pids[0], signal.SIGKILL)
    stop(process)
    started = time.time()
    resumed = subprocess.Popen(['bash', 'scripts/run-lab.sh', 'server'], cwd=project,
        env={**os.environ, 'ROOT_LAB_SAVE_FILE': str(checkpoint), 'ROOT_LAB_RESUME': '1', 'ROOT_LAB_LIFETIME_SECONDS': '1800'},
        stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        deadline = time.monotonic() + 90
        endpoint_file = project / '.lab/results/server/endpoint.json'
        restored = None
        while time.monotonic() < deadline:
            assert resumed.poll() is None, 'Restored host exited before readiness'
            try:
                if endpoint_file.stat().st_mtime >= started:
                    restored = json.loads(endpoint_file.read_text())
                    break
            except (FileNotFoundError, json.JSONDecodeError):
                pass
            time.sleep(0.2)
        assert restored is not None, 'Restored host did not become ready'
        after_messages = []
        for seat, token in enumerate(restored['tokens']):
            after = exchange(restored, {'op': 'join', 'token': token})
            after_messages.append(after['messages'])
            evidence = checkpoint.parent / 'recovery-after.json'
            evidence.write_text(json.dumps(after_messages, indent=2))
            evidence.chmod(0o600)
            assert after['ok'] and after['gameId'] == before[seat]['gameId']
            assert after['roster'] == before[seat]['roster']
            assert canonical(after['messages']) == canonical(before[seat]['messages']), f'Seat {seat + 1} recovered snapshot or pending decision differs'
            assert exchange(restored, {'op': 'join', 'token': endpoint['tokens'][seat]}) == {'ok': False, 'error': 'Unauthorized'}
        return resumed, restored
    except BaseException:
        stop(resumed)
        raise
