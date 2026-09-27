#!/usr/bin/env python3
"""Native online setup, private waiting room and board, on an owned Xvfb."""
import importlib.util
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from network import exchange
from test_diagnostics import Diagnostics, atomic_json
from native_launcher_test import online_session

spec = importlib.util.spec_from_file_location('menu_test', PROJECT / 'scripts/test-native-menu.py')
menu_test = importlib.util.module_from_spec(spec)
spec.loader.exec_module(menu_test)


def main():
    os.umask(0o077)
    diagnostics = Diagnostics.from_environment()
    dlc = '--dlc' in sys.argv
    factions = [0, 2, 11, 3, 6, 7] if dlc else [0, 1, 2, 3, 6, 7]
    controllers = [0, 0, 2, 1, 1, 1] if dlc else [0, 0, 1, 1, 1, 1]
    expected_ai = [None, None, None, 1, 1, 1] if dlc else [None, None, 1, 1, 1, 1]
    guest_faction = 'WoodlandAlliance' if dlc else 'EyrieDynasties' 
    lab = Path(sys.argv[sys.argv.index('--lab') + 1]).resolve() if '--lab' in sys.argv else PROJECT / '.lab/native-session-probe'
    output = lab / 'client/results/online-setup-probe'
    output.mkdir(parents=True, exist_ok=True)
    for name in ('error.txt', 'view.txt', 'command.txt', 'blocked-lobby-write.txt', 'lobby-view.txt', 'lobby-command.txt', 'settings-view.txt', 'second-controller.txt', 'lobby-state.json', 'visual.json', 'coop-ready', 'fixture-ownership-active', 'communication-state.json', 'communication-command.json'):
        (output / name).unlink(missing_ok=True)
    (output / "owned-content.fixture").write_text("Base, Clockwork, Riverfolk, Underworld, Exiles & Partisans")
    with online_session(lab) as session:
        try:
            session.perform('play', {})
            def healthy():
                for name in ('error.txt', 'blocked-lobby-write.txt'):
                    if (output / name).exists():
                        raise RuntimeError((output / name).read_text())
                if session.status()['error']:
                    raise RuntimeError(session.status()['error'] + ' Exit codes: ' + repr({role: process.process.poll() for role, process in [('host', session.host), ('client', session.client)] if process is not None}))
                return True
            def wait(predicate, seconds=240, name='native state'):
                return diagnostics.wait(name, lambda: healthy() and predicate(), timeout=seconds)
            wait(lambda: (lab / 'client/native-menu-ready').exists(), name='load native menu')
            session.assert_launcher_exited()
            session.assert_running_game_is_protected()
            assert (output / 'fixture-ownership-active').exists(), 'The test ownership fixture was not installed'
            env = menu_test.display_environment(lab)
            def click(x, y):
                subprocess.run(['xdotool', 'mousemove', str(x), str(y), 'sleep', '.5', 'mousedown', '1', 'sleep', '.5', 'mouseup', '1'], env=env, check=True)
            def click_client(x, y):
                # Unity reports coordinates inside its client area. Wine can
                # place that window below the Xvfb desktop's top edge.
                windows = subprocess.check_output(['xdotool', 'search', '--onlyvisible', '--name', '^Root$'], env=env, text=True).split()
                assert len(windows) == 1, 'Expected one visible Root window on the isolated display'
                geometry = subprocess.check_output(['xdotool', 'getwindowgeometry', '--shell', windows[0]], env=env, text=True)
                fields = dict(line.split('=', 1) for line in geometry.splitlines() if '=' in line)
                screen = native_state('communication-state.json')['screen']
                assert int(fields['WIDTH']) == screen['width'] and int(fields['HEIGHT']) == screen['height'], geometry
                click(round(x * screen['width'] / 1280 + int(fields['X'])),
                      round(y * screen['height'] / 800 + int(fields['Y'])))
            def capture(name):
                subprocess.run(['import', '-window', 'root', str(PROJECT / 'results' / name)], env=env, check=True, timeout=15)
                if diagnostics.directory is not None:
                    shutil.copy2(PROJECT / 'results' / name, diagnostics.directory / name)
                    state = output / 'communication-state.json'
                    if state.exists():
                        shutil.copy2(state, diagnostics.directory / (Path(name).stem + '-state.json'))
            def native_state(name):
                try:
                    return json.loads((output / name).read_text())
                except (FileNotFoundError, json.JSONDecodeError):
                    return {}
            def command(value):
                (output / 'command.txt').write_text(value)
                wait(lambda: not (output / 'command.txt').exists(), 30)
            click(180, 425)
            wait(lambda: (output / 'view.txt').exists(), name='open six-seat setup')
            command(json.dumps({'factions': factions, 'controllers': controllers,
                                'options': {'AdvancedSetup': 0, 'EnableBluff': 1, 'AILevel': 1, 'PreferLiveGame': 1, 'ChosenMap': 2 if dlc else 0, 'DeckChoice': 1 if dlc else 0}}))
            command('cycle-second-seat')
            assert (output / 'second-controller.txt').read_text() == 'CPU', 'Second seat must support native AI selection'
            command(json.dumps({'controllers': controllers, 'factions': factions}))
            wait(lambda: native_state('visual.json').get('visibleFigures') == 6)
            capture('online-setup-six.png')
            command('settings')
            wait(lambda: (output / 'settings-view.txt').exists())
            time.sleep(2)
            capture('online-private-settings.png')
            command('confirm-settings')
            time.sleep(1)
            click(1090, 765)  # Use the visible native Create Game button.
            if dlc:
                wait(lambda: (output / 'coop-ready').exists(), 30)
                click(755, 615)  # Confirm Root's Clockwork co-op choice.
            print('Native setup created. Waiting for private authority.', flush=True)
            wait(lambda: session.endpoint is not None and (output / 'lobby-view.txt').exists(), name='create private waiting room')
            assert (output / 'lobby-view.txt').read_text().startswith('Slots: 6')
            endpoint = session.endpoint
            def request(seat, op, **fields):
                return json.loads(exchange(endpoint['port'], json.dumps({'op': op, 'token': endpoint['tokens'][seat], **fields}).encode()))
            host = request(0, 'poll', after=0)
            assert host['phase'] == 'lobby' and not host['canStart']
            assert 'messages' not in host, 'A waiting lobby must not start the match'
            assert host['setup']['EnableBluff'] is True
            assert host['setup']['AI'] == expected_ai
            assert request(0, 'lobby-start')['error'] == 'PlayersMissing'
            guest = request(1, 'join', name='Local friend fixture')
            assert guest['phase'] == 'lobby' and guest['account'] != host['account']
            assert request(1, 'lobby-start')['error'] == 'HostOnly'
            assert request(1, 'lobby-metadata', metadata={'Faction': 'MarquiseDeCat'})['error'] == 'FactionUnavailable'
            changed = request(1, 'lobby-join', metadata={'Faction': guest_faction})
            if not changed['ok']:
                raise AssertionError(changed)
            assert request(2, 'join')['error'] == 'BotSeat'
            def communication(op, **fields):
                previous = (native_state('communication-state.json').get('command') or {}).get('sequence', 0)
                atomic_json(output / 'communication-command.json', {'op': op, **fields})
                state = wait(lambda: (native_state('communication-state.json').get('command') or {}).get('sequence', 0) > previous,
                             30, name=op)
                result = native_state('communication-state.json')['command']
                assert result['ok'], result
            def inside(bounds, clip):
                return (bounds is not None and clip is not None and bounds['width'] > 0 and bounds['height'] > 0
                        and bounds['x'] >= clip['x'] - .5 and bounds['y'] >= clip['y'] - .5
                        and bounds['x'] + bounds['width'] <= clip['x'] + clip['width'] + .5
                        and bounds['y'] + bounds['height'] <= clip['y'] + clip['height'] + .5)
            def rendered(text):
                return [message for chat in native_state('communication-state.json').get('chats', []) if chat['active']
                        for message in chat['rendered'] if message['text'] == text and message['textRichText'] is False
                        and message['textActive'] and not message['textCulled'] and message['textAlpha'] > .01
                        and message['meshCharacters'] > 0 and message['textBounds']['width'] > 0
                        and message['textBounds']['height'] > 0
                        and inside(message['textGlyphBounds'], chat['viewportBounds'])
                        and 0 <= message['textBounds']['x'] <= 1280 - message['textBounds']['width']
                        and 0 <= message['textBounds']['y'] <= 800 - message['textBounds']['height']]
            def check_chat(location):
                communication('chat-expand')
                incoming = f'<b>Friend {location}</b>'
                assert request(1, 'chat', text=incoming)['ok']
                wait(lambda: rendered(incoming), 30, name=f'{location} incoming native chat')
                assert all(not item['socialButtonEnabled'] and item['nameActive'] for item in rendered(incoming))
                outgoing = f'Host {location}'
                communication('chat-input', text=outgoing)
                def send_button():
                    for chat in native_state('communication-state.json').get('chats', []):
                        button = chat.get('sendButton')
                        if chat['active'] and button and button['active'] and button['enabled'] and chat['input'] == outgoing:
                            return button['center']
                    return None
                center = wait(send_button, 30, name=f'{location} chat send control')
                click_client(center['x'], center['y'])
                wait(lambda: any(item['Text'] == outgoing and item['Seat'] == 1
                                 for item in request(1, 'poll', after=0)['chat']['Messages']), 30, name=f'{location} outgoing native chat')
                wait(lambda: rendered(outgoing), 30, name=f'{location} chat delivery')
                frame = native_state('communication-state.json')['renderedFrame']
                wait(lambda: native_state('communication-state.json')['renderedFrame'] >= frame + 2,
                     30, name=f'{location} chat rendered frames')
                capture(f'online-{location}-chat.png')
                communication('chat-collapse')
            check_chat('lobby')

            def lobby_command(value):
                (output / 'lobby-command.txt').write_text(value)
                wait(lambda: not (output / 'lobby-command.txt').exists(), 30)
            lobby_command('leave')
            wait(lambda: request(0, 'poll', after=0)['setup']['Factions'][0] == 4, 30)
            def can_rejoin():
                state = native_state('lobby-state.json')
                return (state.get('local') == [{'filled': False, 'faction': 'FactionData: MarquiseDeCat'}]
                        and state.get('joinVisible') and state.get('joinEnabled') and not state.get('startVisible'))
            wait(can_rejoin, 30)
            click(1090, 765)  # Join through the native button after leaving.
            wait(lambda: request(1, 'poll', after=0)['ok'] and request(0, 'poll', after=0)['canStart'], 30)
            time.sleep(3)
            capture('online-private-lobby.png')
            print('Six-slot native waiting room visible. Starting through native button.', flush=True)
            click(1090, 765)  # Use the visible native Start Game button.
            wait(lambda: (lab / 'client/native-menu-screen').read_text() == 'playing', name='load native board')
            time.sleep(3)
            capture('online-private-board.png')
            check_chat('board')
            def running_timer():
                for manager in native_state('communication-state.json').get('timerManagers', []):
                    for timer in manager['timers']:
                        if not timer['error'] and not timer['ended'] and 0 < timer['secondsRemaining'] <= 305:
                            return timer
                return None
            first_timer = wait(running_timer, 30, name='native countdown data')
            def countdown_advanced():
                timer = running_timer()
                if (timer and timer['account'] == first_timer['account'] and timer['timerId'] == first_timer['timerId']
                        and timer['secondsRemaining'] < first_timer['secondsRemaining'] - .5):
                    return timer
                return None
            wait(countdown_advanced, 10, name='native countdown data advances')
            def visible_timer():
                state = native_state('communication-state.json')
                for widget in state.get('timerWidgets', []):
                    bounds = widget['imageBounds']
                    if (not widget['error'] and widget['timerId'] == first_timer['timerId']
                            and widget['active'] and widget['running'] and widget['imageActive'] and widget['imageEnabled']
                            and widget['imageCulled'] is False and widget['imageAlpha'] > .01
                            and 0 < widget['fillAmount'] <= 1 and bounds and bounds['width'] > 2 and bounds['height'] > 2
                            and 0 <= bounds['x'] <= 1280 - bounds['width']
                            and 0 <= bounds['y'] <= 800 - bounds['height']):
                        return {**widget, 'renderedFrame': state['renderedFrame']}
                return None
            # Root uses an animated clock image and can hide it until late in
            # the native turn bank. Let its normal visibility rule run.
            if '--lifecycle' not in sys.argv:
                first_widget = wait(visible_timer, first_timer['secondsRemaining'] + 10, name='native clock image visible')
                def clock_image_advanced():
                    widget = visible_timer()
                    if (widget and widget['renderedFrame'] > first_widget['renderedFrame']
                            and abs(widget['fillAmount'] - first_widget['fillAmount']) > .001):
                        return widget
                    return None
                wait(clock_image_advanced, 10, name='native clock image advances')
                capture('online-native-clock.png')
            final = request(1, 'join')
            assert len(final['roster']) == 6 and len(final['messages']) > 0
            assert final['setup']['EnableBluff'] is True
            assert final['initialization']['EnableBluff'] is True
            assert final['setup']['Factions'] == factions, final['setup']
            assert final['setup']['Map'] == (2 if dlc else 0), final['setup']
            assert final['setup']['Deck'] == (1 if dlc else 0), final['setup']
            board = json.loads((lab / 'client/native-board-settings.json').read_text())
            assert board['map'] == (2 if dlc else 0), board
            assert board['setup']['Deck'] == (1 if dlc else 0), board
            player_log = lab / 'client/compatdata/pfx/drive_c/users/steamuser/AppData/LocalLow/Dire Wolf Digital/Root/output_log_fresh.txt'
            assert 'Initialize lobbyURL' not in player_log.read_text(), 'Private room opened an official lobby socket'
            assert final['setup']['AI'] == expected_ai
            assert final['roster'][0]['account'] == host['account']
            assert final['roster'][1]['account'] == guest['account']
            assert session.status()['saves'], 'Starting the lobby must save the match'
            original_process = session.status()['processId']
            original_save = session.status()['save']
            click_client(500, 32)
            wait(lambda: (lab / 'client/native-menu-interactive').read_text() == 'match', seconds=30, name='open match controls')
            click_client(1000, 625)
            wait(lambda: (lab / 'client/native-menu-interactive').read_text() == 'confirm', seconds=30, name='confirm return to menu')
            click_client(850, 550)
            wait(lambda: (lab / 'client/native-menu-interactive').read_text() == 'home' and session.status()['phase'] == 'menu', name='return without launcher')
            assert session.status()['processId'] == original_process, 'Return to menu restarted Root'
            assert not (lab / 'host/results/server/endpoint.json').exists(), 'Previous host is still alive'
            assert (lab / 'saves' / original_save).is_file(), 'Return to menu lost the saved match'
            capture('native-mod-owned-home.png')
            click_client(180, 580)
            wait(lambda: (lab / 'client/native-menu-interactive').read_text() == 'saves', seconds=30, name='open saved matches')
            click_client(620, 220)
            wait(lambda: session.status()['phase'] == 'hosting', name='resume without launcher')
            wait(lambda: (lab / 'client/native-menu-screen').read_text() == 'playing', name='resume native board')
            assert session.status()['processId'] == original_process, 'Resume restarted Root'
            assert session.status()['save'] == original_save, 'Resumed the wrong saved match'
            session.assert_launcher_exited()
            capture('native-mod-owned-resumed.png')
            result = {'status': 'passed', 'launcherExited': True, 'modOwnedHostAndResume': True, 'map': final['setup']['Map'], 'deck': final['setup']['Deck'], 'nativeOnlineSetup': True, 'nativeWaitingRoom': True, 'sixSeats': True,
                      'privateAuthority': True, 'simulatedFriendJoined': True, 'hostOnlyStart': True,
                      'requiresPlayers': True, 'ordinaryAI': 3 if dlc else 4, 'clockwork': 1 if dlc else 0, 'bluffPreserved': True, 'nativeChatLobbyAndBoard': True, 'nativeTimerCountdown': True, 'nativeTimerImage': '--lifecycle' not in sys.argv, 'nativeRejoin': True, 'secondSeatAIControl': True,
                      'boardOpened': True, 'officialLobbyWrites': 0, 'officialLobbySockets': 0, 'invitationsSent': 0, 'contentOwnership': 'fixture', 'mouseCreateAndStart': True}
            (PROJECT / ('results/online-lobby-dlc-test.json' if dlc else 'results/online-lobby-test.json')).write_text(json.dumps(result, indent=2) + '\n')
            print(json.dumps(result), flush=True)
        except Exception:
            try:
                capture('online-lobby-failure.png')
            except (OSError, subprocess.SubprocessError, UnboundLocalError):
                pass
            raise
        finally:
            (output / "owned-content.fixture").unlink(missing_ok=True)



if __name__ == '__main__':
    main()
