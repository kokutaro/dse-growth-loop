import copy,json,sys,unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'scripts'))
import issue30_http_report as report
class HttpReportTests(unittest.TestCase):
    def setUp(self):
        p=ROOT/'docs/validation-results/20261004-issue-30-independent-v2-production'
        self.fixture=json.loads((ROOT/'evaluation/issue-30/independent-v2/approved/dataset.json').read_text())
        self.http=json.loads((p/'http-warm.json').read_text());self.ui=json.loads((p/'ui-warm-observations.json').read_text())
    def test_failures_are_retained_without_invented_http_time(self):
        d=report.score(self.fixture,self.http,self.ui,'issue30-independent-20261004-v2')
        self.assertEqual(d['observedQuestions'],24);self.assertEqual(d['networkTiming']['count'],22)
        self.assertEqual(len(d['failedUiQuestions']),2);self.assertFalse(d['warmSloPassed'])
        self.assertAlmostEqual(d['metrics']['person']['groupRecallAt5'],.9)
        self.assertAlmostEqual(d['metrics']['knowledge']['groupRecallAt5'],.95)
        self.assertEqual(d['noGoldFalseQuestions'],0)
        self.assertTrue(all(r['networkMs'] is None for r in d['rows'] if r['uiFailed']))
    def test_mismatched_ui_group_or_manual_conditions_are_rejected(self):
        bad=copy.deepcopy(self.ui);bad[0]['ui']['groups'][0]['titles'].reverse()
        with self.assertRaises(ValueError):report.score(self.fixture,self.http,bad,'issue30-independent-20261004-v2')
        bad=copy.deepcopy(self.http);bad[0]['request']['conditions']={}
        with self.assertRaises(ValueError):report.score(self.fixture,bad,self.ui,'issue30-independent-20261004-v2')
