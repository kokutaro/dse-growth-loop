"""Network-free tests of the credit-bounded Jev capture runner."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('jev_evaluation', ROOT / 'scripts/jev_evaluation.py')
jev = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(jev)


class JevEvaluationTests(unittest.TestCase):
    def request(self):
        body = json.dumps({'model': 'jev-1.13.0', 'state': {'text': '架空の職歴'}, 'questions': {
            'domain': {'type': 'choice', 'instructions': '分野', 'criteria': {'software': '開発', 'unknown': '不明'}},
            'handover': {'type': 'noul', 'instructions': '引き継ぎ経験'}}}, ensure_ascii=False)
        return {'id': 'request-1', 'requestJson': body, 'requestSha256': jev.hash_bytes(body.encode())}

    def response(self):
        return {'model': 'jev-1.13.0', 'answers': {
            'domain': {'type': 'choice', 'choice': 'software', 'probabilities': {'software': .9, 'unknown': .1}, 'confidence': .8},
            'handover': {'type': 'noul', 'noul': .7}}, 'usage': {'input_tokens': 100, 'output_tokens': 70}}

    def test_provider_schema_and_model_must_match_and_usage_is_required(self):
        req = self.request()
        self.assertEqual(self.response(), jev.validate_response(self.response(), req))
        for change in [dict(model='jev-latest'), dict(usage=None), dict(answers={}), dict(usage={'input_tokens': True, 'output_tokens': 0})]:
            with self.subTest(change=change):
                self.assertRaises(ValueError, jev.validate_response, {**self.response(), **change}, req)
        bad = self.response(); bad['answers']['domain']['probabilities']['software'] = float('nan')
        self.assertRaises(ValueError, jev.validate_response, bad, req)

    def test_credit_guard_reserves_full_context_and_counts_uncertain_calls(self):
        self.assertEqual(.002688, float(jev.reserved_cost(1)))
        self.assertLess(jev.reserved_cost(350), 1)
        self.assertRaises(ValueError, jev.validate_budget, 351, 1)
        self.assertRaises(ValueError, jev.validate_budget, 350, .1)
        self.assertRaises(ValueError, jev.validate_budget, 1, 5)
        self.assertRaises(ValueError, jev.validate_budget, 1, float('nan'))

    def test_fake_capture_is_incremental_resumable_and_never_resends(self):
        req = self.request(); plan = {'version': 'jev-evaluation-v1', 'modelVersion': 'jev-1.13.0', 'requests': [req]}
        with tempfile.TemporaryDirectory() as temp:
            calls = []
            def send(request):
                calls.append(request['id']); return self.response()
            report = jev.capture(plan, 'plan-hash', Path(temp), 1, 'test-approval', send)
            self.assertEqual(1, len(calls)); self.assertEqual(100, report['inputTokens'])
            report = jev.capture(plan, 'plan-hash', Path(temp), 1, 'test-approval', send)
            self.assertEqual(1, len(calls)); self.assertEqual(1, report['completedRequests'])
            self.assertRaises(ValueError, jev.capture, plan, 'changed-plan', Path(temp), 1, 'test-approval', send)

    def test_explicit_recorded_recovery_keeps_uncertain_cost_and_never_resends_successes(self):
        with tempfile.TemporaryDirectory() as temp:
            req = self.request(); plan = {'version': 'jev-evaluation-v1', 'modelVersion': 'jev-1.13.0', 'requests': [req]}
            def fail(_): raise RuntimeError('redacted')
            self.assertRaises(ValueError, jev.capture, plan, 'hash', Path(temp), 1, 'approval', fail)
            recovery = dict(requestId=req['id'], requestSha256=req['requestSha256'], status='succeeded', manualDiagnosticAttempt=1, body=self.response(), durationMs=10)
            def must_not_send(_): self.fail('Already recovered call must not be resent')
            report = jev.capture(plan, 'hash', Path(temp), 1, 'approval', must_not_send, recovery)
            self.assertEqual(2, report['attemptCount']); self.assertEqual(1, report['retryCount'])
            self.assertEqual(64000, report['uncertainInputTokens'])
            self.assertGreater(report['conservativeCostUsd'], report['estimatedCostUsd'])
            resumed = jev.capture(plan, 'hash', Path(temp), 1, 'approval', must_not_send)
            self.assertEqual(2, resumed['attemptCount'])

    def test_failure_is_journalled_without_exception_text_or_automatic_retry(self):
        with tempfile.TemporaryDirectory() as temp:
            req = self.request(); plan = {'version': 'jev-evaluation-v1', 'modelVersion': 'jev-1.13.0', 'requests': [req]}
            def send(_): raise RuntimeError('secret-must-not-be-logged')
            self.assertRaises(ValueError, jev.capture, plan, 'hash', Path(temp), 1, 'approval', send)
            journal = (Path(temp) / 'responses.jsonl').read_text()
            self.assertNotIn('secret-must-not-be-logged', journal)
            self.assertIn('failed', journal)
            self.assertRaises(ValueError, jev.capture, plan, 'hash', Path(temp), 1, 'approval', send)
