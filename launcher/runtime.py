"""Platform launch adapters for launcher-owned game copies."""
import json
import ctypes
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

from steam import APP_ID, Installation, proton_paths
from test_budget import require_test_budget


class GameProcess:
    def __init__(self, installation: Installation, lab: Path, mode: str, *, headless: bool,
                 save: Path | None = None, resume: bool = False, test_seed: int | None = None):
        if headless and sys.platform == 'linux':
            require_test_budget()
        self.lab = lab
        output = lab / 'results' / mode
        output.mkdir(parents=True, exist_ok=True)
        (output / 'endpoint.json').unlink(missing_ok=True)
        (lab / 'steam-status.json').unlink(missing_ok=True)
        (lab / 'native-menu-ready').unlink(missing_ok=True)
        (lab / 'native-menu-screen').unlink(missing_ok=True)
        (lab / 'native-board-settings.json').unlink(missing_ok=True)
        env = {key: value for key, value in os.environ.items() if not key.startswith('ROOT_LAB_')}
        env.update(ROOT_LAB_MODE=mode, ROOT_LAB_LIFETIME_SECONDS='43200', ROOT_FRIENDS_LAUNCHER='1',
                   SteamAppId=APP_ID, SteamGameId=APP_ID)
        if save is not None:
            env.update(ROOT_LAB_SAVE_FILE=str(save), ROOT_LAB_RESUME='1' if resume else '0')
        if test_seed is not None:
            if not headless or not 0 <= test_seed < 2**31:
                raise ValueError('A deterministic seed requires a headless test and an integer from 0 to 2147483647.')
            env['ROOT_LAB_TEST_SEED'] = str(test_seed)
        args = ['-logFile', str(output / 'player.log')]
        if os.environ.get('ROOT_FRIENDS_MUTE') == '1':
            args.append('-noaudio')
        if mode in ('server', 'bootstrap'):
            args += ['-batchmode', '-nographics', '-noaudio']
        else:
            args += ['-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '800']
        self.proton = None
        self.env = env
        if sys.platform == 'win32':
            if headless:
                raise ValueError('Headless development launches require the Linux Xvfb adapter.')
            command = [str(lab / 'game/Root.exe'), *args]
            options = {'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP}
        elif sys.platform == 'linux':
            self.proton, runtime = proton_paths(installation)
            prefix = lab / 'compatdata'
            prefix.mkdir(exist_ok=True)
            (lab / 'shadercache').mkdir(exist_ok=True)
            env.update(WINEPREFIX=str(prefix / 'pfx'), STEAM_COMPAT_DATA_PATH=str(prefix),
                       STEAM_COMPAT_CLIENT_INSTALL_PATH=str(installation.steam),
                       STEAM_COMPAT_SHADER_PATH=str(lab / 'shadercache'), WINEDLLOVERRIDES='winhttp=n,b')
            command = [str(runtime), '--verb=waitforexitandrun', '--', str(self.proton / 'proton'),
                       'waitforexitandrun', str(lab / 'game/Root.exe'), *args]
            if headless:
                if shutil.which('xvfb-run') is None:
                    raise ValueError('Headless testing requires xvfb-run.')
                for key in ('DISPLAY', 'WAYLAND_DISPLAY', 'WAYLAND_SOCKET'):
                    env.pop(key, None)
                env.update(ROOT_FRIENDS_LAUNCHER='0', SDL_AUDIODRIVER='dummy',
                           PULSE_SERVER=f'unix:{lab}/no-audio-socket',
                           WINEDLLOVERRIDES='winhttp=n,b;winepulse.drv=d;winealsa.drv=d',
                           PROTON_ENABLE_WAYLAND='0', PROTON_USE_WINED3D='1',
                           LIBGL_ALWAYS_SOFTWARE='1', LP_NUM_THREADS='2')
                command = ['xvfb-run', '--auto-servernum', '--server-args=-screen 0 1280x800x24 -nolisten tcp',
                           'nice', '-n', '10', *command, '-noaudio', '-job-worker-count', '2']
            options = {'start_new_session': True}
        else:
            raise ValueError('This test launcher supports Windows and Linux only.')
        with (output / 'launch.log').open('w') as log:
            # PyInstaller's DLL search path belongs to Python, not the child game.
            frozen_windows = sys.platform == 'win32' and getattr(sys, 'frozen', False)
            previous = ctypes.create_unicode_buffer(32768)
            if frozen_windows:
                ctypes.windll.kernel32.GetDllDirectoryW(len(previous), previous)
                if not ctypes.windll.kernel32.SetDllDirectoryW(None):
                    raise OSError('Could not prepare Windows to start Root. Close and reopen the launcher.')
            try:
                self.process = subprocess.Popen(command, cwd=lab / 'game', env=env,
                                                stdout=log, stderr=subprocess.STDOUT, **options)
            finally:
                if frozen_windows:
                    ctypes.windll.kernel32.SetDllDirectoryW(previous.value or None)

    def endpoint(self) -> dict:
        file = self.lab / 'results/server/endpoint.json'
        deadline = time.monotonic() + 600
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(f'Host exited before readiness. See {self.lab / "results/server/launch.log"}. Your Steam installation was not changed.')
            try:
                return json.loads(file.read_text())
            except (FileNotFoundError, json.JSONDecodeError):
                time.sleep(0.25)
        raise TimeoutError(f'Host did not start in ten minutes. See {self.lab / "results/server/launch.log"}.')

    def close(self):
        if sys.platform == 'win32':
            if self.process.poll() is None:
                subprocess.run(['taskkill', '/PID', str(self.process.pid), '/T', '/F'], capture_output=True,
                               creationflags=subprocess.CREATE_NO_WINDOW, timeout=20)
        else:
            # Kill only Wine processes in this launcher's dedicated prefix.
            if self.proton is not None:
                subprocess.run([str(self.proton / 'files/bin/wineserver'), '-k'], env=self.env,
                               capture_output=True, timeout=15)
            try:
                os.killpg(self.process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            if sys.platform == 'linux':
                os.killpg(self.process.pid, signal.SIGKILL)
            else:
                self.process.kill()
            self.process.wait(timeout=10)
        (self.lab / 'connection.json').unlink(missing_ok=True)
        (self.lab / 'steam-config.json').unlink(missing_ok=True)
        (self.lab / 'steam-status.json').unlink(missing_ok=True)
        (self.lab / 'results/server/endpoint.json').unlink(missing_ok=True)


def bootstrap(installation: Installation, data: Path, headless: bool, progress):
    for step, role in enumerate(('host', 'client'), 1):
        lab = data / role
        marker = lab / 'bindings-ready'
        if marker.exists() and (lab / 'game/BepInEx/interop/tuber-canis.dll').is_file():
            continue
        progress(f'Finishing setup ({step} of 2). This can take a few minutes…')
        game = GameProcess(installation, lab, 'bootstrap', headless=headless)
        try:
            code = game.process.wait(timeout=600)
            log = lab / 'game/BepInEx/LogOutput.log'
            if code != 0 or not log.is_file() or 'Private launcher bindings ready' not in log.read_text():
                raise RuntimeError(f'{role.capitalize()} preparation did not complete. Inspect {lab / "results/bootstrap"} and retry Prepare.')
            marker.touch()
        finally:
            game.close()
    progress('Ready to host or join.')
