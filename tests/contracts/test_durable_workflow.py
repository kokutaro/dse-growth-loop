"""Contracts exported by the normal isolated services; no external inference."""
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator, FormatChecker

ROOT=Path(__file__).resolve().parents[2]
class DurableWorkflowContracts(unittest.TestCase):
    def test_actual_persistent_workflow_and_normal_search_contracts(self):
        schema=json.loads((ROOT/'docs/specs/contracts.schema.json').read_text())
        search=json.loads((ROOT/'docs/specs/openapi-search-v2.json').read_text())
        rows=json.loads((ROOT/'tests/Dse.Tests/TestResults/durable-workflow-contracts.json').read_text())
        self.assertEqual({'DemoWorkflow','DocumentFragment','SkillProjectionHead','SearchUnit','NormalSearchResult'},{r['schema'] for r in rows})
        for row in rows:
            if row['schema']=='NormalSearchResult':
                Draft202012Validator({'$ref':'#/components/schemas/Primary','components':search['components']},format_checker=FormatChecker()).validate(row['value'])
            else:
                Draft202012Validator({'$ref':'#/$defs/'+row['schema'],'$defs':schema['$defs']},format_checker=FormatChecker()).validate(row['value'])
    def test_workflow_api_documents_existing_security_and_no_ai_on_start(self):
        from openapi_spec_validator import validate
        api=json.loads((ROOT/'docs/specs/openapi-durable-workflow-v1.json').read_text());validate(api)
        op=api['paths']['/api/v1/demo-workflows/start']['post']
        self.assertEqual(['member','operator'],op['x-required-roles']);self.assertEqual(0,op['x-external-ai-calls']);self.assertTrue(op['security'])
