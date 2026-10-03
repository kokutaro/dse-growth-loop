import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[2]/'scripts'))
from issue30_hardening_production_report import gates
class HardeningGatesTests(unittest.TestCase):
    def limits(self):return dict(primaryRecallPerTypeMinimum=.9,finalHitRateAt5Minimum=.9,targetTypePrecisionAt5Minimum=.9,noGoldFalseRecommendationsMaximum=0,otherTypeRecommendationsMaximum=0)
    def metrics(self):return {'person':dict(primaryRecall=1,visibleHitAt5=1,visiblePrecisionAt5=1),'knowledge':dict(primaryRecall=1,visibleHitAt5=1,visiblePrecisionAt5=1),'noGoldFalseQuestions':0,'otherTypeQuestions':0}
    def test_primary_hit_does_not_hide_failed_rerank(self):
        checks=gates(self.metrics(),[{'gold':['positive'],'status':'not_performed:inputlimit'}],self.limits())
        self.assertFalse(checks['positiveRerankSucceeded']);self.assertFalse(all(checks.values()))
    def test_zero_precision_and_manual_abstention_are_distinct(self):
        rows=[{'gold':['positive'],'status':'succeeded'},{'gold':[],'status':'CLASSIFICATION_MANUAL_REQUIRED'}]
        self.assertTrue(all(gates(self.metrics(),rows,self.limits()).values()))
        metrics=self.metrics();metrics['knowledge']['visiblePrecisionAt5']=0
        self.assertFalse(gates(metrics,rows,self.limits())['knowledgeVisiblePrecisionAt5'])
if __name__=='__main__':unittest.main()
