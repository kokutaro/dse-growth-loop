"""Synthetic contract tests; no AI calls and no accuracy claims."""
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator,FormatChecker
from openapi_spec_validator import validate

ROOT=Path(__file__).resolve().parents[2]
class SkillDictionaryContracts(unittest.TestCase):
    def test_eight_routes_require_operator_and_do_not_claim_business_or_gold_updates(self):
        api=json.loads((ROOT/'docs/specs/openapi-skill-dictionary-v1.json').read_text());validate(api)
        self.assertEqual(8,len(api['paths']))
        for path,methods in api['paths'].items():
            for method,op in methods.items():
                self.assertEqual(['operator'],op['x-required-roles']);self.assertTrue(op['security']);self.assertTrue(op['x-revalidates-source-access'])
                self.assertFalse(op['x-updates-gold']);self.assertFalse(op['x-publishes-personal-experience'])
                refs=[p.get('$ref') for p in op['parameters']]
                if path.endswith(('/collect','/review','/suggest')):self.assertIn('#/components/parameters/IdempotencyKey',refs)
                if path.endswith(('/review','/suggest')):self.assertIn('#/components/parameters/IfMatch',refs)
                if path.endswith('/preview'):self.assertNotIn('#/components/parameters/IdempotencyKey',refs)
    def test_actual_curation_records_validate_without_changing_old_contracts(self):
        schema=json.loads((ROOT/'docs/specs/contracts.schema.json').read_text());rows=json.loads((ROOT/'tests/Dse.Tests/TestResults/skill-dictionary-contracts.json').read_text())
        self.assertEqual({'SkillCandidate','SkillDictionaryHead','SkillDictionaryVersion','SkillDictionaryReview'},{r['schema'] for r in rows})
        for r in rows:Draft202012Validator({'$ref':'#/$defs/'+r['schema'],'$defs':schema['$defs']},format_checker=FormatChecker()).validate(r['value'])
if __name__=='__main__':unittest.main()
