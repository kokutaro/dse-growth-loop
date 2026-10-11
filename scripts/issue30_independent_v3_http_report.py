#!/usr/bin/env python3
"""Score observed v3 JWT responses, including explicit out-of-scope 422s."""
import argparse,json,math
from pathlib import Path
from issue30_http_report import quantile
ROOT=Path(__file__).resolve().parents[1]
SCOPE='issue30-independent-20261004-v3'
def score(fixture,http,ui):
    questions={q['id']:q for q in fixture['questions']}
    if len(ui)!=24 or len(http)!=24 or {r['questionId'] for r in ui}!=set(questions) or {r['questionId'] for r in http}!=set(questions):raise ValueError('Exact24 UI and HTTP records required')
    responses={r['questionId']:r for r in http};observations={r['questionId']:r for r in ui}
    refs={s['id']:s['text'] for u in fixture['units'] for s in u['search']['sourceRefs']};prefix=SCOPE+'-'
    rows=[];grounded=[]
    for qid,q in questions.items():
        r=responses[qid];o=observations[qid];body=r['body']
        if r['request']!={'query':q['text'],'target':'all'}:raise ValueError('Manual conditions or changed query')
        outside=q['draftDomain']=='unknown' and not q['positiveIds']
        expected_outside=r['httpStatus']==422 and body.get('code')=='CLASSIFICATION_OUT_OF_SCOPE' and outside
        candidates=body.get('primary',{}).get('candidates',[])
        if not isinstance(r['networkMs'],(int,float)) or not math.isfinite(r['networkMs']) or r['networkMs']<0:raise ValueError('Observed finite HTTP duration required')
        if r['httpStatus']==200:
            expected=[{'name':name,'titles':[c['title'] for c in candidates if c['entityType']==kind]} for kind,name in [('person','相談候補'),('knowledge','参考事例')]]
            if o['ui']['groups']!=expected or body['runId']!=o['runId']:raise ValueError('UI grouping/order/run mismatch')
            assessed=[(a['entityType'],a['entityId']) for a in body['assessments']]
            expected_ids=[(c['entityType'],c['entityId']) for c in candidates]
            if sorted(assessed)!=sorted(expected_ids) or len(set(assessed))!=len(assessed):raise ValueError('Every recommendation needs exactly one assessment')
            for assessment in body['assessments']:
                ids=assessment['evidenceIds'];reason=assessment['reason'];candidate=next((c for c in candidates if c['entityId']==assessment['entityId'] and c['entityType']==assessment['entityType']),None)
                sent={s['id']:s['text'] for u in candidate['units'] for s in u['sourceRefs']} if candidate else {}
                grounded.append(bool(reason) and bool(ids) and all(i.startswith(prefix) and i in sent and i[len(prefix):] in refs and sent[i]==refs[i[len(prefix):]] for i in ids) and any(reason in sent.get(i,'') for i in ids))
        elif expected_outside:
            if o['ui']['groups'] or '自動検索対象外' not in o['ui']['status'] or o['ui']['alerts']:raise ValueError('Out-of-scope falsely displayed as search/failure')
        elif candidates:raise ValueError('Error response has candidates')
        search_success=not outside and r['httpStatus']==200 and body.get('rerankingStatus') in ['succeeded','all_unsuitable','no_candidates'] and '検索失敗' not in o['ui']['status'] and not o['ui']['alerts']
        ranked=[c['entityId'].removeprefix(prefix) for c in candidates if c['entityType']==q['entityType']][:5];gold=set(q['positiveIds']);hit=len(set(ranked)&gold)
        rows.append({'questionId':qid,'entityType':q['entityType'],'positive':bool(gold),'ranking':ranked,'groupHitAt5':int(hit>0) if gold else None,'groupPrecisionAt5':hit/len(ranked) if ranked else 0,'groupRecallAt5':hit/len(gold) if gold else None,'noGoldFalse':not gold and bool(candidates),'outOfScopeQuestion':outside,'expectedOutOfScope':expected_outside,'outcomePassed':expected_outside or search_success,'networkMs':r['networkMs'],'httpStatus':r['httpStatus'],'runId':body.get('runId'),'uiCompletionUpperBoundMs':o['uiCompletionUpperBoundMs']})
    metrics={}
    for kind in ['person','knowledge']:
        positive=[r for r in rows if r['positive'] and r['entityType']==kind];metrics[kind]={k:sum(r[k] for r in positive)/len(positive) for k in ['groupHitAt5','groupPrecisionAt5','groupRecallAt5']}
    # Preserve the predefined 22/2 split even when an outside question returns an incorrect 200.
    times=[r['networkMs'] for r in rows if not r['outOfScopeQuestion']];all_times=[r['networkMs'] for r in rows]
    def timing(values):return {'count':len(values),'p50Ms':quantile(values,.5),'p95Ms':quantile(values,.95),'maxMs':max(values)}
    successful=timing(times);overall=timing(all_times)
    slo=successful['p95Ms']<=15000 and overall['p95Ms']<=15000
    false=sum(r['noGoldFalse'] for r in rows);quality=all(r['outcomePassed'] for r in rows) and all(m['groupHitAt5']>=.9 and m['groupPrecisionAt5']>=.9 and m['groupRecallAt5']>=.9 for m in metrics.values()) and not false and all(grounded)
    return {'scope':SCOPE,'observedQuestions':24,'all24HttpStatusAndTimeMeasured':True,'qualityPassed':quality,'warmSloThresholdMs':15000,'warmSloPassed':slo,'networkTiming':successful,'allResponseTiming':overall,'timingDefinition':'Linear p95; both all24 responses and searched requests must meet15s. Expected out-of-scope422 durations reported separately, never used to hide slower searches. UI upper bounds include observation overhead.','metrics':metrics,'noGoldFalseQuestions':false,'groundingChecked':len(grounded),'groundingPassed':sum(grounded),'failedQuestions':[r['questionId'] for r in rows if not r['outcomePassed']],'rows':rows}
def main():
    parser=argparse.ArgumentParser();parser.add_argument('--directory',required=True,type=Path);args=parser.parse_args();p=args.directory
    fixture=json.loads((ROOT/'evaluation/issue-30/independent-v3/approved/dataset.json').read_text());result=score(fixture,json.loads((p/'http-warm.json').read_text()),json.loads((p/'ui-warm-observations.json').read_text()));(p/'http-warm-summary.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n');print(json.dumps({k:result[k] for k in ['qualityPassed','warmSloPassed','networkTiming','allResponseTiming']},indent=2))
if __name__=='__main__':main()
