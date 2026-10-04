import copy
import json
import sys
import unittest
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'scripts'))
from issue30_observed_regression_report import score

class ObservedRegressionTests(unittest.TestCase):
    def setUp(self):
        self.data = json.loads((ROOT/'evaluation/issue-30/independent-v2/approved/dataset.json').read_text())
        self.capture = json.loads((ROOT/'docs/validation-results/20261004-issue-30-independent-v2-production/production-report.json').read_text())
    def observed(self):
        report = copy.deepcopy(self.capture)
        report['plan'].update(observedRegressionSet=True, independentHoldout=False, evaluationId='issue30-hardening-test', arms=['all'])
        report['rows'] = [r for r in report['rows'] if r['arm']=='all']
        report['aiAudits'] = []  # Scoring fixture; no claim of new paid evidence.
        return report
    def test_independent_capture_cannot_be_scored_as_regression(self):
        with self.assertRaises(ValueError): score(self.data, self.capture)
    def test_observed_score_never_grants_independent_acceptance_and_retains_failures(self):
        result = score(self.data, self.observed())
        self.assertFalse(result['independentAcceptanceGranted'])
        self.assertFalse(result['httpSloMeasured'])
        self.assertIn('i30n-sales-tender-person-query', result['multipleCareerSupportLost'])
        self.assertTrue(result['failedQuestions'])
    def test_missing_and_duplicate_questions_are_rejected(self):
        report = self.observed()
        report['rows'][-1] = report['rows'][0]
        with self.assertRaises(ValueError): score(self.data, report)
    def test_unsent_reason_does_not_pass_grounding(self):
        report = self.observed()
        report['rows'][0]['assessments'][0]['reason'] = '根拠にない架空の文章'
        result = score(self.data, report)
        self.assertLess(result['groundedRecommendations'], result['recommendationsChecked'])
        self.assertFalse(result['serviceQualityPassed'])
