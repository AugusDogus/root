#!/usr/bin/env python3
"""Build and capture the startup window on a private Xvfb display, without Root."""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import require_test_budget


def main():
    require_test_budget()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--steam', type=Path, required=True)
    parser.add_argument('--isolated', action='store_true')
    parser.add_argument('--appimage', type=Path, help='Exercise this packaged launcher instead of building a raw executable')
    args = parser.parse_args()
    output = Path(os.environ['ROOT_TEST_RUN_DIR'])
    binary = output / 'launcher-preview'
    if not args.isolated:
        if args.appimage is None:
            subprocess.run(['go', 'build', '-p', '1', '-o', str(binary), '.'], cwd=PROJECT / 'launcher-native',
                           env=dict(os.environ, CGO_ENABLED='0', GOMAXPROCS='2'), check=True)
        extra = ['--appimage', str(args.appimage.resolve())] if args.appimage else []
        subprocess.run(['xvfb-run', '--auto-servernum', '--server-args=-screen 0 1600x1000x24 -nolisten tcp',
                        sys.executable, __file__, '--isolated', '--game', str(args.game), '--steam', str(args.steam), *extra], check=True)
        return

    for name, game, steam in [('root-art', args.game, args.steam), ('fallback', output / 'missing', output / 'missing')]:
        command = [str(args.appimage), '--appimage-extract-and-run'] if args.appimage else [str(binary)]
        child = subprocess.Popen([*command, '--launcher-progress', str(game), str(steam)], stdin=subprocess.PIPE, text=True)
        try:
            deadline = time.monotonic() + 10
            window = None
            while time.monotonic() < deadline:
                if child.poll() is not None:
                    raise RuntimeError(f'{name}: startup window exited before becoming visible')
                # Shiny sets _NET_WM_NAME, while xdotool's search uses WM_NAME.
                # This display contains only our test; identify its client size.
                result = subprocess.run(['xdotool', 'search', '--onlyvisible', '--name', ''], capture_output=True, text=True)
                if result.returncode == 0:
                    windows = []
                    for candidate in result.stdout.split():
                        geometry = subprocess.check_output(['xdotool', 'getwindowgeometry', '--shell', candidate], text=True)
                        if 'WIDTH=800\n' in geometry and 'HEIGHT=500\n' in geometry:
                            windows.append(candidate)
                    if not windows:
                        time.sleep(0.1)
                        continue
                    if len(windows) != 1:
                        raise RuntimeError('Expected one window on the isolated display')
                    window = windows[0]
                    break
                time.sleep(0.1)
            if window is None:
                raise RuntimeError('Startup window did not appear within ten seconds')
            for label, message in [('preparing', 'Preparing your game (1 of 2)...'),
                                   ('setup', 'Finishing setup (1 of 2). This can take a few minutes.')]:
                child.stdin.write(message + '\n')
                child.stdin.flush()
                time.sleep(0.3)
                subprocess.run(['import', '-window', window, str(output / f'{name}-{label}.png')], check=True)
            subprocess.run(['xdotool', 'windowsize', window, '1200', '750'], check=True)
            time.sleep(0.3)
            subprocess.run(['import', '-window', window, str(output / f'{name}-scaled.png')], check=True)
            child.stdin.close()
            if child.wait(timeout=5) != 0:
                raise RuntimeError('Startup window exited with an error')
        finally:
            if child.poll() is None:
                child.kill()
                child.wait()
    print(f'Window updates, scaling, fallback artwork, and shutdown passed. Captures: {output}', flush=True)


if __name__ == '__main__':
    main()
