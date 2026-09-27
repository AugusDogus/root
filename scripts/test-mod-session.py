#!/usr/bin/env python3
"""Exercise mod-owned resume, return, and quit with the launcher already exited."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys

from native_launcher_test import PROJECT, online_session
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import require_test_budget
from test_diagnostics import Diagnostics

spec = importlib.util.spec_from_file_location('menu_test', PROJECT / 'scripts/test-native-menu.py')
menu_test = importlib.util.module_from_spec(spec)
spec.loader.exec_module(menu_test)


def main():
    require_test_budget()
    os.umask(0o077)
    diagnostics = Diagnostics.from_environment()
    lab = PROJECT / '.lab/native-session-probe'
    saves = sorted((lab / 'saves').glob('*.json'), reverse=True)
    assert saves, 'Run the native online lobby test to create a saved-match fixture first'
    expected_save = saves[0].name
    with online_session(lab) as session:
        session.perform('play', {})

        def wait(name, predicate, seconds=240):
            def check():
                error = session.status()['error']
                if error:
                    raise RuntimeError(error)
                return predicate()
            return diagnostics.wait(name, check, timeout=seconds)

        def screen(name):
            path = lab / 'client/native-menu-interactive'
            return path.exists() and path.read_text() == name

        wait('interactive native home', lambda: screen('home'))
        session.assert_launcher_exited()
        session.assert_running_game_is_protected()
        original_process = session.status()['processId']
        env = menu_test.display_environment(lab)

        def click(x, y):
            windows = subprocess.check_output(['xdotool', 'search', '--onlyvisible', '--name', '^Root$'], env=env, text=True).split()
            assert len(windows) == 1
            geometry = subprocess.check_output(['xdotool', 'getwindowgeometry', '--shell', windows[0]], env=env, text=True)
            fields = dict(line.split('=', 1) for line in geometry.splitlines() if '=' in line)
            target_x = round(x * int(fields['WIDTH']) / 1280 + int(fields['X']))
            target_y = round(y * int(fields['HEIGHT']) / 800 + int(fields['Y']))
            subprocess.run(['xdotool', 'mousemove', str(target_x), str(target_y), 'sleep', '.5',
                            'mousedown', '1', 'sleep', '1', 'mouseup', '1'], env=env, check=True)

        for attempt in (1, 2):
            click(180, 580)
            wait(f'open saved matches {attempt}', lambda: screen('saves'))
            click(620, 220)
            wait(f'mod resumes host {attempt}', lambda: session.status()['phase'] == 'hosting')
            wait(f'interactive resumed board {attempt}', lambda: screen('playing'))
            assert session.status()['processId'] == original_process
            assert session.status()['save'] == expected_save
            session.assert_launcher_exited()
            subprocess.run(['import', '-window', 'root', str(diagnostics.directory / f'resumed-{attempt}.png')], env=env, check=True)
            if attempt == 1:
                click(500, 32)
                wait('interactive match controls', lambda: screen('match'))
                click(1000, 625)
                wait('interactive return confirmation', lambda: screen('confirm'))
                click(850, 550)
                wait('interactive home in same process', lambda: screen('home'))
                assert session.status()['phase'] == 'menu'
                assert session.status()['processId'] == original_process
                assert session.endpoint is None, 'Returning left an orphaned authority'
                assert (lab / 'saves' / expected_save).is_file()
        session.stop()
        assert session.status()['phase'] == 'stopped'
        assert session.endpoint is None
    result = {'status': 'passed', 'launcherExited': True, 'controlAPIAbsent': True,
              'runningGameProtected': True, 'modStartsAndResumesHost': True, 'returnKeepsGameProcess': True,
              'repeatResume': True, 'quitStopsAuthority': True, 'savePreserved': True,
              'nativeWindowsGameplayTested': False}
    (PROJECT / 'results/mod-session-test.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    main()
