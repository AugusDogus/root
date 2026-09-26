#!/usr/bin/env python3
"""Build mod-only Windows and Linux playtest ZIPs from an explicit file allowlist."""
import hashlib
import io
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile
from windows_launcher import build_windows
from steam import BUILD, VERSION

PROJECT = Path(__file__).resolve().parents[1]
SOURCE = PROJECT / 'launcher'
DIST = PROJECT / 'dist'
CACHE = PROJECT / '.lab/launcher-downloads'


def fetch(url):
    with urllib.request.urlopen(url, timeout=60) as response:
        return response.read()


def main():
    CACHE.mkdir(parents=True, exist_ok=True)
    DIST.mkdir(exist_ok=True)
    # Pure Python dependency, bundled so friends do not need pip.
    metadata = json.loads(fetch('https://pypi.org/pypi/websocket-client/1.8.0/json'))
    wheel = next(item for item in metadata['urls'] if item['filename'].endswith('py3-none-any.whl'))
    content = fetch(wheel['url'])
    if hashlib.sha256(content).hexdigest() != wheel['digests']['sha256']:
        raise ValueError('websocket-client checksum mismatch')
    vendor = SOURCE / 'vendor'
    vendor.mkdir(exist_ok=True)
    with zipfile.ZipFile(io.BytesIO(content)) as archive:
        for entry in archive.infolist():
            if entry.filename.startswith('websocket/') or entry.filename.endswith('LICENSE'):
                if not (vendor / entry.filename).resolve().is_relative_to(vendor.resolve()):
                    raise ValueError('Unsafe dependency path')
                archive.extract(entry, vendor)
    plugin = PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll'
    payload = SOURCE / 'payload'
    payload.mkdir(exist_ok=True)
    shutil.copy2(plugin, payload / plugin.name)
    (payload / 'manifest.json').write_text(json.dumps({'build': BUILD, 'version': VERSION,
        'plugin_sha256': hashlib.sha256(plugin.read_bytes()).hexdigest()}))
    python_zip = CACHE / 'python-3.12.10-embed-amd64.zip'
    if not python_zip.exists():
        python_zip.write_bytes(fetch('https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip'))
    # Pin the official runtime artifact; subsequent builds reject modified cache files.
    runtime_digest = hashlib.sha256(python_zip.read_bytes()).hexdigest()
    sources = SOURCE / 'runtime-sha256.txt'
    if not sources.exists():
        sources.write_text(runtime_digest + '  python-3.12.10-embed-amd64.zip\n')
    if sources.read_text().split()[0] != runtime_digest:
        raise ValueError('Windows Python runtime checksum mismatch')
    executable = build_windows()
    shutil.copy2(executable, DIST / 'Root Six Player.exe')
    # Player packages contain only executable code, runtime assets and licenses.
    files = [file for file in SOURCE.rglob('*') if file.is_file() and
             '__pycache__' not in file.parts and 'tests' not in file.relative_to(SOURCE).parts and
             (file.parent == SOURCE and file.suffix == '.py' or
              file.relative_to(SOURCE).parts[0] in ('web', 'payload', 'vendor')) and
             file.suffix not in ('.pyc', '.pyo', '.md')]
    for platform in ('windows', 'linux'):
        target = DIST / f'root-six-player-{VERSION}-{platform}.zip'
        with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED) as archive:
            if platform == 'windows':
                archive.write(executable, 'Root Six Player.exe')
            else:
                for file in files:
                    archive.write(file, 'RootSixPlayer/launcher/' + str(file.relative_to(SOURCE)))
                entry = zipfile.ZipInfo('RootSixPlayer/Play.sh')
                entry.external_attr = 0o100755 << 16
                archive.writestr(entry, '#!/bin/sh\ncd -- "$(dirname -- "$0")" || exit 1\nexec python3 launcher/desktop.py "$@"\n')
        with zipfile.ZipFile(target) as archive:
            for name in archive.namelist():
                if any(part in name.lower() for part in ('root.exe', 'gameassembly', 'global-metadata', 'connection.json',
                                                        'endpoint.json', 'steam-config.json', 'steam-status.json', 'checkpoint')):
                    raise ValueError(f'Forbidden game or private file in package: {name}')
        print(f'{target}: {target.stat().st_size / 1024**2:.1f} MiB')
    outputs = [DIST / 'Root Six Player.exe', *sorted(DIST.glob(f'root-six-player-{VERSION}-*.zip'))]
    (DIST / 'SHA256SUMS').write_text(''.join(f'{hashlib.sha256(file.read_bytes()).hexdigest()}  {file.name}\n'
                                         for file in outputs))


if __name__ == '__main__':
    main()
