#!/usr/bin/env python3
"""Inspect native communication boundaries on the isolated host."""
import json
from pathlib import Path
import shutil
import sys

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from runtime import GameProcess
from steam import discover
from test_diagnostics import Diagnostics


def main():
    mode = 'chat' if '--chat' in sys.argv else 'timer'
    lab = PROJECT / '.lab/steam-game-probe'
    with lock_data(lab):
        root = lab / 'host'
        shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', root / 'game/BepInEx/plugins/EngineProbe.dll')
        output = root / f'results/{mode}-probe.json'
        output.unlink(missing_ok=True)
        game = GameProcess(discover(), root, mode + '-probe', headless=True)
        try:
            diagnostics = Diagnostics.from_environment()
            with diagnostics.stage(f'native {mode} boundary probe'):
                game.process.wait(timeout=90)
                report = json.loads(output.read_text())
                summary = {key: value for key, value in report.items() if key != 'methods'}
                if diagnostics.directory is not None:
                    shutil.copy2(output, diagnostics.directory / output.name)
                assert report['status'] == 'completed', summary
                assert game.process.returncode == 0, f'Native {mode} process exited {game.process.returncode}: {summary}'
                print(json.dumps(summary, indent=2))
        finally:
            game.close()


if __name__ == '__main__':
    main()
