#!/usr/bin/env python3
"""Check readiness, resignation and recovery in one isolated native authority."""
import json
import os
from pathlib import Path
import random
import shutil
import sys
import tempfile
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from network import exchange
from runtime import GameProcess
from steam import discover
from selection_messages import latest_selection
from gameplay_driver import choose


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-game-probe'
    with lock_data(lab), tempfile.TemporaryDirectory(dir=lab) as scratch:
        shutil.copy2(PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll', lab / 'host/game/BepInEx/plugins/EngineProbe.dll')
        save = Path(scratch) / 'checkpoint.json'
        game = GameProcess(discover(), lab / 'host', 'server', headless=True, save=save, test_seed=12345)
        try:
            endpoint = game.endpoint()
            def request(seat, action):
                return json.loads(exchange(endpoint['port'], json.dumps({**action, 'token': endpoint['tokens'][seat]}).encode()))
            assert request(1, {'op': 'ready', 'ready': True}) == {'ok': True}
            assert request(0, {'op': 'join'})['lobby'][1]['Ready'] is True
            assert request(1, {'op': 'ready', 'ready': 'yes'})['ok'] is False
            rng, undo = random.Random(12345), set()
            for step in range(100):
                replies = [request(seat, {'op': 'join'}) for seat in range(6)]
                if replies[0]['gameOver']:
                    break
                active = [(seat, latest_selection(reply)) for seat, reply in enumerate(replies)]
                active = [(seat, selection) for seat, selection in active if selection is not None]
                if not active and step > 40:
                    deadline = time.monotonic() + 30
                    while not active and time.monotonic() < deadline:
                        time.sleep(.1)
                        replies = [request(seat, {'op': 'join'}) for seat in range(6)]
                        if replies[0]['gameOver']:
                            break
                        active = [(seat, latest_selection(reply)) for seat, reply in enumerate(replies)]
                        active = [(seat, selection) for seat, selection in active if selection is not None]
                    if replies[0]['gameOver']:
                        break
                assert active, f'No human decision at step {step}'
                seat, selection = active[0]
                if step == 40:
                    resigned = seat
                    assert request(seat, {'op': 'resign'})['ok'], 'Native resignation failed'
                    deadline = time.monotonic() + 30
                    while request(0, {'op': 'join'})['transitioning']:
                        assert time.monotonic() < deadline, 'Native resignation did not finish'
                        time.sleep(.1)
                    current = request(0, {'op': 'join'})
                    assert current['lobby'][seat]['State'] == 'Resigned'
                    assert latest_selection(request(seat, {'op': 'join'})) is None, 'Resigned spectator received a playable choice'
                    assert request(seat, choose(selection, rng, undo))['error'] == 'SeatResigned'
                    game.close()
                    # The pre-AI release used this same native save shape with
                    # version 1 and no configured-AI seat metadata.
                    legacy = json.loads(save.read_text())
                    assert legacy['Version'] == 2
                    legacy['Version'] = 1
                    save.write_text(json.dumps(legacy))
                    game = GameProcess(discover(), lab / 'host', 'server', headless=True, save=save, resume=True)
                    endpoint = game.endpoint()
                    restored = request(0, {'op': 'join'})
                    assert restored['lobby'][seat]['State'] == 'Resigned', 'Resignation lost on resume'
                    spectator = request(seat, {'op': 'join'})
                    assert latest_selection(spectator) is None, 'Resumed spectator received a playable choice'
                    spectator_cursor = spectator['next']
                    continue
                if step > 40:
                    assert seat != resigned, 'Resigned player still blocks a human decision'
                    spectator = request(resigned, {'op': 'poll', 'after': spectator_cursor})
                    spectator_cursor = spectator['next']
                    assert all(latest_selection({'messages': [message]}) is None for message in spectator['messages']), 'Spectator received a live playable choice'
                assert request(seat, choose(selection, rng, undo)) == {'ok': True}
            result = {'status': 'passed', 'decisions': step, 'readiness': True, 'resignation': True, 'resignedSeatRecovery': True, 'legacySaveRecovery': True}
            (PROJECT / 'results/session-features-test.json').write_text(json.dumps(result))
            print(json.dumps(result), flush=True)
        except Exception:
            print(f'Authority process exit code: {game.process.poll()}', flush=True)
            raise
        finally:
            game.close()


if __name__ == '__main__':
    main()
