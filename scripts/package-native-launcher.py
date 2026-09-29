#!/usr/bin/env python3
"""Stage verified dependencies and build native Windows/Linux launchers."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import urllib.request
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import require_test_budget

VERSION = '0.7.3'
GAME_BUILD = '22238765'
UNITY_VERSION = '2022.3.62'
LOADER_SHA256 = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'
UNITY_SHA256 = '575e7d600f69de8200ccf4db700b3ae6252366c22e8c3434c860e428974518d1'
LOADER_URL = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
UNITY_URL = f'https://unity.bepinex.dev/libraries/{UNITY_VERSION}.zip'
SOURCE = PROJECT / 'launcher-native'
PAYLOAD = SOURCE / 'payload'
NOTICES = SOURCE / 'third-party'
CACHE = PROJECT / '.lab/native-launcher-downloads'
WORK = PROJECT / '.lab/native-launcher-build'
DIST = PROJECT / 'dist'


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def verified_archive(name, url, expected, local=None):
    target = CACHE / name
    if not target.exists():
        temporary = target.with_suffix('.download')
        try:
            if local is not None and local.exists():
                shutil.copyfile(local, temporary)
            else:
                request = urllib.request.Request(url, headers={'User-Agent': 'RootSixPlayer-package/0.7'})
                with urllib.request.urlopen(request, timeout=60) as response, temporary.open('wb') as stream:
                    shutil.copyfileobj(response, stream)
            if sha256(temporary) != expected:
                raise ValueError(f'{name} checksum mismatch. No dependency was staged; remove the modified cached input and retry.')
            temporary.replace(target)
        finally:
            temporary.unlink(missing_ok=True)
    if sha256(target) != expected:
        raise ValueError(f'{target} checksum mismatch. The cached file was not used; remove it and retry.')
    return target


def add_file(archive, path, name, executable=False):
    info = zipfile.ZipInfo(name)
    info.create_system = 3
    info.external_attr = (0o100755 if executable else 0o100644) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    with path.open('rb') as source, archive.open(info, 'w') as output:
        shutil.copyfileobj(source, output)


def stage(go):
    CACHE.mkdir(parents=True, exist_ok=True)
    PAYLOAD.mkdir(parents=True, exist_ok=True)
    plugin = PROJECT / 'engine-probe/bin/Debug/net6.0/EngineProbe.dll'
    if not plugin.is_file():
        raise FileNotFoundError('Build EngineProbe.dll before packaging. No launcher package was changed.')
    loader = verified_archive('loader.zip', LOADER_URL, LOADER_SHA256,
                              PROJECT / '.lab/friends-launcher-test/downloads/loader.zip')
    unity = verified_archive('unity.zip', UNITY_URL, UNITY_SHA256,
                             PROJECT / f'.lab/friends-launcher-test/client/game/BepInEx/unity-libs/{UNITY_VERSION}.zip')
    license_records = json.loads((NOTICES / 'license-sources.json').read_text())
    text = [(NOTICES / 'NOTICE.txt').read_text()]
    for record in license_records:
        path = NOTICES / (record['name'] + '.txt')
        if sha256(path) != record['sha256']:
            raise ValueError(f'License text changed: {path}. Verify its upstream provenance before packaging.')
        text.append(f"\n\n=== {record['name']} ===\nSource: {record['url']}\n\n" + path.read_text())
    goroot = Path(subprocess.check_output([go, 'env', 'GOROOT'], text=True).strip())
    go_license = next((path for path in (goroot / 'LICENSE', Path('/usr/share/licenses/go/LICENSE')) if path.is_file()), None)
    if go_license is None:
        raise FileNotFoundError('Go license was not found in GOROOT/LICENSE or /usr/share/licenses/go/LICENSE. Install the toolchain license before packaging.')
    text.append('\n\n=== Go ===\n\n' + go_license.read_text())
    source_records = json.loads((NOTICES / 'source-archives.json').read_text())
    source_files = [(record, verified_archive(record['name'], record['url'], record['sha256']))
                    for record in source_records]
    with zipfile.ZipFile(PAYLOAD / 'dependency-sources.zip', 'w') as archive:
        add_file(archive, NOTICES / 'source-archives.json', 'source-archives.json')
        for record, path in source_files:
            add_file(archive, path, record['name'])
    for path, name in [(plugin, 'EngineProbe.dll'), (loader, 'loader.zip'), (unity, 'unity.zip')]:
        shutil.copyfile(path, PAYLOAD / name)
    (PAYLOAD / 'THIRD-PARTY-NOTICES.txt').write_text('\n'.join(text), encoding='utf-8')
    shutil.copyfile(NOTICES / 'license-sources.json', PAYLOAD / 'license-sources.json')
    manifest = {'build': GAME_BUILD, 'version': VERSION, 'plugin_sha256': sha256(plugin),
                'loader_sha256': LOADER_SHA256, 'unity_sha256': UNITY_SHA256, 'unity_version': UNITY_VERSION}
    (PAYLOAD / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    allowed = {'.gitkeep', 'EngineProbe.dll', 'loader.zip', 'unity.zip', 'manifest.json',
               'THIRD-PARTY-NOTICES.txt', 'license-sources.json', 'dependency-sources.zip'}
    extras = {path.name for path in PAYLOAD.iterdir()} - allowed
    if extras:
        raise ValueError(f'Unexpected embedded files: {sorted(extras)}. Remove them before building a friend package.')
    print('Staged verified loader, Unity base libraries, plugin, licenses, and corresponding sources.', flush=True)


def build(go):
    WORK.mkdir(parents=True, exist_ok=True)
    DIST.mkdir(exist_ok=True)
    env = dict(os.environ, CGO_ENABLED='0', GOARCH='amd64', GOMAXPROCS='2',
               GOCACHE=str(PROJECT / '.lab/go-cache'), GOMODCACHE=str(PROJECT / '.lab/go-mod'),
               GOPATH=str(PROJECT / '.lab/go'), GOTOOLCHAIN='local')
    executables = {}
    for platform in ('linux', 'windows'):
        name = 'Root Six Player.exe' if platform == 'windows' else 'Root Six Player'
        target = WORK / name
        flags = '-s -w' + (' -H=windowsgui' if platform == 'windows' else '')
        resource = None
        compiler = shutil.which('x86_64-w64-mingw32-windres') if platform == 'windows' else None
        if compiler:
            resource = SOURCE / 'packaging_generated_windows_amd64.syso'
            if resource.exists():
                raise FileExistsError(f'{resource} already exists. Remove the previous generated resource before packaging.')
            icon = PROJECT / 'launcher/assets/launcher.ico'
            rc = WORK / 'launcher.rc'
            rc.write_text('1 ICON "' + str(icon).replace('"', '\\"') + '"\n')
            subprocess.run([compiler, '-i', str(rc), '-o', str(resource), '-O', 'coff'], check=True)
        try:
            subprocess.run([go, 'build', '-p', '1', '-trimpath', '-buildvcs=false', '-ldflags', flags, '-o', str(target), '.'],
                           cwd=SOURCE, env=dict(env, GOOS=platform), check=True)
        finally:
            if resource is not None:
                resource.unlink(missing_ok=True)
        executables[platform] = target
    outputs = []
    for platform, executable in executables.items():
        target = WORK / f'root-six-player-{VERSION}-{platform}.zip'
        with zipfile.ZipFile(target, 'w') as archive:
            add_file(archive, executable, executable.name, executable=platform == 'linux')
        outputs.append(target)
    outputs.insert(0, executables['windows'])
    # Publish only after both cross-compilations succeeded. No Python or shell
    # entrypoints, loose dependencies, or copied game files enter these ZIPs.
    for path in outputs:
        temporary = DIST / (path.name + '.tmp')
        shutil.copyfile(path, temporary)
        temporary.replace(DIST / path.name)
        print(f'{path.name}: {path.stat().st_size / 1024**2:.1f} MiB', flush=True)
    linux = DIST / 'Root Six Player.tmp'
    shutil.copyfile(executables['linux'], linux)
    linux.chmod(0o755)
    linux.replace(DIST / 'Root Six Player')
    checksums = ''.join(f'{sha256(path)}  {path.name}\n' for path in outputs)
    (DIST / 'SHA256SUMS.tmp').write_text(checksums)
    (DIST / 'SHA256SUMS.tmp').replace(DIST / 'SHA256SUMS')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--stage', action='store_true', help='Prepare embedded payload without compiling or replacing dist packages.')
    parser.add_argument('--github-hosted', action='store_true', help='Build on an isolated GitHub-hosted runner.')
    args = parser.parse_args()
    require_build_budget(args.github_hosted)
    go = shutil.which('go')
    if go is None:
        raise RuntimeError('Go is not installed. Install the Go toolchain before building the launcher.')
    stage(go)
    if not args.stage:
        build(go)


def require_build_budget(github_hosted):
    # Only packaging can use hosted runners. Game-launch guards stay unchanged.
    if github_hosted:
        if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
            raise RuntimeError('--github-hosted requires a GitHub-hosted Actions runner. Use scripts/safe-test.py locally.')
    else:
        require_test_budget()


if __name__ == '__main__':
    main()
