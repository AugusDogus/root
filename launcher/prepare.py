"""Create launcher-owned game copies; never modify the Steam installation."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import urllib.request
import zipfile

from steam import BUILD, VERSION, Installation, validate_game

LOADER_URL = 'https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip'
LOADER_SHA256 = 'f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a'


def download_loader(cache: Path) -> Path:
    cache.mkdir(parents=True, exist_ok=True)
    archive = cache / 'loader.zip'
    if not archive.exists() or hashlib.sha256(archive.read_bytes()).hexdigest() != LOADER_SHA256:
        temporary = cache / 'loader.download'
        try:
            request = urllib.request.Request(LOADER_URL, headers={'User-Agent': 'RootSixPlayer/0.1'})
            with urllib.request.urlopen(request, timeout=60) as source, temporary.open('wb') as target:
                shutil.copyfileobj(source, target)
            if hashlib.sha256(temporary.read_bytes()).hexdigest() != LOADER_SHA256:
                raise ValueError('Loader checksum mismatch. The download was rejected; retry preparation.')
            temporary.replace(archive)
        finally:
            temporary.unlink(missing_ok=True)
    return archive


def extract_loader(archive: Path, destination: Path) -> None:
    with zipfile.ZipFile(archive) as bundle:
        for entry in bundle.infolist():
            name = entry.filename.replace('\\', '/')
            if ':' in name or not (destination / name).resolve().is_relative_to(destination.resolve()):
                raise ValueError('Loader archive contains an unsafe path. Preparation stopped.')
        bundle.extractall(destination)


def prepare(installation: Installation, data: Path, payload: Path, progress) -> None:
    validate_game(installation.game)
    plugin = payload / 'EngineProbe.dll'
    expected = json.loads((payload / 'manifest.json').read_text())
    digest = hashlib.sha256(plugin.read_bytes()).hexdigest()
    if expected != {'build': BUILD, 'version': VERSION, 'plugin_sha256': digest}:
        raise ValueError('Mod package integrity or version check failed. Extract a fresh launcher package.')
    marker = data / 'prepared.json'
    if marker.is_file():
        previous = json.loads(marker.read_text())
        if previous.get('build') != BUILD or previous.get('version') != VERSION:
            raise ValueError('This prepared copy uses a different game version. Your saved matches are preserved. Install the matching launcher before playing.')
        for role in ('host', 'client'):
            installed = data / role / 'game/BepInEx/plugins/EngineProbe.dll'
            if not installed.is_file() or hashlib.sha256(installed.read_bytes()).hexdigest() not in (digest, previous.get('plugin_sha256')):
                raise ValueError('Prepared mod files changed outside this launcher. They were preserved. Restore your previous mod files before trying again.')
            if not (data / role / 'game/Root.exe').is_file():
                raise ValueError('A prepared game copy is missing. Use a fresh launcher data folder to prepare again.')
        for role in ('host', 'client'):
            installed = data / role / 'game/BepInEx/plugins/EngineProbe.dll'
            temporary = installed.with_suffix('.update')
            shutil.copy2(plugin, temporary)
            temporary.replace(installed)
        marker.write_text(json.dumps(expected))
        progress('Already prepared. Ready to host or join.')
        return
    if any((data / role).exists() for role in ('host', 'client')):
        raise ValueError('An incomplete or different installation exists. Use a fresh data folder; existing files and saves were preserved.')
    size = sum(path.stat().st_size for path in installation.game.rglob('*') if path.is_file())
    if shutil.disk_usage(data).free < size * 2 + 2 * 1024**3:
        raise ValueError('Not enough free space. Allow space for two game copies plus 2 GiB for the loader and Proton.')
    progress('Downloading setup files…')
    archive = download_loader(data / 'downloads')
    staging = data / 'preparing'
    if staging.exists():
        raise ValueError('An interrupted preparation remains in the data folder. Remove only its preparing folder and retry.')
    staging.mkdir()
    moved = []
    try:
        for step, role in enumerate(('host', 'client'), 1):
            progress(f'Preparing your game ({step} of 2)…')
            game = staging / role / 'game'
            # Do not carry an existing mod or user-generated loader files into this installation.
            shutil.copytree(installation.game, game, ignore=shutil.ignore_patterns(
                'BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini'))
            extract_loader(archive, game)
            plugins = game / 'BepInEx/plugins'
            plugins.mkdir(parents=True, exist_ok=True)
            shutil.copy2(plugin, plugins / plugin.name)
        validate_game(installation.game)
        for role in ('host', 'client'):
            (staging / role).replace(data / role)
            moved.append(data / role)
        marker.write_text(json.dumps(expected))
    except BaseException:
        for directory in moved:
            shutil.rmtree(directory)
        raise
    finally:
        shutil.rmtree(staging)
    progress('Game files ready. Finishing setup…')
