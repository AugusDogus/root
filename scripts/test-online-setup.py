#!/usr/bin/env python3
"""Inspect native online setup on an isolated display without submitting a lobby."""
import os
from pathlib import Path
import shutil
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from runtime import GameProcess
from steam import discover


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-game-probe'
    with lock_data(lab):
        output = lab / 'client/results/online-setup-probe'
        output.mkdir(parents=True, exist_ok=True)
        for name in ('error.txt', 'configured.json', 'view.txt', 'options.json', 'command.txt', 'stop', 'blocked-lobby-write.txt'):
            (output / name).unlink(missing_ok=True)
        shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', lab / 'client/game/BepInEx/plugins/EngineProbe.dll')
        game = GameProcess(discover(), lab / 'client', 'online-setup-probe', headless=True)
        try:
            deadline = time.monotonic() + 600
            announced = False
            while time.monotonic() < deadline:
                for name in ('error.txt', 'blocked-lobby-write.txt'):
                    if (output / name).exists():
                        raise RuntimeError((output / name).read_text())
                if (output / 'view.txt').exists() and not announced:
                    print('Online setup view loaded. Waiting for test commands.', flush=True)
                    announced = True
                if (output / 'configured.json').exists():
                    print('Online setup resolved locally.', flush=True)
                    return
                if (output / 'stop').exists():
                    return
                if game.process.poll() is not None:
                    raise RuntimeError('The isolated test game exited before setup completed.')
                time.sleep(.5)
            raise TimeoutError('Online setup test exceeded ten minutes.')
        finally:
            game.close()


if __name__ == '__main__':
    main()
