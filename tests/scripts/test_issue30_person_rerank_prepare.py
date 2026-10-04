import json, sys, tempfile, unittest
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'scripts'))
import issue30_person_limit_rerank as evaluation

class PersonRerankPrepareTests(unittest.TestCase):
    def test_wire_payloads_are_frozen_within_budget_and_only_changed_cohort(self):
        with tempfile.TemporaryDirectory() as tmp:
            out = Path(tmp)/'plan'
            evaluation.prepare(out)
            manifest = json.loads((out/'manifest.json').read_text())
            self.assertFalse(manifest['executionEnabled'])
            tasks = json.loads((out/'tasks.json').read_text())
            self.assertEqual(len(tasks), 24)
            self.assertEqual(len({t['questionId'] for t in tasks}), 8)
            self.assertEqual({t['attempt'] for t in tasks}, {1, 2})
            self.assertEqual(manifest['automaticRetries'], 0)
            self.assertLessEqual(manifest['maximumInputTokensPerCall'], 16000)
            evaluation.verify(out)
            manifest["maximumTotalTokens"] += 1
            (out/"manifest.json").write_text(json.dumps(manifest))
            with self.assertRaises(ValueError): evaluation.verify(out)
            manifest["maximumTotalTokens"] -= 1
            (out/"manifest.json").write_text(json.dumps(manifest))
            task_file = out/'tasks.json';task_file.write_text(task_file.read_text()+'\n')
            with self.assertRaises(ValueError): evaluation.verify(out)
            with self.assertRaises(ValueError): evaluation.prepare(out)
