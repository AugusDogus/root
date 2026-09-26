#!/usr/bin/env python3
"""Exercise the real Steam Host launcher flow in existing isolated test copies."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from session import Session
from steam_invite import validate_invite


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-game-probe'
    payload = PROJECT / 'launcher/payload'
    with lock_data(lab):
        manifest = json.loads((payload / 'manifest.json').read_text())
        for role in ('host', 'client'):
            game = lab / role / 'game'
            if not (game / 'BepInEx/interop/tuber-canis.dll').exists():
                raise RuntimeError('Run test-steamworks-game.py first to create the isolated test copies.')
            plugin = game / 'BepInEx/plugins/EngineProbe.dll'
            shutil.copy2(payload / 'EngineProbe.dll', plugin)
            assert hashlib.sha256(plugin.read_bytes()).hexdigest() == manifest['plugin_sha256']
            (lab / role / 'bindings-ready').touch()
        (lab / 'prepared.json').write_text(json.dumps(manifest))
        session = Session(lab, payload, headless=True)
        try:
            session.perform('host', {'transport': 'steam'})
            deadline = time.monotonic() + 180
            while time.monotonic() < deadline:
                state = session.status()
                if state['error']:
                    raise RuntimeError(state['error'])
                if len(state['invitations']) == 6:
                    for invitation in state['invitations'][1:]:
                        validate_invite(invitation)
                    log = (lab / 'client/game/BepInEx/LogOutput.log').read_text()
                    if 'Native match relay connected to the private host' in log:
                        break
                time.sleep(0.5)
            else:
                raise TimeoutError('Steam launcher host did not publish invitations and connect its native board.')
            assert session.host_relay is None and session.client_relay is None
        finally:
            session.stop()
        assert not any((lab / 'client' / name).exists() for name in ('connection.json', 'steam-config.json', 'steam-status.json'))
        result = {'status': 'passed', 'steamHostLauncher': True, 'privateInvitations': 5,
                  'nativeBoardConnected': True, 'cloudflareUsed': False, 'credentialCleanup': True,
                  'headless': True, 'invitationsSent': 0, 'remotePeerTested': False}
        (PROJECT / 'results/steam-launcher-test.json').write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(result))


if __name__ == '__main__':
    main()
