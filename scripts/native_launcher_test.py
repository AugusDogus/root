"""Drive the native launcher from existing isolated UI regressions."""
from contextlib import contextmanager
import json
from pathlib import Path
import subprocess
import time

from appimage_package import appimage_name
from launcher_release import VERSION

PROJECT = Path(__file__).resolve().parents[1]


class NativeLauncherSession:
    worker = None
    host = None
    client = None

    def __init__(self, lab):
        self.lab = lab
        self.launcher = None
        self.log = None

    def perform(self, action, _):
        if action != 'play':
            raise ValueError('Native UI driver only starts the normal launcher')
        for name in ('native-menu-ready', 'native-menu-screen', 'native-menu-interactive', 'session-status.json', 'session-command.txt'):
            (self.lab / 'client' / name).unlink(missing_ok=True)
        self.log = (self.lab / 'native-launcher-test.log').open('w')
        self.launcher = subprocess.Popen([str(PROJECT / 'dist' / appimage_name(VERSION)), '--appimage-extract-and-run', '--headless',
                                         '--data', str(self.lab), '--test-menu', 'steam-online-test'],
                                        stdout=self.log, stderr=subprocess.STDOUT)

    @property
    def invitations(self):
        try:
            return json.loads((self.lab / 'client/steam-status.json').read_text()).get('invitations', [])
        except (FileNotFoundError, json.JSONDecodeError):
            return []

    @property
    def endpoint(self):
        try:
            return json.loads((self.lab / 'host/results/server/endpoint.json').read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            return None

    def status(self):
        if self.launcher and self.launcher.poll() not in (None, 0):
            raise RuntimeError('Native launcher failed. Inspect native-launcher-test.log.')
        state = {'error': '', 'phase': 'starting'}
        path = self.lab / 'client/session-status.json'
        if path.exists():
            state = json.loads(path.read_text())
        state['saves'] = sorted(file.name for file in (self.lab / 'saves').glob('*.json'))
        return state

    def assert_launcher_exited(self):
        assert self.launcher is not None and self.launcher.wait(timeout=10) == 0
        assert not (self.lab / 'client/launcher-control.json').exists(), 'Control API credentials remain'

    def assert_running_game_is_protected(self):
        retry = subprocess.run([str(PROJECT / 'dist' / appimage_name(VERSION)), '--appimage-extract-and-run', '--headless', '--prepare-only', '--data', str(self.lab)],
                               capture_output=True, text=True, timeout=15)
        assert retry.returncode != 0 and 'already running' in retry.stderr, 'Launcher allowed setup while Root was running'

    def stop(self):
        try:
            if self.launcher is None:
                return
            if self.launcher.poll() is None:
                self.launcher.terminate()
                self.launcher.wait(timeout=30)
            status = self.lab / 'client/session-status.json'
            if not status.exists():
                return
            (self.lab / 'client/session-command.txt').write_text('quit')
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if json.loads(status.read_text())['phase'] == 'stopped':
                    assert not (self.lab / 'host/results/server/endpoint.json').exists(), 'Mod did not clean up its host'
                    return
                time.sleep(.25)
            raise RuntimeError('The mod did not stop its owned host on quit. The constrained test service will clean up remaining processes.')
        finally:
            if self.log is not None:
                self.log.close()


@contextmanager
def online_session(lab):
    session = NativeLauncherSession(lab)
    try:
        yield session
    finally:
        session.stop()
