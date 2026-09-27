#!/usr/bin/env python3
"""Small boundary and isolation tests; never start Steam, Root, or a browser."""
import json
import hashlib
from pathlib import Path
import sys
import tempfile
import threading
import unittest
from unittest.mock import Mock, patch
import urllib.error
import urllib.request
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data, make_server, reopen_launcher
from prepare import extract_loader, prepare
from session import Session
from steam import BUILD, VERSION, discover, vdf_pairs
from steam_invite import validate_invite


class LauncherTests(unittest.TestCase):
    def test_prepare_updates_only_known_mod_files_and_preserves_saves(self):
        with tempfile.TemporaryDirectory() as temp:
            data = Path(temp)
            payload = data / 'payload'
            payload.mkdir()
            (payload / 'EngineProbe.dll').write_bytes(b'new plugin')
            previous = {'build': BUILD, 'version': '0.1.0', 'plugin_sha256': hashlib.sha256(b'old plugin').hexdigest()}
            expected = {**previous, 'version': VERSION, 'plugin_sha256': hashlib.sha256(b'new plugin').hexdigest()}
            (payload / 'manifest.json').write_text(json.dumps(expected))
            (data / 'prepared.json').write_text(json.dumps(previous))
            (data / 'saves').mkdir()
            save = data / 'saves/test.json'
            save.write_text('saved match')
            plugins = []
            for role in ('host', 'client'):
                game = data / role / 'game'
                (game / 'BepInEx/plugins').mkdir(parents=True)
                (game / 'Root.exe').write_bytes(b'game')
                plugin = game / 'BepInEx/plugins/EngineProbe.dll'
                plugin.write_bytes(b'old plugin')
                plugins.append(plugin)
            with patch('prepare.validate_game'):
                plugins[1].write_bytes(b'user edit')
                with self.assertRaisesRegex(ValueError, 'changed outside this launcher'):
                    prepare(Mock(), data, payload, Mock())
                self.assertEqual(plugins[0].read_bytes(), b'old plugin')
                plugins[1].write_bytes(b'old plugin')
                replace = Path.replace
                def interrupt(path, target):
                    if path == plugins[1].with_suffix('.update'):
                        raise OSError('Interrupted update')
                    return replace(path, target)
                with patch.object(Path, 'replace', interrupt):
                    with self.assertRaisesRegex(OSError, 'Interrupted update'):
                        prepare(Mock(), data, payload, Mock())
                self.assertTrue(all(plugin.read_bytes() == b'old plugin' for plugin in plugins))
                self.assertEqual(json.loads((data / 'prepared.json').read_text()), previous)
                prepare(Mock(), data, payload, Mock())
                self.assertTrue(all(plugin.read_bytes() == b'new plugin' for plugin in plugins))
                self.assertEqual(save.read_text(), 'saved match')
                self.assertEqual(json.loads((data / 'prepared.json').read_text()), expected)

    def test_return_to_menu_closes_old_session_before_opening_new_game(self):
        with tempfile.TemporaryDirectory() as temp:
            session = Session(Path(temp), Path(temp), True)
            session.client = Mock()
            order = []
            with patch.object(session, 'stop', side_effect=lambda: order.append('stop')), patch('session.discover'), \
                    patch('session.proton_paths'), patch.object(session, 'prepared', return_value=True), \
                    patch('session.GameProcess', side_effect=lambda *args, **kwargs: order.append(args[2])):
                for role in ('host', 'client'):
                    (Path(temp) / role).mkdir()
                    (Path(temp) / role / 'bindings-ready').touch()
                (Path(temp) / 'prepared.json').write_text('{}')
                session.perform('return-menu', {})
            self.assertEqual(order, ['stop', 'steam-menu'])

    def test_native_host_reuses_game_and_keeps_shutdown_token_private(self):
        with tempfile.TemporaryDirectory() as temp:
            data = Path(temp)
            payload = data / 'payload'
            payload.mkdir()
            (payload / 'manifest.json').write_text('{}')
            (data / 'prepared.json').write_text('{}')
            for role in ('host', 'client'):
                (data / role).mkdir()
                (data / role / 'bindings-ready').touch()
            endpoint = {'port': 12345, 'tokens': [format(index, '032x') for index in range(1, 7)], 'controlToken': 'private-control'}
            authority = Mock()
            authority.endpoint.return_value = endpoint
            authority.process.poll.return_value = None
            game = Mock()
            game.process.poll.return_value = None
            session = Session(data, payload, True)
            with patch('session.discover'), patch('session.proton_paths'), patch('session.GameProcess', side_effect=[game, authority]) as launch:
                with self.assertRaisesRegex(ValueError, 'Open the six-player game menu'):
                    session.perform('host-native', {})
                session.perform('play', {})
                initialization = {'AdvancedSetup': True, 'AdsetDisableDraft': False,
                                  'DoNotShufflePlayers': False, 'options': {'randomSuits': 'true'}}
                session.start('host-native', {'initialization': json.dumps(initialization)})
                session.worker.join()
                self.assertEqual(session.error, '')
                self.assertIs(session.client, game)
                self.assertIs(session.host, authority)
                self.assertEqual([call.args[2] for call in launch.call_args_list], ['steam-menu', 'server'])
                config = json.loads((data / 'client/steam-config.json').read_text())
                self.assertNotIn('controlToken', config)
                self.assertEqual(config['tokens'], endpoint['tokens'])
                self.assertEqual(json.loads((data / 'host/native-setup.json').read_text()), initialization)
                self.assertNotIn('initialization', config)
                with self.assertRaisesRegex(ValueError, 'Stop the current session'):
                    session.start('host-native', {})

    def test_reopen_only_authenticated_local_launcher(self):
        with tempfile.TemporaryDirectory() as temp:
            data = Path(temp)
            session = Session(data, PROJECT / 'launcher/payload', True)
            token = 'a' * 43
            server = make_server(session, token)
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            url = f'http://127.0.0.1:{server.server_port}/#{token}'
            link = data / 'launcher-url.txt'
            try:
                with patch('main.webbrowser.open', return_value=True) as browser:
                    for invalid in ('https://example.com/#' + token, url.replace('127.0.0.1', 'localhost'),
                                    url.replace(token, 'b' * 43)):
                        link.write_text(invalid)
                        self.assertFalse(reopen_launcher(data))
                    browser.assert_not_called()
                    link.write_text(url)
                    self.assertTrue(reopen_launcher(data))
                    browser.assert_called_once_with(url)
            finally:
                server.shutdown()
                server.server_close()
                thread.join()

    def test_dlc_setup_reaches_host_and_resume_uses_saved_settings(self):
        with tempfile.TemporaryDirectory() as temp:
            data = Path(temp)
            payload = data / 'payload'
            payload.mkdir()
            (payload / 'manifest.json').write_text('{}')
            (data / 'prepared.json').write_text('{}')
            for role in ('host', 'client'):
                (data / role).mkdir()
                (data / role / 'bindings-ready').touch()
            setup = {'Factions': [14, 15, 10, 11, 12, 13], 'Map': 3, 'Deck': 1}
            endpoint = {'port': 12345, 'tokens': [format(index, '032x') for index in range(1, 7)],
                        'controlToken': 'private-control', 'setup': setup}
            fake = Mock()
            fake.endpoint.return_value = endpoint
            fake.process.poll.return_value = 0
            session = Session(data, payload, True)
            with patch('session.discover'), patch('session.proton_paths'), patch('session.GameProcess', return_value=fake) as launch:
                session.perform('host', {'setup': json.dumps(setup)})
                self.assertEqual(json.loads((data / 'host/match-setup.json').read_text()), setup)
                self.assertEqual(json.loads((data / 'client/steam-config.json').read_text())['setup'], setup)
                save = session.save
                (data / 'saves' / save).write_text('native checkpoint')
                session.stop()
                self.assertFalse((data / 'host/match-setup.json').exists())
                (data / 'host/match-setup.json').write_text('{"Map":0}')
                (data / 'host/native-setup.json').write_text('{"AdvancedSetup":false}')
                session.perform('resume', {'save': save, 'setup': '{"Map":0}'})
                self.assertFalse((data / 'host/match-setup.json').exists())
                self.assertFalse((data / 'host/native-setup.json').exists())
                self.assertTrue(launch.call_args_list[-2].kwargs['resume'])
                self.assertEqual(json.loads((data / 'client/steam-config.json').read_text())['setup'], setup)
                session.stop()

    def test_steam_launcher_modes_and_private_configuration(self):
        with tempfile.TemporaryDirectory() as temp:
            data = Path(temp)
            payload = data / 'payload'
            payload.mkdir()
            (payload / 'manifest.json').write_text('{}')
            (data / 'prepared.json').write_text('{}')
            for role in ('host', 'client'):
                (data / role).mkdir()
                (data / role / 'bindings-ready').touch()
            endpoint = {'port': 12345, 'tokens': [format(index, '032x') for index in range(1, 7)], 'controlToken': 'private-control'}
            fake = Mock()
            fake.endpoint.return_value = endpoint
            fake.process.poll.return_value = None
            session = Session(data, payload, True)
            with patch('session.discover'), patch('session.proton_paths'), patch('session.GameProcess', return_value=fake) as launch:
                with self.assertRaisesRegex(ValueError, 'Steam'):
                    session.perform('host', {'transport': 'cloudflare'})
                with self.assertRaises(ValueError):
                    session.perform('join', {'invite': json.dumps({'relay': 'https://relay.example'})})
                launch.assert_not_called()
                session.perform('host', {'transport': 'steam'})
                self.assertEqual([call.args[2] for call in launch.call_args_list], ['server', 'steam-host'])
                config = json.loads((data / 'client/steam-config.json').read_text())
                self.assertEqual(config, {'port': endpoint['port'], 'tokens': endpoint['tokens']})
                self.assertNotIn('controlToken', config)
                endpoint['pending'] = True
                session = Session(data, payload, True)
                session.perform('host', {'transport': 'steam'})
                pending = json.loads((data / 'client/steam-config.json').read_text())
                self.assertTrue(pending['pending'])
                self.assertNotIn('controlToken', pending)
                invite = f'root6:6:{BUILD}:{0x0110000100000001}:501:2:' + 'a' * 32
                session = Session(data, payload, True)
                session.perform('join', {'invite': invite})
                self.assertEqual(launch.call_args.args[2], 'steam-client')
                session = Session(data, payload, True)
                session.perform('wait', {})
                self.assertEqual(launch.call_args.args[2], 'steam-wait')
                (payload / 'manifest.json').write_text('{"different":true}')
                self.assertFalse(session.prepared())
                with self.assertRaisesRegex(ValueError, 'different mod package'):
                    session.perform('host', {'transport': 'steam'})

    def test_steam_invitation_validation(self):
        invite = f'root6:6:{BUILD}:{0x0110000100000001}:501:6:' + 'a' * 32
        self.assertEqual(validate_invite(invite), invite)
        for value in (invite + '\n', invite.replace(':501:', ':0:'), invite.replace(':6:', ':1:'),
                      invite.replace(BUILD, '0'), invite.replace('root6:6:', 'root6:3:'), invite + ':extra', invite.replace(str(0x0110000100000001), '1')):
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_invite(value)

    def test_secondary_steam_library_and_build_guard(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / 'Steam'
            library = Path(temp) / 'Other Library'
            (root / 'steamapps').mkdir(parents=True)
            (root / 'steamapps/libraryfolders.vdf').write_text(f'"libraryfolders" {{ "1" {{ "path" "{library}" }} }}')
            game = library / 'steamapps/common/Root'
            game.mkdir(parents=True)
            for name in ('Root.exe', 'GameAssembly.dll', 'Root_Data/il2cpp_data/Metadata/global-metadata.dat'):
                path = game / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.touch()
            manifest = library / 'steamapps/appmanifest_965580.acf'
            manifest.write_text(f'"appid" "965580" "buildid" "{BUILD}" "installdir" "Root"')
            self.assertEqual(discover(roots=[root]).game, game)
            manifest.write_text('"appid" "965580" "buildid" "old" "installdir" "Root"')
            with self.assertRaisesRegex(ValueError, 'needs Steam build'):
                discover(roots=[root])

    def test_windows_library_escaping(self):
        with tempfile.TemporaryDirectory() as temp:
            file = Path(temp) / 'libraries.vdf'
            file.write_text(r'"path" "D:\\Steam Library"')
            self.assertEqual(vdf_pairs(file), [('path', r'D:\Steam Library')])

    def test_archive_traversal_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / 'loader.zip'
            with zipfile.ZipFile(archive, 'w') as bundle:
                bundle.writestr('../escaped', 'bad')
            with self.assertRaisesRegex(ValueError, 'unsafe path'):
                extract_loader(archive, root / 'game')
            self.assertFalse((root / 'escaped').exists())

    def test_launcher_lock(self):
        with tempfile.TemporaryDirectory() as temp, lock_data(Path(temp)):
            with self.assertRaisesRegex(ValueError, 'Another launcher'):
                lock_data(Path(temp))

    def test_local_api_requires_secret_and_same_origin(self):
        with tempfile.TemporaryDirectory() as temp:
            session = Session(Path(temp), PROJECT / 'launcher/payload', True)
            server = make_server(session, 'test-secret')
            thread = threading.Thread(target=server.serve_forever)
            thread.start()
            url = f'http://127.0.0.1:{server.server_port}'
            try:
                for headers in ({}, {'Authorization': 'Bearer test-secret', 'Origin': 'https://evil.example'}):
                    with self.assertRaises(urllib.error.HTTPError) as caught:
                        urllib.request.urlopen(urllib.request.Request(url + '/api/status', headers=headers))
                    self.assertEqual(caught.exception.code, 403)
                    caught.exception.close()
                with urllib.request.urlopen(urllib.request.Request(url + '/api/status', headers={'Authorization': 'Bearer test-secret'})) as response:
                    self.assertFalse(json.load(response)['active'])
                with urllib.request.urlopen(url) as response:
                    self.assertIn(b'Prepare my game', response.read())
                    self.assertIn("frame-ancestors 'none'", response.headers['Content-Security-Policy'])
                request = urllib.request.Request(url + '/api/prepare', data=b'{}', headers={'Content-Type': 'application/json'})
                with self.assertRaises(urllib.error.HTTPError) as caught:
                    urllib.request.urlopen(request)
                self.assertEqual(caught.exception.code, 403)
                caught.exception.close()
                self.assertIsNone(session.worker)
            finally:
                server.shutdown()
                server.server_close()
                thread.join()


if __name__ == '__main__':
    unittest.main()
