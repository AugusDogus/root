"""Choose a focused Alliance Recruit, Organize, and Undo regression sequence."""
from gameplay_driver import choose, choose_undo


class AllianceActions:
    def __init__(self):
        self.completed = []

    @property
    def done(self):
        return self.completed == ['recruit', 'organize', 'undo-organize']

    def request(self, selection, rng, undo_ids):
        value = selection['value']
        if value['prompt']['id'].endswith('WoodlandAllianceDaylightAction'):
            training = {source: targets for source, targets in value['targetMap'].items()
                        if any(target.get('targetPrompt', {}).get('id', '').endswith('SingleTrainAbility') for target in targets)}
            if training:
                return choose({**selection, 'value': {**value, 'forced': True, 'targetMap': training}}, rng, undo_ids)
        if not value['prompt']['id'].endswith('WoodlandAllianceEveningAction'):
            return None
        if self.completed == ['recruit', 'organize']:
            undo = choose_undo(selection)
            if undo is None:
                raise AssertionError('Organize returned to military operations without offering Undo')
            self.completed.append('undo-organize')
            return undo
        preferences = [('recruit', 'WoodlandAllianceRecruitAbility')] if not self.completed else []
        preferences += [('organize', 'OrganizeAbility'), ('move', 'MovePieces.SelectSource')]
        for action, label in preferences:
            sources = {source: targets for source, targets in value['targetMap'].items()
                       if any(target.get('targetPrompt', {}).get('id', '').endswith(label) for target in targets)}
            if not sources:
                continue
            request = choose({**selection, 'value': {**value, 'forced': True, 'targetMap': sources}}, rng, undo_ids)
            if action == 'recruit' or action == 'organize' and self.completed == ['recruit']:
                self.completed.append(action)
            return request
        return None
