#!/usr/bin/env python3
"""Score observed v3 JWT responses, including explicit out-of-scope 422s."""
import argparse,json,math
from pathlib import Path
from issue30_http_report import quantile
ROOT=Path(__file__).resolve().parents[1]
SCOPE='issue30-independent-20261004-v3'
def validate_ui_shape(observation):
    ui=observation.get('ui') if isinstance(observation,dict) else None
    if (not isinstance(ui,dict) or not isinstance(ui.get('status'),str)
            or not isinstance(ui.get('alerts'),list) or any(not isinstance(a,str) for a in ui['alerts'])
            or not isinstance(ui.get('groups'),list)
            or any(not isinstance(g,dict) or not isinstance(g.get('name'),str)
                   or not isinstance(g.get('titles'),list) or any(not isinstance(t,str) for t in g['titles'])
                   for g in ui['groups'])):raise ValueError('UI status/alerts/groups shape mismatch')
    return ui
def response_evidence(fixture, body, observation):
    """Shared HTTP200/UI/grounding contract for warm and cold captures."""
    if not isinstance(body,dict) or not isinstance(observation,dict):raise ValueError('Response and UI record required')
    run_id=body.get('runId')
    if not isinstance(run_id,str) or not run_id or run_id!=observation.get('runId'):raise ValueError('UI run mismatch')
    primary=body.get('primary')
    if not isinstance(primary,dict):raise ValueError('Response primary object required')
    candidates=primary.get('candidates');assessments=body.get('assessments');ui=validate_ui_shape(observation)
    if (not isinstance(candidates,list) or any(not isinstance(c,dict) for c in candidates)
            or not isinstance(assessments,list) or any(not isinstance(a,dict) for a in assessments)
            or not isinstance(ui,dict)):raise ValueError('Response shape mismatch')
    if body.get('code') is not None or any(c.get('entityType') not in ('person','knowledge') for c in candidates):raise ValueError('Error response or unknown candidate type')
    if body.get('rerankingStatus')!='succeeded' and candidates:raise ValueError('Non-success reranking cannot retain visible candidates')
    if any(not isinstance(c.get('entityId'),str) or not c['entityId'].startswith(SCOPE+'-') for c in candidates):raise ValueError('Candidate ID must belong to the expected v3 scope')
    expected=[{'name':name,'titles':[c['title'] for c in candidates if c['entityType']==kind]} for kind,name in [('person','相談候補'),('knowledge','参考事例')]]
    if ui['groups']!=expected:raise ValueError('UI grouping/order mismatch')
    assessed=[(a['entityType'],a['entityId']) for a in assessments];expected_ids=[(c['entityType'],c['entityId']) for c in candidates]
    if sorted(assessed)!=sorted(expected_ids) or len(set(assessed))!=len(assessed) or len(set(expected_ids))!=len(expected_ids):raise ValueError('Every recommendation needs exactly one assessment')
    refs={s['id']:s['text'] for u in fixture['units'] for s in u['search']['sourceRefs']};prefix=SCOPE+'-';grounded=[]
    for a in assessments:
        ids=a['evidenceIds'];reason=a['reason'];candidate=next(c for c in candidates if c['entityId']==a['entityId'] and c['entityType']==a['entityType'])
        sent={s['id']:s['text'] for u in candidate['units'] for s in u['sourceRefs']}
        originals={s['id']:s['text'] for u in fixture['units'] if u['search']['entityId']==candidate['entityId'].removeprefix(prefix) and u['search']['entityType']==candidate['entityType'] for s in u['search']['sourceRefs']}
        grounded.append(isinstance(reason,str) and bool(reason) and isinstance(ids,list) and bool(ids) and all(isinstance(i,str) and i.startswith(prefix) and i in sent and i[len(prefix):] in refs and i[len(prefix):] in originals and sent[i]==originals[i[len(prefix):]] for i in ids) and any(reason in sent.get(i,'') for i in ids))
    return candidates,grounded

def score(fixture,http,ui):
    questions={q['id']:q for q in fixture['questions']}
    if len(ui)!=24 or len(http)!=24 or {r['questionId'] for r in ui}!=set(questions) or {r['questionId'] for r in http}!=set(questions):raise ValueError('Exact24 UI and HTTP records required')
    responses={r['questionId']:r for r in http};observations={r['questionId']:r for r in ui}
    prefix=SCOPE+'-'
    rows=[];grounded=[];response_ids=set()
    for qid,q in questions.items():
        r=responses[qid];o=observations[qid];body=r['body']
        validate_ui_shape(o)
        if r['request']!={'query':q['text'],'target':'all'}:raise ValueError('Manual conditions or changed query')
        if type(r['httpStatus']) is not int or not 100<=r['httpStatus']<=599:raise ValueError('Observed integer HTTP status from 100 to 599 required')
        if r.get('errorCode') is not None:raise ValueError('Observed HTTP status cannot coexist with transport errorCode')
        outside=q['draftDomain']=='unknown' and not q['positiveIds']
        expected_outside=r['httpStatus']==422 and body.get('code')=='CLASSIFICATION_OUT_OF_SCOPE' and outside
        candidates=body.get('primary',{}).get('candidates',[])
        if type(r['networkMs']) not in (int,float) or not math.isfinite(r['networkMs']) or r['networkMs']<0:raise ValueError('Observed finite HTTP duration required')
        ui_ms=o['uiCompletionUpperBoundMs']
        if type(ui_ms) not in (int,float) or not math.isfinite(ui_ms) or ui_ms<0:raise ValueError('Observed finite UI completion duration required')
        if ui_ms<r['networkMs']:raise ValueError('UI completion cannot precede HTTP completion')
        if r['httpStatus']==200:
            candidates,checked=response_evidence(fixture,body,o)
            if body['runId'] in response_ids:raise ValueError('Warm response runId reused')
            response_ids.add(body['runId'])
            grounded.extend(checked)
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
    try:
        fixture=json.loads((ROOT/'evaluation/issue-30/independent-v3/approved/dataset.json').read_text())
        result=score(fixture,json.loads((p/'http-warm.json').read_text()),json.loads((p/'ui-warm-observations.json').read_text()))
        with (p/'http-warm-summary.json').open('x',encoding='utf-8') as file:
            json.dump(result,file,ensure_ascii=False,indent=2,allow_nan=False);file.write('\n')
        print(json.dumps({k:result[k] for k in ['qualityPassed','warmSloPassed','networkTiming','allResponseTiming']},indent=2))
        return 0 if result['qualityPassed'] and result['warmSloPassed'] else 1
    except (OSError,ValueError,TypeError,KeyError,AttributeError,OverflowError):
        parser.exit(2,'Invalid or incomplete warm records, or output already exists; no live acceptance granted.\n')
if __name__=='__main__':raise SystemExit(main())
