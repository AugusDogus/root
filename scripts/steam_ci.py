"""Isolated SteamCMD sessions for CI downloads and one-time authentication."""
import base64
import binascii
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile
import urllib.request

STEAMCMD_URL = 'https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz'


def validate_username(username):
    if not re.fullmatch(r'[A-Za-z0-9_][A-Za-z0-9_.@-]{0,127}', username) or username.lower() == 'anonymous':
        raise ValueError('Provide the Steam account login name for an account that owns Root.')
    return username


def decode_session(encoded):
    try:
        config = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error):
        raise ValueError('STEAM_CONFIG_VDF is invalid. Run scripts/setup-steam-ci.py again.') from None
    if not config or len(encoded) > 48 * 1024 or b'ConnectCache' not in config:
        raise ValueError('The saved Steam session is missing or incomplete. Run scripts/setup-steam-ci.py again.')
    return config


def bootstrap(home):
    steam = home / 'Steam'
    steam.mkdir(parents=True, mode=0o700)
    # Valve updates SteamCMD itself on startup. This is its official HTTPS
    # bootstrap distribution, not a third-party action handling our session.
    with urllib.request.urlopen(STEAMCMD_URL, timeout=60) as response, tempfile.TemporaryFile() as download:
        shutil.copyfileobj(response, download)
        download.seek(0)
        with tarfile.open(fileobj=download, mode='r:gz') as archive:
            archive.extractall(steam, filter='data')
    return steam


def write_session(home, config):
    directory = home / 'Steam/config'
    directory.mkdir(parents=True, exist_ok=True, mode=0o700)
    target = directory / 'config.vdf'
    with target.open('xb') as output:
        target.chmod(0o600)
        output.write(config)


def command(home, arguments):
    if shutil.which('bwrap') is None:
        raise RuntimeError('Install bubblewrap before running isolated SteamCMD authentication.')
    # Mask the real home without changing HOME. SteamCMD cannot read or alter
    # the desktop Steam session. Only this temporary home is writable.
    real_home = str(Path.home())
    return ['bwrap', '--ro-bind', '/', '/', '--bind', str(home), real_home,
            '--tmpfs', '/tmp', '--dev', '/dev', '--proc', '/proc', '--unshare-pid',
            '--die-with-parent', '--new-session', '--chdir', str(Path.home() / 'Steam'),
            './steamcmd.sh', *arguments]


def environment():
    # Inherit the existing home identity, never pass GitHub or Steam secrets
    # through the environment of SteamCMD or its subprocesses.
    return {key: value for key, value in os.environ.items()
            if key in {'PATH', 'HOME', 'USER', 'LOGNAME', 'LANG', 'LC_ALL'}}


def failure_reason(output):
    text = output.lower()
    if b'no subscription' in text:
        return 'This Steam account cannot download Root. Use an account that owns the game.'
    if any(marker in text for marker in (b'invalid password', b'account logon denied', b'license expired',
                                          b'logon failure', b'two-factor', b'steam guard', b'cached credentials not found')):
        return 'Steam requires a new login. Run scripts/setup-steam-ci.py to refresh the GitHub session secrets.'
    return 'SteamCMD did not complete the download. Retry once; if it still fails, run scripts/setup-steam-ci.py to refresh the session. Raw authentication logs were not published.'


def run_private(home, arguments, timeout):
    try:
        result = subprocess.run(command(home, arguments), env=environment(), stdin=subprocess.DEVNULL,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=timeout)
    except subprocess.TimeoutExpired:
        # TimeoutExpired includes captured output, so never propagate it.
        raise RuntimeError('SteamCMD timed out. Retry, or run scripts/setup-steam-ci.py if Steam needs a new login.') from None
    if result.returncode != 0:
        raise RuntimeError(failure_reason(result.stdout))
    return result.stdout


def login_arguments(username):
    return ['+@ShutdownOnFailedCommand', '1', '+@NoPromptForPassword', '1', '+login', username, '+quit']
