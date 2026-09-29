"""Validate and stage the exact player assets used by local and CI releases."""
import argparse
import hashlib
from pathlib import Path
import re
import shutil
import sys
from appimage_package import appimage_name, validate_appimage
from windows_installer import installer_name, validate_installer

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from steam import VERSION


def validate_version(project, version, tag=None):
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise ValueError('Launcher version must be a three-part numeric version.')
    if tag is not None and tag != f'v{version}':
        raise ValueError(f'Release tag must be v{version}; got {tag}.')
    declarations = {
        'launcher-native/metadata.go': f'const Version = "{version}"',
        'scripts/package-native-launcher.py': f"VERSION = '{version}'",
        'engine-probe/Plugin.cs': f'"Root Engine Probe", "{version}")',
        'RELEASE.md': f'# Root Six Player {version}\n',
    }
    for name, declaration in declarations.items():
        if declaration not in (project / name).read_text():
            raise ValueError(f'{name} does not declare release version {version}. Update versions together.')


def stage_release(dist, destination, version):
    names = [installer_name(version), appimage_name(version)]
    lines = [line.split('  ', 1) for line in (dist / 'SHA256SUMS').read_text().splitlines()]
    if len(lines) != len(names) or any(len(line) != 2 for line in lines):
        raise ValueError('Release checksums must describe exactly the two player assets.')
    expected = {name: checksum for checksum, name in lines}
    if set(expected) != set(names):
        raise ValueError('Release checksums do not match this version. Rebuild the packages.')
    for name in names:
        with (dist / name).open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest() != expected[name]:
                raise ValueError(f'{name} changed after packaging. Rebuild before uploading.')
    validate_installer(dist / names[0])
    validate_appimage(dist / names[1])
    destination.mkdir(parents=True, exist_ok=False)
    sums = []
    for name in names:
        target = destination / name
        shutil.copyfile(dist / name, target)
        if name == names[1]:
            target.chmod(0o755)
        sums.append(f'{expected[name]}  {target.name}\n')
    (destination / 'SHA256SUMS').write_text(''.join(sums))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--tag')
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    validate_version(PROJECT, VERSION, args.tag)
    if args.output:
        stage_release(PROJECT / 'dist', args.output, VERSION)
    print(VERSION)


if __name__ == '__main__':
    main()
