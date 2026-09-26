"""Check Office extraction expectations, provenance and v1 contract compatibility."""
import hashlib
import importlib.util
import tempfile
import json
from pathlib import Path
import sys
import unittest
import xml.etree.ElementTree as ET
from zipfile import ZipFile

from jsonschema import Draft202012Validator

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tests/contracts'))
from contract_rules import validate_invariants


def read(path):
    return json.loads((ROOT / path).read_text())


class FixtureTests(unittest.TestCase):
    def test_generator_reproduces_versioned_inputs_byte_for_byte(self):
        spec = importlib.util.spec_from_file_location("generate_evaluation", ROOT / "scripts/generate_evaluation.py")
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        with tempfile.TemporaryDirectory() as temporary:
            module.OUT = Path(temporary)
            module.main()
            for path in module.OUT.rglob("*"):
                if path.is_file():
                    self.assertEqual(path.read_bytes(), (ROOT / "evaluation" / path.relative_to(module.OUT)).read_bytes(), str(path))

    def test_ooxml_extraction_matches_every_expected_paragraph_and_cell(self):
        for expected in read('evaluation/extraction-expected.json'):
            path = ROOT / 'evaluation' / expected['path']
            self.assertEqual(expected['sha256'], hashlib.sha256(path.read_bytes()).hexdigest())
            with ZipFile(path) as z:
                self.assertIsNone(z.testzip())
                for name in z.namelist():
                    ET.fromstring(z.read(name))
                if path.suffix == '.docx':
                    ns = {'w': 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'}
                    xml = ET.fromstring(z.read('word/document.xml'))
                    actual = [''.join(p.itertext()) for p in xml.findall('.//w:p', ns)]
                    self.assertEqual(expected['paragraphs'], actual)
                else:
                    ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
                    xml = ET.fromstring(z.read('xl/worksheets/sheet1.xml'))
                    actual = {c.attrib['r']: ''.join(c.itertext()) for c in xml.findall('.//s:c', ns)}
                    self.assertEqual(expected['cells'], actual)

    def test_smoke_records_conform_to_issue_3_schema(self):
        schema = read('docs/specs/contracts.schema.json')
        taxonomy = read('docs/specs/taxonomy-v1.json')
        smoke_ids = {u['search']['unitId'] for u in read('evaluation/dataset.json')['units'] if u['smoke']}
        for record in read('evaluation/contract-records.json'):
            if record['value']['unitId'] not in smoke_ids:
                continue
            with self.subTest(id=record['value']['id']):
                Draft202012Validator({'$ref': '#/$defs/' + record['schema'], '$defs': schema['$defs']}).validate(record['value'])
                validate_invariants(record['schema'], record['value'], taxonomy)

    def test_every_unit_has_exact_document_evidence_and_disjoint_groups(self):
        data = read('evaluation/dataset.json')
        expected = {x['path']: x for x in read('evaluation/extraction-expected.json')}
        groups = {}
        for u in data['units']:
            groups.setdefault(u['groupId'], set()).add(u['split'])
            ref = u['search']['sourceRefs'][0]
            cell = ref['location'].split('!')[1]
            self.assertEqual(ref['text'], expected['documents/' + ref['documentTitle']]['cells'][cell])
        self.assertTrue(all(len(splits) == 1 for splits in groups.values()))
        self.assertEqual('pending', data['humanReviewStatus'])
        self.assertEqual('synthetic', data['origin'])
        scenarios = {u['scenario'] for u in data['units']}
        self.assertTrue({'multiple-careers', 'secondary-category', 'low-confidence', 'flat-distribution',
                         'unknown-experience', 'department-only', 'self-correction', 'unavailable',
                         'outside-scope', 'withdrawn', 'old-version', 'repeated-report', 'hard-negative'} <= scenarios)


if __name__ == '__main__':
    unittest.main()
