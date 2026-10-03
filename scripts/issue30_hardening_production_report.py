#!/usr/bin/env python3
"""Score observed production regression evidence; never sends AI requests."""
import hashlib,json,math,statistics
from pathlib import Path
from issue30_production_report import summarize
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'docs/validation-results/20261003-issue-30-hardening-production'
def gates(metrics,rows,limits):
    checks={}
    for typ in ('person','knowledge'):
        m=metrics[typ]
        checks[typ+'PrimaryRecall']=m['primaryRecall'] is not None and m['primaryRecall']>=limits['primaryRecallPerTypeMinimum']
        checks[typ+'VisibleHitAt5']=m['visibleHitAt5']>=limits['finalHitRateAt5Minimum']
        checks[typ+'VisiblePrecisionAt5']=m['visiblePrecisionAt5']>=limits['targetTypePrecisionAt5Minimum']
    checks['noGoldFalse']=metrics['noGoldFalseQuestions']<=limits['noGoldFalseRecommendationsMaximum']
    checks['otherType']=metrics['otherTypeQuestions']<=limits['otherTypeRecommendationsMaximum']
    checks['positiveRerankSucceeded']=all(r['status']=='succeeded' for r in rows if r['gold'])
    return checks
def main():
    plan=json.loads((OUT/'execution-plan.json').read_text())
    for path,expected in plan['sha256'].items():
        if hashlib.sha256((ROOT/path).read_bytes()).hexdigest()!=expected:raise ValueError('Frozen file changed: '+path)
    report=json.loads((OUT/'production-report.json').read_text())
    if not report['completed'] or not report['plan']['observedRegressionSet'] or report['plan']['independentHoldout']:raise ValueError('Completed observed regression required')
    questions={q['id']:q for q in json.loads((ROOT/'evaluation/issue-30/independent/dataset.json').read_text())['questions']}
    metrics={};evaluated={}
    for arm in plan['arms']:
        rows=[r for r in report['rows'] if r['arm']==arm]
        if len(rows)!=24 or len({r['questionId'] for r in rows})!=24:raise ValueError('Expected 24 unique questions per arm')
        metrics[arm],evaluated[arm]=summarize(rows,questions)
        ms=sorted(r['durationMs'] for r in rows)
        for typ in ('person','knowledge'):
            positive=[r for r in evaluated[arm] if r['entityType']==typ and r['gold']]
            metrics[arm][typ]['visibleRecallAt5']=statistics.mean(len(set(r['gold']).intersection(r['ranking'][:5]))/len(r['gold']) for r in positive)
            metrics[arm][typ]['lostGoldAt5']=[{'questionId':r['questionId'],'entityIds':sorted(set(r['gold'])-set(r['ranking'][:5]))} for r in positive if set(r['gold'])-set(r['ranking'][:5])]
        metrics[arm]['serviceTiming']={'p50Ms':statistics.median(ms),'p95Ms':ms[math.ceil(.95*len(ms))-1],'maxMs':max(ms),'publicHttpSlo':False}
    checks=gates(metrics['targeted'],evaluated['targeted'],plan['gates'])
    audits=report['aiAudits'];counts={}
    for audit in audits:counts[audit['status']]=counts.get(audit['status'],0)+1
    trace=[a['trace'] for a in audits]
    usage={'aiAuditCount':len(audits),'statusCounts':counts,'attemptCount':sum(t['attemptCount'] for t in trace),'cachedResponses':sum('cachedFromRunId' in t for t in trace),'inputTokens':sum(t['inputTokens'] for t in trace),'outputTokens':sum(t['outputTokens'] for t in trace),'estimatedAiJpy':sum(t['estimatedCostJpy'] for t in trace),'usageEstimatedRuns':sum(t['usageEstimated'] for t in trace),'inputLimitFailures':sum(a.get('errorCode')=='AI_INPUTLIMIT' for a in audits),'requestUnits':sum(r.get('requestUnits') or 0 for r in report['rows']),'ruScope':'Search budgets only; audit export/blob/admin operations excluded','ledgerBefore':report['ledgerBefore'],'ledgerAfter':report['ledgerAfter']}
    result={'regressionQualityGate':'passed' if all(checks.values()) else 'failed','gateChecks':checks,'independentHoldout':False,'observedQuestions':24,'searches':48,'arms':metrics,'usage':usage,'evaluatedRows':evaluated,'previousManagedIdentity':json.loads((ROOT/'docs/validation-results/20261003-issue-30-independent-production/summary.json').read_text())['phase1ManagedIdentity']}
    (OUT/'summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({k:v for k,v in result.items() if k!='evaluatedRows'},ensure_ascii=False,indent=2))
if __name__=='__main__':main()
