#!/usr/bin/env python3
"""Prepare private copies of the installed game, Proton prefix, loader, and SDK."""
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
from urllib.request import urlopen
import zipfile

PROJECT = Path(__file__).resolve().parents[1]
LAB = PROJECT / '.lab'
STEAM = Path.home() / '.local/share/Steam'
LOADER_URL = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
LOADER_SHA256 = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'
SDK_VERSION = '10.0.401'


def download(url):
    with urlopen(url, timeout=60) as response:
        return response.read()


LAB.mkdir(exist_ok=True)
for source, target in [
    (STEAM / 'steamapps/common/Root', LAB / 'game'),
    (STEAM / 'steamapps/compatdata/965580', LAB / 'compatdata'),
]:
    if not source.is_dir():
        raise SystemExit(f'Missing {source}. Install and initialize Root in Steam before preparing the lab.')
    if not target.exists():
        subprocess.run(['cp', '-a', '--reflink=auto', str(source), str(target)], check=True)

if not (LAB / 'game/BepInEx/core/BepInEx.Unity.IL2CPP.dll').exists():
    archive = download(LOADER_URL)
    if hashlib.sha256(archive).hexdigest() != LOADER_SHA256:
        raise SystemExit('BepInEx checksum mismatch. Loader was not installed.')
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        if not all((LAB / 'game' / name).resolve().is_relative_to(LAB / 'game') for name in bundle.namelist()):
            raise SystemExit('BepInEx archive contains an invalid path. Loader was not installed.')
        bundle.extractall(LAB / 'game')

if not (LAB / 'sdk/sdk' / SDK_VERSION).is_dir():
    releases = json.loads(download('https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'))
    sdk = next(sdk for release in releases['releases'] for sdk in release.get('sdks', [release['sdk']]) if sdk['version'] == SDK_VERSION)
    package = next(file for file in sdk['files'] if file['rid'] == 'linux-x64' and file['name'].endswith('.tar.gz'))
    archive = download(package['url'])
    if hashlib.sha512(archive).hexdigest() != package['hash'].lower():
        raise SystemExit('.NET SDK checksum mismatch. SDK was not installed.')
    with tarfile.open(fileobj=io.BytesIO(archive), mode='r:gz') as bundle:
        bundle.extractall(LAB / 'sdk', filter='data')

interop = LAB / 'game/BepInEx/interop/tuber-canis.dll'
if not interop.exists():
    print('Generating native interop bindings in muted Xvfb. First startup may take a few minutes.', flush=True)
    with (LAB / 'bootstrap.log').open('w') as log:
        result = subprocess.run(['bash', str(PROJECT / 'scripts/run-lab.sh'), 'inspect'], stdout=log, stderr=subprocess.STDOUT)
    if not interop.exists():
        raise SystemExit(f'Interop generation failed (exit {result.returncode}). Inspect {LAB / "bootstrap.log"}.')
print('Root lab is ready. Run bash scripts/build-probe.sh next.')
