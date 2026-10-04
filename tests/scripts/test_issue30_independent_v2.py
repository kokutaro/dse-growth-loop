import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, str(Path(__file__).resolve().parents[2]/'scripts'))
import generate_issue30_independent_v2 as generator

class IndependentDraftTests(unittest.TestCase):
    def test_pending_review_reproducible_and_no_previous_scenario_reuse(self):
        with tempfile.TemporaryDirectory() as temp:
            out = Path(temp)/'draft'
            with patch.object(generator, 'OUT', out):
                generator.generate()
                with self.assertRaises(ValueError): generator.generate()
            saved = generator.ROOT/'evaluation/issue-30/independent-v2'
            for name in ('dataset.json', 'sources.md', 'review.md', 'pre-model-manifest.json'):
                self.assertEqual((out/name).read_bytes(), (saved/name).read_bytes())
            data = json.loads((out/'dataset.json').read_text())
            self.assertEqual(data['humanReviewStatus'], 'pending')
            self.assertEqual(len(data['units']), 60)
            self.assertEqual(len(data['questions']), 24)
            manifest = json.loads((out/'pre-model-manifest.json').read_text())
            self.assertEqual(manifest['modelCalls'], 0)
            for name, sha in manifest['sha256'].items():
                self.assertEqual(hashlib.sha256((out/name).read_bytes()).hexdigest(), sha)
            for q in data['questions']:
                self.assertEqual(len(q['judgments']), 30)
                self.assertEqual({j['entityId'] for j in q['judgments'] if j['grade'] >= 2}, set(q['positiveIds']))
                self.assertTrue(all(j['evidenceRefs'] for j in q['judgments']))
            support = [u for u in data['units'] if u['scenario']=='support' and u['search']['entityType']=='person']
            self.assertEqual(len(support), 10)
            self.assertTrue(all('第一の職歴' in u['search']['summary'] and '副業務として' in u['search']['summary'] for u in support))
            self.assertFalse((saved/'approval.json').exists())

    def test_execution_plan_requires_approval_and_budget_is_bounded(self):
        directory = generator.ROOT/'evaluation/issue-30/independent-v2'
        plan = json.loads((directory/'execution-plan.json').read_text())
        if plan['executionEnabled']:
            approval = json.loads((directory/'execution-approval.json').read_text())
            self.assertEqual(plan['approvalReference'], approval['approvalReference'])
            self.assertEqual(approval['independentAiBudgetJpy'], 300)
        else:
            self.assertEqual(plan['status'], 'pending-human-label-and-budget-approval')
        self.assertGreaterEqual(plan['modelCallsSoFar'], 0)
        self.assertLessEqual(plan['modelCallsSoFar'], plan['budget']['maximumBillableCalls'])
        if not plan['executionEnabled']: self.assertEqual(plan['modelCallsSoFar'], 0)
        amendment_path = generator.ROOT/'docs/validation-results/20261004-issue-30-regression/source-amendment.json'
        amendment = json.loads(amendment_path.read_text()) if amendment_path.exists() else {'changes': {}}
        for path, sha in plan['sha256'].items():
            if path in amendment['changes']:
                change = amendment['changes'][path]
                self.assertEqual(change['baselineSha256'], sha)
                self.assertEqual(hashlib.sha256((generator.ROOT/change['baselineSnapshot']).read_bytes()).hexdigest(), sha)
                sha = change['currentSha256']
            self.assertEqual(hashlib.sha256((generator.ROOT/path).read_bytes()).hexdigest(), sha)
        budget = plan['budget']
        self.assertEqual(sum(s.get('requests', 0) for s in plan['stages']), budget['searchRequests'])
        self.assertEqual(budget['maximumBillableCalls'], budget['baseCallsUpperBound']+budget['manualFailureRetryReserve'])
        self.assertEqual(budget['totalTokenBound'], budget['maximumBillableCalls']*(budget['inputTokensPerCallBound']+budget['outputTokensPerCallBound']))
        self.assertLessEqual(budget['estimatedAiJpyBound'], budget['requestedAiJpyCeiling'])
