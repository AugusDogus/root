#!/usr/bin/env python3
"""Exercise CI input boundaries and player-release validation without a game."""
import base64
import hashlib
import io
import subprocess
import os
from pathlib import Path
import runpy
import tempfile
import unittest
import urllib.error
from unittest.mock import Mock, patch
import zipfile
from appimage_package import appimage_name

import ci_generate_references as references
import steam_ci
import launcher_release as release

PACKAGE = runpy.run_path(str(release.PROJECT / 'scripts/package-native-launcher.py'))
SETUP = runpy.run_path(str(release.PROJECT / 'scripts/setup-steam-ci.py'))


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.dist = self.root / 'dist'
        self.dist.mkdir()
        self.version = '1.2.3'
        self.windows = 'root-six-player-1.2.3-windows.zip'
        self.appimage = appimage_name(self.version)
        header = bytearray(64)
        header[:7] = b'\x7fELF\x02\x01\x01'
        header[8:11] = b'AI\x02'
        header[18:20] = b'\x3e\x00'
        (self.dist / self.appimage).write_bytes(header)
        binary = 'Root Six Player.exe'
        (self.dist / binary).write_bytes(b'executable fixture')
        with zipfile.ZipFile(self.dist / self.windows, 'w') as output:
            info = zipfile.ZipInfo(binary)
            info.external_attr = 0o100755 << 16
            output.writestr(info, (self.dist / binary).read_bytes())
        self.checksums()

    def checksums(self):
        names = ['Root Six Player.exe', self.windows, self.appimage]
        (self.dist / 'SHA256SUMS').write_text(''.join(f'{digest(self.dist / name)}  {name}\n' for name in names))

    def test_download_names_match_checksums(self):
        target = self.root / 'assets'
        release.stage_release(self.dist, target, self.version)
        self.assertEqual({p.name for p in target.iterdir()}, {'RootSixPlayer.exe', self.windows, self.appimage, 'SHA256SUMS'})
        for line in (target / 'SHA256SUMS').read_text().splitlines():
            checksum, name = line.split('  ', 1)
            self.assertEqual(digest(target / name), checksum)

    def test_changed_binary_rejected(self):
        (self.dist / 'Root Six Player.exe').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'changed after packaging'):
            release.stage_release(self.dist, self.root / 'assets', self.version)

    def test_non_appimage_rejected_even_with_valid_checksum(self):
        (self.dist / self.appimage).write_bytes(b'not an AppImage')
        self.checksums()
        with self.assertRaisesRegex(ValueError, 'type-2 AppImage'):
            release.stage_release(self.dist, self.root / 'assets', self.version)

    def test_extra_game_files_rejected_even_with_valid_checksums(self):
        with zipfile.ZipFile(self.dist / self.windows, 'a') as output:
            output.writestr('GameAssembly.dll', b'private')
        self.checksums()
        with self.assertRaisesRegex(ValueError, 'must contain only'):
            release.stage_release(self.dist, self.root / 'assets', self.version)

    def test_old_binary_in_zip_rejected(self):
        (self.dist / 'Root Six Player.exe').write_bytes(b'new binary')
        self.checksums()
        with self.assertRaisesRegex(ValueError, 'different executable'):
            release.stage_release(self.dist, self.root / 'assets', self.version)

    def test_appimage_is_staged_as_executable(self):
        target = self.root / 'assets'
        release.stage_release(self.dist, target, self.version)
        if os.name != 'nt':
            self.assertTrue(os.access(target / self.appimage, os.X_OK))

    def test_tag_must_match_source(self):
        with self.assertRaisesRegex(ValueError, 'Release tag must'):
            release.validate_version(release.PROJECT, release.VERSION, 'v0.0.0')
        release.validate_version(release.PROJECT, release.VERSION, f'v{release.VERSION}')


class SteamInputTests(unittest.TestCase):
    def run_setup(self, login_output, login_exit=0):
        config = b'"ConnectCache" { "fixture" "test-only" }'
        uploads = []

        def fake_command(home, arguments):
            return ['steamcmd-fixture', str(home), *arguments]

        def fake_run(arguments, **kwargs):
            if arguments[0] == 'steamcmd-fixture':
                if '+@NoPromptForPassword' not in arguments:
                    steam_ci.write_session(Path(arguments[1]), config)
                    return subprocess.CompletedProcess(arguments, 0)
                return subprocess.CompletedProcess(arguments, login_exit, login_output)
            if arguments[:3] == ['gh', 'secret', 'set']:
                uploads.append(arguments[3])
            return subprocess.CompletedProcess(arguments, 0)

        main = SETUP['main']
        with patch.dict(main.__globals__, bootstrap=Mock(), command=fake_command), \
                patch('steam_ci.command', side_effect=fake_command), \
                patch('sys.argv', ['setup-steam-ci.py']), patch('sys.stdin.isatty', return_value=True), \
                patch('builtins.input', return_value='fixture_user'), patch('sys.stdout', new_callable=io.StringIO), \
                patch('subprocess.run', side_effect=fake_run):
            try:
                main()
            finally:
                self.uploads = uploads
        return uploads

    def test_setup_accepts_success_with_interleaved_steam_status(self):
        output = (b'Waiting for client config...\x1b[0mOK\n'
                  b'Waiting for user info...Waiting for compat in post-logon took: 0.098241s\x1b[0mOK\n'
                  b'Unloading Steam API...\x1b[0mOK\n')
        self.assertEqual(self.run_setup(output), ['STEAM_USERNAME', 'STEAM_CONFIG_VDF'])

    def test_setup_does_not_upload_after_failed_session_reuse(self):
        with self.assertRaisesRegex(RuntimeError, 'new login'):
            self.run_setup(b'Cached credentials not found.', login_exit=5)
        self.assertEqual(self.uploads, [])

    def test_wrong_game_build_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / 'appmanifest.acf'
            manifest.write_text('"appid" "965580" "buildid" "unexpected"')
            with self.assertRaisesRegex(ValueError, 'Expected Root Steam build'):
                references.validate_game(root, manifest)

    def test_missing_game_files_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            manifest = root / 'appmanifest.acf'
            manifest.write_text(f'"appid" "965580" "buildid" "{references.BUILD}"')
            with self.assertRaisesRegex(ValueError, 'missing Root.exe'):
                references.validate_game(root, manifest)

    def test_invalid_sessions_do_not_echo_secrets(self):
        for value in ['not-base64-private-value', base64.b64encode(b'private incomplete config').decode()]:
            with self.assertRaises(ValueError) as failure:
                steam_ci.decode_session(value)
            self.assertNotIn(value, str(failure.exception))
        self.assertEqual(steam_ci.decode_session(base64.b64encode(b'"ConnectCache" { "fixture" "test-only" }')),
                         b'"ConnectCache" { "fixture" "test-only" }')

    def test_steamcmd_never_inherits_ci_secrets_or_desktop(self):
        with patch.dict(os.environ, STEAM_CONFIG_VDF='private-session', GH_TOKEN='private-token',
                        DISPLAY=':0', WAYLAND_DISPLAY='desktop'):
            child = steam_ci.environment()
        for key in ['STEAM_CONFIG_VDF', 'GH_TOKEN', 'DISPLAY', 'WAYLAND_DISPLAY']:
            self.assertNotIn(key, child)

    def test_auth_failure_output_is_not_exposed(self):
        result = subprocess.CompletedProcess([], 1, b'License expired: private-session-token')
        with patch('steam_ci.command', return_value=['test']), patch('subprocess.run', return_value=result):
            with self.assertRaisesRegex(RuntimeError, 'new login') as failure:
                steam_ci.run_private(Path('/fixture'), [], 10)
        self.assertNotIn('private-session-token', str(failure.exception))

    def test_timeout_output_is_not_exposed(self):
        error = subprocess.TimeoutExpired(['test'], 1, output=b'private-session-token')
        with patch('steam_ci.command', return_value=['test']), patch('subprocess.run', side_effect=error):
            with self.assertRaisesRegex(RuntimeError, 'timed out') as failure:
                steam_ci.run_private(Path('/fixture'), [], 10)
        self.assertNotIn('private-session-token', str(failure.exception))

    def test_steamcmd_argument_injection_rejected(self):
        for value in ['+quit', 'user\n+quit', 'anonymous', '']:
            with self.assertRaises(ValueError):
                steam_ci.validate_username(value)

    def test_supported_build_matches_packager(self):
        self.assertEqual(references.BUILD, PACKAGE['GAME_BUILD'])


class DependencyDownloadTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.cache = Path(self.temporary.name)
        self.verify = PACKAGE['verified_archive']
        self.payload = b'verified dependency'
        self.expected = hashlib.sha256(self.payload).hexdigest()
        self.globals = patch.dict(self.verify.__globals__, CACHE=self.cache)
        self.globals.start()
        self.addCleanup(self.globals.stop)

    def download(self):
        return self.verify('loader.zip', 'https://example.com/loader.zip', self.expected)

    def test_timeout_retries_then_verifies(self):
        with patch('urllib.request.urlopen', side_effect=[TimeoutError(), io.BytesIO(self.payload)]) as request, \
                patch('time.sleep'):
            self.assertEqual(self.download().read_bytes(), self.payload)
        self.assertEqual(request.call_count, 2)
        self.assertFalse((self.cache / 'loader.download').exists())

    def test_partial_download_is_replaced_on_retry(self):
        partial = Mock()
        partial.__enter__ = Mock(return_value=partial)
        partial.__exit__ = Mock(return_value=False)
        partial.read.side_effect = [b'partial', TimeoutError()]
        with patch('urllib.request.urlopen', side_effect=[partial, io.BytesIO(self.payload)]), patch('time.sleep'):
            self.assertEqual(self.download().read_bytes(), self.payload)

    def test_retries_stop_and_remove_partial_file(self):
        with patch('urllib.request.urlopen', side_effect=TimeoutError()) as request, patch('time.sleep'):
            with self.assertRaisesRegex(RuntimeError, 'after 3 attempts'):
                self.download()
        self.assertEqual(request.call_count, 3)
        self.assertEqual(list(self.cache.iterdir()), [])

    def test_transient_http_error_retries(self):
        error = urllib.error.HTTPError('https://example.com', 503, 'Unavailable', {}, None)
        with patch('urllib.request.urlopen', side_effect=[error, io.BytesIO(self.payload)]), patch('time.sleep'):
            self.assertEqual(self.download().read_bytes(), self.payload)

    def test_permanent_http_error_does_not_retry(self):
        error = urllib.error.HTTPError('https://example.com', 403, 'Forbidden', {}, None)
        with patch('urllib.request.urlopen', side_effect=error) as request, patch('time.sleep'):
            with self.assertRaises(urllib.error.HTTPError):
                self.download()
        self.assertEqual(request.call_count, 1)
        self.assertEqual(list(self.cache.iterdir()), [])

    def test_checksum_failure_does_not_retry_or_stage(self):
        with patch('urllib.request.urlopen', return_value=io.BytesIO(b'wrong bytes')) as request:
            with self.assertRaisesRegex(ValueError, 'checksum mismatch'):
                self.download()
        self.assertEqual(request.call_count, 1)
        self.assertEqual(list(self.cache.iterdir()), [])


class BuildBudgetTests(unittest.TestCase):
    def test_local_packaging_keeps_guard(self):
        guard = PACKAGE['require_build_budget']
        check = Mock()
        with patch.dict(guard.__globals__, require_test_budget=check):
            guard(False)
        check.assert_called_once_with()

    def test_hosted_flag_rejects_local_and_self_hosted(self):
        guard = PACKAGE['require_build_budget']
        for environment in [{}, {'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'self-hosted'}]:
            with patch.dict(os.environ, environment, clear=True), self.assertRaisesRegex(RuntimeError, 'GitHub-hosted'):
                guard(True)
        with patch.dict(os.environ, GITHUB_ACTIONS='true', RUNNER_ENVIRONMENT='github-hosted'):
            guard(True)


if __name__ == '__main__':
    unittest.main()
