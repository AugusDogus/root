#!/usr/bin/env python3
"""Check code-audited six-player UI paths in one muted isolated game."""
import argparse
from contextlib import contextmanager
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import shutil
import tempfile
import time

from native_launcher_test import PROJECT, online_session
from test_diagnostics import Diagnostics, atomic_json
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import require_test_budget

spec = importlib.util.spec_from_file_location('menu_test', PROJECT / 'scripts/test-native-menu.py')
menu_test = importlib.util.module_from_spec(spec)
spec.loader.exec_module(menu_test)


@contextmanager
def fixture_save(lab, source, untimed=False):
    if source is None:
        yield
        return
    target = None
    try:
        with source.open('rb') as checkpoint, tempfile.NamedTemporaryFile(prefix='zz_ui_audit_', suffix='.json', dir=lab / 'saves', delete=False) as output:
            target = Path(output.name)
            shutil.copyfileobj(checkpoint, output)
        if untimed:
            # A historical reproduction must not time out while its board loads.
            document = json.loads(target.read_text())
            document['Timers'] = []
            document['Initialization']['options']['Timers'] = '0'
            target.write_text(json.dumps(document))
        yield
    finally:
        if target is not None:
            target.unlink(missing_ok=True)


def main():
    require_test_budget()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--targets-only", action="store_true", help="Only check player-target dialogs and rendering")
    parser.add_argument("--full-hd", action="store_true", help="Check 1920x1080 instead of repeating the smaller resolutions")
    parser.add_argument('--fixture', type=Path, help='Resume a copied graphical-test checkpoint; preserve the source')
    parser.add_argument('--trading', action='store_true', help='Check the native Riverfolk price controls without submitting moves')
    parser.add_argument('--explore-item', action='store_true', help='Resume an Explore checkpoint and take an item through the native prompt')
    args = parser.parse_args()
    if args.explore_item and args.fixture is None:
        parser.error('--explore-item requires --fixture so the test plays a disposable copy')
    os.umask(0o077)
    diagnostics = Diagnostics.from_environment()
    lab = PROJECT / '.lab/native-session-probe'
    output = lab / 'client/results/online-setup-probe'
    output.mkdir(parents=True, exist_ok=True)
    for name in ('ui-audit-command.json', 'ui-audit-state.json', 'owned-content.fixture', 'fixture-ownership-active', 'view.txt', 'command.txt', 'error.txt', 'blocked-online-flow.txt'):
        (output / name).unlink(missing_ok=True)
    evidence = []
    with fixture_save(lab, args.fixture, untimed=args.explore_item), online_session(lab) as session:
        launched_at = time.time()
        session.perform('play', {})

        def state():
            path = output / 'ui-audit-state.json'
            if not path.exists():
                return {}
            return json.loads(path.read_text())

        def wait(name, predicate, seconds=240):
            def check():
                problem = session.status()['error'] or state().get('error')
                log = lab / 'client/game/BepInEx/LogOutput.log'
                if log.exists() and log.stat().st_mtime >= launched_at and '[Error  :Il2CppInterop] Exception in IL2CPP-to-Managed trampoline' in log.read_text():
                    raise RuntimeError('A mod callback failed. Inspect the isolated client BepInEx log before retrying.')
                probe_error = output / 'error.txt'
                if probe_error.exists():
                    problem = probe_error.read_text()
                if (output / 'blocked-online-flow.txt').exists():
                    problem = (output / 'blocked-online-flow.txt').read_text()
                player_log = lab / 'client/results/steam-online-test/player.log'
                if player_log.exists() and player_log.stat().st_mtime >= launched_at:
                    text = player_log.read_text()
                    if 'Prefab lookup failed for prompt' in text or 'at Lib.src.match.prompt.behaviours.RiverfolkSetPricesPromptBehaviour.initialize' in text:
                        problem = 'A native UI fixture failed to initialize. Inspect the isolated player.log.'
                if problem:
                    raise RuntimeError(problem)
                return predicate()
            return diagnostics.wait(name, check, timeout=seconds)

        def screen(name):
            path = lab / 'client/native-menu-interactive'
            return path.exists() and path.read_text() == name

        wait('native home', lambda: screen('home'))
        env = menu_test.display_environment(lab)

        def click(x, y):
            windows = subprocess.check_output(['xdotool', 'search', '--onlyvisible', '--name', '^Root$'], env=env, text=True).split()
            assert len(windows) == 1
            geometry = subprocess.check_output(['xdotool', 'getwindowgeometry', '--shell', windows[0]], env=env, text=True)
            fields = dict(line.split('=', 1) for line in geometry.splitlines() if '=' in line)
            subprocess.run(['xdotool', 'mousemove', str(round(x + int(fields['X']))), str(round(y + int(fields['Y']))),
                            'sleep', '.3', 'mousedown', '1', 'sleep', '.5', 'mouseup', '1'], env=env, check=True)

        def command(op, **fields):
            sequence = state().get('sequence', 0)
            atomic_json(output / 'ui-audit-command.json', {'op': op, **fields})
            wait(op, lambda: state().get('sequence', 0) > sequence, 30)

        def capture(name, width, height):
            windows = subprocess.check_output(['xdotool', 'search', '--onlyvisible', '--name', '^Root$'], env=env, text=True).split()
            assert len(windows) == 1
            # Native hover cards can outlive a dialog transition. Park over the
            # top-center background before judging the next dialog's appearance.
            subprocess.run(['xdotool', 'mousemove', '--window', windows[0], str(width // 2), '80'], env=env, check=True)
            frame = state()['frame']
            wait('render ' + name, lambda: state()['frame'] >= frame + 3, 60)
            subprocess.run(['import', '-window', windows[0], str(diagnostics.directory / (name + '.png'))], env=env, check=True)
            actual = state()
            atomic_json(diagnostics.directory / (name + '.json'), actual)
            assert (actual['width'], actual['height']) == (width, height), (
                f"Root changed the requested {width}x{height} size to {actual['width']}x{actual['height']} during {name}")

        def toolbar_visible():
            buttons = state().get('toolbar', [])
            return len(buttons) == 1 and all(button['active'] and button['bounds']['reachable'] for button in buttons)

        def toolbar_hidden():
            buttons = state().get('toolbar', [])
            return len(buttons) == 1 and all(not button['active'] for button in buttons)

        # Exercise the normal private setup entry, not a test-only content hook.
        click(180, 415)
        wait('private setup', lambda: (output / 'view.txt').exists())
        command('capture')
        products = state()['playtestProducts']
        assert len(products) == 9 and all(product['gameplay'] and (not product['requiresPurchase'] or product['menu']) for product in products), products
        assert not (output / 'fixture-ownership-active').exists(), 'Demo must work without the old ownership fixture'
        (output / 'command.txt').write_text('back')
        wait('back from setup', lambda: screen('home'))
        click(180, 580)
        wait('saved matches', lambda: screen('saves'))
        click(620, 220)
        wait('resumed board', lambda: (lab / 'client/native-menu-screen').read_text() == 'playing' and
             any(panel['count'] == 6 for panel in state().get('panels', [])))
        session.assert_launcher_exited()
        command('capture')
        if args.explore_item:
            from network import exchange
            endpoint = session.endpoint
            def poll():
                return json.loads(exchange(endpoint['port'], json.dumps({'op': 'join', 'token': endpoint['tokens'][0]}).encode()))
            def explore_pending(response):
                return any(message.get('value', {}).get('msg', {}).get('value', {}).get('targetType') == 'ExploreItemChoice'
                           for message in response['messages'])
            before = poll()
            assert explore_pending(before), 'Fixture has no pending Explore choice for the host'
            atomic_json(diagnostics.directory / 'explore-snapshot.json', before)
            wait('native Explore item prompt', lambda: state().get('itemSelection'), 30)
            capture('explore-items', state()['width'], state()['height'])
            items = state()['itemSelection']
            assert items['choices'] == 2 and items['views'] == 2, items
            command('take-first-item')
            wait('Explore choice accepted by host', lambda: not explore_pending(poll()), 30)
            wait('Explore prompt closed', lambda: state().get('itemSelection') is None, 30)
            capture('explore-complete', state()['width'], state()['height'])
            wait('Match control visible after Explore', toolbar_visible, 30)
            button = state()['toolbar'][0]['bounds']
            click(button['x'], button['y'])
            wait('Match menu after Explore', lambda: screen('match'), 30)
            assert not {'I\'m ready', 'Not ready', 'Invite friends'} & set(state()['matchControls'])
            capture('explore-match-menu', state()['width'], state()['height'])
            command('back-to-board')
            print('Native Explore displayed both items and the host accepted the choice.', flush=True)
            return
        wait('six player panels', lambda: any(p['count'] == 6 for p in state().get('panels', [])), 30)
        assert any(p['count'] == 6 and all(view['active'] for view in p['views']) for p in state()['panels'])
        cases = [(1920, 1080)] if args.full_hd else [(1280, 800), (1280, 720)]
        for width, height in cases:
            command('resize', width=width, height=height)
            wait('window size', lambda: (state().get('width'), state().get('height')) == (width, height), 30)
            tag = f'{width}x{height}'
            capture(f'board-{tag}', width, height)
            wait('toolbar visible on board', toolbar_visible, 30)
            for button in state()['toolbar']:
                bounds = button['bounds']
                assert bounds['inside'] and bounds['y'] - bounds['height'] / 2 >= 70, button
            seen = set()
            if not args.targets_only:
                command('info-open')
                wait('six info pages', lambda: (state().get('info') or {}).get('count') == 6, 30)
                wait('toolbar hidden behind information', toolbar_hidden, 30)
                for _ in range(6):
                    wait('info navigation reachable', lambda: (state().get('info') or {}).get('initialized') and
                         (state()['info'].get('next') or {}).get('reachable') and state()['info']['next']['enabled'] and state()['info']['active'], 60)
                    current = state()['info']
                    seen.add(current['index'])
                    capture(f"info-{tag}-page-{current['index']}", width, height)
                    assert toolbar_hidden(), state()['toolbar']
                    # The observed native edge-arrow hitboxes extend slightly beyond
                    # the screen. Require a usable visible target and a clear raycast.
                    assert current['next']['visibleWidth'] >= 44 and current['next']['visibleHeight'] >= 44, current
                    button = current['next']
                    click(button['x'], button['y'])
                    wait('next info page', lambda: state()['info']['index'] != current['index'], 60)
                assert seen == set(range(6)), seen
                wait('info animation complete', lambda: state()['info']['next']['enabled'], 60)
                command('info-close')
                wait('info closed', lambda: state().get('info') is None, 30)
                wait('toolbar restored after information', toolbar_visible, 30)
            command('select-open')
            wait('six reachable player choices', lambda: (state().get('selector') or {}).get('choices') == 6 and
                 len(state()['selector']['slots']) == 6 and all(s['button'] and s['button']['reachable'] for s in state()['selector']['slots']), 60)
            capture(f'select-{tag}', width, height)
            assert toolbar_hidden(), state()['toolbar']
            selector = state()['selector']
            assert all(s['button']['inside'] and s['button']['enabled'] and s['button']['reachable'] for s in selector['slots']), selector
            button = selector['slots'][-1]['button']
            click(button['x'], button['y'])
            wait('native choice resolves', lambda: state().get('selectionResolved'), 30)
            wait('selection closes', lambda: state().get('selector') is None, 30)
            wait('toolbar restored after selection', toolbar_visible, 30)
            if args.trading:
                command('prices-open')
                wait('Riverfolk price controls', lambda: (state().get('prices') or {}).get('rows') and
                     all(control['control']['reachable'] for row in state()['prices']['rows'] for control in row), 60)
                rows = state()['prices']['rows']
                assert len(rows) == 3 and all(len(row) == 4 for row in rows), rows
                assert all(control['control']['inside'] for row in rows for control in row), rows
                capture(f'prices-{tag}', width, height)
                assert toolbar_hidden()
                for row_index, row in enumerate(rows):
                    button = row[-1]['control']
                    click(button['x'], button['y'])
                    wait('Riverfolk price changes', lambda: state()['prices']['rows'][row_index][-1]['isOn'], 30)
                command('prices-close')
                wait('Riverfolk prices resolved', lambda: state().get('pricesResolved') and state().get('prices') is None, 30)
                wait('toolbar restored after prices', toolbar_visible, 30)
            # Exercise the match control and its return path with real clicks.
            button = next(button['bounds'] for button in state()['toolbar'] if button['name'] == 'Match')
            click(button['x'], button['y'])
            wait('match', lambda: screen('match'), 30)
            assert not {"I'm ready", 'Not ready', 'Invite friends'} & set(state()['matchControls'])
            wait('diagnostics button reachable', lambda: (state().get('diagnosticsButton') or {}).get('reachable'), 30)
            copied = lab / 'client/diagnostics-copy-verified'
            copied.unlink(missing_ok=True)
            button = state()['diagnosticsButton']
            click(button['x'], button['y'])
            wait('safe diagnostics copied', lambda: copied.exists() and copied.read_text() == 'True', 30)
            capture(f'diagnostics-{tag}', width, height)
            # Invoke the current menu's actual back-button listener.
            command('back-to-board')
            wait('toolbar return path', lambda: screen('playing') and toolbar_visible(), 30)
            evidence.append({'width': width, 'height': height, 'platform': state()['platform'], 'infoPages': sorted(seen), 'reachableChoices': 6})
        session.stop()
    result = {'status': 'passed', 'resolutions': evidence, 'syntheticSelectionPresentation': True,
              'gameplayMoveSubmitted': False, 'privatePlaytestProducts': 9, 'diagnosticsCopied': True,
              'targetsOnly': args.targets_only, 'riverfolkPrices': args.trading,
              'playerPanels': state().get('panels', []), 'screenshots': str(diagnostics.directory)}
    result_name = 'native-ui-fixture-test.json' if args.fixture else 'native-ui-1080p-test.json' if args.full_hd else 'native-ui-targets-test.json' if args.targets_only else 'native-ui-test.json'
    atomic_json(PROJECT / 'results' / result_name, result)
    print(json.dumps(result), flush=True)


if __name__ == '__main__':
    main()
