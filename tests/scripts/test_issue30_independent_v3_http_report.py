import copy
import json
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'scripts'))
from issue30_independent_v3_http_report import SCOPE, score


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

    def test_changed_request_and_fast_outside_cannot_hide_slow_search(self):
        bad = copy.deepcopy(self.http)
        bad[0]['request']['conditions'] = {}
        with self.assertRaises(ValueError): score(self.fixture, bad, self.ui)
        for r in self.http:
            r['networkMs'] = 16000 if r['httpStatus'] == 200 else 1
        self.assertFalse(score(self.fixture, self.http, self.ui)['warmSloPassed'])

    def test_missing_assessment_and_invented_reason_cannot_pass_grounding(self):
        bad = copy.deepcopy(self.http)
        bad[0]['body']['assessments'].pop()
        with self.assertRaises(ValueError): score(self.fixture, bad, self.ui)
        self.http[0]['body']['assessments'][0]['reason'] = 'invented evidence'
        self.assertFalse(score(self.fixture, self.http, self.ui)['qualityPassed'])
