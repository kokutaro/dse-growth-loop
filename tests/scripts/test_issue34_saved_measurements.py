import copy
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import issue34_saved_measurements as report


def fixture():
    return json.loads((ROOT / 'evaluation/issue-30/independent-v3/approved/dataset.json').read_text())


def capture():
    questions = fixture()['questions']
    rows = [{'phase': 'warm', 'sequenceOrCycle': i + 1, 'questionId': q['id'],
             'httpStatus': 422 if i >= 22 else 200, 'networkMs': 700 if i >= 22 else 18000 if i in (0, 10) else 10000,
             'errorCode': 'CLASSIFICATION_OUT_OF_SCOPE' if i >= 22 else None,
             'responseRunId': None if i >= 22 else f'warm-response-{i}'} for i, q in enumerate(questions)]
    for cycle, index in [('cold01', 0), ('cold02', 1), ('cold03', 20)]:
        rows.append({'phase': 'cold', 'sequenceOrCycle': cycle, 'questionId': questions[index]['id'],
                     'httpStatus': None if cycle == 'cold02' else 200,
                     'networkMs': None if cycle == 'cold02' else 17000, 'errorCode': None,
                     'responseRunId': cycle + '-response'})
    return {'sourceHead': 'a' * 40, 'measurements': rows,
            'warm': {'p95Passed': True}, 'stop': {'stopConfirmed': True}}


class SavedMeasurementTests(unittest.TestCase):
    def build(self, data=None):
        return report.build_record(capture() if data is None else data, fixture(), 'b' * 64, 'c' * 64)

    def test_recomputes_fixed_populations_and_keeps_first_startup_tail(self):
        value = self.build()
        self.assertEqual(value['latency']['warmAll24']['observedCount'], 24)
        self.assertEqual(value['latency']['warmSearch22']['observedCount'], 22)
        self.assertAlmostEqual(value['latency']['warmAll24']['p95Ms'], 16800)
        self.assertAlmostEqual(value['latency']['warmSearch22']['p95Ms'], 17600)
        self.assertEqual(value['latency']['warmGate'], 'failed')

    def test_cold_missing_http_stays_incomplete_and_never_borrows_server_duration(self):
        data = capture(); data['measurements'][25]['serverDurationMs'] = 17880
        value = self.build(data)
        self.assertEqual(value['latency']['cold3']['observedCount'], 2)
        self.assertIsNone(value['latency']['cold3']['p50Ms'])
        self.assertIsNone(value['latency']['cold3']['p95Ms'])
        self.assertTrue(any('cold02' in text for text in value['limitations']))

    def test_unknown_components_remain_null_and_no_service_operations_are_invented(self):
        value = self.build()
        self.assertEqual(value['state'], 'incomplete')
        self.assertEqual(value['evidenceKind'], 'offline_replay')
        self.assertEqual(value['operations'], [])
        self.assertEqual(value['storage'], [])
        self.assertIsNone(value['cost']['estimatedMonthlyTotalJpy'])
        self.assertIsNone(value['cost']['invoiceActualTotalJpy'])
        self.assertEqual(value['cost']['monthlyGate'], 'pending')
        self.assertTrue(value['cost']['unknownComponents'])
        self.assertIsNone(value['provenance']['configSha256'])
        self.assertIsNone(value['provenance']['serviceStateSha256'])

    def test_even_fast_complete_timings_cannot_promote_overall_acceptance(self):
        data = capture()
        for row in data['measurements']:
            if row['networkMs'] is not None: row['networkMs'] = 100
        value = self.build(data)
        self.assertEqual(value['latency']['warmGate'], 'passed')
        self.assertEqual(value['state'], 'incomplete')
        self.assertEqual(value['cost']['monthlyGate'], 'pending')

    def test_complete_cold_is_report_only_and_input_is_unchanged(self):
        data = capture(); data['measurements'][25].update(httpStatus=200, networkMs=19000)
        before = copy.deepcopy(data)
        value = self.build(data)
        self.assertEqual(value['latency']['cold3']['observedCount'], 3)
        self.assertEqual(value['latency']['cold3']['p50Ms'], 17000)
        self.assertAlmostEqual(value['latency']['cold3']['p95Ms'], 18800)
        self.assertEqual(value['latency']['cold3']['gate'], 'report_only')
        self.assertEqual(data, before)

    def test_output_matches_existing_measurement_schema(self):
        from jsonschema import Draft202012Validator, FormatChecker
        schema = json.loads((ROOT / 'docs/evaluation/issue-34/measurement-record.schema.json').read_text())
        Draft202012Validator(schema, format_checker=FormatChecker()).validate(self.build())

    def test_partial_warm_has_no_full_cohort_percentile(self):
        data = capture(); data['measurements'] = data['measurements'][1:]
        value = self.build(data)
        self.assertEqual(value['latency']['warmAll24']['observedCount'], 23)
        self.assertIsNone(value['latency']['warmAll24']['p95Ms'])
        self.assertEqual(value['latency']['warmGate'], 'pending')

    def test_wrong_outside_success_does_not_change_fixed_22_denominator_or_pass(self):
        data = capture(); row = data['measurements'][22]
        row.update(httpStatus=200, errorCode=None, responseRunId='wrong-outside', networkMs=100)
        for r in data['measurements'][:22]: r['networkMs'] = 100
        value = self.build(data)
        self.assertEqual(value['latency']['warmSearch22']['observedCount'], 22)
        self.assertEqual(value['latency']['warmGate'], 'failed')

    def test_duplicate_or_wrong_binding_is_rejected(self):
        changes = [lambda d: d['measurements'].append(copy.deepcopy(d['measurements'][0])),
                   lambda d: d['measurements'][1].update(questionId=d['measurements'][0]['questionId']),
                   lambda d: d['measurements'][1].update(responseRunId=d['measurements'][0]['responseRunId']),
                   lambda d: d['measurements'][24].update(responseRunId=d['measurements'][0]['responseRunId']),
                   lambda d: d['measurements'][24].update(questionId=d['measurements'][20]['questionId']),
                   lambda d: d.update(sourceHead='not-a-commit')]
        for change in changes:
            with self.subTest(change=change):
                data = capture(); change(data)
                with self.assertRaises(ValueError): self.build(data)

    def test_boolean_nonfinite_negative_status_and_contradictions_are_rejected(self):
        for field, values in [('networkMs', [True, -1, float('nan'), float('inf'), '12']),
                              ('httpStatus', [True, 99, 600, 200.0, '200']),
                              ('sequenceOrCycle', [True, 0, 25, '1'])]:
            for invalid in values:
                with self.subTest(field=field, invalid=invalid):
                    data = capture(); data['measurements'][0][field] = invalid
                    with self.assertRaises(ValueError): self.build(data)
        data = capture(); data['measurements'][0]['errorCode'] = 'HTTP_FAILED'
        with self.assertRaises(ValueError): self.build(data)

    def test_invalid_capture_fixture_hash_and_missing_response_are_rejected(self):
        for data in (None, [], {}, {'sourceHead': 'a' * 40, 'measurements': {}},
                     {'sourceHead': 'a' * 40, 'measurements': [None]}):
            with self.subTest(data=data), self.assertRaises(ValueError):
                report.build_record(data, fixture(), 'b' * 64, 'c' * 64)
        for bad_fixture in (None, {}, {'questions': []}, {'questions': [None] * 24}):
            with self.subTest(fixture=bad_fixture), self.assertRaises(ValueError):
                report.build_record(capture(), bad_fixture, 'b' * 64, 'c' * 64)
        with self.assertRaises(ValueError): report.build_record(capture(), fixture(), 'bad', 'c' * 64)
        for change in [dict(responseRunId=None), dict(responseRunId=' '), dict(phase='unknown'),
                       dict(httpStatus=None, networkMs=1), dict(networkMs=10**500), dict(errorCode=True)]:
            data = capture(); data['measurements'][0].update(change)
            with self.subTest(change=change), self.assertRaises(ValueError): self.build(data)

    def test_input_reader_rejects_nonfinite_oversize_and_nonfiles(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'input.json'
            for value in ('NaN', '{invalid', ' ' * (16 * 1024 * 1024 + 1)):
                path.write_text(value)
                with self.assertRaises(ValueError): report.read_json(path)
            with self.assertRaises(ValueError): report.read_json(Path(folder))

    def test_failed_atomic_write_leaves_no_partial_output_or_temporary_file(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / 'report.json'
            with patch.object(report.os, 'fsync', side_effect=OSError('private-storage-error')):
                with self.assertRaises(OSError): report.exclusive_write(output, self.build())
            self.assertEqual(list(Path(folder).iterdir()), [])

    def test_cli_rejects_duplicate_json_keys_and_does_not_echo_input(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / 'secret-input.json'; output = Path(folder) / 'out.json'
            source.write_text('{"sourceHead":"private-token","sourceHead":"private-key"}')
            result = subprocess.run([sys.executable, str(ROOT / 'scripts/issue34_saved_measurements.py'),
                                     '--capture', str(source), '--output', str(output)], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            self.assertFalse(output.exists())
            self.assertNotIn('private-', result.stdout + result.stderr)

    def test_cli_exclusive_output_and_input_unchanged(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / 'capture.json'; output = Path(folder) / 'report.json'
            raw = json.dumps(capture()); source.write_text(raw)
            args = ['--capture', str(source), '--output', str(output)]
            with patch('subprocess.run', side_effect=AssertionError('No external command')):
                with patch('sys.stdout', new_callable=io.StringIO): self.assertEqual(report.main(args), 0)
            original = output.read_bytes()
            with patch('sys.stderr', new_callable=io.StringIO): self.assertEqual(report.main(args), 2)
            self.assertEqual(output.read_bytes(), original)
            self.assertEqual(source.read_text(), raw)
