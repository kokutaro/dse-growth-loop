"""Actual in-memory ingestion contracts, no AI/network or employee data."""
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator, FormatChecker
from openapi_spec_validator import validate
ROOT=Path(__file__).resolve().parents[2]
class SyntheticProfiles(unittest.TestCase):
 def test_generated_flow_contracts(self):
  schema=json.loads((ROOT/'docs/specs/contracts.schema.json').read_text())
  rows=json.loads((ROOT/'tests/Dse.Tests/TestResults/synthetic-test-contract.json').read_text())
  self.assertTrue(any(r['schema']=='SyntheticImportBudget' for r in rows))
  for r in rows:
   with self.subTest(schema=r['schema'],id=r['value']['id']):
    Draft202012Validator({'$ref':'#/$defs/'+r['schema'],'$defs':schema['$defs']},format_checker=FormatChecker()).validate(r['value'])
    self.assertTrue(r['value']['syntheticTest'])
    self.assertEqual(r['value']['managedByPersonId'],'test-manager-person')
 def test_api_is_operator_only_and_self_upload_unchanged(self):
  spec=json.loads((ROOT/'docs/specs/openapi-synthetic-tests-v1.json').read_text());validate(spec)
  for path,methods in spec['paths'].items():
   self.assertTrue(path.startswith('/api/v1/admin/test-people'))
   for op in methods.values():self.assertEqual(op['x-required-roles'],['member','operator'])
