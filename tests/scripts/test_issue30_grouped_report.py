import sys, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'scripts'))
from issue30_grouped_report import evaluate

class GroupedReportTests(unittest.TestCase):
    def test_groups_use_actual_type_and_preserve_order(self):
        questions = {'q': {'entityType': 'knowledge', 'judgments': [{'entityId': 'case', 'grade': 3}]}}
        types = {'person': 'person', 'case': 'knowledge', 'other': 'knowledge'}
        row = evaluate({'questionId': 'q', 'ranking': ['person', 'case', 'other']}, questions, types)
        self.assertEqual(row['displayRanking'], ['case', 'other'])
        self.assertEqual(row['groupPrecisionAt5'], .5)
        self.assertEqual(row['combinedPrecisionAt5'], 1/3)

    def test_no_gold_and_unknown_ids_cannot_look_successful(self):
        q = {'q': {'entityType': 'person', 'judgments': []}}
        self.assertTrue(evaluate({'questionId': 'q', 'ranking': ['p']}, q, {'p': 'person'})['noGoldFalse'])
        with self.assertRaises(ValueError): evaluate({'questionId': 'q', 'ranking': ['missing']}, q, {})

    def test_discount_diagnosis_does_not_claim_new_inference(self):
        from issue30_grouped_report import cap_diagnosis, ROOT
        import json
        audits = json.loads((ROOT/'docs/validation-results/20261003-issue-30-independent-production/audits.json').read_text())
        rows = cap_diagnosis(audits)
        self.assertEqual(len(rows), 2)
        self.assertTrue(all(r['personMergedRank'] == 12 and not r['selected'] for r in rows))
        with self.assertRaises(ValueError): cap_diagnosis([])
