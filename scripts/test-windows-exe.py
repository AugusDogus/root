#!/usr/bin/env python3
"""Test the standalone EXE on an isolated display without opening a browser or game."""
import json
import os
from pathlib import Path
import shutil
import struct
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile

from windows_launcher import PROJECT, windows_path, windows_process


def main():
    os.umask(0o077)
    lab = PROJECT / '.lab/windows-exe-test'
    isolated = lab / 'standalone'
    isolated.mkdir(parents=True, exist_ok=True)
    executable = isolated / 'Root Six Player.exe'
    shutil.copy2(PROJECT / 'dist/Root Six Player.exe', executable)
    assert sorted(file.name for file in isolated.iterdir()) == ['Root Six Player.exe']
    content = executable.read_bytes()
    pe_offset = struct.unpack_from('<I', content, 0x3c)[0]
    assert content[pe_offset:pe_offset + 4] == b'PE\0\0'
    assert struct.unpack_from('<H', content, pe_offset + 24 + 68)[0] == 2, 'Expected a windowed executable'
    with zipfile.ZipFile(PROJECT / 'dist/root-six-player-0.1.0-windows.zip') as archive:
        assert archive.namelist() == ['Root Six Player.exe']
        assert archive.read('Root Six Player.exe') == content
    data = lab / 'data'
    data.mkdir(exist_ok=True)
    ready = data / 'launcher-url.txt'
    ready.unlink(missing_ok=True)
    with windows_process(lab, [str(executable), '--no-browser', '--data', windows_path(data)]) as process:
        deadline = time.monotonic() + 120
        while not ready.exists():
            if process.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError('Packaged launcher did not open. Inspect the isolated prefix’s AppData/Local/RootSixPlayer/launcher.log.')
            time.sleep(0.25)
        url = urllib.parse.urlsplit(ready.read_text())
        origin = f'http://127.0.0.1:{url.port}'
        headers = {'Authorization': 'Bearer ' + url.fragment, 'Origin': origin}

        def status():
            with urllib.request.urlopen(urllib.request.Request(origin + '/api/status', headers=headers), timeout=5) as response:
                return json.load(response)

        def action(name, values):
            request = urllib.request.Request(origin + '/api/' + name, data=json.dumps(values).encode(),
                                             headers={**headers, 'Content-Type': 'application/json'})
            with urllib.request.urlopen(request, timeout=5) as response:
                assert response.status == 202

        state = status()
        assert not state['active'] and not state['prepared']
        for route, filename in (('/', 'index.html'), ('/app.js', 'app.js'), ('/style.css', 'style.css')):
            with urllib.request.urlopen(origin + route, timeout=5) as response:
                assert response.read() == (PROJECT / 'launcher/web' / filename).read_bytes()
        html = (PROJECT / 'launcher/web/index.html').read_text()
        assert 'Cloudflare' not in html and 'runtime testing' not in html and 'bindings' not in html
        try:
            urllib.request.urlopen(origin + '/api/status', timeout=5)
            raise AssertionError('Unauthenticated launcher access was accepted')
        except urllib.error.HTTPError as error:
            assert error.code == 403
            error.close()
        # No Root process is started: this deliberately selects a nonexistent folder.
        action('prepare', {'game': windows_path(lab / 'missing-game')})
        deadline = time.monotonic() + 10
        while (state := status())['busy'] and time.monotonic() < deadline:
            time.sleep(0.1)
        assert not state['busy'] and state['error'] and not state['active']
        assert not (data / 'host/game').exists()
        action('quit', {})
        code = process.wait(timeout=30)
        assert code == 0, f'Packaged launcher exited with {code}'
    assert not ready.exists()
    result = {'status': 'passed', 'standaloneWindowsExe': True, 'windowed': True,
              'adjacentFilesRequired': False, 'uiAssets': True, 'apiAuthentication': True,
              'setupErrorDisplayed': True, 'cleanExit': True, 'markdownInWindowsPackage': False,
              'browserOpened': False, 'gameStarted': False, 'nativeWindowsTested': False,
              'scope': 'Actual Windows executable under isolated Proton and Xvfb'}
    (PROJECT / 'results/windows-exe-test.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
