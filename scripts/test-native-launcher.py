#!/usr/bin/env python3
"""Verify native package contents, then prepare real game copies from embedded files."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from steam import VERSION
from test_budget import require_test_budget
from test_diagnostics import Diagnostics
from appimage_package import appimage_name, validate_appimage


def main():
    require_test_budget()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--fresh', action='store_true')
    parser.add_argument('--data', type=Path, default=PROJECT / '.lab/native-launcher-fresh')
    args = parser.parse_args()
    diagnostics = Diagnostics.from_environment()
    executable = PROJECT / 'dist' / appimage_name(VERSION)
    command = [executable, '--appimage-extract-and-run']
    with diagnostics.stage('native package integrity'):
        validate_appimage(executable)
        assert subprocess.check_output([*command, '--version'], text=True).strip() == VERSION
        for platform, name in [('windows', 'Root Six Player.exe')]:
            with zipfile.ZipFile(PROJECT / f'dist/root-six-player-{VERSION}-{platform}.zip') as archive:
                assert archive.namelist() == [name]
                assert archive.read(name) == (PROJECT / 'dist' / name).read_bytes()
        for line in (PROJECT / 'dist/SHA256SUMS').read_text().splitlines():
            expected, name = line.split('  ', 1)
            assert hashlib.sha256((PROJECT / 'dist' / name).read_bytes()).hexdigest() == expected
        licenses = diagnostics.directory / 'dependency-notices'
        subprocess.run([*command, '--headless', '--licenses', licenses], check=True, timeout=20)
        assert (licenses / 'THIRD-PARTY-NOTICES.txt').stat().st_size > 1000
        assert (licenses / 'dependency-sources.zip').is_file()
    if not args.fresh:
        return
    lab = args.data.resolve()
    if lab.exists():
        raise FileExistsError(f'{lab} already exists. Preserve it and choose a new dedicated fresh-test directory.')
    with diagnostics.stage('fresh offline preparation and binding generation'):
        subprocess.run([*command, '--headless', '--prepare-only', '--data', lab], check=True, timeout=1250)
        manifest = json.loads((lab / 'prepared.json').read_text())
        assert manifest['version'] == VERSION
        for role in ('host', 'client'):
            root = lab / role
            assert (root / 'bindings-ready').exists()
            assert (root / 'game/BepInEx/interop/tuber-canis.dll').is_file()
            config = (root / 'game/BepInEx/config/BepInEx.cfg').read_text()
            assert 'UnityBaseLibrariesSource = 2022.3.62.zip' in config
            log = (root / 'game/BepInEx/LogOutput.log').read_text()
            assert 'Downloading unity base libraries' not in log
            assert 'Private launcher bindings ready' in log
    print(json.dumps({'status': 'passed', 'nativeExecutable': True, 'bundledDependencies': True,
                      'freshCopiesPrepared': True, 'localBindingsGenerated': True,
                      'unityDependencyDownload': False, 'gameMenuOpened': False}), flush=True)


if __name__ == '__main__':
    main()
