"""Read-only discovery of Steam libraries and the supported Windows game build."""
from dataclasses import dataclass
import os
from pathlib import Path
import re
import sys

APP_ID = '965580'
BUILD = '22238765'
VERSION = '0.7.7'


@dataclass(frozen=True)
class Installation:
    game: Path
    steam: Path
    libraries: tuple[Path, ...]


def vdf_strings(path: Path) -> dict[str, str]:
    # Only scalar fields are needed; library paths may occur more than once.
    return dict(vdf_pairs(path))


def vdf_pairs(path: Path) -> list[tuple[str, str]]:
    text = path.read_text(encoding='utf-8-sig')
    pairs = re.findall(r'"((?:[^"\\]|\\.)*)"\s*"((?:[^"\\]|\\.)*)"', text)
    return [(key, value.replace('\\\\', '\\').replace('\\"', '"')) for key, value in pairs]


def steam_roots() -> list[Path]:
    if sys.platform == 'win32':
        import winreg
        roots = []
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam') as key:
                roots.append(Path(winreg.QueryValueEx(key, 'SteamPath')[0]))
        except OSError:
            pass
        roots.append(Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'Steam')
        return roots
    return [Path.home() / '.local/share/Steam', Path.home() / '.steam/steam',
            Path.home() / '.var/app/com.valvesoftware.Steam/.local/share/Steam']


def libraries_for(root: Path) -> tuple[Path, ...]:
    libraries = [root.resolve()]
    file = root / 'steamapps/libraryfolders.vdf'
    if file.is_file():
        for key, value in vdf_pairs(file):
            if key == 'path':
                library = Path(value).resolve()
                if library not in libraries:
                    libraries.append(library)
    return tuple(libraries)


def validate_game(game: Path) -> None:
    manifest = game.parent.parent / f'appmanifest_{APP_ID}.acf'
    if not manifest.is_file():
        raise ValueError('Root Steam manifest not found. Select Root inside a Steam library, or install it through Steam first.')
    fields = vdf_strings(manifest)
    if fields.get('appid') != APP_ID or fields.get('buildid') != BUILD:
        raise ValueError(f'This test mod needs Steam build {BUILD}; found {fields.get("buildid", "unknown")}. No files were changed.')
    if fields.get('installdir') != game.name:
        raise ValueError('The selected folder does not match Root’s Steam manifest.')
    for filename in ('Root.exe', 'GameAssembly.dll', 'Root_Data/il2cpp_data/Metadata/global-metadata.dat'):
        if not (game / filename).is_file():
            raise ValueError(f'Missing {filename}. Verify Root files in Steam before preparing the mod.')


def discover(override: str = '', roots: list[Path] | None = None) -> Installation:
    candidates = roots if roots is not None else steam_roots()
    errors = []
    for root in candidates:
        if not (root / 'steamapps').is_dir():
            continue
        libraries = libraries_for(root)
        for library in libraries:
            game = Path(override).expanduser().resolve() if override else library / 'steamapps/common/Root'
            if not game.is_dir():
                continue
            try:
                validate_game(game)
                return Installation(game, root.resolve(), libraries)
            except ValueError as error:
                errors.append(str(error))
    raise ValueError(errors[0] if errors else 'Root was not found. Install it in Steam, or enter its full game folder path.')


def proton_paths(installation: Installation) -> tuple[Path, Path]:
    proton = next((library / 'steamapps/common/Proton - Experimental' for library in installation.libraries
                   if (library / 'steamapps/common/Proton - Experimental/proton').is_file()), None)
    runtime = next((library / 'steamapps/common/SteamLinuxRuntime_4/_v2-entry-point' for library in installation.libraries
                    if (library / 'steamapps/common/SteamLinuxRuntime_4/_v2-entry-point').is_file()), None)
    if proton is None or runtime is None:
        raise ValueError('Install Proton Experimental and Steam Linux Runtime 4 in Steam, then retry.')
    return proton, runtime
