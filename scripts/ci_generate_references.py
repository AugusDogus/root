#!/usr/bin/env python3
"""Generate compilation references from a verified Root installation, without running it."""
import argparse
from pathlib import Path, PurePosixPath
import runpy
import shutil
import subprocess
import sys
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from steam import APP_ID, BUILD, vdf_strings

PACKAGE = runpy.run_path(str(PROJECT / 'scripts/package-native-launcher.py'))


def validate_game(game, manifest):
    values = vdf_strings(manifest)
    if values.get('appid') != APP_ID or values.get('buildid') != BUILD:
        raise ValueError(f'Expected Root Steam build {BUILD}; found {values.get("buildid", "unknown")}. Review the game update before changing the supported build.')
    for name in ['Root.exe', 'GameAssembly.dll', 'Root_Data/globalgamemanagers',
                 'Root_Data/il2cpp_data/Metadata/global-metadata.dat']:
        if not (game / name).is_file():
            raise ValueError(f'The downloaded Root installation is missing {name}. Verify the SteamCMD download.')


def prepare(output):
    if output.exists():
        raise FileExistsError('The reference output already exists. Use a fresh output directory.')
    PACKAGE['CACHE'].mkdir(parents=True, exist_ok=True)
    loader = PACKAGE['verified_archive']('loader.zip', PACKAGE['LOADER_URL'], PACKAGE['LOADER_SHA256'])
    unity = PACKAGE['verified_archive']('unity.zip', PACKAGE['UNITY_URL'], PACKAGE['UNITY_SHA256'])
    with zipfile.ZipFile(loader) as archive:
        for name in archive.namelist():
            parts = PurePosixPath(name).parts
            # The loader places these managed dependencies beside its bundled
            # runtime. Our standalone .NET tool needs them among its references.
            if len(parts) == 2 and parts[0] == 'dotnet' and parts[1].startswith('Microsoft.Extensions.') and parts[1].endswith('.dll'):
                parts = ('BepInEx', 'core', parts[1])
            if len(parts) == 3 and parts[:2] == ('BepInEx', 'core') and parts[2].endswith('.dll') and '\\' not in name:
                path = output.joinpath(*parts)
                path.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(name) as source, path.open('xb') as target:
                    shutil.copyfileobj(source, target)
    libraries = output / 'BepInEx/unity-libs'
    libraries.mkdir(parents=True)
    version = PACKAGE['UNITY_VERSION']
    shutil.copyfile(unity, libraries / f'{version}.zip')
    config = output / 'BepInEx/config'
    config.mkdir()
    (config / 'BepInEx.cfg').write_text(f'[IL2CPP]\nUnityBaseLibrariesSource = {version}.zip\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--output', type=Path, default=PROJECT / '.lab/ci-references')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--github-hosted', action='store_true')
    args = parser.parse_args()
    PACKAGE['require_build_budget'](args.github_hosted)
    validate_game(args.game, args.manifest)
    output = args.output.resolve()
    prepare(output)
    subprocess.run([args.dotnet, 'build', str(PROJECT / 'tools/ci-interop'), '--no-incremental', '-m:1',
                    f'-p:LoaderDir={output / "BepInEx/core"}', '-p:UseSharedCompilation=false'], check=True)
    subprocess.run([args.dotnet, str(PROJECT / 'tools/ci-interop/bin/Debug/net10.0/CiInterop.dll'),
                    str(args.game.resolve()), str(output / 'BepInEx'), PACKAGE['UNITY_VERSION']], check=True)


if __name__ == '__main__':
    main()
