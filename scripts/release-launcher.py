#!/usr/bin/env python3
"""Upload verified playtest packages as a private repository's draft release."""
from pathlib import Path
import subprocess
import sys
import tempfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from steam import VERSION
from launcher_release import stage_release, validate_version


def main():
    if subprocess.check_output(['git', 'status', '--porcelain'], cwd=PROJECT).strip():
        raise SystemExit('Commit the reviewed source before creating a release.')
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=PROJECT, text=True).strip()
    remote = subprocess.check_output(['gh', 'api', 'repos/AugusDogus/root-six-player/commits/main', '--jq', '.sha'], text=True).strip()
    if head != remote:
        raise SystemExit('Push the reviewed commit to main before creating a release.')
    validate_version(PROJECT, VERSION)
    notes = (PROJECT / 'RELEASE.md').read_text()
    with tempfile.TemporaryDirectory(prefix='root-release-') as temporary:
        body = Path(temporary) / 'notes.md'
        body.write_text(notes)
        assets = Path(temporary) / 'assets'
        stage_release(PROJECT / 'dist', assets, VERSION)
        subprocess.run(['gh', 'release', 'create', f'v{VERSION}', '--repo', 'AugusDogus/root-six-player',
            '--target', head, '--draft', '--prerelease', '--title', f'Root Six Player {VERSION}', '--notes-file', str(body),
            *[str(path) for path in sorted(assets.iterdir())]], check=True)


if __name__ == '__main__':
    main()
