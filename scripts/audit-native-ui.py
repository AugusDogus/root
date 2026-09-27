#!/usr/bin/env python3
"""Inventory serialized UI collections without starting Root. Requires UnityPy."""
import argparse
from collections import Counter
import gc
import json
from pathlib import Path
import re
import sys

PROJECT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT / 'launcher'))
from test_budget import require_test_budget

RELEVANT = re.compile(r'Player|Faction|Opponent|Relationship|PositionPool|MatchResults|Battle|Combat|Trade|Riverfolk|Vagabond|PromptBehaviour')


def collections(value, prefix=''):
    if isinstance(value, dict):
        for key, child in value.items():
            if not key.startswith('m_'):
                yield from collections(child, f'{prefix}.{key}' if prefix else key)
    elif isinstance(value, list):
        yield {'field': prefix, 'length': len(value), 'values': value}


def inspect(path, scripts):
    import UnityPy
    env = UnityPy.load(str(path))
    records, errors = [], []
    counts = Counter()
    for asset in env.assets:
        objects = asset.objects
        cache = {}

        def tree(identity):
            if identity not in cache:
                cache[identity] = objects[identity].read_typetree()
            return cache[identity]

        def hierarchy(identity):
            names, seen = [], set()
            while identity and identity not in seen:
                seen.add(identity)
                game_object = tree(identity)
                names.append(game_object['m_Name'])
                transforms = [item['component']['m_PathID'] for item in game_object['m_Component']
                              if objects[item['component']['m_PathID']].type.name in ('Transform', 'RectTransform')]
                if not transforms:
                    break
                parent = tree(transforms[0])['m_Father']
                if parent['m_FileID'] or not parent['m_PathID']:
                    break
                identity = tree(parent['m_PathID'])['m_GameObject']['m_PathID']
            return '/'.join(reversed(names))

        for obj in objects.values():
            if obj.type.name != 'MonoBehaviour':
                continue
            try:
                data = obj.read_typetree()
            except ValueError as error:
                errors.append({'file': path.name, 'asset': asset.name, 'id': obj.path_id, 'error': str(error)})
                continue
            script = scripts.get(data['m_Script']['m_PathID'])
            if script is None:
                counts['unresolved_scripts'] += 1
                continue
            counts['components'] += 1
            arrays = list(collections(data))
            if not RELEVANT.search(script):
                continue
            game_object = data['m_GameObject']
            records.append({'bundle': path.name, 'asset': asset.name, 'id': obj.path_id,
                            'type': script, 'object': hierarchy(game_object['m_PathID']) if game_object['m_PathID'] else '',
                            'arrays': arrays,
                            'scalars': {k: v for k, v in data.items() if not k.startswith('m_') and isinstance(v, (int, float, str, bool))}})
    return records, counts, errors


def main():
    require_test_budget()
    import UnityPy
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data', type=Path, default=PROJECT / '.lab/game/Root_Data')
    parser.add_argument('--output', type=Path, default=PROJECT / 'results/native-ui-assets.json')
    args = parser.parse_args()
    bundles = args.data / 'StreamingAssets/aa/StandaloneWindows64'
    scripts = {}
    for path in bundles.glob('*monoscripts.bundle'):
        env = UnityPy.load(str(path))
        for obj in env.objects:
            if obj.type.name == 'MonoScript':
                data = obj.read_typetree()
                name = '.'.join(filter(None, [data['m_Namespace'], data['m_ClassName']]))
                if obj.path_id in scripts and scripts[obj.path_id] != name:
                    raise ValueError('Script path-ID collision; qualify script IDs by external asset before continuing')
                scripts[obj.path_id] = name
    if not scripts:
        raise ValueError('No script metadata found in the game bundles')
    records, errors, counts = [], [], Counter()
    for path in [*sorted(bundles.glob('*.bundle')), args.data / 'resources.assets', args.data / 'globalgamemanagers.assets']:
        found, scanned, unreadable = inspect(path, scripts)
        records.extend(found)
        errors.extend(unreadable)
        counts.update(scanned)
        counts['files'] += 1
        if counts['files'] % 25 == 0:
            print(f"Scanned {counts['files']} files", flush=True)
        gc.collect()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({'counts': dict(counts), 'records': records, 'unreadable': errors}, indent=2) + '\n')
    print(json.dumps({'output': str(args.output), 'counts': dict(counts), 'relevant_components': len(records),
                      'unreadable': len(errors)}), flush=True)


if __name__ == '__main__':
    main()
