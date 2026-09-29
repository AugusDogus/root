#!/usr/bin/env python3
import random
import unittest
from gameplay_driver import choose


class DriverTests(unittest.TestCase):
    def test_riverfolk_can_buy_available_services_or_decline(self):
        selection = {'name': 'ArchetypeCustomChoiceRequired', 'value': {
            'counter': 231, 'forced': False,
            'prompt': {'id': 'tuber.canis.actions.RiverfolkCompanyActions.BuyRiverfolkServices'},
            'buttons': [[{'name': 500041, 'value': disabled}] for disabled in (False, True, False)]}}
        choices = {choose(selection, random.Random(seed), set())['choice'] for seed in range(20)}
        self.assertEqual(choices, {None, 0, 2})
        selection['value']['buttons'] = [[{'name': 500041, 'value': True}]]
        self.assertIsNone(choose(selection, random.Random(0), set())['choice'])

    def test_optional_steps_can_end_even_when_sources_remain(self):
        selection = {'name': 'SelectionWithTargetsRequired', 'value': {
            'counter': 1, 'forced': False, 'prompt': {'id': 'optional-step'},
            'targetMap': {'clearing': []}}}
        choices = {choose(selection, random.Random(seed), set())['op'] for seed in range(20)}
        self.assertEqual(choices, {'pass', 'targets'})
        selection['value']['forced'] = True
        self.assertTrue(all(choose(selection, random.Random(seed), set())['op'] == 'targets'
                            for seed in range(20)))

    def test_weighted_choice_fills_budget_without_exceeding_it(self):
        weights = {'first': 2, 'second': 3, 'third': 4}
        selection = {'name': 'SelectionWithTargetsRequired', 'value': {
            'counter': 1, 'forced': True, 'prompt': {'id': 'weighted'},
            'targetMap': {'source': [{'name': 'KnapsackEntityListTargetInformation',
                                      'selectionMode': 'AsMuchAsPossibleButNoMoreThan',
                                      'targetWeight': 5, 'validTargets': weights}]}}}
        for seed in range(10):
            chosen = choose(selection, random.Random(seed), set())['targets'][0]['values']
            total = sum(weights[target] for target in chosen)
            self.assertLessEqual(total, 5)
            self.assertEqual(len(chosen), len(set(chosen)))
            self.assertTrue(all(total + cost > 5 for target, cost in weights.items() if target not in chosen))

    def test_automatically_selected_source_still_requires_its_targets(self):
        selection = {'name': 'SelectionWithTargetsRequired', 'value': {
            'counter': 33, 'forced': False, 'ignoreFirst': True, 'sourceID': 'recruiter',
            'prompt': {'id': 'place-recruiter'}, 'targetMap': {'recruiter': [{
                'name': 'EntityListTargetInformation', 'validTargets': ['clearing'],
                'numberToSelect': 1, 'minimumToSelect': 1, 'forced': True}]}}}
        for seed in range(20):
            self.assertEqual(choose(selection, random.Random(seed), set())['op'], 'targets')

    def test_optional_action_without_required_targets_passes(self):
        selection = {'name': 'SelectionWithTargetsRequired', 'value': {
            'counter': 243, 'forced': False,
            'prompt': {'id': 'tuber.canis.actions.LordOfTheHundredsActions.NoClearingsForMob'},
            'targetMap': {'mob': [{'name': 'EntityListTargetInformation', 'validTargets': [],
                                   'numberToSelect': 1, 'minimumToSelect': 1, 'forced': False}]}}}
        self.assertEqual(choose(selection, random.Random(0), set()), {'counter': 243, 'op': 'pass'})
        selection['value']['targetMap']['mob'][0]['validTargets'] = ['clearing']
        self.assertEqual(choose(selection, random.Random(0), set()), {
            'counter': 243, 'op': 'targets', 'source': 'mob',
            'targets': [{'kind': 'entities', 'values': ['clearing']}]})


if __name__ == '__main__':
    unittest.main()
