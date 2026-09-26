#!/usr/bin/env python3
"""Click the native menu on its own muted Xvfb display; never send invitations."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import threading
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data, make_server
from session import Session


def wait_for(predicate, seconds=240):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.5)
    raise TimeoutError('Native menu did not reach the expected state.')


def display_environment(lab):
    for entry in Path('/proc').iterdir():
        if not entry.name.isdigit():
            continue
        try:
            if b'Root.exe' not in (entry / 'cmdline').read_bytes():
                continue
            env = dict(field.split('=', 1) for field in (entry / 'environ').read_text().split('\0') if '=' in field)
            if env.get('WINEPREFIX') == str(lab / 'client/compatdata/pfx') and env.get('DISPLAY') and env.get('XAUTHORITY'):
                return {**os.environ, 'DISPLAY': env['DISPLAY'], 'XAUTHORITY': env['XAUTHORITY']}
        except (OSError, UnicodeError):
            continue
    raise RuntimeError('The test game has no dedicated Xvfb display.')


def main():
    os.umask(0o077)
    dlc = '--dlc' in sys.argv
    lab = PROJECT / ('.lab/friends-launcher-test' if dlc else '.lab/steam-game-probe')
    payload = PROJECT / 'launcher/payload'
    plugin = PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll'
    # Update only the explicitly owned test copies and their matching manifest.
    shutil.copy2(plugin, payload / plugin.name)
    manifest = {'build': '22238765', 'version': '0.1.0', 'plugin_sha256': hashlib.sha256(plugin.read_bytes()).hexdigest()}
    (payload / 'manifest.json').write_text(json.dumps(manifest))
    with lock_data(lab):
        for role in ('host', 'client'):
            shutil.copy2(plugin, lab / role / 'game/BepInEx/plugins/EngineProbe.dll')
            (lab / role / 'bindings-ready').touch()
        (lab / 'prepared.json').write_text(json.dumps(manifest))
        session = Session(lab, payload, headless=True)
        import secrets
        token = secrets.token_urlsafe(32)
        server = make_server(session, token)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        control = lab / 'client/launcher-control.json'
        control.write_text(json.dumps({'port': server.server_port, 'token': token}))
        try:
            session.perform('play', {})
            client = session.client
            def healthy():
                state = session.status()
                if state['error']:
                    raise RuntimeError(state['error'])
                return True
            wait_for(lambda: healthy() and (lab / 'client/native-menu-ready').exists())
            env = display_environment(lab)
            def capture(name):
                time.sleep(3)
                subprocess.run(['import', '-window', 'root', str(PROJECT / 'results' / name)], env=env, check=True)
            def click(x, y):
                # Hold across several frames of the software-rendered test game.
                subprocess.run(['xdotool', 'mousemove', str(x), str(y), 'sleep', '0.5',
                                'mousedown', '1', 'sleep', '0.5', 'mouseup', '1'], env=env, check=True)
            def screen(name):
                wait_for(lambda: healthy() and (lab / 'client/native-menu-screen').read_text() == name)
            capture('native-menu-home.png')
            print('Native menu visible.', flush=True)
            click(180, 520)
            screen('join')
            capture('native-menu-join.png')
            click(640, 550)
            screen('home')
            click(180, 610)
            screen('saves')
            capture('native-menu-saves.png')
            click(640, 720)
            screen('home')
            click(180, 425)
            screen('setup')
            capture('native-menu-setup.png')
            if dlc:
                def setup_click(x, y):
                    click(x, y)
                    time.sleep(3)
                setup_click(300, 245)  # Host faction.
                setup_click(920, 500)  # Lord of the Hundreds.
                screen('setup')
                setup_click(300, 315)  # Seat 2.
                setup_click(930, 735)  # Clockwork page.
                setup_click(920, 245)  # Electric Eyrie.
                screen('setup')
                setup_click(900, 245)  # Map.
                setup_click(300, 310)  # Lake.
                screen('setup')
                setup_click(900, 310)  # Exiles and Partisans.
                setup_click(900, 380)  # Vagabond characters.
                setup_click(300, 245)  # Vagabond.
                setup_click(920, 500)  # Adventurer.
                setup_click(280, 735)  # Back to setup.
                screen('setup')
                setup_click(900, 450)  # Landmarks.
                setup_click(300, 375)  # Black Market.
                setup_click(920, 375)  # Legendary Forge.
                setup_click(280, 735)
                setup_click(900, 520)  # Hirelings.
                setup_click(300, 245)  # Forest Patrol.
                setup_click(920, 245)  # Popular Band.
                setup_click(300, 310)  # Vault Keepers.
                setup_click(280, 735)
                screen('setup')
                capture('native-menu-dlc-setup.png')
            click(920, 735)
            wait_for(lambda: healthy() and session.host is not None and len(session.invitations) == 6)
            wait_for(lambda: healthy() and 'Native match relay connected to the private host' in (lab / 'client/game/BepInEx/LogOutput.log').read_text())
            assert session.client is client, 'Hosting must reuse the open game.'
            screen('playing')
            if dlc:
                actual = json.loads((lab / 'client/native-board-settings.json').read_text())
                assert actual['map'] == 2, actual
                assert actual['setup']['Factions'] == [14, 11, 2, 3, 6, 7], actual
                assert actual['setup']['Characters'][3] == 9, actual
                assert actual['setup']['Deck'] == 1, actual
                assert actual['setup']['Landmarks'] == [4, 5], actual
                assert set(actual['setup']['Hirelings']) == {16, 20, 21}, actual
                assert session.invitations[1] == '', 'A bot must not receive an invitation.'
            # The native loading curtain fades after the entities are ready.
            time.sleep(30)
            capture('native-menu-host.png')
            click(310, 55)
            screen('seats')
            capture('native-menu-invite.png')
            click(640, 320 if dlc else 250)
            screen('friends')
            result = {'status': 'passed', 'nativeMenu': True, 'hostFromGame': True,
                      'gameProcessReused': True, 'sixNativePlayers': True, 'friendPickerOpened': True, 'steamInvitationsAvailable': 4 if dlc else 5,
                      'invitationsSent': 0, 'browserOpened': False, 'headless': True}
            if dlc:
                result.update(actual)
            (PROJECT / 'results' / ('native-dlc-menu-test.json' if dlc else 'native-menu-test.json')).write_text(json.dumps(result, indent=2) + '\n')
            print(json.dumps(result), flush=True)
        finally:
            if session.worker is not None:
                session.worker.join(timeout=660)
            session.stop()
            control.unlink(missing_ok=True)
            server.shutdown()
            server.server_close()
            thread.join()


if __name__ == '__main__':
    main()
