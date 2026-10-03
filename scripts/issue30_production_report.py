#!/usr/bin/env python3
"""Aggregate captured independent production evidence; no network or AI calls."""
import csv,json,math,statistics
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'docs/validation-results/20261003-issue-30-independent-production'
def summarize(rows,questions):
    evaluated=[]
    for row in rows:
        q=questions[row['questionId']];gold={j['entityId'] for j in q['judgments'] if j['grade']>=2 and not j.get('insufficient',False)}
        ranking=row['ranking'];typed=[i for i in ranking if i in {j['entityId'] for j in q['judgments']}]
        actual=ranking[:5];typed5=typed[:5]
        evaluated.append({'questionId':q['id'],'entityType':q['entityType'],'gold':sorted(gold),'ranking':ranking,'status':row.get('status',row.get('rerankingStatus')) or row.get('error'),'primaryRecall':row.get('primaryRecall'),'visibleHitAt5':int(bool(gold.intersection(actual))) if gold else None,'visiblePrecisionAt5':len(gold.intersection(actual))/len(actual) if actual else 0,'typedHitAt5':int(bool(gold.intersection(typed5))) if gold else None,'typedPrecisionAt5':len(gold.intersection(typed5))/len(typed5) if typed5 else 0,'noGoldFalse':not gold and bool(ranking),'otherTypeCount':len(ranking)-len(typed),'rankingCount':len(ranking),'requestUnits':row.get('requestUnits')})
    metrics={}
    for typ in ['person','knowledge']:
        subset=[r for r in evaluated if r['entityType']==typ and r['gold']]
        primary=[r['primaryRecall'] for r in subset if r['primaryRecall'] is not None]
        metrics[typ]={'positiveQuestions':len(subset),'primaryRecall':statistics.mean(primary) if primary else None,'visibleHitAt5':sum(r['visibleHitAt5'] for r in subset)/len(subset),'visiblePrecisionAt5':statistics.mean(r['visiblePrecisionAt5'] for r in subset),'typedHitAt5':sum(r['typedHitAt5'] for r in subset)/len(subset),'typedPrecisionAt5':statistics.mean(r['typedPrecisionAt5'] for r in subset)}
    metrics.update(noGoldQuestions=sum(not r['gold'] for r in evaluated),noGoldFalseQuestions=sum(r['noGoldFalse'] for r in evaluated),otherTypeQuestions=sum(r['otherTypeCount']>0 for r in evaluated),statusCounts={s:sum(r['status']==s for r in evaluated) for s in sorted({r['status'] for r in evaluated})})
    return metrics,evaluated
def main():
    questions={q['id']:q for q in json.loads((ROOT/'evaluation/issue-30/independent/dataset.json').read_text())['questions']}
    mi=json.loads((OUT/'production-report.json').read_text());http=json.loads((OUT/'http-observations.json').read_text())
    mm,mr=summarize(mi['rows'],questions);hm,hr=summarize(http['rows'],questions)
    durations=sorted(r['uiCompletionUpperBoundMs'] for r in http['rows'] if r['uiCompletionUpperBoundMs'] is not None)
    timing={'measurement':'Submit action to UI completion observation, an upper bound including HTTP/UI/polling; not isolated network timing','measuredWarmQuestions':len(durations),'firstUseMissing':1,'p50UpperBoundMs':statistics.median(durations),'p95UpperBoundMs':durations[math.ceil(.95*len(durations))-1],'maxUpperBoundMs':max(durations),'full24HttpSloClaim':False}
    if 'cold' in http:timing['cold']=http['cold']
    summary={'overallQualityGate':'failed','phase1ManagedIdentity':mm,'phase2AuthenticatedHttp':hm,'httpTiming':timing,'independentSyntheticOnly':True,'holdoutTuned':False}
    (OUT/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n')
    for name,rows in [('production',mr),('http',hr)]:
        with (OUT/(name+'-metrics.csv')).open('w') as f:
            fields=['questionId','entityType','status','primaryRecall','visibleHitAt5','visiblePrecisionAt5','typedHitAt5','typedPrecisionAt5','noGoldFalse','otherTypeCount','rankingCount','requestUnits'];w=csv.DictWriter(f,fieldnames=fields);w.writeheader();w.writerows({k:r[k] for k in fields} for r in rows)
    print(json.dumps(summary,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
