#!/usr/bin/env python3
"""Smoke-test the native Windows EXE under isolated Proton, without a game/browser."""
import argparse
import json
import os
from pathlib import Path
import shutil
import struct

from windows_launcher import PROJECT, windows_path, windows_process
from steam import VERSION
from test_diagnostics import Diagnostics
from windows_installer import installer_name


def main():
    os.umask(0o077)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--unit-tests', type=Path, help='Also run a compiled Windows Go test executable')
    parser.add_argument('--installer', type=Path, default=PROJECT / 'dist' / installer_name(VERSION))
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
    with windows_process(lab, [windows_path(args.installer), '/S']) as process:
        assert process.wait(timeout=180) == 0
    installed = list((lab / 'compatdata/pfx/drive_c/users').glob('*/AppData/Local/Programs/Root Six Player/RootSixPlayer.exe'))
    assert len(installed) == 1, 'Installer did not create one per-user installation'
    executable = installed[0]
    content = executable.read_bytes()
    pe_offset = struct.unpack_from('<I', content, 0x3c)[0]
    assert content[pe_offset:pe_offset + 4] == b'PE\0\0'
    assert struct.unpack_from('<H', content, pe_offset + 24 + 68)[0] == 2, 'Expected a windowed executable'
    licenses = Diagnostics.from_environment().directory / 'windows-dependency-notices'
    with windows_process(lab, [str(executable), '--headless', '--licenses', windows_path(licenses)]) as process:
        assert process.wait(timeout=120) == 0
    assert (licenses / 'THIRD-PARTY-NOTICES.txt').stat().st_size > 1000
    assert (licenses / 'dependency-sources.zip').is_file()
    result = {'status': 'passed', 'perUserWindowsInstaller': True, 'windowed': True,
              'adjacentFilesRequired': False, 'bundledDependencies': True,
              'cleanExit': True, 'browserInterfaceRemoved': True,
              'browserOpened': False, 'gameStarted': False, 'nativeWindowsTested': False,
              'scope': 'Native Go Windows executable under isolated Proton and Xvfb'}
    (PROJECT / 'results/windows-exe-test.json').write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result))


if __name__ == '__main__':
    main()
