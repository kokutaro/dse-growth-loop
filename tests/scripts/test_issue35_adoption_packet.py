import copy
import hashlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import issue34_saved_measurements as saved
import issue35_adoption_packet as packet


class AdoptionPacketTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        folder = ROOT / 'docs/validation-results/20261017-issue34-saved-evidence'
        candidate = json.loads((ROOT / 'docs/evaluation/issue-35/candidate-settings-20261017.json').read_text())
        self.docs = {
            'candidate': candidate, 'issue30': json.loads((folder / 'capture.json').read_text()),
            'issue31': {'version': 'offline-quality-v1', 'qualityClaim': False, 'conditionCount': 1,
                        'falsePositiveCases': [{'questionId': 'historical-q'}, {'questionId': 'historical-q'}]},
            'issue32': {'version': 'offline-quality-v1', 'qualityClaim': False, 'humanGoldApproved': False,
                        'complete': False, 'gatePassed': False, 'agreementFinal': {'value': None, 'denominator': 0}},
            'issue33': {'schemaVersion': 'issue33-local-evidence-v1', 'evidenceKind': 'local_tests',
                        'realAcceptance': 'pending', 'targetedTestsPassed': 30, 'allDotnetTestsPassed': 596},
        }
        self.refresh()

    def refresh(self):
        capture_raw = (json.dumps(self.docs['issue30'], ensure_ascii=False, indent=2) + '\n').encode()
        self.docs['issue34'] = saved.build_record(self.docs['issue30'], json.loads(saved.DATASET.read_text()),
            hashlib.sha256(capture_raw).hexdigest(), hashlib.sha256(saved.DATASET.read_bytes()).hexdigest())
        self.manifest = {'schemaVersion': 'issue35-adoption-input-v1', 'artifacts': {}}
        for role, value in self.docs.items():
            raw = (json.dumps(value, ensure_ascii=False, indent=2) + '\n').encode()
            path = role + '.json'; (self.root / path).write_bytes(raw)
            self.manifest['artifacts'][role] = {'path': path, 'sha256': hashlib.sha256(raw).hexdigest()}

    def build(self): return packet.build_packet(self.root, self.manifest)

    def test_real_saved_failure_and_unknowns_are_preserved_without_adoption(self):
        value = self.build()
        self.assertEqual(value['adoptionStatus'], 'not_adopted')
        self.assertIs(value['productionApplicationAllowed'], False)
        self.assertEqual(value['decision'], 'pending-human-decision')
        self.assertEqual(value['dependencies']['30']['warmGate'], 'failed')
        self.assertEqual(value['dependencies']['30']['coldHttpObservedCount'], 2)
        self.assertEqual(value['dependencies']['31']['historicalFalsePositiveQuestionCount'], 1)
        self.assertIsNone(value['monthly']['estimatedTotalJpy'])
        self.assertEqual(value['monthly']['gate'], 'pending')
        self.assertEqual(len(value['evidence']), 6)
        self.assertIn('effectiveRuntimeConfigSha256', value['candidate']['unknownSettingSlots'])

    def test_all_fast_times_and_declared_user_approval_never_grant_adoption(self):
        for row in self.docs['issue30']['measurements']:
            if row['networkMs'] is not None: row['networkMs'] = 100
        self.docs['issue30']['userDecision'] = {'acceptanceApproved': True}
        self.refresh(); value = self.build()
        self.assertEqual(value['dependencies']['30']['warmGate'], 'passed')
        self.assertEqual(value['dependencies']['30']['status'], 'pending')
        self.assertIs(value['productionApplicationAllowed'], False)

    def test_changed_candidate_or_dataset_binding_is_rejected(self):
        for name in ('sourceCommit', 'datasetSha256'):
            with self.subTest(name=name):
                original = self.docs['candidate'][name]
                self.docs['candidate'][name] = 'f' * len(original)
                self.refresh()
                with self.assertRaises(ValueError): self.build()
                self.docs['candidate'][name] = original

    def test_borrowed_or_modified_measurement_record_is_rejected(self):
        self.docs['issue34']['latency']['warmGate'] = 'passed'
        raw = json.dumps(self.docs['issue34']).encode(); (self.root / 'issue34.json').write_bytes(raw)
        self.manifest['artifacts']['issue34']['sha256'] = hashlib.sha256(raw).hexdigest()
        with self.assertRaises(ValueError): self.build()

    def test_missing_or_reused_artifact_and_wrong_hash_are_rejected(self):
        for mutation in ('missing', 'same-path', 'wrong-hash', 'extra'):
            with self.subTest(mutation=mutation):
                self.refresh()
                if mutation == 'missing': del self.manifest['artifacts']['issue32']
                elif mutation == 'same-path': self.manifest['artifacts']['issue32'] = self.manifest['artifacts']['issue31']
                elif mutation == 'wrong-hash': self.manifest['artifacts']['issue31']['sha256'] = '0' * 64
                else: self.manifest['approval'] = True
                with self.assertRaises(ValueError): self.build()

    def test_artifacts_cannot_escape_root_through_parent_or_symlink(self):
        for path in ('../outside.json', '/etc/passwd', 'escape.json'):
            with self.subTest(path=path):
                self.refresh()
                if path == 'escape.json': (self.root / path).symlink_to('/etc/passwd')
                self.manifest['artifacts']['issue31']['path'] = path
                with self.assertRaises(ValueError): self.build()

    def test_fixture_success_cannot_be_relabelled_as_real_service_acceptance(self):
        self.docs['issue33']['realAcceptance'] = 'passed'; self.refresh()
        with self.assertRaises(ValueError): self.build()
        self.docs['issue33']['realAcceptance'] = 'pending'
        self.docs['issue31']['qualityClaim'] = True; self.refresh()
        with self.assertRaises(ValueError): self.build()

    def test_invalid_candidate_number_and_unknown_credential_field_are_rejected(self):
        original = copy.deepcopy(self.docs['candidate'])
        for value in (True, -1, float('inf'), '10'):
            self.docs['candidate'] = copy.deepcopy(original)
            self.docs['candidate']['settings']['personLimit'] = value; self.refresh()
            with self.assertRaises(ValueError): self.build()
        self.docs['candidate'] = original
        self.docs['candidate']['settings']['apiKey'] = 'private-secret'; self.refresh()
        with self.assertRaises(ValueError): self.build()

    def test_cli_does_not_echo_input_and_never_overwrites(self):
        manifest = self.root / 'manifest.json'; output = self.root / 'packet.json'
        manifest.write_text(json.dumps(self.manifest))
        args = ['--manifest', str(manifest), '--root', str(self.root), '--output', str(output)]
        with patch('sys.stdout', new_callable=io.StringIO): self.assertEqual(packet.main(args), 0)
        original = output.read_bytes()
        with patch('sys.stderr', new_callable=io.StringIO): self.assertEqual(packet.main(args), 2)
        self.assertEqual(output.read_bytes(), original)
        manifest.write_text('{"private-token":"secret-value"}')
        with patch('sys.stderr', new_callable=io.StringIO) as error: self.assertEqual(packet.main(args), 2)
        self.assertNotIn('secret', error.getvalue()); self.assertNotIn('private', error.getvalue())

    def test_reported_semantic_agreement_is_not_promoted_and_invalid_numbers_are_rejected(self):
        self.docs['issue32']['agreementFinal'] = {'value': .95, 'denominator': 100}
        self.docs['issue32'].update(humanGoldApproved=True, complete=True, gatePassed=True)
        self.refresh(); value = self.build()
        self.assertEqual(value['dependencies']['32']['agreement'], .95)
        self.assertEqual(value['dependencies']['32']['status'], 'pending')
        for invalid in (True, -1, 1.1):
            self.docs['issue32']['agreementFinal']['value'] = invalid; self.refresh()
            with self.assertRaises(ValueError): self.build()

    def test_packet_invariant_schema_forbids_application_and_known_cost_claims(self):
        from jsonschema import Draft202012Validator
        schema = json.loads((ROOT / 'docs/evaluation/issue-35/adoption-packet.schema.json').read_text())
        validator = Draft202012Validator(schema); value = self.build(); validator.validate(value)
        for key, changed in [('productionApplicationAllowed', True), ('adoptionStatus', 'adopted')]:
            invalid = copy.deepcopy(value); invalid[key] = changed
            self.assertTrue(list(validator.iter_errors(invalid)))
        value['monthly']['estimatedTotalJpy'] = 0
        self.assertTrue(list(validator.iter_errors(value)))
