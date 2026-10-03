import unittest
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/"scripts"))
from issue30_production_report import summarize

class ProductionReportTests(unittest.TestCase):
    def questions(self):
        return {
            'person-q':{'id':'person-q','entityType':'person','judgments':[{'entityId':'p-gold','grade':3}]},
            'knowledge-q':{'id':'knowledge-q','entityType':'knowledge','judgments':[{'entityId':'k-gold','grade':3}]},
            'empty-q':{'id':'empty-q','entityType':'person','judgments':[{'entityId':'p-gold','grade':0}]},
        }
    def base(self):
        return [{'questionId':'person-q','ranking':['p-gold'],'status':'succeeded','primaryRecall':1},
                {'questionId':'knowledge-q','ranking':['k-gold'],'status':'succeeded','primaryRecall':1}]
    def test_actual_visible_top_five_keeps_other_types_in_display_slots(self):
        rows=self.base();rows[1]['ranking']=['other-'+str(i) for i in range(5)]+['k-gold']
        metrics,_=summarize(rows,self.questions())
        self.assertEqual(metrics['knowledge']['typedHitAt5'],1)
        self.assertEqual(metrics['knowledge']['typedPrecisionAt5'],1)
        self.assertEqual(metrics['knowledge']['visibleHitAt5'],0)
        self.assertEqual(metrics['knowledge']['visiblePrecisionAt5'],0)
        self.assertEqual(metrics['otherTypeQuestions'],1)
    def test_manual_unknown_is_separate_from_nonempty_false_recommendation(self):
        rows=self.base()+[{'questionId':'empty-q','ranking':[],'error':'CLASSIFICATION_MANUAL_REQUIRED'}]
        metrics,_=summarize(rows,self.questions())
        self.assertEqual(metrics['noGoldFalseQuestions'],0)
        self.assertEqual(metrics['statusCounts']['CLASSIFICATION_MANUAL_REQUIRED'],1)
        rows[-1].update(ranking=['p-gold'],status='not_performed:inputlimit')
        metrics,_=summarize(rows,self.questions())
        self.assertEqual(metrics['noGoldFalseQuestions'],1)
        self.assertEqual(metrics['person']['positiveQuestions'],1)
    def test_fallback_with_a_gold_hit_is_not_counted_as_rerank_success(self):
        rows=self.base();rows[0]['status']='not_performed:inputlimit'
        metrics,_=summarize(rows,self.questions())
        self.assertEqual(metrics['person']['visibleHitAt5'],1)
        self.assertEqual(metrics['statusCounts']['succeeded'],1)
        self.assertEqual(metrics['statusCounts']['not_performed:inputlimit'],1)
if __name__=='__main__':unittest.main()
