import sys
import unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'scripts'))
import issue30_full_llm_evaluation as full
import llm_rerank_evaluation as llm

class FullLlmEvaluationTests(unittest.TestCase):
    def test_no_gold_empty_is_separate_from_positive_success(self):
        gold = [{'entityId': 'negative', 'grade': 0, 'insufficient': False}]
        result = full.measure(gold, [], ['negative'], 5)
        self.assertIsNone(result['primaryRecall'])
        self.assertIsNone(result['hitRate'])
        self.assertIsNone(result['ndcg'])
        self.assertIsNone(result['precision'])
        self.assertEqual(0, result['falseRecommendation'])

    def test_no_gold_recommendation_is_not_hidden(self):
        result = full.measure([{'entityId': 'negative', 'grade': 0, 'insufficient': False}], ['negative'], ['negative'], 5)
        self.assertEqual(0, result['precision'])
        self.assertEqual(1, result['falseRecommendation'])
        self.assertEqual(['negative'], result['irrelevantRecommended'])

    def test_empty_positive_is_failure_and_out_of_set_is_rejected(self):
        gold = [{'entityId': 'positive', 'grade': 3, 'insufficient': False}]
        result = full.measure(gold, [], ['positive'], 5)
        self.assertEqual(1, result['primaryRecall'])
        self.assertEqual(0, result['hitRate'])
        self.assertEqual(['positive'], result['lostGold'])
        with self.assertRaises(ValueError): full.measure(gold, ['positive'], [], 5)

    def test_reuse_requires_exact_input_and_repeat_condition(self):
        task = {'id': 'q-original-1', 'questionId': 'q', 'order': 'original', 'attempt': 1,
                'candidateSha256': 'current', 'input': {'question': '質問', 'candidates': []}}
        body = {'model': llm.MODEL_RESPONSE, 'choices': [{'finish_reason': 'stop', 'message': {'content': '{"items":[]}'}}]}
        response = {'questionId': 'q', 'order': 'original', 'attempt': 1, 'candidateSha256': 'old',
                    'status': 'success', 'items': [], 'wireAttempts': [{'body': body}]}
        sources = [('prior', [task], [response])]
        self.assertEqual('current', full.reusable(task, sources)['result']['candidateSha256'])
        self.assertIsNone(full.reusable({**task, 'input': {'question': '別の質問', 'candidates': []}}, sources))
        self.assertIsNone(full.reusable({**task, 'attempt': 2}, sources))

    def test_all_empty_precision_average_remains_null(self):
        self.assertIsNone(full.average([None, None]))
        self.assertEqual(.5, full.average([None, 0, 1]))

if __name__ == '__main__': unittest.main()
