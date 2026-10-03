import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import issue30_llm_evaluation as evaluation

class Issue30LlmEvaluationTests(unittest.TestCase):
    gold = [{'entityId': 'direct', 'grade': 3, 'insufficient': False},
            {'entityId': 'support', 'grade': 2, 'insufficient': False},
            {'entityId': 'negative', 'grade': 0, 'insufficient': False}]

    def test_valid_grounded_but_irrelevant_output_is_quality_failure(self):
        scores = evaluation.measure(self.gold, ['negative', 'direct'], ['support'], ['negative'])
        self.assertEqual(['negative'], scores['irrelevantRecommended'])
        self.assertEqual(['negative'], scores['addedOtherRecommended'])
        self.assertEqual(['support'], scores['addedGoldLost'])
        self.assertEqual(.5, scores['precisionAt5'])
        self.assertEqual(1, scores['hitRateAt5'])

    def test_empty_output_does_not_hide_lost_gold(self):
        scores = evaluation.measure(self.gold, [], ['direct', 'support'], ['negative'])
        self.assertIsNone(scores['precisionAt5'])
        self.assertEqual(0, scores['hitRateAt5'])
        self.assertEqual(0, scores['ndcgAt5'])
        self.assertEqual(['direct', 'support'], scores['addedGoldLost'])

    def test_positive_outside_display_limit_counts_as_lost(self):
        scores = evaluation.measure(self.gold, ['direct', 'support'], ['support'], [], k=1)
        self.assertEqual(['support'], scores['addedGoldLost'])
        self.assertEqual(1, scores['precisionAt5'])

    def test_unknown_or_duplicate_ids_are_rejected(self):
        for ids in [['direct', 'direct'], ['invented']]:
            with self.assertRaises(ValueError): evaluation.measure(self.gold, ids, [], [])

if __name__ == '__main__': unittest.main()
