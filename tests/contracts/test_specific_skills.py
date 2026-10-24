import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator


class SpecificSkillContracts(unittest.TestCase):
    def test_contract_preserves_null_empty_and_participation(self):
        schema = json.loads((Path(__file__).resolve().parents[2] / 'docs/specs/contracts.schema.json').read_text())
        validator = Draft202012Validator({'$ref': '#/$defs/SpecificSkills', '$defs': schema['$defs']})
        for state, items in [('unknown', None), ('not_evaluated', None), ('extracted', [])]:
            value = dict(version='specific-skills-v1', state=state, items=items, searchTerms=[])
            self.assertTrue(validator.is_valid(value))
            broken = copy.deepcopy(value); broken['items'] = [] if items is None else None
            self.assertFalse(validator.is_valid(broken))
        value = dict(version='specific-skills-v1', state='extracted', searchTerms=['c#'], items=[dict(raw='C#', normalizationCandidate=None, evidenceId='e1', quote='C#で実装した。', participation='joint')])
        self.assertTrue(validator.is_valid(value))
        value['items'][0]['participation'] = 'expert'
        self.assertFalse(validator.is_valid(value))

    def test_legacy_skill_ids_are_deprecated_but_optional(self):
        schema = json.loads((Path(__file__).resolve().parents[2] / 'docs/specs/contracts.schema.json').read_text())
        unit = schema['$defs']['SearchUnit']
        self.assertTrue(unit['properties']['skillIds']['deprecated'])
        self.assertNotIn('specificSkills', unit['required'])
