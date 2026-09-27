#!/usr/bin/env python3
"""Exercise the private lobby authority without a graphical client."""
import json
import os
from pathlib import Path
import shutil
import sys
import time

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from network import exchange
from runtime import GameProcess
from steam import discover


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/steam-game-probe'
    with lock_data(lab):
        root = lab / 'host'
        plugin = PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll'
        shutil.copy2(plugin, root / 'game/BepInEx/plugins/EngineProbe.dll')
        configuration = root / 'native-setup.json'
        fixture = json.loads((PROJECT / 'scripts/fixtures/native-online-lobby.json').read_text())
        fixture['value']['options'].update(ChosenMap='Lake', ChosenDeck='ExilesAndPartisans')
        configuration.write_text(json.dumps(fixture))
        save = lab / 'saves/online-authority-test.json'
        save.unlink(missing_ok=True)
        model = '--model' in sys.argv
        output = root / 'results/lobby-data-tests'
        output.mkdir(parents=True, exist_ok=True)
        for name in ('before.txt', 'after.txt', 'error.txt', 'passed'):
            (output / name).unlink(missing_ok=True)
        game = GameProcess(discover(), root, 'lobby-data-tests' if model else 'server', headless=True, save=save)
        try:
            if model:
                deadline = time.monotonic() + 90
                while game.process.poll() is None and time.monotonic() < deadline:
                    time.sleep(.25)
                if (output / 'error.txt').exists():
                    raise RuntimeError((output / 'error.txt').read_text())
                assert (output / 'passed').exists(), 'Native lobby model test did not finish'
                print((output / 'passed').read_text())
                return
            endpoint = game.endpoint()
            def request(seat, op, **fields):
                return json.loads(exchange(endpoint['port'], json.dumps({'op': op, 'token': endpoint['tokens'][seat], **fields}).encode()))
            first = request(0, 'join', name='Host fixture')
            assert first['phase'] == 'lobby' and not first['canStart'] and not save.exists()
            assert first['setup']['Map'] == 2 and first['setup']['Deck'] == 1, first['setup']
            assert request(0, 'lobby-start')['error'] == 'PlayersMissing'
            second = request(1, 'join', name='Friend fixture')
            assert request(0, 'chat', text='Hello from lobby', name='Forged friend')['ok']
            assert request(0, 'chat', text='Too soon')['error'] == 'ChatRateLimited'
            assert request(1, 'chat', text='')['error'] == 'InvalidChat'
            assert request(2, 'chat', text='Bot message')['error'] == 'BotSeat'
            history = request(1, 'poll')['chat']
            assert history['Messages'][0]['Name'] == 'Host fixture'
            assert history['Messages'][0]['Seat'] == 1
            assert request(1, 'poll', chatVersion=history['Version'])['chat'] is None
            assert request(1, 'lobby-start')['error'] == 'HostOnly'
            assert request(2, 'join')['error'] == 'BotSeat'
            assert request(1, 'lobby-metadata', metadata={'Faction': 'MarquiseDeCat'})['error'] == 'FactionUnavailable'
            assert request(1, 'lobby-join', metadata={'Faction': 'MechanicalMarquise'})['error'] == 'FactionUnavailable'
            assert request(1, 'lobby-join', metadata={'Faction': 'EyrieDynasties'})['ok']
            assert request(1, 'lobby-leave')['setup']['Factions'][1] == 4
            assert request(0, 'lobby-start')['error'] == 'PlayersMissing'
            assert request(0, 'lobby-metadata', metadata={'Faction': 'EyrieDynasties'})['ok'], 'Leaving must release that faction'
            assert request(0, 'lobby-metadata', metadata={'Faction': 'MarquiseDeCat'})['ok']
            assert request(1, 'lobby-join', metadata={'Faction': 'EyrieDynasties'})['ok']
            assert request(0, 'lobby-start')['ok']
            assert request(1, 'lobby-metadata', metadata={'Faction': 'Invalid'})['error'] == 'LobbyAlreadyStarted'
            for seat in (0, 1):
                state = request(seat, 'join')
                assert len(state['roster']) == 6 and state['messages']
                assert state['setup']['AI'] == [None, None, 1, 1, 1, 1], state['setup']
                assert state['setup']['Factions'] == [0, 1, 2, 3, 6, 7], state['setup']
                assert state['chat']['Messages'][0]['Text'] == 'Hello from lobby'
                assert state['initialization']['EnableBluff'] is True
                assert state['setup']['Map'] == 2 and state['setup']['Deck'] == 1, state['setup']
                assert state['initialization']['ChosenMap'] == 'Lake'
                assert state['initialization']['ChosenDeck'] == 'ExilesAndPartisans'
                assert state['roster'][0]['account'] == first['account']
                assert state['roster'][1]['account'] == second['account']
            assert save.exists()
            saved = json.loads(save.read_text())['Initialization']
            assert json.loads(save.read_text())['Chat'][0]['Text'] == 'Hello from lobby'
            assert saved['ChosenMap'] == 'Lake' and saved['ChosenDeck'] == 'ExilesAndPartisans'
            game.close()
            configuration.unlink()
            game = GameProcess(discover(), root, 'server', headless=True, save=save, resume=True)
            endpoint = game.endpoint()
            restored = request(1, 'join', name='Renamed friend')
            assert restored['chat']['Messages'][0]['Text'] == 'Hello from lobby'
            assert request(1, 'chat', text='Hello after restore')['ok']
            latest = request(0, 'poll', after=0)['chat']['Messages'][-1]
            assert latest['Name'] == 'Renamed friend' and latest['Seat'] == 2
            result = {'status': 'passed', 'hostOnlyStart': True, 'requiresPlayers': True, 'rejectsDuplicateFactions': True,
                      'nativeAI': 4, 'stableIdentities': True, 'savedAfterStart': True, 'bluffPreserved': True, 'mapAndDeckPreserved': True}
            (PROJECT / 'results/online-authority-test.json').write_text(json.dumps(result, indent=2)+'\n')
            print(json.dumps(result), flush=True)
        finally:
            game.close()
            configuration.unlink(missing_ok=True)
            save.unlink(missing_ok=True)


if __name__ == '__main__':
    main()
