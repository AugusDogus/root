"""Build the per-user Windows installer and inspect its embedded launcher."""
import os
from pathlib import Path
import struct
import subprocess
import tempfile


def installer_name(version):
    return f'RootSixPlayer-{version}-Setup.exe'


def validate_installer(path):
    with path.open('rb') as stream:
        header = stream.read(1 << 20)
    if len(header) < 64 or header[:2] != b'MZ':
        raise ValueError(f'{path.name} is not a Windows installer.')
    offset = struct.unpack_from('<I', header, 60)[0]
    if header[offset:offset + 4] != b'PE\0\0' or b'\xef\xbe\xad\xdeNullsoftInst' not in header:
        raise ValueError(f'{path.name} is not an NSIS installer.')


def build_installer(project, work, binary, version):
    target = work / installer_name(version)
    subprocess.run([os.environ.get('MAKENSIS', 'makensis'), '-V2',
                    f'-DVERSION={version}', f'-DLAUNCHER={binary.resolve()}',
                    f'-DOUTPUT={target.resolve()}', f'-DICON={project / "launcher/assets/launcher.ico"}',
                    str(project / 'packaging/windows/installer.nsi')], check=True)
    validate_installer(target)
    return target


def check_installer(artifact, binary, digest):
    validate_installer(artifact)
    with tempfile.TemporaryDirectory(prefix='root-installer-check-') as temporary:
        subprocess.run(['7z', 'x', '-y', f'-o{temporary}', str(artifact.resolve())],
                       check=True, stdout=subprocess.DEVNULL)
        extracted = Path(temporary)
        files = {str(path.relative_to(extracted)) for path in extracted.rglob('*') if path.is_file()}
        expected = {'RootSixPlayer.exe.new',
                    '$PLUGINSDIR/System.dll', '$PLUGINSDIR/nsDialogs.dll', '$PLUGINSDIR/modern-wizard.bmp'}
        # p7zip 16 extracts embedded files but not NSIS's generated uninstaller.
        # Newer 7-Zip reconstructs it. Native Windows CI exercises that executable.
        if files - {'Uninstall.exe.new'} != expected:
            raise ValueError(f'Installer has unexpected contents: {sorted(files)}')
        if digest(extracted / 'RootSixPlayer.exe.new') != digest(binary):
            raise ValueError('Installer contains a different launcher. Rebuild before publishing.')
