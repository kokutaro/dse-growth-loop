"""No-network tests of the Luna6 evaluation protocol and cost accounting."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location('rerank', ROOT / 'scripts/llm_rerank_evaluation.py')
m = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m)

class RerankTests(unittest.TestCase):
    def task(self):
        return {'id':'q-original-1','questionId':'q','candidateSha256':'h','order':'original','attempt':1,
                'input':{'question':'相談','candidates':[{'candidateId':'a','evidence':[{'id':'e','text':'架空の実践'}]}]}}
    def body(self, content=None):
        return {'model':m.MODEL_RESPONSE,'usage':{'prompt_tokens':100,'completion_tokens':20,
                'prompt_tokens_details':{'cached_tokens':10},'completion_tokens_details':{'reasoning_tokens':5}},
                'choices':[{'finish_reason':'stop','message':{'content':content or json.dumps({'items':[{'candidateId':'a','reason':'実践','evidenceIds':['e']}]})}}]}
    def test_schema_is_strict_and_never_contains_labels(self):
        p=m.payload(self.task(),'deployment');schema=p['response_format']['json_schema']
        self.assertTrue(schema['strict']);self.assertFalse(schema['schema']['additionalProperties'])
        self.assertFalse(schema['schema']['properties']['items']['items']['additionalProperties'])
        self.assertNotIn('judgments',json.dumps(p));self.assertEqual('none',p['reasoning_effort'])
    def test_parse_retries_three_additional_attempts_and_accounts_all_usage(self):
        calls=[]
        def send(p):
            calls.append(p);return self.body('bad' if len(calls)<4 else None)
        r=m.execute(self.task(),'d',send)
        self.assertEqual(4,len(calls));self.assertEqual('success',r['status'])
        self.assertEqual(400,r['inputTokens']);self.assertEqual(80,r['outputTokens']);self.assertEqual(3,r['parseRetries'])
    def test_exhaustion_stops_after_four(self):
        r=m.execute(self.task(),'d',lambda p:self.body('bad'))
        self.assertEqual('failed',r['status']);self.assertEqual(4,len(r['wireAttempts']))
    def test_extra_fields_and_duplicate_keys_are_parse_failures(self):
        for content in ['{"items":[],"extra":1}','{"items":[],"items":[]}','{"items":[{"candidateId":1,"reason":"x","evidenceIds":[]}]}']:
            self.assertRaises(m.ParseFailure,m.parse,self.body(content),self.task())
    def test_invented_ids_and_ungrounded_quotes_are_terminal(self):
        for item in [{'candidateId':'b','reason':'実践','evidenceIds':['e']},{'candidateId':'a','reason':'創作','evidenceIds':['e']}]:
            r=m.execute(self.task(),'d',lambda p:self.body(json.dumps({'items':[item]})))
            self.assertEqual(1,len(r['wireAttempts']));self.assertEqual('grounding_failure',r['failure'])
    def test_refusal_truncation_and_http_are_not_parse_retried(self):
        for finish in ['length','content_filter']:
            b=self.body();b['choices'][0]['finish_reason']=finish
            r=m.execute(self.task(),'d',lambda p:b);self.assertEqual(1,len(r['wireAttempts']))
        def fail(p):raise RuntimeError('secret')
        r=m.execute(self.task(),'d',fail);self.assertEqual(1,len(r['wireAttempts']))
        self.assertNotIn('secret',json.dumps(r));self.assertTrue(r['wireAttempts'][0]['usageEstimated'])
    def test_cached_tokens_are_not_double_counted(self):
        usage=m.usage(self.body());self.assertEqual('0.0000191',str(m.cost(usage)))
        b=self.body();b['usage']['prompt_tokens_details']['cached_tokens']=101
        self.assertRaises(ValueError,m.usage,b)
    def test_resume_never_resends_and_pending_requires_manual_review(self):
        with tempfile.TemporaryDirectory() as d:
            path=Path(d)/'result.json';calls=[]
            def send(p):calls.append(p);return self.body()
            m.capture(self.task(),'d',path,send,'approval');m.capture(self.task(),'d',path,send,'approval')
            self.assertEqual(1,len(calls))
            path.write_text('{"state":"pending"}')
            self.assertRaises(ValueError,m.capture,self.task(),'d',path,send,'approval')

    def test_http_status_is_redacted_and_terminal(self):
        def send(p): raise urllib.error.HTTPError('https://example.test',401,'secret',{},None)
        r=m.execute(self.task(),'d',send)
        self.assertEqual('http_401',r['failure']);self.assertEqual(1,len(r['wireAttempts']))
        self.assertNotIn('secret',json.dumps(r))
    def test_empty_result_is_valid_and_input_bound_prevents_a_call(self):
        self.assertEqual([],m.parse(self.body('{"items":[]}'),self.task()))
        task=self.task();task['input']['question']='あ'*16000
        self.assertRaises(ValueError,m.execute,task,'d',lambda p:self.fail('oversized input sent'))
    def test_final_plan_has_24_questions_69_paid_requests_and_no_labels(self):
        data=json.loads((ROOT/'evaluation/expansion-v2/dataset.json').read_text())
        primary=json.loads((ROOT/'docs/validation-results/20261003-issue-14-jev-v2/final-primary.json').read_text())
        tasks=m.plan(primary,data)
        self.assertEqual(72,len(tasks));self.assertEqual(69,sum(not t['skip'] for t in tasks))
        self.assertEqual(24,len({t['questionId'] for t in tasks}))
        self.assertNotIn('judgments',json.dumps(tasks));self.assertNotIn('positiveIds',json.dumps(tasks))
        a,b=tasks[:2];self.assertEqual(a['candidateSha256'],b['candidateSha256'])
        self.assertEqual(a['input']['candidates'],list(reversed(b['input']['candidates'])))

    def test_batch_reserves_every_parse_retry_before_any_request_and_resumes(self):
        from decimal import Decimal
        task={**self.task(),'skip':False}
        plan={'modelVersion':m.MODEL_RESPONSE,'promptVersion':m.PROMPT,'parseRetries':3,'requests':[task]}
        with tempfile.TemporaryDirectory() as d:
            calls=[]
            def send(p):calls.append(p);return self.body()
            self.assertRaises(ValueError,m.batch,plan,'d',Path(d),send,'approval',Decimal('.001'))
            self.assertEqual([],calls)
            m.batch(plan,'d',Path(d),send,'approval',Decimal('.02'))
            m.batch(plan,'d',Path(d),send,'approval',Decimal('.02'))
            self.assertEqual(1,len(calls))
            self.assertRaises(ValueError,m.batch,plan,'d',Path(d),send,'changed-approval',Decimal('.02'))

    def test_tighter_recovery_reservation_requires_utf8_bound_and_keeps_old_cost(self):
        from decimal import Decimal
        task={**self.task(),'inputTokenBound':7000,'skip':False}
        self.assertEqual(Decimal('.0017'),m.task_reservation(task))
        task['inputTokenBound']=1
        self.assertRaises(ValueError,m.task_reservation,task)

    def test_batch_stops_on_429_and_preserves_partial_results(self):
        from decimal import Decimal
        task={**self.task(),'skip':False};calls=[]
        plan={'modelVersion':m.MODEL_RESPONSE,'promptVersion':m.PROMPT,'parseRetries':3,'stopOnHttp429':True,'requests':[task,{**task,'id':'next'}]}
        def send(p):calls.append(p);raise urllib.error.HTTPError('https://example.test',429,'redacted',{'retry-after':'60'},None)
        with tempfile.TemporaryDirectory() as d:
            self.assertRaises(ValueError,m.batch,plan,'d',Path(d),send,'approval',Decimal('.05'))
            self.assertEqual(1,len(calls));self.assertEqual(1,len(json.loads((Path(d)/'comparisons.json').read_text())))

    def test_sender_uses_only_approved_endpoint_and_never_logs_key(self):
        from unittest.mock import patch
        from types import SimpleNamespace
        import sys
        self.assertRaises(ValueError,m.sender,'https://untrusted.example/')
        fake_env=SimpleNamespace(ROOT=ROOT,load_env=lambda p:{'AZURE_OPENAI_API_KEY':'dummy-test-key'})
        captured=[]
        class Response:
            def __enter__(self):return self
            def __exit__(self,*a):pass
            def read(self,n):return json.dumps(self_outer.body()).encode()
        self_outer=self
        opener=SimpleNamespace(open=lambda request,timeout:(captured.append(request) or Response()))
        with patch.dict(sys.modules,{'env_config':fake_env}), patch.object(m.subprocess,'run',return_value=SimpleNamespace(returncode=0,stdout='dummy-token')), patch.object(m.urllib.request,'build_opener',return_value=opener):
            send=m.sender('https://dse-foundry-ypbunss3xh66m.openai.azure.com/')
            self.assertEqual(m.MODEL_RESPONSE,send(m.payload(self.task(),'d'))['model'])
        self.assertEqual('dummy-test-key',captured[0].get_header('Api-key'))
        self.assertIsNone(captured[0].get_header('Authorization'))
        self.assertNotIn('dummy-test-key',captured[0].data.decode())

    def test_cli_denies_missing_approval_before_credentials_or_network(self):
        from unittest.mock import patch
        import sys
        with patch.object(sys,'argv',['rerank','--task','missing.json','--output','out.json','--approval','approval','--credit-usd','.05']),patch.object(m,'sender',side_effect=AssertionError('network attempted')):
            self.assertRaises(SystemExit,m.main)
