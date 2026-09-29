"""Seeded legal-action policy for exercising the six-faction match."""

def choose_undo(selection):
    if selection['name'] != 'SelectionWithTargetsRequired':
        return None
    value = selection['value']
    for source, information in value['targetMap'].items():
        if not any(item.get('targetPrompt', {}).get('id') == 'undoAbility.prompt' for item in information):
            continue
        if all(item['name'] == 'EntityListTargetInformation' and not item.get('forced', False)
               and not item['validTargets'] for item in information):
            return {'counter': value['counter'], 'op': 'targets', 'source': source,
                    'targets': [{'kind': 'entities', 'values': []} for item in information if item.get('selected', True)]}
    return None


def choose(selection, rng, undo_ids):
    value = selection['value']
    request = {'counter': value['counter']}
    name = selection['name']
    if name == 'RiverfolkPricesRequired':
        return {**request, 'op': 'prices', 'handCard': 1, 'riverboats': 1, 'mercenaries': 1}
    if name in ('ArchetypeCustomChoiceRequired', 'CustomChoiceRequired'):
        if value['prompt']['id'].endswith('.BuyRiverfolkServices'):
            # Root's registered Disabled attribute excludes unaffordable or
            # already purchased services. Exercise purchases as well as decline.
            available = [index for index, button in enumerate(value['buttons'])
                         if not any(attribute['name'] == 500041 and attribute['value'] for attribute in button)]
            if not value.get('forced', True) and (not available or rng.randrange(4) == 0):
                return {**request, 'op': 'custom', 'choice': None}
            if not available:
                raise ValueError('No available Riverfolk service for a forced choice')
            return {**request, 'op': 'custom', 'choice': rng.choice(available)}
        return {**request, 'op': 'custom', 'choice': rng.randrange(len(value['buttons']))}
    if name == 'IntChoiceRequired':
        return {**request, 'op': 'custom', 'choice': value['min']}
    if name != 'SelectionWithTargetsRequired':
        raise ValueError(f'Unsupported selection: {name}')
    prompt = value['prompt']['id']
    for source, information in value['targetMap'].items():
        if any(item.get('targetPrompt', {}).get('id') == 'undoAbility.prompt' for item in information):
            undo_ids.add(source)
    if not value['forced'] and ('.undo.' in prompt or prompt.endswith(('.ChooseDecrees', '.NoActions'))):
        return {**request, 'op': 'pass'}
    # Optional phases can keep offering sources after an attempted action.
    # Exercise their Skip/Continue choice too, rather than retrying forever.
    if not value['forced'] and not value.get('ignoreFirst', False) and rng.randrange(4) == 0:
        return {**request, 'op': 'pass'}
    candidates = []
    for source, information in value['targetMap'].items():
        if any(item.get('targetPrompt', {}).get('id') == 'undoAbility.prompt' or 'Undo' in item.get('overrideKind', '') for item in information):
            continue
        targets = []
        actionable = True
        for item in information:
            if not item.get('selected', True):
                continue
            kind = item['name']
            if kind in ('EntityListTargetInformation', 'RevealEntityListTargetInformation'):
                valid = [target for target in item['validTargets'] if target not in undo_ids]
                if not valid and item['validTargets']:
                    actionable = False
                    break
                count = min(item['numberToSelect'], len(valid))
                if count < 0:
                    raise ValueError(f'Negative selection count: {item}')
                if not item.get('forced', False) and count < item.get('minimumToSelect', 0):
                    actionable = False
                    break
                targets.append({'kind': 'entities', 'values': rng.sample(valid, count)})
            elif kind == 'KnapsackEntityListTargetInformation':
                if item['selectionMode'] != 'AsMuchAsPossibleButNoMoreThan':
                    raise ValueError(f'Unsupported weighted selection mode: {item["selectionMode"]}')
                remaining = item['targetWeight']
                weights = list(item['validTargets'].items())
                if remaining < 0 or any(weight < 0 for _, weight in weights):
                    raise ValueError('Negative weighted selection budget or cost')
                chosen = []
                for target, weight in rng.sample(weights, len(weights)):
                    if weight <= remaining:
                        chosen.append(target)
                        remaining -= weight
                targets.append({'kind': 'entities', 'values': chosen})
            elif kind == 'XTargetInformation':
                targets.append({'kind': 'number', 'value': item['max']})
            elif kind == 'CustomChoiceTargetInformation':
                targets.append({'kind': 'number', 'value': rng.randrange(len(item['choices']))})
            else:
                raise ValueError(f'Unsupported target: {kind}')
        if not actionable:
            continue
        label = ' '.join(item.get('targetPrompt', {}).get('id', '') for item in information)
        priority = 2 if 'BattleAbility' in label else 1
        candidates.append((priority, {**request, 'op': 'targets', 'source': source, 'targets': targets}))
    if candidates:
        best = max(priority for priority, _ in candidates)
        return rng.choice([request for priority, request in candidates if priority == best])
    if not value['forced']:
        return {**request, 'op': 'pass'}
    raise ValueError('No non-undo action is available for the forced selection')
