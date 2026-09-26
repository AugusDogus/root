#!/usr/bin/env python3
"""Upload verified playtest packages as a private repository's draft release."""
import hashlib
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from steam import VERSION


def main():
    if subprocess.check_output(['git', 'status', '--porcelain'], cwd=PROJECT).strip():
        raise SystemExit('Commit the reviewed source before creating a release.')
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=PROJECT, text=True).strip()
    remote = subprocess.check_output(['gh', 'api', 'repos/AugusDogus/root-six-player/commits/main', '--jq', '.sha'], text=True).strip()
    if head != remote:
        raise SystemExit('Push the reviewed commit to main before creating a release.')
    dist = PROJECT / 'dist'
    names = ['Root Six Player.exe', f'root-six-player-{VERSION}-windows.zip', f'root-six-player-{VERSION}-linux.zip']
    expected = {}
    for line in (dist / 'SHA256SUMS').read_text().splitlines():
        digest, name = line.split('  ', 1)
        expected[name] = digest
    if set(expected) != set(names):
        raise SystemExit('Checksums do not match the current release. Rebuild the packages.')
    for name in names:
        if hashlib.sha256((dist / name).read_bytes()).hexdigest() != expected[name]:
            raise SystemExit(f'{name} changed after packaging. Rebuild before uploading.')
    with zipfile.ZipFile(dist / names[1]) as package:
        if package.namelist() != ['Root Six Player.exe']:
            raise SystemExit('The Windows package contains unexpected files.')
    with zipfile.ZipFile(dist / names[2]) as package:
        for name in package.namelist():
            if any(part in name.lower() for part in ('root.exe', 'gameassembly', 'global-metadata', 'checkpoint', 'connection.json', 'steam-config.json')):
                raise SystemExit('The Linux package contains game or private files. Rebuild it from the source allowlist.')
    notes = (PROJECT / 'RELEASE.md').read_text()
    with tempfile.TemporaryDirectory(prefix='root-release-') as temporary:
        body = Path(temporary) / 'notes.md'
        body.write_text(notes)
        # GitHub normalizes spaces in asset names. Stage explicit download names
        # so SHA256SUMS works directly against the downloaded files.
        assets = []
        sums = []
        for name in names:
            target = Path(temporary) / ('RootSixPlayer.exe' if name == names[0] else name)
            shutil.copy2(dist / name, target)
            assets.append(str(target))
            sums.append(f'{expected[name]}  {target.name}\n')
        checksums = Path(temporary) / 'SHA256SUMS'
        checksums.write_text(''.join(sums))
        subprocess.run(['gh', 'release', 'create', f'v{VERSION}', '--repo', 'AugusDogus/root-six-player',
            '--target', head, '--draft', '--prerelease', '--title', f'Root Six Player {VERSION}', '--notes-file', str(body),
            *assets, str(checksums)], check=True)


if __name__ == '__main__':
    main()
