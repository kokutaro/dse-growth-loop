"""Synthetic contract tests; no AI calls and no accuracy claims."""
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator,FormatChecker
from openapi_spec_validator import validate

ROOT=Path(__file__).resolve().parents[2]
class SkillDictionaryContracts(unittest.TestCase):
    def test_ten_routes_require_operator_and_do_not_claim_business_or_gold_updates(self):
        api=json.loads((ROOT/'docs/specs/openapi-skill-dictionary-v1.json').read_text());validate(api)
        self.assertEqual(10,len(api['paths']))
        for path,methods in api['paths'].items():
            for method,op in methods.items():
                self.assertEqual(['operator'],op['x-required-roles']);self.assertTrue(op['security']);self.assertTrue(op['x-revalidates-source-access'])
                self.assertFalse(op['x-updates-gold']);self.assertFalse(op['x-publishes-personal-experience'])
                refs=[p.get('$ref') for p in op['parameters']]
                if path.endswith(('/collect','/review','/suggest')):self.assertIn('#/components/parameters/IdempotencyKey',refs)
                if path.endswith(('/review','/suggest')):self.assertIn('#/components/parameters/IfMatch',refs)
                if path.endswith('/preview'):self.assertNotIn('#/components/parameters/IdempotencyKey',refs)
    def test_skill_search_requires_fixed_version_and_never_writes_or_calls_ai(self):
        api=json.loads((ROOT/'docs/specs/openapi-skill-dictionary-v1.json').read_text())
        op=api['paths']['/api/v1/skill-dictionary/search']['post']
        self.assertTrue(op['x-read-only']);self.assertEqual(0,op['x-external-ai-calls'])
        schema=api['components']['schemas']['SkillSearchInput'];check=Draft202012Validator(schema)
        valid={'version':2,'conceptId':'skill-example','eligibility':'content_only'}
        self.assertTrue(check.is_valid(valid))
        for change in ({'version':None},{'eligibility':'invented'},{'limit':101}):self.assertFalse(check.is_valid({**valid,**change}))
        self.assertFalse(check.is_valid({k:v for k,v in valid.items() if k!='version'}))
    def test_ordinary_skill_search_response_matches_the_published_contract(self):
        api=json.loads((ROOT/'docs/specs/openapi-skill-dictionary-v1.json').read_text())
        value=json.loads((ROOT/'tests/contracts/fixtures/skill-dictionary-search.json').read_text())
        Draft202012Validator({'$ref':'#/components/schemas/SkillSearchResult','components':api['components']}).validate(value)
        self.assertEqual(2,len(value['items']))
    def test_actual_curation_records_validate_without_changing_old_contracts(self):
        schema=json.loads((ROOT/'docs/specs/contracts.schema.json').read_text());rows=json.loads((ROOT/'tests/Dse.Tests/TestResults/skill-dictionary-contracts.json').read_text())
        self.assertEqual({'SkillCandidate','SkillDictionaryHead','SkillDictionaryVersion','SkillDictionaryReview'},{r['schema'] for r in rows})
        for r in rows:Draft202012Validator({'$ref':'#/$defs/'+r['schema'],'$defs':schema['$defs']},format_checker=FormatChecker()).validate(r['value'])
    def test_collection_notice_cannot_masquerade_as_an_approvable_skill(self):
        schema=json.loads((ROOT/'docs/specs/contracts.schema.json').read_text())
        rows=json.loads((ROOT/'tests/Dse.Tests/TestResults/skill-dictionary-contracts.json').read_text())
        notice=next(r['value'] for r in rows if r['value'].get('candidateType')=='collection')
        validator=Draft202012Validator({'$ref':'#/$defs/SkillCandidate','$defs':schema['$defs']})
        self.assertTrue(validator.is_valid(notice))
        self.assertFalse(validator.is_valid({**notice,'state':'pending'}))
        self.assertFalse(validator.is_valid({k:v for k,v in notice.items() if k!='candidateType'}))
if __name__=='__main__':unittest.main()
