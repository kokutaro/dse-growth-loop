"""Local contract validation. Product HTTP is tested separately; no live-AI quality claim."""
import json
from pathlib import Path
import unittest
from jsonschema import Draft202012Validator, FormatChecker

ROOT=Path(__file__).resolve().parents[2]
class SkillSemanticContractTests(unittest.TestCase):
    def test_recorded_mock_proposal_has_typed_fixed_version_and_nullable_noul_values(self):
        schema=json.loads((ROOT/'docs/specs/contracts.schema.json').read_text())
        row=json.loads((ROOT/'tests/Dse.Tests/TestResults/skill-semantic-contract.json').read_text())
        validator=Draft202012Validator({'$ref':'#/$defs/'+row['schema'],'$defs':schema['$defs']},format_checker=FormatChecker())
        validator.validate(row['value'])
        invalid={**row['value'],'outcome':'approved'}
        self.assertFalse(validator.is_valid(invalid))
        self.assertFalse(validator.is_valid({**row['value'],'dictionaryVersion':None}))
        self.assertFalse(validator.is_valid({**row['value'],'targetIds':['a','a']}))
if __name__=='__main__':unittest.main()
