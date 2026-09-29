"""Build and inspect the Linux AppImage using Compositor's pinned tooling."""
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile

TOOL_URL = 'https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-x86_64.AppImage'
TOOL_SHA256 = 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0'
RUNTIME_URL = 'https://github.com/AppImage/type2-runtime/releases/download/20251108/runtime-x86_64'
RUNTIME_SHA256 = '2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d'


def appimage_name(version):
    return f'RootSixPlayer-{version}-x86_64.AppImage'


def validate_appimage(path):
    with path.open('rb') as stream:
        header = stream.read(64)
    if (len(header) != 64 or header[:7] != b'\x7fELF\x02\x01\x01'
            or header[8:11] != b'AI\x02' or struct.unpack_from('<H', header, 18)[0] != 62):
        raise ValueError(f'{path.name} is not an x86_64 type-2 AppImage.')


def build_appimage(project, work, binary, version, verified_archive):
    tool = verified_archive('appimagetool-1.9.1-x86_64.AppImage', TOOL_URL, TOOL_SHA256)
    runtime = verified_archive('appimage-runtime-20251108-x86_64', RUNTIME_URL, RUNTIME_SHA256)
    tool.chmod(0o755)
    target = work / appimage_name(version)
    with tempfile.TemporaryDirectory(prefix='appimage-', dir=work) as temporary:
        appdir = Path(temporary) / 'RootSixPlayer.AppDir'
        (appdir / 'usr/bin').mkdir(parents=True)
        shutil.copyfile(binary, appdir / 'usr/bin/RootSixPlayer')
        (appdir / 'usr/bin/RootSixPlayer').chmod(0o755)
        for name in ['AppRun', 'RootSixPlayer.desktop', 'root-six-player.png']:
            shutil.copyfile(project / 'packaging/linux' / name, appdir / name)
        (appdir / 'AppRun').chmod(0o755)
        shutil.copyfile(appdir / 'root-six-player.png', appdir / '.DirIcon')
        subprocess.run(['desktop-file-validate', str(appdir / 'RootSixPlayer.desktop')], check=True)
        # Explicit runtime prevents appimagetool from fetching an unpinned one.
        subprocess.run([str(tool), '--no-appstream', '--comp', 'zstd',
                        '--mksquashfs-opt', '-processors', '--mksquashfs-opt', '2',
                        '--runtime-file', str(runtime), str(appdir), str(target)],
                       env=dict(os.environ, APPIMAGE_EXTRACT_AND_RUN='1', ARCH='x86_64', VERSION=version), check=True)
    target.chmod(0o755)
    validate_appimage(target)
    return target


def check_appimage(artifact, binary, version, digest):
    """Inspect the packaged bytes and exercise the FUSE-free entry point."""
    validate_appimage(artifact)
    artifact = artifact.resolve()
    with tempfile.TemporaryDirectory(prefix='root-appimage-check-') as temporary:
        directory = Path(temporary)
        subprocess.run([str(artifact), '--appimage-extract'], cwd=directory, check=True, stdout=subprocess.DEVNULL)
        appdir = directory / 'squashfs-root'
        expected = {'AppRun', 'RootSixPlayer.desktop', 'root-six-player.png', '.DirIcon', 'usr/bin/RootSixPlayer'}
        actual = {str(path.relative_to(appdir)) for path in appdir.rglob('*') if not path.is_dir()}
        if actual != expected or any(path.is_symlink() for path in appdir.rglob('*')):
            raise ValueError('AppImage contains unexpected files or symlinks. Rebuild before publishing.')
        for name in expected:
            if not (appdir / name).stat().st_size:
                raise ValueError(f'AppImage contains an empty {name}.')
        if digest(appdir / 'usr/bin/RootSixPlayer') != digest(binary):
            raise ValueError('AppImage contains a different launcher executable. Rebuild before publishing.')
        for name in ['AppRun', 'usr/bin/RootSixPlayer']:
            if not os.access(appdir / name, os.X_OK):
                raise ValueError(f'AppImage lost executable permission on {name}.')
        subprocess.run(['desktop-file-validate', str(appdir / 'RootSixPlayer.desktop')], check=True)
        found = subprocess.check_output([str(artifact), '--appimage-extract-and-run', '--headless', '--version'], text=True).strip()
        if found != version:
            raise ValueError(f'AppImage reports version {found!r}; expected {version}.')
        subprocess.run([str(artifact), '--appimage-extract-and-run', '--headless', '--licenses', str(directory / 'notices')], check=True)
        for name in ['THIRD-PARTY-NOTICES.txt', 'dependency-sources.zip']:
            if not (directory / 'notices' / name).stat().st_size:
                raise ValueError(f'AppImage could not export {name}.')
