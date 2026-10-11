"""No-network, independent gold and saved-rerank regressions for Issues 31/32."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('offline_quality', ROOT / 'scripts/offline_quality.py')
m = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m)


class OfflineQualityTests(unittest.TestCase):
    def gold_and_prediction(self):
        values = {'description': '二重請求を防ぐ処理を担当', 'start': None, 'end': None,
                  'precision': 'unknown', 'months': None, 'skills': ['software'],
                  'domain': 'software', 'evidenceIds': ['e1']}
        expected = {k: {'state': 'unknown'} if v is None else {'state': 'known', 'value': v}
                    for k, v in values.items()}
        record = {'id': 'unit1', 'split': 'final', 'sourceSha256': 'a' * 64,
                  'modelVersion': 'frozen-model', 'promptVersion': 'frozen-prompt', 'configSha256': 'b' * 64,
                  'expected': expected}
        gold = {'version': m.VERSION, 'fields': list(m.FIELDS), 'records': [record]}
        self.approve(gold)
        prediction = {'origin': 'recorded-structured-responses', 'records': [
            {k: v for k, v in record.items() if k != 'expected'} | {'fields': values}]}
        return gold, prediction

    def approve(self, gold):
        gold['approval'] = {'status': 'human-approved', 'reviewer': 'test-only-reviewer',
                            'reviewedAt': '2026-10-11T00:00:00Z', 'evidenceReference': 'unit-test-only',
                            'recordsSha256': hashlib.sha256(json.dumps(gold['records'], ensure_ascii=False,
                                                sort_keys=True, separators=(',', ':')).encode()).hexdigest()}

    def score(self, gold, predictions):
        with tempfile.TemporaryDirectory() as d:
            g, p = Path(d) / 'gold.json', Path(d) / 'prediction.json'
            g.write_text(json.dumps(gold)); p.write_text(json.dumps(predictions))
            return m.semantic(g, p)

    def test_matching_record_keeps_missing_unknown_in_denominator(self):
        g, p = self.gold_and_prediction()
        report, rows = self.score(g, p)
        self.assertTrue(report['gatePassed']); self.assertEqual(8, report['agreementFinal']['denominator'])
        self.assertFalse(report['qualityClaim']); self.assertEqual(0, report['callsThisRun'])

    def test_missing_unknown_field_is_failure_not_excluded(self):
        g, p = self.gold_and_prediction(); del p['records'][0]['fields']['months']
        report, _ = self.score(g, p)
        self.assertFalse(report['gatePassed']); self.assertEqual(8, report['agreementFinal']['denominator'])
        self.assertEqual('missing_field', report['failures'][0]['reason'])

    def test_unknown_value_cannot_be_inferred(self):
        g, p = self.gold_and_prediction(); p['records'][0]['fields']['start'] = '2020-01-01'
        report, _ = self.score(g, p)
        self.assertFalse(report['gatePassed']); self.assertEqual('invented_unknown_value', report['failures'][0]['reason'])

    def test_one_field_under_90_blocks_even_if_pooled_score_passes(self):
        g, p = self.gold_and_prediction()
        for n in range(1, 10):
            g['records'].append({**copy.deepcopy(g['records'][0]), 'id': str(n)})
            p['records'].append({**copy.deepcopy(p['records'][0]), 'id': str(n)})
        p['records'][0]['fields']['domain'] = 'sales'; p['records'][1]['fields']['domain'] = 'sales'
        self.approve(g); report, _ = self.score(g, p)
        self.assertGreater(report['agreementFinal']['value'], .9); self.assertFalse(report['gatePassed'])

    def test_hash_approval_and_input_model_prompt_config_are_required(self):
        for field in m.BINDINGS:
            g, p = self.gold_and_prediction(); p['records'][0][field] = 'changed'
            report, _ = self.score(g, p); self.assertFalse(report['gatePassed']); self.assertFalse(report['complete'])
        g, p = self.gold_and_prediction(); g['records'][0]['expected']['domain']['value'] = 'sales'
        report, _ = self.score(g, p); self.assertFalse(report['humanGoldApproved'])

    def test_fixture_origin_or_unapproved_labels_never_pass(self):
        g, p = self.gold_and_prediction(); p['origin'] = 'fixture'
        self.assertFalse(self.score(g, p)[0]['gatePassed'])
        p['origin'] = 'recorded-structured-responses'; g['approval']['status'] = 'pending'
        self.assertFalse(self.score(g, p)[0]['gatePassed'])

    def test_aliases_are_human_listed_no_embedding_or_ai_judge(self):
        g, p = self.gold_and_prediction()
        g['records'][0]['expected']['description']['acceptedValues'] = ['二重請求を防ぐ処理を担当', '請求重複防止を担当']
        p['records'][0]['fields']['description'] = '請求重複防止を担当'; self.approve(g)
        self.assertTrue(self.score(g, p)[0]['gatePassed'])
        p['records'][0]['fields']['description'] = 'システム設計の責任者'
        self.assertFalse(self.score(g, p)[0]['gatePassed'])

    def test_not_applicable_explicitly_excluded_and_sets_reject_wrong_citation(self):
        g, p = self.gold_and_prediction(); g['records'][0]['expected']['start'] = {'state': 'not_applicable'}
        del p['records'][0]['fields']['start']; self.approve(g)
        report, _ = self.score(g, p); self.assertEqual(7, report['agreementFinal']['denominator'])
        p['records'][0]['fields']['evidenceIds'] = ['e2']
        report, _ = self.score(g, p); self.assertEqual('wrong_set_or_evidence', report['failures'][0]['reason'])

    def test_saved_reverse_double_billing_false_positive_is_visible(self):
        d = ROOT / 'docs/validation-results/20261003-issue-14-luna6'
        report, rows = m.rerank(d / 'scored-report.json', d / 'comparisons.json', ROOT / 'evaluation/expansion-v2/dataset.json')
        self.assertEqual(144, len(rows)); self.assertEqual(1, report['conditionCount'])
        false = report['falsePositiveCases']; self.assertEqual(2, len(false))
        self.assertTrue(all(r['order'] == 'reversed' and r['questionId'] == 'v2f-software-04-query' for r in false))
        self.assertEqual(69, report['historicalUsageInSelectedSuccessfulCaptures']['wireAttempts'])
        self.assertEqual(65763, report['historicalUsageInSelectedSuccessfulCaptures']['inputTokens'])

    def test_fixed_fault_protocol_distinguishes_terminal_failures_and_parse_retries(self):
        report, rows = m.fault_matrix()
        self.assertTrue(report['passed']); self.assertEqual(10, len(rows))
        self.assertEqual(0, report['callsThisRun'])
        self.assertEqual(4, next(r for r in rows if r['case'] == 'invalid_json')['fakeAttempts'])
        self.assertEqual(1, next(r for r in rows if r['case'] == 'http429')['fakeAttempts'])

    def test_changed_candidates_cannot_be_compared_as_model_conditions(self):
        d = ROOT / 'docs/validation-results/20261003-issue-14-luna6'
        responses = json.loads((d / 'comparisons.json').read_text())
        responses[1]['candidateSha256'] = 'c' * 64
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'responses.json'; path.write_text(json.dumps(responses))
            self.assertRaisesRegex(ValueError, 'identical primary candidate hash', m.rerank,
                                   d / 'scored-report.json', path, ROOT / 'evaluation/expansion-v2/dataset.json')

    def test_stale_dataset_cannot_reuse_scored_report(self):
        d = ROOT / 'docs/validation-results/20261003-issue-14-luna6'
        data = json.loads((ROOT / 'evaluation/expansion-v2/dataset.json').read_text()); data['version'] = 'changed'
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / 'data.json'; path.write_text(json.dumps(data))
            self.assertRaisesRegex(ValueError, 'Dataset hash differs', m.rerank,
                                   d / 'scored-report.json', d / 'comparisons.json', path)

    def test_review_draft_has_no_gold_labels_or_approval(self):
        draft = m.draft(ROOT / 'evaluation/expansion-v2/dataset.json')
        self.assertEqual('pending', draft['approval']['status'])
        self.assertTrue(all(v['state'] == 'unreviewed' for r in draft['records'] for v in r['expected'].values()))

    def test_existing_output_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as d:
            m.save(d, {'a': 1}, [])
            self.assertRaises(ValueError, m.save, d, {'a': 2}, [])


if __name__ == '__main__':
    unittest.main()
