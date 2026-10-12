import copy
import json
import contextlib
import io
import tempfile
from unittest.mock import patch
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
import issue30_timing_report as report_module
from issue30_timing_report import score_cold
from azure_validation import environment_id

SUB = 'e17a163e-1f3f-4a5d-8709-b8697066a8e5'


class ColdTimingTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.loads((ROOT / 'evaluation/issue-30/independent-v3/approved/dataset.json').read_text())
        questions = [q for q in self.fixture['questions'] if q['draftDomain'] != 'unknown'][:3]
        self.plan = [q['id'] for q in questions]
        self.rows = []
        for i, q in enumerate(questions):
            env = environment_id(SUB, 'dse-environment-vnet-jpe')
            snapshot = {'mode': 'replicas', 'runId': f'zero-{i}', 'finishedAt': f'2026-10-11T14:{i:02d}:00Z',
                        'target': {'subscription': SUB, 'resourceGroup': 'dse-growth-loop-rg', 'app': 'dse-api-vnet', 'environment': 'dse-environment-vnet-jpe'},
                        'checks': [{'id': 'app-environment', 'status': 'passed', 'data': {'name': 'dse-api-vnet', 'environment': env}},
                                   {'id': 'all-revisions', 'status': 'passed', 'data': [{'name': 'rev', 'active': False, 'replicas': 0, 'status': 'Stopped'}]},
                                   {'id': 'replicas-rev', 'status': 'passed', 'data': {'count': 0}},
                                   {'id': 'all-revisions-final', 'status': 'passed', 'data': [{'name': 'rev', 'active': False, 'replicas': 0, 'status': 'Stopped'}]}]}
            self.rows.append({'questionId': q['id'], 'cycleId': f'cycle-{i}', 'attempt': 1,
                              'request': {'query': q['text'], 'target': 'all'}, 'httpStatus': 200,
                              'networkMs': 5000, 'uiCompletionUpperBoundMs': 5500,
                              'startedAtUtc': f'2026-10-11T14:{i:02d}:01Z', 'finishedAtUtc': f'2026-10-11T14:{i:02d}:06Z',
                              'preSearchRequestCount': 0, 'zeroSnapshot': snapshot})
        # Reuse the existing v3 quality fixture, not a second invented quality contract.
        import test_issue30_independent_v3_http_report as warm_fixture
        warm=warm_fixture.IndependentV3HttpTests();warm.setUp()
        http={r['questionId']:r for r in warm.http};ui={r['questionId']:r for r in warm.ui}
        for row in self.rows:
            row['body']=copy.deepcopy(http[row['questionId']]['body'])
            row['uiObservation']=copy.deepcopy(ui[row['questionId']])
            row['body']['runId']='cold-'+row['body']['runId']
            row['uiObservation']['runId']=row['body']['runId']
            row['uiObservation']['uiCompletionUpperBoundMs']=row['uiCompletionUpperBoundMs']

    def score(self, rows=None, plan=None):
        return score_cold(self.fixture, self.plan if plan is None else plan, self.rows if rows is None else rows, SUB)

    def test_complete_three_independent_cold_cycles_have_no_slo_or_live_promotion(self):
        report = self.score()
        self.assertTrue(report['coldMeasurementComplete'])
        self.assertEqual(report['networkTiming']['count'], 3)
        self.assertEqual(report['networkTiming']['maxMs'], 5000)
        self.assertIsNone(report['coldSloPassed'])
        self.assertEqual(report['liveAcceptance'], 'pending')
        self.assertEqual(report['evidenceKind'], 'supplied-records-not-independently-attested')

    def test_missing_duplicate_changed_or_unplanned_questions_are_rejected(self):
        for rows in (self.rows[:2], self.rows + [self.rows[0]], [self.rows[0]] * 3):
            with self.subTest(rows=len(rows)), self.assertRaises(ValueError): self.score(rows)
        with self.assertRaises(ValueError): self.score(plan=self.plan[:2])
        self.rows[0]['request']['conditions'] = {}
        with self.assertRaises(ValueError): self.score()

    def test_reused_cycle_or_zero_snapshot_cannot_count_as_three_colds(self):
        for field in ('cycleId', 'zeroSnapshot'):
            rows = copy.deepcopy(self.rows)
            rows[1][field] = rows[0][field]
            with self.subTest(field=field), self.assertRaises(ValueError): self.score(rows)

    def test_get_warming_retry_and_nonzero_revision_refuse_cold_classification(self):
        for field, value in (('preSearchRequestCount', 1), ('preSearchRequestCount', False), ('attempt', 2), ('attempt', True)):
            rows = copy.deepcopy(self.rows); rows[0][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError): self.score(rows)
        self.rows[0]['zeroSnapshot']['checks'][1]['data'][0]['replicas'] = 1
        with self.assertRaises(ValueError): self.score()

    def test_wrong_target_stale_order_or_unknown_replica_inventory_are_rejected(self):
        rows = copy.deepcopy(self.rows); rows[0]['zeroSnapshot']['target']['app'] = 'dse-api'
        with self.assertRaises(ValueError): self.score(rows)
        rows = copy.deepcopy(self.rows); rows[0]['zeroSnapshot']['checks'][0]['data']['environment'] = 'wrong-env'
        with self.assertRaises(ValueError): self.score(rows)
        rows = copy.deepcopy(self.rows); rows[0]['zeroSnapshot']['finishedAt'] = '2026-10-12T00:00:00Z'
        with self.assertRaises(ValueError): self.score(rows)
        rows = copy.deepcopy(self.rows); rows[0]['zeroSnapshot']['checks'][2]['data']['count'] = None
        with self.assertRaises(ValueError): self.score(rows)

    def test_duration_bool_nan_infinity_negative_or_inconsistent_timestamps_are_rejected(self):
        for value in (True, float('nan'), float('inf'), -1, 4000):
            rows = copy.deepcopy(self.rows); rows[0]['networkMs'] = value
            with self.subTest(value=value), self.assertRaises(ValueError): self.score(rows)
        self.rows[0]['startedAtUtc'] = '2026-10-11T14:00:01'
        with self.assertRaises(ValueError): self.score()

    def test_timeout_is_retained_without_becoming_success_or_zero_cost(self):
        self.rows[1]['httpStatus'] = None
        self.rows[1]['errorCode'] = 'TIMEOUT'
        self.rows[1]['uiCompletionUpperBoundMs'] = None
        report = self.score()
        self.assertFalse(report['coldMeasurementComplete'])
        self.assertEqual(report['networkTiming']['count'], 3)
        self.assertEqual(report['failedQuestions'], [self.plan[1]])
        self.assertIsNone(report['billedCost'])

    def test_ui_failure_or_timing_before_http_completion_is_not_complete(self):
        self.rows[0]['uiCompletionUpperBoundMs'] = None
        self.assertFalse(self.score()['coldMeasurementComplete'])
        for value in (True, float('nan'), -1, 4000):
            self.rows[0]['uiCompletionUpperBoundMs'] = value
            with self.subTest(value=value), self.assertRaises(ValueError): self.score()

    def test_cli_is_offline_keeps_acceptance_pending_and_preserves_existing_output(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            for name, value in (('http-warm.json', []), ('ui-warm-observations.json', []), ('http-cold.json', self.rows)):
                (folder / name).write_text(json.dumps(value))
            args = ['timing-report', '--directory', directory, '--subscription', SUB, '--cold-question-ids', *self.plan]
            with patch.object(sys, 'argv', args), patch.object(report_module, 'score_warm', return_value={'qualityPassed': True, 'warmSloPassed': True}), \
                    patch('subprocess.run', side_effect=AssertionError('External command forbidden')) as external, contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(report_module.main(), 0)
                output = (folder / 'timing-summary.json').read_text()
                self.assertEqual(json.loads(output)['liveAcceptance'], 'pending')
                self.assertEqual(json.loads(output)['approval'], 'not_granted_by_this_report')
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                    report_module.main()
                self.assertEqual(error.exception.code, 2)
                self.assertEqual((folder / 'timing-summary.json').read_text(), output)
                external.assert_not_called()

    def test_cli_incomplete_record_is_nonzero_without_exporting_payload(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            for name in ('http-warm.json', 'ui-warm-observations.json', 'http-cold.json'):
                (folder / name).write_text(json.dumps({'secret': 'never-store'}))
            args = ['timing-report', '--directory', directory, '--subscription', SUB, '--cold-question-ids', *self.plan]
            with patch.object(sys, 'argv', args), contextlib.redirect_stderr(io.StringIO()) as output, self.assertRaises(SystemExit) as error:
                report_module.main()
            self.assertEqual(error.exception.code, 2)
            self.assertNotIn('never-store', output.getvalue())
            self.assertFalse((folder / 'timing-summary.json').exists())

    def test_malformed_identity_and_transport_status_are_rejected(self):
        for change in ('missing-checks', 'unknown-status', 'missing-error', 'nonlist-rows', 'invalid-identity'):
            rows = copy.deepcopy(self.rows)
            if change == 'missing-checks': rows[0]['zeroSnapshot']['checks'] = []
            elif change == 'unknown-status': rows[0]['httpStatus'] = True
            elif change == 'missing-error': rows[0]['httpStatus'] = None
            elif change == 'nonlist-rows': rows = {'token': 'never-store'}
            elif change == 'invalid-identity': rows[0]['zeroSnapshot']['checks'][0]['data'] = [1]
            with self.subTest(change=change), self.assertRaises(ValueError): self.score(rows)

    def test_cold_http200_empty_body_invalid_evidence_or_ui_error_cannot_pass(self):
        for kind in ('empty-body','invalid-evidence','ui-alert','empty-positive-candidates','wrong-run','unscoped-candidate'):
            rows=copy.deepcopy(self.rows)
            if kind=='empty-body':rows[0]['body']={}
            elif kind=='invalid-evidence':rows[0]['body']['assessments'][0]['evidenceIds']=['invented']
            elif kind=='ui-alert':rows[0]['uiObservation']['ui']['alerts']=['検索失敗']
            elif kind=='empty-positive-candidates':
                rows[0]['body']['primary']['candidates']=[];rows[0]['body']['assessments']=[]
                rows[0]['uiObservation']['ui']['groups']=[{'name':'相談候補','titles':[]},{'name':'参考事例','titles':[]}]
            elif kind=='wrong-run':rows[0]['uiObservation']['runId']='other-request'
            else:
                for item in rows[0]['body']['primary']['candidates']+rows[0]['body']['assessments']:
                    item['entityId']=item['entityId'].removeprefix(report_module.SCOPE+'-')
            with self.subTest(kind=kind):
                result=self.score(rows)
                self.assertFalse(result['coldQualityPassed'])
                self.assertIn(self.plan[0],result['failedQuestions'])
                self.assertEqual(result['networkTiming']['count'],3)
                self.assertEqual(result['liveAcceptance'],'pending')

    def test_expired_http401_is_retained_as_failure(self):
        self.rows[0]['httpStatus']=401
        result=self.score()
        self.assertFalse(result['coldQualityPassed'])
        self.assertIn(self.plan[0],result['failedQuestions'])
        self.assertEqual(result['networkTiming']['count'],3)

    def test_borrowed_original_and_irrelevant_top5_cannot_pass_existing_quality_gate(self):
        rows=copy.deepcopy(self.rows)
        borrowed=rows[2]['body']['primary']['candidates'][0]
        original_assessment=rows[2]['body']['assessments'][0]
        rows[0]['body']['primary']['candidates'][0]['units']=borrowed['units']
        rows[0]['body']['assessments'][0]['evidenceIds']=original_assessment['evidenceIds']
        rows[0]['body']['assessments'][0]['reason']=original_assessment['reason']
        self.assertFalse(self.score(rows)['coldQualityPassed'])
        rows=copy.deepcopy(self.rows)
        rows[0]['body']['primary']['candidates'].append(copy.deepcopy(rows[2]['body']['primary']['candidates'][0]))
        rows[0]['body']['assessments'].append(copy.deepcopy(rows[2]['body']['assessments'][0]))
        candidates=rows[0]['body']['primary']['candidates']
        rows[0]['uiObservation']['ui']['groups']=[{'name':name,'titles':[c['title'] for c in candidates if c['entityType']==kind]} for kind,name in [('person','相談候補'),('knowledge','参考事例')]]
        self.assertFalse(self.score(rows)['coldQualityPassed'])

    def test_cold_response_run_ids_cannot_be_reused_across_cycles(self):
        rows=copy.deepcopy(self.rows)
        for row in rows:
            row['body']['runId']='same-response'
            row['uiObservation']['runId']='same-response'
        with self.assertRaises(ValueError):self.score(rows)

    def test_cli_rejects_response_run_id_reused_from_warm(self):
        import test_issue30_independent_v3_http_report as warm_fixture
        warm=warm_fixture.IndependentV3HttpTests();warm.setUp()
        rows=copy.deepcopy(self.rows)
        rows[0]['body']['runId']=warm.http[0]['body']['runId']
        rows[0]['uiObservation']['runId']=warm.http[0]['body']['runId']
        with tempfile.TemporaryDirectory() as directory:
            folder=Path(directory)
            for name,value in (('http-warm.json',warm.http),('ui-warm-observations.json',warm.ui),('http-cold.json',rows)):
                (folder/name).write_text(json.dumps(value))
            args=['timing-report','--directory',directory,'--subscription',SUB,'--cold-question-ids',*self.plan]
            with patch.object(sys,'argv',args),contextlib.redirect_stderr(io.StringIO()),self.assertRaises(SystemExit) as error:
                report_module.main()
            self.assertEqual(error.exception.code,2)
            self.assertFalse((folder/'timing-summary.json').exists())

    def test_cli_malformed_warm_shapes_and_duration_return_sanitized_exit2(self):
        import test_issue30_independent_v3_http_report as warm_fixture
        warm=warm_fixture.IndependentV3HttpTests();warm.setUp()
        for kind in ('null-candidate','null-primary','null-body','unrepresentable-duration'):
            http=copy.deepcopy(warm.http)
            http[0]['body']['secret']='never-store'
            if kind=='null-candidate':http[0]['body']['primary']['candidates']=[None]
            elif kind=='null-primary':http[0]['body']['primary']=None
            elif kind=='null-body':http[0]['body']=None
            else:http[0]['networkMs']=10**1000
            with self.subTest(kind=kind),tempfile.TemporaryDirectory() as directory:
                folder=Path(directory)
                for name,value in (('http-warm.json',http),('ui-warm-observations.json',warm.ui),('http-cold.json',self.rows)):
                    (folder/name).write_text(json.dumps(value))
                args=['timing-report','--directory',directory,'--subscription',SUB,'--cold-question-ids',*self.plan]
                with patch.object(sys,'argv',args),contextlib.redirect_stderr(io.StringIO()) as error_output, \
                        contextlib.redirect_stdout(io.StringIO()) as output,self.assertRaises(SystemExit) as error:
                    report_module.main()
                self.assertEqual(error.exception.code,2)
                self.assertEqual(output.getvalue(),'')
                self.assertNotIn('Traceback',error_output.getvalue())
                self.assertNotIn('never-store',error_output.getvalue())
                self.assertFalse((folder/'timing-summary.json').exists())
