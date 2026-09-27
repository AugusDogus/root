"""Build/test the Windows launcher without using the desktop or default Wine prefix."""
from contextlib import contextmanager
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import urllib.request
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from main import lock_data
from steam import discover, proton_paths
from test_budget import require_test_budget


def windows_path(path: Path) -> str:
    return 'Z:' + str(path.resolve()).replace('/', '\\')


@contextmanager
def windows_process(lab: Path, arguments: list[str]):
    require_test_budget()
    with lock_data(lab):
        installation = discover()
        proton, runtime = proton_paths(installation)
        prefix = lab / 'compatdata'
        prefix.mkdir(exist_ok=True)
        env = {key: value for key, value in os.environ.items()
               if key not in ('DISPLAY', 'WAYLAND_DISPLAY', 'WAYLAND_SOCKET') and not key.startswith('ROOT_LAB_')}
        env.update(WINEPREFIX=str(prefix / 'pfx'), STEAM_COMPAT_DATA_PATH=str(prefix),
                   STEAM_COMPAT_CLIENT_INSTALL_PATH=str(installation.steam),
                   SDL_AUDIODRIVER='dummy', PULSE_SERVER=f'unix:{lab}/no-audio-socket',
                   WINEDLLOVERRIDES='winepulse.drv=d;winealsa.drv=d', PROTON_ENABLE_WAYLAND='0',
                   PROTON_USE_WINED3D='1', LIBGL_ALWAYS_SOFTWARE='1', LP_NUM_THREADS='2')
        command = ['xvfb-run', '--auto-servernum', '--server-args=-screen 0 1024x768x24 -nolisten tcp',
                   'nice', '-n', '10', str(runtime), '--verb=waitforexitandrun', '--',
                   str(proton / 'proton'), 'waitforexitandrun', *arguments]
        with (lab / 'process.log').open('w') as log:
            process = subprocess.Popen(command, env=env, cwd=PROJECT, stdout=log,
                                       stderr=subprocess.STDOUT, start_new_session=True)
        try:
            yield process
        finally:
            subprocess.run([str(proton / 'files/bin/wineserver'), '-k'], env=env,
                           capture_output=True, timeout=15)
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=15)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait(timeout=10)


def extract(archive: Path, target: Path):
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            if not (target / entry.filename).resolve().is_relative_to(target.resolve()):
                raise ValueError('Unsafe build dependency path.')
        bundle.extractall(target)


def build_windows() -> Path:
    lab = PROJECT / '.lab/windows-exe-build'
    runtime = lab / 'python'
    runtime.mkdir(parents=True, exist_ok=True)
    archive = PROJECT / '.lab/launcher-downloads/python-3.12.10-embed-amd64.zip'
    expected = (PROJECT / 'launcher/runtime-sha256.txt').read_text().split()[0]
    if hashlib.sha256(archive.read_bytes()).hexdigest() != expected:
        raise ValueError('Windows Python runtime checksum mismatch.')
    extract(archive, runtime)
    (runtime / 'python312._pth').write_text('python312.zip\n.\nLib/site-packages\nimport site\n')
    dependencies = [('pyinstaller', '6.16.0', 'win_amd64'), ('pyinstaller-hooks-contrib', '2025.9', 'any'),
                    ('altgraph', '0.17.4', 'any'), ('packaging', '25.0', 'any'), ('pefile', '2023.2.7', 'any'),
                    ('pywin32-ctypes', '0.2.3', 'any'), ('setuptools', '80.9.0', 'any')]
    wheels = lab / 'wheels'
    wheels.mkdir(exist_ok=True)
    for name, version, platform in dependencies:
        with urllib.request.urlopen(f'https://pypi.org/pypi/{name}/{version}/json', timeout=30) as response:
            metadata = json.load(response)
        wheel = next(item for item in metadata['urls'] if item['filename'].endswith(f'-{platform}.whl'))
        file = wheels / wheel['filename']
        if not file.exists():
            with urllib.request.urlopen(wheel['url'], timeout=60) as response:
                file.write_bytes(response.read())
        if hashlib.sha256(file.read_bytes()).hexdigest() != wheel['digests']['sha256']:
            raise ValueError(f'{name} build dependency checksum mismatch.')
        extract(file, runtime / 'Lib/site-packages')
    source = PROJECT / 'launcher'
    job = lab / 'build.py'
    job.write_text('import sys, traceback\n'
                   f'log = open({windows_path(lab / "build.log")!r}, "w", encoding="utf-8")\n'
                   'sys.stdout = log\nsys.stderr = log\n'
                   'try:\n import PyInstaller.__main__\n PyInstaller.__main__.run(sys.argv[1:])\n'
                   'except BaseException:\n traceback.print_exc(file=log)\n raise\n'
                   'finally:\n log.flush()\n')
    command = [str(runtime / 'python.exe'), windows_path(job), '--noconfirm', '--clean',
               '--onefile', '--windowed', '--name', 'Root Six Player',
               '--icon', windows_path(source / 'assets/launcher.ico'),
               '--paths', windows_path(source),
               '--distpath', windows_path(lab / 'dist'), '--workpath', windows_path(lab / 'work'),
               '--specpath', windows_path(lab), '--add-data', f'{windows_path(source / "web")};web',
               '--add-data', f'{windows_path(source / "payload")};payload',
               '--add-data', f'{windows_path(runtime / "LICENSE.txt")};licenses/Python',
               windows_path(source / 'desktop.py')]
    with windows_process(lab, command) as process:
        code = process.wait(timeout=600)
        if code != 0:
            raise RuntimeError(f'Windows executable build failed ({code}). Inspect {lab / "build.log"}.')
    executable = lab / 'dist/Root Six Player.exe'
    if not executable.is_file():
        raise RuntimeError('Windows executable was not produced.')
    return executable
