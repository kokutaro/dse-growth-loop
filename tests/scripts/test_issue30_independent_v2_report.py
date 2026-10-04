import copy
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'scripts'))
from issue30_independent_v2_report import ROOT, RUN, score


class IndependentReportTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT/'evaluation/issue-30/independent-v2/approved/dataset.json').read_text())
        self.report = {'completed': True, 'plan': {'evaluationId': RUN, 'independentHoldout': True, 'observedRegressionSet': False},
                       'rows': [], 'aiAudits': [], 'ledgerBefore': {}, 'ledgerAfter': {}}
        for arm in ('all', 'targeted'):
            for q in self.data['questions']:
                gold = [j['entityId'] for j in q['judgments'] if j['grade'] >= 2 and not j.get('insufficient', False)]
                self.report['rows'].append({'questionId': q['id'], 'arm': arm, 'ranking': gold, 'primary': gold,
                                           'rerankingStatus': 'succeeded' if gold else 'all_unsuitable', 'assessments': []})

    def test_service_success_does_not_claim_http_or_full_completion(self):
        result = score(self.data, self.report)
        self.assertTrue(result['serviceQualityPassed'])
        self.assertEqual(result['overallGate'], 'pending-authenticated-http-and-timing')

    def test_missing_duplicate_and_observed_capture_are_rejected(self):
        for report in (copy.deepcopy(self.report), copy.deepcopy(self.report)):
            report['rows'].pop()
            with self.assertRaises(ValueError): score(self.data, report)
        self.report['rows'][-1] = self.report['rows'][0]
        with self.assertRaises(ValueError): score(self.data, self.report)
        self.report['plan']['independentHoldout'] = False
        with self.assertRaises(ValueError): score(self.data, self.report)

    def test_unknown_manual_shift_is_separate_from_budget_failure(self):
        row = next(r for r in self.report['rows'] if not r['ranking'])
        row['error'] = 'CLASSIFICATION_MANUAL_REQUIRED'; row['rerankingStatus'] = None
        row['queryClassification'] = {'status': 'Succeeded', 'choice': 'unknown'}
        result = score(self.data, self.report)
        self.assertTrue(result['serviceQualityPassed'])
        self.assertEqual(result['arms']['all']['manualUnknownQuestions'], [row['questionId']])
        row['queryClassification']['status'] = 'BudgetStopped'
        self.assertFalse(score(self.data, self.report)['serviceQualityPassed'])

    def test_no_gold_false_recommendations_and_bad_grounding_fail(self):
        row = next(r for r in self.report['rows'] if not r['ranking'])
        row['ranking'] = self.report['rows'][0]['ranking']
        result = score(self.data, self.report)
        self.assertFalse(result['serviceQualityPassed'])
        self.assertEqual(result['arms']['all']['noGoldFalseQuestions'], 1)
        row['ranking'] = []
        self.report['rows'][0]['assessments'] = [{'reason': 'invented text', 'evidenceIds': ['unknown']}]
        self.assertFalse(score(self.data, self.report)['serviceQualityPassed'])


if __name__ == '__main__': unittest.main()
