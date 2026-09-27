#!/usr/bin/env python3
"""Log in privately, verify session reuse, and store it in GitHub Actions secrets."""
import argparse
import base64
from pathlib import Path
import subprocess
import sys
import tempfile

from steam_ci import bootstrap, command, decode_session, environment, login_arguments, run_private, validate_username, write_session


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repo', default='AugusDogus/root-six-player')
    args = parser.parse_args()
    if not sys.stdin.isatty():
        raise SystemExit('Run this setup in your own terminal so Steam can prompt privately for your password and Steam Guard approval.')
    result = subprocess.run(['gh', 'auth', 'status'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if result.returncode != 0:
        raise SystemExit('Sign into GitHub with gh auth login, then rerun Steam CI setup.')
    print('This signs into SteamCMD and uploads its reusable session to GitHub Actions secrets.')
    print('Use an account that owns Root. Do this when that account is not being used to play.')
    print('Your password and Steam Guard code stay in this terminal; neither is uploaded.')
    username = validate_username(input('Steam account login name: ').strip())
    with tempfile.TemporaryDirectory(prefix='root-steam-login-') as temporary:
        home = Path(temporary) / 'login'
        bootstrap(home)
        result = subprocess.run(command(home, ['+login', username, '+quit']), env=environment())
        if result.returncode != 0:
            raise SystemExit('Steam login failed. No GitHub secrets were changed.')
        config_path = home / 'Steam/config/config.vdf'
        if not config_path.is_file():
            raise SystemExit('Steam did not save a reusable session. No GitHub secrets were changed.')
        encoded = base64.b64encode(config_path.read_bytes())
        config = decode_session(encoded)
        # Only transfer the same file CI will receive, not other local login state.
        verify_home = Path(temporary) / 'verify'
        bootstrap(verify_home)
        write_session(verify_home, config)
        # ShutdownOnFailedCommand makes login failure a nonzero exit. Steam's
        # human-readable success output can contain interleaved timing messages.
        run_private(verify_home, login_arguments(username), timeout=90)
        # Use the refreshed copy after the successful reuse check.
        encoded = base64.b64encode((verify_home / 'Steam/config/config.vdf').read_bytes())
        decode_session(encoded)
        for name, value in [('STEAM_USERNAME', username.encode()), ('STEAM_CONFIG_VDF', encoded)]:
            result = subprocess.run(['gh', 'secret', 'set', name, '--repo', args.repo], input=value,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            if result.returncode != 0:
                raise SystemExit(f'Could not set {name}. Check GitHub repository access and rerun setup; an earlier secret may already have been updated.')
    print('Steam CI secrets configured. Temporary login files removed.')


if __name__ == '__main__':
    main()
