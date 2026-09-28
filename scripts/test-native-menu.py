#!/usr/bin/env python3
"""Click the native menu on its own muted Xvfb display; never send invitations."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from native_launcher_test import online_session


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
    ai = '--ai' in sys.argv
    completed = Path(sys.argv[sys.argv.index('--completed') + 1]).resolve() if '--completed' in sys.argv else None
    lab = Path(sys.argv[sys.argv.index('--lab') + 1]).resolve() if '--lab' in sys.argv else PROJECT / '.lab/native-session-probe'
    with online_session(lab) as session:
        try:
            probe = lab / 'client/results/online-setup-probe'
            probe.mkdir(parents=True, exist_ok=True)
            for name in ('view.txt', 'visual.json', 'error.txt', 'command.txt', 'lobby-view.txt', 'lobby-command.txt', 'blocked-lobby-write.txt'):
                (probe / name).unlink(missing_ok=True)
            session.perform('play', {})
            def healthy():
                state = session.status()
                if state['error']:
                    raise RuntimeError(state['error'])
                return True
            wait_for(lambda: healthy() and (lab / 'client/native-menu-ready').exists())
            session.assert_launcher_exited()
            client = session.status()['processId']
            env = display_environment(lab)
            def capture(name):
                time.sleep(3)
                subprocess.run(['import', '-window', 'root', str(PROJECT / 'results' / name)], env=env, check=True)
            def click(x, y):
                # Hold across several frames of the software-rendered test game.
                subprocess.run(['xdotool', 'mousemove', str(x), str(y), 'sleep', '0.5',
                                'mousedown', '1', 'sleep', '0.5', 'mouseup', '1'], env=env, check=True)
            def screen(name):
                wait_for(lambda: healthy() and (lab / 'client/native-menu-screen').read_text() == name,
                         seconds=240 if name in ('playing', 'results') else 30)
            capture('native-menu-home.png')
            print('Native menu visible.', flush=True)
            if '--completed-only' in sys.argv:
                assert completed is not None, '--completed-only requires --completed CHECKPOINT'
                shutil.copy2(completed, lab / 'saves/zz_completed-ui-test.json')
                click(180, 610)
                screen('saves')
                click(640, 250)
                screen('results')
                capture('native-menu-results.png')
                click(370, 700)
                screen('playing')
                capture('native-menu-final-board.png')
                result = {'status': 'passed', 'completedMatchScreen': True, 'finalBoardAccessible': True}
                (PROJECT / 'results/native-results-menu-test.json').write_text(json.dumps(result) + '\n')
                print(json.dumps(result), flush=True)
                return
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
            wait_for(lambda: (probe / 'view.txt').exists())
            assert (probe / 'view.txt').read_text().startswith('Prefab slots: 6')
            def setup_visible():
                if (probe / 'error.txt').exists():
                    raise RuntimeError((probe / 'error.txt').read_text())
                try:
                    visual = json.loads((probe / 'visual.json').read_text())
                except (FileNotFoundError, json.JSONDecodeError):
                    return False
                return visual == {'slots': 6, 'visibleFigures': 6, 'title': 'Six Player'}
            wait_for(setup_visible)
            capture('native-menu-setup.png')
            click(160, 765)  # Native Back returns without starting a host.
            screen('home')
            assert session.endpoint is None
            (probe / 'visual.json').unlink(missing_ok=True)
            click(180, 425)
            screen('setup')
            wait_for(setup_visible)
            # Exercise Root's setup model and its Create Game command. The
            # isolated fixture supplies factions without testing DLC purchases.
            values = {'factions': [0, 1, 2, 3, 6, 7], 'controllers': [0] * 6,
                      'options': {'AILevel': 1, 'AdvancedSetup': 0, 'RandomSuits': 1}}
            if ai:
                values['controllers'] = [0, 0, 1, 1, 1, 1]
            if dlc:
                values.update(factions=[14, 11, 2, 3, 6, 7], controllers=[0, 2, 0, 0, 0, 0])
                values['options'].update(ChosenMap=2, DeckChoice=1)
            def command(value):
                (probe / 'command.txt').write_text(value)
                wait_for(lambda: healthy() and not (probe / 'command.txt').exists())
                if (probe / 'error.txt').exists():
                    raise RuntimeError((probe / 'error.txt').read_text())
            command(json.dumps(values))
            command('create')
            wait_for(lambda: healthy() and session.endpoint is not None and len(session.invitations) == 6)
            wait_for(lambda: healthy() and (probe / 'lobby-view.txt').exists())
            from network import exchange
            human_factions = [faction for faction, controller in zip(values['factions'], values['controllers']) if controller == 0]
            faction_names = {0: 'MarquiseDeCat', 1: 'EyrieDynasties', 2: 'WoodlandAlliance', 3: 'Vagabond',
                             6: 'LizardCult', 7: 'RiverfolkCompany', 14: 'LordOfTheHundreds'}
            for seat, faction in enumerate(human_factions[1:], 1):
                endpoint = session.endpoint
                def lobby_request(op, **fields):
                    return json.loads(exchange(endpoint['port'], json.dumps({'op': op, 'token': endpoint['tokens'][seat], **fields}).encode()))
                assert lobby_request('join', name=f'Local friend {seat}')['phase'] == 'lobby'
                assert lobby_request('lobby-join', metadata={'Faction': faction_names[faction]})['ok']
            time.sleep(1)
            (probe / 'lobby-command.txt').write_text('start')
            wait_for(lambda: healthy() and 'Native match relay connected to the private host' in (lab / 'client/game/BepInEx/LogOutput.log').read_text())
            assert session.status()['processId'] == client, 'Hosting must reuse the open game.'
            screen('playing')
            if ai:
                actual = json.loads((lab / 'client/native-board-settings.json').read_text())
                assert actual['setup']['AI'] == [None, None, 1, 1, 1, 1], actual['setup']
                assert sum(bool(invite) for invite in session.invitations) == 1, 'Only the human friend seat should have an invitation'
            if dlc:
                actual = json.loads((lab / 'client/native-board-settings.json').read_text())
                assert actual['map'] == 2, actual
                assert actual['setup']['Factions'] == [14, 2, 3, 6, 7, 11], actual
                assert actual['setup']['Deck'] == 1, actual
                assert session.invitations[5] == '', 'A bot must not receive an invitation.'
            # "playing" is published only after Root's loading curtain closes.
            capture('native-menu-host.png')
            click(1180, 125)
            screen('match')
            capture('native-menu-table.png')
            if ai:
                from network import exchange
                endpoint = session.endpoint
                reply = json.loads(exchange(endpoint['port'], json.dumps({'op': 'join', 'token': endpoint['tokens'][0]}).encode()))
                assert [seat['State'] for seat in reply['lobby'][2:]] == ['AI · Medium'] * 4
                capture('native-menu-ai-table.png')
            click(1020, 650)
            screen('confirm')
            click(850, 590)
            wait_for(lambda: (lab / 'client/native-menu-ready').exists())
            screen('home')
            assert session.status()['processId'] == client, 'Return to menu restarted Root'
            assert session.endpoint is None, 'Return to menu left the old authority running'
            assert session.status()['saves'], 'Return to menu lost the hosted save'
            if completed is not None:
                fixture = lab / 'saves/zz_completed-ui-test.json'
                shutil.copy2(completed, fixture)
                env = display_environment(lab)
                click(180, 610)
                screen('saves')
                click(640, 250)
                screen('results')
                time.sleep(20)
                capture('native-menu-results.png')
            result = {'status': 'passed', 'nativeMenu': True, 'hostFromGame': True,
                      'gameProcessReused': True, 'sixNativePlayers': True, 'steamInvitationsAvailable': 1 if ai else 4 if dlc else 5,
                      'invitationsSent': 0, 'browserOpened': False, 'headless': True,
                      'returnToMenu': True, 'savedMatchPreserved': True}
            result['completedMatchScreen'] = completed is not None
            if dlc or ai:
                result.update(actual)
            (PROJECT / 'results' / ('native-ai-menu-test.json' if ai else 'native-dlc-menu-test.json' if dlc else 'native-menu-test.json')).write_text(json.dumps(result, indent=2) + '\n')
            print(json.dumps(result), flush=True)
        finally:
            (lab / 'saves/zz_completed-ui-test.json').unlink(missing_ok=True)


if __name__ == '__main__':
    main()
