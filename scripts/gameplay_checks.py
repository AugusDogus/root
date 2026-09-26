"""Check player visibility in current native match snapshots."""
from test_client_support import exchange

PLAYER_ENTITIES = [
    'MarquiseDeCatPlayer', 'EyrieDynastiesPlayer', 'WoodlandAlliancePlayer',
    'VagabondPlayer', 'LizardCultPlayer', 'RiverfolkCompanyPlayer',
]


def child(entity, name):
    return next(item for item in entity['children'] if item['entityName'] == name)


def check_private_snapshots(endpoint):
    states = []
    for seat, token in enumerate(endpoint['tokens']):
        reply = exchange(endpoint, {'op': 'join', 'token': token})
        assert reply['ok'] and len(reply['messages']) <= 2, 'Join must return current state without history replay'
        state = next(message['value']['msg']['value'] for message in reply['messages']
                     if message['name'] == 'SequenceMessage' and message['value']['msg']['name'] == 'SerializedGameState')
        assert state['playerAccounts'] == [reply['roster'][seat]['account']]
        assert child(state['entities'], 'Deck')['children'] == [], f'Seat {seat + 1} sees the shared deck'
        for other, name in enumerate(PLAYER_ENTITIES):
            if other != seat and other != 5:
                assert child(child(state['entities'], name), 'Hand')['children'] == [], f'Seat {seat + 1} sees seat {other + 1} private cards'
        states.append(state)
    riverfolk = child(child(states[5]['entities'], PLAYER_ENTITIES[5]), 'Hand')['children']
    assert all(child(child(state['entities'], PLAYER_ENTITIES[5]), 'Hand')['children'] == riverfolk for state in states), 'Riverfolk hand must be equally public to all seats'
    return {'ownHandCounts': [len(child(child(state['entities'], PLAYER_ENTITIES[seat]), 'Hand')['children'])
                              for seat, state in enumerate(states)], 'riverfolkPublicCards': len(riverfolk)}
