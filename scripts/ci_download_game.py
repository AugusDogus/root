#!/usr/bin/env python3
"""Download the supported Root build with a private saved SteamCMD session."""
import argparse
import os
from pathlib import Path
import shutil
import tempfile

from ci_generate_references import PACKAGE, PROJECT, validate_game
from steam_ci import bootstrap, decode_session, run_private, validate_username, write_session


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=PROJECT / '.lab/steam-game')
    parser.add_argument('--github-hosted', action='store_true')
    args = parser.parse_args()
    PACKAGE['require_build_budget'](args.github_hosted)
    username_value = os.environ.pop('STEAM_USERNAME', '')
    if not username_value:
        raise RuntimeError('STEAM_USERNAME is missing. Run scripts/setup-steam-ci.py in your terminal to configure CI authentication.')
    username = validate_username(username_value)
    config = decode_session(os.environ.pop('STEAM_CONFIG_VDF', ''))
    if args.output.exists():
        raise FileExistsError('The Steam download destination already exists. Use a fresh directory.')
    with tempfile.TemporaryDirectory(prefix='root-steam-ci-') as temporary:
        home = Path(temporary) / 'home'
        bootstrap(home)
        write_session(home, config)
        print('Downloading Root through an isolated SteamCMD session.', flush=True)
        run_private(home, ['+@ShutdownOnFailedCommand', '1', '+@NoPromptForPassword', '1',
                           '+@sSteamCmdForcePlatformType', 'windows', '+force_install_dir',
                           str(Path.home() / 'RootDownload'), '+login', username,
                           '+app_info_update', '1', '+app_update', '965580', 'validate', '+quit'], timeout=900)
        game = home / 'RootDownload'
        manifest = game / 'steamapps/appmanifest_965580.acf'
        if not manifest.is_file():
            raise RuntimeError('Steam did not produce the Root installation manifest. Refresh the saved session with scripts/setup-steam-ci.py and retry.')
        validate_game(game, manifest)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(game), args.output)
    print('Downloaded and verified the supported Root build. Temporary Steam credentials removed.', flush=True)


if __name__ == '__main__':
    main()
