#!/usr/bin/env python3
"""Smoke-test the native Windows EXE under isolated Proton, without a game/browser."""
import argparse
import json
import os
from pathlib import Path
import shutil
import struct
import zipfile

from windows_launcher import PROJECT, windows_path, windows_process
from steam import VERSION
from test_diagnostics import Diagnostics


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unit-tests', type=Path, help='Also run a compiled Windows Go test executable')
    args = parser.parse_args()
    lab = PROJECT / '.lab/windows-native-exe-test'
    if args.unit_tests:
        test_lab = lab / 'unit-tests'
        test_lab.mkdir(parents=True, exist_ok=True)
        transcript = test_lab / 'test-output.txt'
        transcript.unlink(missing_ok=True)
        job = test_lab / 'run-tests.cmd'
        job.write_text('@echo off\n"' + windows_path(args.unit_tests) + '" -test.v -test.timeout=120s > "' + windows_path(transcript) + '" 2>&1\nexit /b %errorlevel%\n')
        with windows_process(test_lab, ['cmd.exe', '/d', '/c', windows_path(job)]) as process:
            code = process.wait(timeout=180)
        if code != 0 or not transcript.exists() or not transcript.read_text().rstrip().endswith('PASS'):
            raise RuntimeError(f'Windows unit tests failed ({code}). Inspect {test_lab / "process.log"}.')
        print('Windows Go tests and native progress lifecycle passed.', flush=True)
    isolated = lab / 'standalone'
    isolated.mkdir(parents=True, exist_ok=True)
    executable = isolated / 'Root Six Player.exe'
    shutil.copy2(PROJECT / 'dist/Root Six Player.exe', executable)
    assert sorted(file.name for file in isolated.iterdir()) == ['Root Six Player.exe']
    content = executable.read_bytes()
    pe_offset = struct.unpack_from('<I', content, 0x3c)[0]
    assert content[pe_offset:pe_offset + 4] == b'PE\0\0'
    assert struct.unpack_from('<H', content, pe_offset + 24 + 68)[0] == 2, 'Expected a windowed executable'
    with zipfile.ZipFile(PROJECT / f'dist/root-six-player-{VERSION}-windows.zip') as archive:
        assert archive.namelist() == ['Root Six Player.exe']
        assert archive.read('Root Six Player.exe') == content
    licenses = Diagnostics.from_environment().directory / 'windows-dependency-notices'
    with windows_process(lab, [str(executable), '--headless', '--licenses', windows_path(licenses)]) as process:
        assert process.wait(timeout=120) == 0
    assert (licenses / 'THIRD-PARTY-NOTICES.txt').stat().st_size > 1000
    assert (licenses / 'dependency-sources.zip').is_file()
    result = {'status': 'passed', 'standaloneWindowsExe': True, 'windowed': True,
              'adjacentFilesRequired': False, 'bundledDependencies': True,
              'cleanExit': True, 'browserInterfaceRemoved': True,
              'browserOpened': False, 'gameStarted': False, 'nativeWindowsTested': False,
              'scope': 'Native Go Windows executable under isolated Proton and Xvfb'}
    (PROJECT / 'results/windows-exe-test.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
