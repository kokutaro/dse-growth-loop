import hashlib
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'scripts'))
import generate_issue30_independent_v3 as generator

class IndependentV3Tests(unittest.TestCase):
    def test_fresh_draft_reproduces_and_never_inherits_approval(self):
        with tempfile.TemporaryDirectory() as temp:
            out=Path(temp)/'draft'
            with patch.object(generator,'OUT',out):
                generator.generate()
                with self.assertRaises(ValueError): generator.generate()
            saved=ROOT/'evaluation/issue-30/independent-v3'
            for name in ['dataset.json','sources.md','review.md','pre-model-manifest.json']:
                self.assertEqual((out/name).read_bytes(),(saved/name).read_bytes())
            data=json.loads((out/'dataset.json').read_text())
            self.assertEqual(data['humanReviewStatus'],'pending')
            self.assertEqual(len(data['questions']),24)
            self.assertEqual(len(data['units']),60)
            self.assertTrue(all(len(q['judgments'])==30 for q in data['questions']))
            manifest=json.loads((out/'pre-model-manifest.json').read_text())
            self.assertEqual(manifest['modelCalls'],0)
            for name,sha in manifest['sha256'].items():
                self.assertEqual(hashlib.sha256((out/name).read_bytes()).hexdigest(),sha)
            self.assertFalse((saved/'approval.json').exists())
    def test_execution_requires_exact_approval_and_retains_weekly_and_token_limits(self):
        plan=json.loads((ROOT/'evaluation/issue-30/independent-v3/execution-plan.json').read_text())
        if plan['executionEnabled']:
            approval=json.loads((ROOT/'evaluation/issue-30/independent-v3/execution-approval.json').read_text())
            self.assertEqual(plan['approvalReference'],approval['approvalReference'])
            self.assertEqual(plan['approvedDatasetSha256'],approval['datasetSha256'])
            self.assertEqual(approval['humanEvidence']['replyText'],'お願いします')
            self.assertFalse(approval['permanentAdoptionApproved'])
        else: self.assertEqual(plan['modelCallsSoFar'],0)
        self.assertFalse(plan['labelsInheritPreviousApproval'])
        self.assertEqual(sum(plan['stages'].values()),plan['maximumBillableCalls'])
        self.assertLessEqual(float(plan['conservativeAiStageBoundJpy']),plan['aiCeilingJpy'])
        self.assertEqual(plan['weeklyTotalJpy'],2000)
        self.assertEqual(plan['usdToJpy'],162)
        self.assertEqual(plan['dailyTokens'],4500000)
        self.assertTrue(plan['dailyResetContinuationIfStageBoundDoesNotFit'])
