#!/usr/bin/env python3
"""保存済みJWT検索応答とUI観測の集計。追加推論・失敗の補完は行わない。"""
import argparse,json,math
from pathlib import Path

def quantile(values, q):
    values=sorted(values)
    if not values:return None
    pos=(len(values)-1)*q;lo=math.floor(pos);hi=math.ceil(pos)
    return values[lo]+(values[hi]-values[lo])*(pos-lo)

def score(fixture, http, ui, scope):
    questions={q['id']:q for q in fixture['questions']}
    if len(ui)!=24 or len({r['questionId'] for r in ui})!=24 or {r['questionId'] for r in ui}!=set(questions):
        raise ValueError('Exact approved 24 observations required')
    responses={r['questionId']:r for r in http}
    if len(responses)!=len(http):raise ValueError('Duplicate HTTP response')
    evidence={s['id']:s['text'] for u in fixture['units'] for s in u['search']['sourceRefs']}
    prefix=scope+'-';rows=[];grounded=[]
    for observed in ui:
        q=questions[observed['questionId']];r=responses.get(q['id']);failed='検索失敗' in observed['ui']['status']
        candidates=[];body={}
        if r is not None:
            if r['request']!={'query':q['text'],'target':'all'}:raise ValueError('Changed query/target or manual conditions')
            body=r['body'];candidates=body['primary']['candidates']
            expected=[{'name':name,'titles':[c['title'] for c in candidates if c['entityType']==kind]} for kind,name in [('person','相談候補'),('knowledge','参考事例')]]
            if observed['ui']['groups']!=expected:raise ValueError('UI grouping/order differs from HTTP response')
            if body['runId']!=observed['runId']:raise ValueError('Wrong observed retrieval run')
            for a in body['assessments']:
                cand=next((c for c in candidates if c['entityId']==a['entityId'] and c['entityType']==a['entityType']),None)
                refs={s['id']:s['text'] for u in cand['units'] for s in u['sourceRefs']} if cand else {}
                valid=bool(a['evidenceIds']) and all(s.startswith(prefix) and s in refs and s[len(prefix):] in evidence and refs[s]==evidence[s[len(prefix):]] for s in a['evidenceIds'])
                grounded.append(valid and any(a['reason'] in refs.get(s,'') for s in a['evidenceIds']))
        elif not failed:raise ValueError('Successful UI observation missing HTTP capture')
        ranked=[c['entityId'].removeprefix(prefix) for c in candidates if c['entityType']==q['entityType']][:5]
        combined=[c['entityId'].removeprefix(prefix) for c in candidates][:5]
        gold=set(q['positiveIds']);hits=len(set(ranked)&gold)
        rows.append({'questionId':q['id'],'entityType':q['entityType'],'positive':bool(gold),'ranking':ranked,
            'groupHitAt5':int(hits>0) if gold else None,'groupPrecisionAt5':hits/len(ranked) if ranked else 0 if gold else None,
            'groupRecallAt5':hits/len(gold) if gold else None,'combinedPrecisionAt5':len(set(combined)&gold)/len(combined) if combined else 0 if gold else None,
            'noGoldFalse':not gold and bool(candidates),'uiFailed':failed,'networkMs':r['networkMs'] if r else None,
            'httpStatus':r['httpStatus'] if r else None,'runId':body.get('runId'),'uiCompletionUpperBoundMs':observed['uiCompletionUpperBoundMs']})
    metrics={}
    for kind in ['person','knowledge']:
        positive=[r for r in rows if r['positive'] and r['entityType']==kind]
        metrics[kind]={key:sum(r[key] for r in positive)/len(positive) for key in ['groupHitAt5','groupPrecisionAt5','groupRecallAt5','combinedPrecisionAt5']}
    times=[r['networkMs'] for r in rows if r['networkMs'] is not None];timing={'count':len(times),'p50Ms':quantile(times,.5),'p95Ms':quantile(times,.95),'maxMs':max(times)}
    return {'scope':'JWT SPA warm all-target 24 timing repeats; observed holdout, no tuning; initial independent results retained',
        'observedQuestions':len(rows),'failedUiQuestions':[r['questionId'] for r in rows if r['uiFailed']],
        'noGoldFalseQuestions':sum(r['noGoldFalse'] for r in rows),'metrics':metrics,'groundingChecked':len(grounded),'groundingPassed':sum(grounded),
        'networkTiming':timing,'timingLimitation':'Two failed UI requests absent from HAR; HTTP status/time not invented. UI upper bounds include observation overhead. Warm HTTP p95 only successful captures.',
        'warmSloThresholdMs':15000,'warmSloPassed':timing['p95Ms']<=15000,'all24AvailabilityMeasured':False,'rows':rows}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--directory',type=Path,default=Path('docs/validation-results/20261004-issue-30-independent-v2-production'));args=parser.parse_args()
    fixture=json.loads(Path('evaluation/issue-30/independent-v2/approved/dataset.json').read_text());p=args.directory
    result=score(fixture,json.loads((p/'http-warm.json').read_text()),json.loads((p/'ui-warm-observations.json').read_text()),'issue30-independent-20261004-v2')
    (p/'http-warm-summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');print(json.dumps({k:v for k,v in result.items() if k!='rows'},ensure_ascii=False))
if __name__=='__main__':main()
