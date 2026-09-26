#!/usr/bin/env python3
"""Reproduce live Bash edits safely, without invoking Wine, Xvfb, or Root."""
import os
from pathlib import Path
import subprocess
import tempfile
import time

PROJECT = Path(__file__).resolve().parents[1]


def exercise(source):
    with tempfile.TemporaryDirectory(prefix='root-runner-test-') as temporary:
        root = Path(temporary)
        scripts = root / 'scripts'
        scripts.mkdir()
        runner = scripts / 'run-lab.sh'
        runner.write_text(source)
        lab = root / '.lab'
        (lab / 'game').mkdir(parents=True)
        (lab / 'compatdata/pfx').mkdir(parents=True)
        escaped = root / 'escaped'
        executable = lab / 'game/Root.exe'
        executable.write_text('#!/bin/sh\ntouch "$TEST_ESCAPED"\n')
        executable.chmod(0o700)
        mock = root / 'xvfb-run'
        mock.write_text('#!/bin/sh\ntouch "$TEST_READY"\nwhile [ ! -f "$TEST_RELEASE" ]; do sleep 0.02; done\n')
        mock.chmod(0o700)
        ready, release = root / 'ready', root / 'release'
        invitation = root / 'invitation.json'
        invitation.write_text('{"token":"test-only"}\n')
        env = {**os.environ, 'PATH': f'{root}:{os.environ["PATH"]}', 'ROOT_LAB_DIR': str(lab),
               'ROOT_CLIENT_CONNECTION': str(invitation),
               'TEST_READY': str(ready), 'TEST_RELEASE': str(release), 'TEST_ESCAPED': str(escaped)}
        process = subprocess.Popen(['bash', str(runner), 'client-probe'], env=env,
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        try:
            deadline = time.monotonic() + 5
            while not ready.exists():
                if process.poll() is not None or time.monotonic() > deadline:
                    raise AssertionError('Mock launch did not become ready')
                time.sleep(0.02)
            connection = lab / 'connection.json'
            assert connection.read_bytes() == invitation.read_bytes()
            assert connection.stat().st_mode & 0o777 == 0o600
            competing = root / 'competing.json'
            competing.write_text('{"token":"another-test"}\n')
            rejected = subprocess.run(['bash', str(runner), 'client-probe'],
                                      env={**env, 'ROOT_CLIENT_CONNECTION': str(competing)},
                                      capture_output=True, timeout=5)
            assert rejected.returncode == 1 and b'Another Root lab run is active' in rejected.stderr
            assert connection.read_bytes() == invitation.read_bytes(), 'Rejected launch must preserve active credentials'
            # Simulate inserting lines while Bash waits for its child. In the old
            # runner, the previous EOF now points directly at the executable.
            inserted_bytes = len(source) - source.rindex('"$lab_dir/game/Root.exe"')
            runner.write_text('#' + ' ' * (inserted_bytes - 2) + '\n' + source)
            release.touch()
            stdout, stderr = process.communicate(timeout=5)
            assert process.returncode == 0, (stdout, stderr)
            assert not connection.exists(), 'Finished launch must remove copied credentials'
            return escaped.exists()
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()


source = (PROJECT / 'scripts/run-lab.sh').read_text()
unwrapped = '#!/usr/bin/env bash\n' + source.split('run_lab() {\n', 1)[1].rsplit('exit 0\n}', 1)[0]
assert exercise(unwrapped), 'The harmless reproduction must expose the original escape'
assert not exercise(source), 'The runner must never execute trailing lines after a live edit'
print('PASS: live-edit escape reproduced; guarded runner stays isolated; invitation copying and cleanup respect the lab lock')
