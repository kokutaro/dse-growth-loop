import copy
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator


class SpecificSkillContracts(unittest.TestCase):
    def test_context_metadata_and_quarantine_roundtrip_in_storage_and_openapi(self):
        spec = Path(__file__).resolve().parents[2] / 'docs/specs'
        for name in ('contracts.schema.json', 'openapi.json', 'openapi-skill-dictionary-v1.json'):
            source = json.loads((spec / name).read_text())
            defs = source.get('$defs', source.get('components', {}).get('schemas'))
            prefix = '#/$defs/' if '$defs' in source else '#/components/schemas/'
            validator = Draft202012Validator({'$ref': prefix + 'SpecificSkill', '$defs': defs} if '$defs' in source else
                                            {'$ref': prefix + 'SpecificSkill', 'components': {'schemas': defs}})
            for kind in ('skill_statement', 'technology_list', 'context', 'uncertain', 'future_context'):
                item = dict(raw='通信処理の開発担当', normalizationCandidate=None, evidenceId='e1',
                            quote='通信処理の開発担当 ／ Go、Linux、Git', basis='experience',
                            contentKind=kind, contextPolicy='skill-context-v1')
                with self.subTest(file=name, kind=kind):
                    self.assertTrue(validator.is_valid(item))
            # Missing metadata remains readable; only fresh extraction has the new policy.
            item.pop('contentKind'); item.pop('contextPolicy')
            self.assertTrue(validator.is_valid(item))
            if 'SpecificSkills' in defs:
                root = {'$defs': defs} if '$defs' in source else {'components': {'schemas': defs}}
                skills = Draft202012Validator({'$ref': prefix + 'SpecificSkills', **root})
                for reason in ('non_skill_context', 'skill_context_unverified', 'skill_context_conflict'):
                    value = dict(version='specific-skills-v2', state='extracted', items=[], searchTerms=[],
                                 rejectedItems=[dict(item={**item, 'contextPolicy': 'skill-context-v1'}, reason=reason)])
                    with self.subTest(file=name, reason=reason):
                        self.assertTrue(skills.is_valid(value))

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

    def test_legacy_skill_ids_require_separate_current_approved_bindings(self):
        schema = json.loads((Path(__file__).resolve().parents[2] / 'docs/specs/contracts.schema.json').read_text())
        unit = schema['$defs']['SearchUnit']
        conditional=next(rule for rule in unit['allOf'] if 'if' in rule)
        self.assertTrue(conditional['else']['properties']['skillIds']['deprecated'])
        self.assertEqual({'skillProjectionHeadId','skillBindings','skillDictionaryVersion','skillIds'},set(conditional['then']['required']))
        self.assertNotIn('specificSkills', unit['required'])

    def test_quarantine_is_explicit_and_distinct_from_an_empty_result(self):
        schema = json.loads((Path(__file__).resolve().parents[2] / 'docs/specs/contracts.schema.json').read_text())
        validator = Draft202012Validator({'$ref': '#/$defs/SpecificSkills', '$defs': schema['$defs']})
        item = dict(raw='C#', normalizationCandidate=None, evidenceId='e1', quote='同僚がC#で計算した。', participation='performed')
        value = dict(version='specific-skills-v1', state='extracted', items=[], searchTerms=[], guardVersion='scoped-assertion-v2', rejectedItems=[dict(item=item, reason='other_actor')])
        self.assertTrue(validator.is_valid(value))
        value['rejectedItems'][0]['reason'] = 'silent_drop'
        self.assertFalse(validator.is_valid(value))

    def test_v2_ai_basis_and_legacy_roles_remain_distinct(self):
        schema = json.loads((Path(__file__).resolve().parents[2] / 'docs/specs/contracts.schema.json').read_text())
        validator = Draft202012Validator({'$ref': '#/$defs/SpecificSkills', '$defs': schema['$defs']})
        for basis in ('experience','knowledge','qualification','not_experience','uncertain'):
            value = dict(version='specific-skills-v2', state='extracted', searchTerms=[], guardVersion='source-integrity-v1', items=[dict(raw='C#', normalizationCandidate=None,evidenceId='e1',quote='C#の原文',basis=basis)])
            self.assertTrue(validator.is_valid(value))
        value['items'][0]['basis']='lead'
        self.assertFalse(validator.is_valid(value))
