import copy
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from issue30_independent_v3_http_report import SCOPE, score
import issue30_independent_v3_http_report as report_module


class IndependentV3HttpTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.loads((ROOT / 'evaluation/issue-30/independent-v3/approved/dataset.json').read_text())
        self.http, self.ui = [], []
        units = {u['search']['entityId']: u for u in self.fixture['units']}
        for q in self.fixture['questions']:
            outside = q['draftDomain'] == 'unknown'
            body = {'code': 'CLASSIFICATION_OUT_OF_SCOPE'} if outside else {
                'runId': q['id'], 'rerankingStatus': 'succeeded', 'assessments': [],
                'primary': {'candidates': [{'entityId': SCOPE + '-' + i, 'entityType': q['entityType'], 'title': i}
                                            for i in q['positiveIds']]}}
            if not outside:
                for c in body['primary']['candidates']:
                    original = units[c['title']]['search']['sourceRefs'][0]
                    ref = {'id': SCOPE + '-' + original['id'], 'text': original['text']}
                    c['units'] = [{'sourceRefs': [ref]}]
                    body['assessments'].append({'entityId': c['entityId'], 'entityType': c['entityType'],
                                                'evidenceIds': [ref['id']], 'reason': ref['text']})
            groups = [] if outside else [
                {'name': name, 'titles': [c['title'] for c in body['primary']['candidates'] if c['entityType'] == kind]}
                for kind, name in [('person', '相談候補'), ('knowledge', '参考事例')]]
            self.http.append({'questionId': q['id'], 'request': {'query': q['text'], 'target': 'all'},
                              'httpStatus': 422 if outside else 200, 'body': body, 'networkMs': 1000})
            self.ui.append({'questionId': q['id'], 'runId': None if outside else q['id'],
                            'uiCompletionUpperBoundMs': 1500,
                            'ui': {'groups': groups, 'status': '自動検索対象外' if outside else '完了', 'alerts': []}})

    def test_http_and_ui_durations_reject_bool_negative_and_unknown(self):
        for field in ('networkMs', 'uiCompletionUpperBoundMs'):
            for value in (True, -1, None, float('nan'), float('inf')):
                http, ui = copy.deepcopy(self.http), copy.deepcopy(self.ui)
                (http if field == 'networkMs' else ui)[0][field] = value
                with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                    score(self.fixture, http, ui)

    def test_ui_completion_cannot_precede_its_http_response(self):
        for index in (0,next(i for i,r in enumerate(self.http) if r['httpStatus']==422)):
            ui=copy.deepcopy(self.ui)
            ui[index]['uiCompletionUpperBoundMs']=self.http[index]['networkMs']-1
            with self.subTest(index=index),self.assertRaises(ValueError):score(self.fixture,self.http,ui)

    def test_unscoped_candidate_and_assessment_ids_are_rejected(self):
        http=copy.deepcopy(self.http)
        for row in http:
            if row['httpStatus']==200:
                for candidate in row['body']['primary']['candidates']:
                    candidate['entityId']=candidate['entityId'].removeprefix(SCOPE+'-')
                for assessment in row['body']['assessments']:
                    assessment['entityId']=assessment['entityId'].removeprefix(SCOPE+'-')
        with self.assertRaises(ValueError):score(self.fixture,http,self.ui)

    def test_successful_warm_response_run_ids_must_be_distinct(self):
        self.http[1]['body']['runId']=self.http[0]['body']['runId']
        self.ui[1]['runId']=self.http[0]['body']['runId']
        with self.assertRaises(ValueError):score(self.fixture,self.http,self.ui)

    def test_non_success_reranking_status_cannot_keep_visible_candidates(self):
        for status in ('no_candidates','all_unsuitable','failed'):
            http=copy.deepcopy(self.http);http[0]['body']['rerankingStatus']=status
            with self.subTest(status=status),self.assertRaises(ValueError):score(self.fixture,http,self.ui)

    def test_standalone_cli_malformed_records_exit2_without_traceback_or_output(self):
        http=copy.deepcopy(self.http);http[0]['body']['primary']['candidates']=[None];http[0]['body']['secret']='never-store'
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory);(folder/'http-warm.json').write_text(json.dumps(http));(folder/'ui-warm-observations.json').write_text(json.dumps(self.ui))
            with patch.object(sys,'argv',['warm-report','--directory',directory]),contextlib.redirect_stderr(io.StringIO()) as errors, \
                    contextlib.redirect_stdout(io.StringIO()) as output,self.assertRaises(SystemExit) as error:
                report_module.main()
            self.assertEqual(error.exception.code,2)
            self.assertEqual(output.getvalue(),'')
            self.assertNotIn('Traceback',errors.getvalue())
            self.assertNotIn('never-store',errors.getvalue())
            self.assertFalse((folder/'http-warm-summary.json').exists())

    def test_expected_outside_is_not_failure_or_empty_search(self):
        result = score(self.fixture, self.http, self.ui)
        self.assertTrue(result['qualityPassed'])
        self.assertEqual(result['networkTiming']['count'], 22)
        self.assertEqual(result['allResponseTiming']['count'], 24)
        index = next(i for i, r in enumerate(self.http) if r['httpStatus'] == 422)
        self.ui[index]['ui']['status'] = '該当なし'
        with self.assertRaises(ValueError): score(self.fixture, self.http, self.ui)

    def test_real_failure_is_retained_and_missing_http_is_rejected(self):
        self.http[0]['httpStatus'] = 503
        self.http[0]['body'] = {'code': 'CLASSIFICATION_MANUAL_REQUIRED'}
        result = score(self.fixture, self.http, self.ui)
        self.assertFalse(result['qualityPassed'])
        self.assertIn(self.http[0]['questionId'], result['failedQuestions'])
        with self.assertRaises(ValueError): score(self.fixture, self.http[:-1], self.ui)

    def test_warm_status_requires_integer_http_range_for_every_question(self):
        outside=next(i for i,r in enumerate(self.http) if r['httpStatus']==422)
        for index in (0,outside):
            for status in (True,False,None,'200',200.0,99,600,{},[]):
                http=copy.deepcopy(self.http);http[index]['httpStatus']=status
                with self.subTest(index=index,status=status),self.assertRaises(ValueError):
                    score(self.fixture,http,self.ui)

    def test_warm_transport_error_cannot_coexist_with_observed_status(self):
        outside=next(i for i,r in enumerate(self.http) if r['httpStatus']==422)
        for index in (0,outside):
            for code in ('TIMEOUT','NETWORK_ERROR','CANCELLED','',False,{}):
                http=copy.deepcopy(self.http);http[index]['errorCode']=code
                with self.subTest(index=index,code=code),self.assertRaises(ValueError):score(self.fixture,http,self.ui)
        self.http[0]['errorCode']=None
        self.assertTrue(score(self.fixture,self.http,self.ui)['qualityPassed'])

    def test_cli_malformed_outside_status_has_no_summary(self):
        http=copy.deepcopy(self.http)
        next(r for r in http if r['httpStatus']==422)['httpStatus']=True
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory)
            (folder/'http-warm.json').write_text(json.dumps(http))
            (folder/'ui-warm-observations.json').write_text(json.dumps(self.ui))
            with patch.object(sys,'argv',['warm-report','--directory',directory]),contextlib.redirect_stderr(io.StringIO()) as errors, \
                    contextlib.redirect_stdout(io.StringIO()) as output,self.assertRaises(SystemExit) as error:
                report_module.main()
            self.assertEqual(error.exception.code,2)
            self.assertEqual(output.getvalue(),'')
            self.assertNotIn('Traceback',errors.getvalue())
            self.assertFalse((folder/'http-warm-summary.json').exists())

    def test_ui_shape_is_required_for_success_and_outside_records(self):
        outside=next(i for i,r in enumerate(self.http) if r['httpStatus']==422)
        for index in (0,outside):
            for field,value in (('status',True),('alerts',{}),('alerts',[None]),('groups',{}),
                                ('groups',[None]),('groups',[{'name':'相談候補','titles':{}}])):
                ui=copy.deepcopy(self.ui);ui[index]['ui'][field]=value
                with self.subTest(index=index,field=field,value=value),self.assertRaises(ValueError):
                    score(self.fixture,self.http,ui)

    def test_cli_empty_object_outside_ui_is_not_successful_evidence(self):
        ui=copy.deepcopy(self.ui)
        outside=next(i for i,r in enumerate(self.http) if r['httpStatus']==422)
        ui[outside]['ui'].update(groups={},alerts={})
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory)
            (folder/'http-warm.json').write_text(json.dumps(self.http))
            (folder/'ui-warm-observations.json').write_text(json.dumps(ui))
            with patch.object(sys,'argv',['warm-report','--directory',directory]),contextlib.redirect_stderr(io.StringIO()) as errors, \
                    contextlib.redirect_stdout(io.StringIO()) as output,self.assertRaises(SystemExit) as error:
                report_module.main()
            self.assertEqual(error.exception.code,2)
            self.assertEqual(output.getvalue(),'')
            self.assertNotIn('Traceback',errors.getvalue())
            self.assertFalse((folder/'http-warm-summary.json').exists())

    def test_changed_request_and_fast_outside_cannot_hide_slow_search(self):
        bad = copy.deepcopy(self.http)
        bad[0]['request']['conditions'] = {}
        with self.assertRaises(ValueError): score(self.fixture, bad, self.ui)
        for r in self.http:
            r['networkMs'] = 16000 if r['httpStatus'] == 200 else 1
        for r,o in zip(self.http,self.ui):o['uiCompletionUpperBoundMs']=r['networkMs']+500
        self.assertFalse(score(self.fixture, self.http, self.ui)['warmSloPassed'])

    def test_missing_assessment_and_invented_reason_cannot_pass_grounding(self):
        bad = copy.deepcopy(self.http)
        bad[0]['body']['assessments'].pop()
        with self.assertRaises(ValueError): score(self.fixture, bad, self.ui)
        self.http[0]['body']['assessments'][0]['reason'] = 'invented evidence'
        self.assertFalse(score(self.fixture, self.http, self.ui)['qualityPassed'])

    def test_outside_http_200_empty_success_is_a_contract_failure(self):
        index = next(i for i, r in enumerate(self.http) if r['httpStatus'] == 422)
        qid = self.http[index]['questionId']
        self.http[index].update(httpStatus=200, body={
            'runId': qid, 'rerankingStatus': 'no_candidates', 'assessments': [],
            'primary': {'candidates': []}})
        self.ui[index].update(runId=qid, ui={
            'groups': [{'name': '相談候補', 'titles': []}, {'name': '参考事例', 'titles': []}],
            'status': '完了', 'alerts': []})
        result = score(self.fixture, self.http, self.ui)
        self.assertFalse(result['qualityPassed'])
        self.assertIn(qid, result['failedQuestions'])
        self.assertEqual(result['networkTiming']['count'], 22)
        self.assertEqual(result['allResponseTiming']['count'], 24)

    def test_both_linear_p95_populations_must_pass(self):
        inside = [r for r in self.http if r['httpStatus'] == 200]
        inside[-2]['networkMs'], inside[-1]['networkMs'] = 15001, 16000
        for r,o in zip(self.http,self.ui):o['uiCompletionUpperBoundMs']=r['networkMs']+500
        result = score(self.fixture, self.http, self.ui)
        self.assertAlmostEqual(result['networkTiming']['p95Ms'], 14300.95)
        self.assertTrue(result['warmSloPassed'])
        for r in self.http:
            if r['httpStatus'] == 422: r['networkMs'] = 20000
        for r,o in zip(self.http,self.ui):o['uiCompletionUpperBoundMs']=r['networkMs']+500
        result = score(self.fixture, self.http, self.ui)
        self.assertGreater(result['allResponseTiming']['p95Ms'], 15000)
        self.assertFalse(result['warmSloPassed'])

    def test_ui_alert_cannot_count_as_completed_search(self):
        self.ui[0]['ui']['alerts'] = ['検索に失敗しました']
        result = score(self.fixture, self.http, self.ui)
        self.assertFalse(result['qualityPassed'])

    def test_each_group_recall_below_90_percent_fails_even_when_hit_and_precision_pass(self):
        for kind in ['person', 'knowledge']:
            with self.subTest(kind=kind):
                http, ui = copy.deepcopy(self.http), copy.deepcopy(self.ui)
                positives = {q['id'] for q in self.fixture['questions'] if q['entityType'] == kind and q['positiveIds']}
                # Every result remains correct, but three of ten two-answer questions lose one answer.
                for row in [r for r in http if r['questionId'] in positives][:3]:
                    body = row['body']; body['primary']['candidates'].pop(); body['assessments'].pop()
                    observation = next(o for o in ui if o['questionId'] == row['questionId'])
                    observation['ui']['groups'] = [
                        {'name': name, 'titles': [c['title'] for c in body['primary']['candidates'] if c['entityType'] == group]}
                        for group, name in [('person','相談候補'),('knowledge','参考事例')]]
                result = score(self.fixture,http,ui)
                self.assertEqual(result['metrics'][kind]['groupHitAt5'],1)
                self.assertEqual(result['metrics'][kind]['groupPrecisionAt5'],1)
                self.assertAlmostEqual(result['metrics'][kind]['groupRecallAt5'],.85)
                self.assertFalse(result['qualityPassed'])
                third = [r for r in http if r['questionId'] in positives][2]['questionId']
                hi = next(i for i,r in enumerate(http) if r['questionId'] == third)
                ui_index = next(i for i,r in enumerate(ui) if r['questionId'] == third)
                http[hi] = copy.deepcopy(self.http[hi]); ui[ui_index] = copy.deepcopy(self.ui[ui_index])
                boundary = score(self.fixture,http,ui)
                self.assertAlmostEqual(boundary['metrics'][kind]['groupRecallAt5'],.9)
                self.assertTrue(boundary['qualityPassed'])
