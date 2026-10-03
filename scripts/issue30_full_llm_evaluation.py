#!/usr/bin/env python3
"""Frozen full-cohort Issue 30 evaluation; planning and scoring never send requests."""
import argparse
import csv
import io
import json
from decimal import Decimal
from pathlib import Path
import llm_rerank_evaluation as llm
import issue30_llm_evaluation as prior

ROOT = prior.ROOT
PLAN = ROOT / 'evaluation/issue-30/full-llm'
OUTPUT = ROOT / 'docs/validation-results/20261003-issue-30-full-llm'
SOURCE = prior.SOURCE
ORDERS = [('original', 1), ('reversed', 1), ('original', 2)]

def key(t): return t['questionId'], t['order'], t['attempt']
def tasks_for(rows, dataset):
    rows = [r for r in rows if r['caseLimit'] == 15]
    if len(rows) != 48 or len({r['retrieval']['questionId'] for r in rows}) != 48:
        raise ValueError('Unexpected full cohort')
    return llm.plan([{'setting': {'variant': 'E'}, 'questionId': r['retrieval']['questionId'],
                     'candidateSha256': r['retrieval']['candidateSha256'], 'candidates': r['retrieval']['selected']}
                    for r in rows], dataset)

def reusable(task, sources):
    for name, tasks, responses in sources:
        match = next((t for t in tasks if key(t) == key(task) and llm.payload(t, 'dse-luna6-eval') == llm.payload(task, 'dse-luna6-eval')), None)
        if match:
            response = next((r for r in responses if key(r) == key(match) and r['status'] == 'success'), None)
            if response:
                parsed = llm.parse(response['wireAttempts'][-1]['body'], task)
                if [{'entityId': i['candidateId'], 'reason': i['reason'], 'evidenceIds': i['evidenceIds']} for i in parsed] != response['items']:
                    raise ValueError('Prior raw response mismatch')
                return {'taskId': task['id'], 'source': name, 'payloadSha256': llm.sha(llm.encode(llm.payload(task, 'dse-luna6-eval'))),
                        'priorCandidateSha256': response['candidateSha256'],
                        'result': {**response, 'candidateSha256': task['candidateSha256'], 'origin': 'exact_prior_payload_reuse'}}
    return None

def prepare():
    if PLAN.exists(): raise ValueError('Plan already exists')
    dataset = prior.load(ROOT / 'evaluation/expansion-v2/dataset.json')
    tasks = tasks_for(prior.load(SOURCE / 'questions.json'), dataset)
    old_plan = ROOT / 'evaluation/expansion-v2/luna6/plan.json'
    old_responses = ROOT / 'docs/validation-results/20261003-issue-14-luna6/comparisons.json'
    changed_tasks = ROOT / 'evaluation/issue-30/llm/all-tasks.json'
    changed_responses = ROOT / 'docs/validation-results/20261003-issue-30-llm/scored/comparisons.json'
    sources = [('issue30-changed-three', prior.load(changed_tasks), prior.load(changed_responses)),
               ('issue14-legacy-final', prior.load(old_plan)['requests'], prior.load(old_responses))]
    skipped, reused, new = [], [], []
    for task in tasks:
        if task['skip']:
            skipped.append({**{k: task[k] for k in ('questionId','candidateSha256','order','attempt')},
                'modelVersion': llm.MODEL_RESPONSE, 'promptVersion': llm.PROMPT, 'evidenceBytes': 1000,
                'includeProbabilities': False, 'status': 'success', 'items': [], 'wireAttempts': [],
                'costUsd': '0', 'origin': 'no_inference_empty_candidates'})
        else:
            saved = reusable(task, sources)
            if saved: reused.append(saved)
            else: new.append(task)
    qs = {q['id']: q for q in dataset['questions']}
    plans = {}
    for split in ('tuning','final'):
        requests = [t for t in new if qs[t['questionId']]['split'] == split]
        plan = {'modelVersion': llm.MODEL_RESPONSE, 'promptVersion': llm.PROMPT, 'parseRetries': 3,
                'captureCodeSha256': prior.digest(ROOT / 'scripts/llm_rerank_evaluation.py'),
                'minimumRequestIntervalSeconds': 1, 'stopOnHttp429': True, 'requests': requests}
        reservation = sum((llm.task_reservation(t)*4 for t in requests), Decimal(0))
        if len(requests)>69 or reservation>Decimal('.75'): raise ValueError('Split budget exceeded')
        prior.save(PLAN / f'{split}-plan.json', plan)
        plans[split] = {'requests':len(requests),'maximumWireAttempts':len(requests)*4,'reservationUsd':str(reservation),'creditUsd':'.75'}
    prior.save(PLAN/'all-tasks.json', tasks);prior.save(PLAN/'reused.json',reused);prior.save(PLAN/'skipped.json',skipped)
    files = [SOURCE/'questions.json',SOURCE/'report.json', ROOT/'evaluation/expansion-v2/dataset.json',
             old_plan,old_responses,changed_tasks,changed_responses,Path(__file__).resolve(),
             ROOT/'scripts/issue30_llm_evaluation.py',ROOT/'scripts/llm_rerank_evaluation.py'] + list(PLAN.glob('*.json'))
    prior.save(PLAN/'manifest.json', {'version':'issue30-full-llm-v1','approval':'user-20261003-issue30-full-evaluation',
        'conditions':144,'questions':48,'newRequests':len(new),'reused':len(reused),'noInference':len(skipped),
        'plans':plans,'totalCreditUsd':'1.50','evidenceBytes':1000,'includeProbabilities':False,'k':[3,5],
        'scope':'Full previously observed synthetic regression; not independent confirmation or production integration',
        'gates':{'primaryRecallPerSplitAndTypeMinimum':.9,'finalHitRateAt5PerSplitMinimum':.9,
                 'precisionAt5Minimum':.9,'noGoldFalseRecommendationsMaximum':0,'schemaAndGroundingRequired':True},
        'sha256':{str(p.relative_to(ROOT)):prior.digest(p) for p in files}})
    print(json.dumps({'newRequests':len(new),'reused':len(reused),'noInference':len(skipped),'plans':plans}))

def measure(gold, ranked, candidates, k):
    relevant = {j['entityId'] for j in gold if not j['insufficient'] and j['grade']>=2}
    if ranked and any(i not in candidates for i in ranked): raise ValueError('Ranking outside primary candidates')
    if len(set(ranked))!=len(ranked): raise ValueError('Duplicate ranking')
    if relevant:
        result = prior.measure(gold, ranked, [], [], k)
    else:
        result = {'ranking':ranked,'displayed':ranked[:k],'precisionAt5':0 if ranked[:k] else None,
                  'hitRateAt5':None,'recallAt5':None,'ndcgAt5':None,
                  'irrelevantRecommended':ranked,'addedGoldKept':[],'addedGoldLost':[],'addedOtherRecommended':[]}
    return {'ranking':ranked,'displayed':ranked[:k],'precision':result['precisionAt5'],
            'hitRate':result['hitRateAt5'],'recall':result['recallAt5'],'ndcg':result['ndcgAt5'],
            'primaryRecall':len(relevant&set(candidates))/len(relevant) if relevant else None,
            'relevantCount':len(relevant),'empty':not ranked,'noGold':not relevant,
            'falseRecommendation':int(bool(ranked[:k])) if not relevant else None,
            'irrelevantRecommended':result['irrelevantRecommended'],'lostGold':sorted(relevant-set(ranked[:k]))}

def average(values):
    values=[v for v in values if v is not None]
    return sum(values)/len(values) if values else None

def aggregate(rows):
    summaries=[]
    for split in ('tuning','final'):
        for kind in ('person','knowledge','all'):
            for order,attempt in ORDERS:
                for k in (3,5):
                    group=[r for r in rows if r['split']==split and (kind=='all' or r['entityType']==kind)
                           and r['order']==order and r['attempt']==attempt and r['k']==k]
                    summaries.append({'split':split,'entityType':kind,'order':order,'attempt':attempt,'k':k,'questions':len(group),
                        'positiveQuestions':sum(not r['noGold'] for r in group),'noGoldQuestions':sum(r['noGold'] for r in group),
                        'manualQuestions':sum(r['manualRequired'] for r in group),'emptyOutputs':sum(r['empty'] for r in group),
                        'primaryRecall':average(r['primaryRecall'] for r in group),'hitRate':average(r['hitRate'] for r in group),
                        'precision':average(r['precision'] for r in group),'precisionDenominator':sum(r['precision'] is not None for r in group),
                        'ndcg':average(r['ndcg'] for r in group),'finalRecall':average(r['recall'] for r in group),
                        'falseRecommendations':sum(r['falseRecommendation'] or 0 for r in group),
                        'noGoldNonemptyInputQuestions':sum(r['noGold'] and r['primaryCandidates']>0 for r in group),
                        'displayed':sum(len(r['displayed']) for r in group),
                        'irrelevantDisplayed':sum(len(set(r['displayed'])&set(r['irrelevantRecommended'])) for r in group),
                        'lostGold':sum(len(r['lostGold']) for r in group)})
    return summaries

def score(output):
    if output.exists(): raise ValueError('Output exists')
    manifest=prior.load(PLAN/'manifest.json')
    for name,expected in manifest['sha256'].items():
        if prior.digest(ROOT/name)!=expected: raise ValueError('Frozen input changed: '+name)
    tasks=prior.load(PLAN/'all-tasks.json')
    fixture=prior.load(ROOT/'evaluation/expansion-v2/dataset.json')
    source_rows=prior.load(SOURCE/'questions.json')
    if tasks!=tasks_for(source_rows,fixture):raise ValueError('Task reconstruction mismatch')
    sources={r['retrieval']['questionId']:r['retrieval'] for r in source_rows if r['caseLimit']==15}
    qs={q['id']:q for q in fixture['questions']}
    live=[]
    for split in ('tuning','final'):
        plan=prior.load(PLAN/f'{split}-plan.json')
        if not plan['requests']:continue
        capture=OUTPUT/'capture'/split
        approval=prior.load(capture/'approval.json')
        if approval['planSha256']!=llm.sha(llm.encode(plan)) or approval['approval']!=manifest['approval']:
            raise ValueError('Capture approval mismatch')
        results=prior.load(capture/'comparisons.json')
        if len(results)!=len(plan['requests']):raise ValueError('Incomplete live split')
        for task in plan['requests']:
            result=next(r for r in results if key(r)==key(task))
            journal=prior.load(capture/(llm.sha(task['id'].encode())+'.json'))
            expected=llm.sha(llm.encode({'task':task,'payload':llm.payload(task,'dse-luna6-eval'),'maxAttempts':4}))
            if journal['state']!='completed' or journal['requestSha256']!=expected or journal['result']!=result or journal['wireAttempts']!=result['wireAttempts']:
                raise ValueError('Journal mismatch')
        live+=results
    responses=live+[r['result'] for r in prior.load(PLAN/'reused.json')]+prior.load(PLAN/'skipped.json')
    if len(responses)!=144 or len({key(r) for r in responses})!=144:raise ValueError('Incomplete or duplicate conditions')
    rows=[]; baseline=[]; validated=0; failures=[]
    for task in tasks:
        response=next(r for r in responses if key(r)==key(task))
        source=sources[task['questionId']];q=qs[task['questionId']]
        if response['candidateSha256']!=task['candidateSha256'] or response['modelVersion']!=llm.MODEL_RESPONSE or response['promptVersion']!=llm.PROMPT:
            raise ValueError('Response binding mismatch')
        if response['status']=='success' and not task['skip']:
            parsed=llm.parse(response['wireAttempts'][-1]['body'],task)
            if [{'entityId':i['candidateId'],'reason':i['reason'],'evidenceIds':i['evidenceIds']} for i in parsed]!=response['items']:raise ValueError('Raw mismatch')
            validated+=1
        if response['status']!='success':failures.append({'questionId':task['questionId'],'order':task['order'],'attempt':task['attempt'],'failure':response.get('failure')})
        ranked=[i['entityId'] for i in response['items']]
        candidates=[c['entityId'] for c in source['selected']]
        for k in (3,5):
            rows.append({'questionId':q['id'],'split':q['split'],'entityType':q['entityType'],'order':task['order'],'attempt':task['attempt'],
                         'k':k,'status':response['status'],'origin':response.get('origin','new_live_capture'),'manualRequired':source['manualRequired'],
                         'primaryCandidates':len(candidates),**measure(q['judgments'],ranked,candidates,k)})
            if task['order']=='original' and task['attempt']==1:
                baseline.append({'questionId':q['id'],'split':q['split'],'entityType':q['entityType'],'k':k,
                                 **measure(q['judgments'],candidates,candidates,k)})
    summaries=aggregate(rows)
    attempts=[a for r in live for a in r['wireAttempts']]
    cost=sum((Decimal(r['costUsd']) for r in live),Decimal(0))
    if len(attempts)>manifest['newRequests']*4 or cost>Decimal(manifest['totalCreditUsd']):raise ValueError('Budget exceeded')
    gates=[s for s in summaries if s['k']==5]
    agreement=[]
    for i in sources:
        group=[r for r in rows if r['questionId']==i and r['k']==5]
        original=next(r for r in group if r['order']=='original' and r['attempt']==1)['ranking']
        reverse=next(r for r in group if r['order']=='reversed')['ranking']
        repeat=next(r for r in group if r['order']=='original' and r['attempt']==2)['ranking']
        agreement.append({'questionId':i,'sameOrderMatch':original==repeat,'reverseOrderMatch':original==reverse})
    report={'version':manifest['version'],'scope':manifest['scope'],'newRequests':len(live),'reusedResponses':manifest['reused'],
            'noInferenceConditions':manifest['noInference'],'wireAttempts':len(attempts),'parseRetries':sum(r.get('parseRetries',0) for r in live),
            'costUsd':str(cost),'inputTokens':sum(a['inputTokens'] for a in attempts),'cachedTokens':sum(a['cachedTokens'] for a in attempts),
            'outputTokens':sum(a['outputTokens'] for a in attempts),'validatedResponses':validated,'failures':failures,'productionApplied':False,
            'sameOrderMatches':sum(r['sameOrderMatch'] for r in agreement),'reverseOrderMatches':sum(r['reverseOrderMatch'] for r in agreement),
            'summaries':summaries,'gatesPassed':not failures and all(s['primaryRecall']>=.9 and s['hitRate']>=.9 and
                s['precision'] is not None and s['precision']>=.9 and s['falseRecommendations']==0 for s in gates)}
    for name,value in [('report.json',report),('questions.json',rows),('baseline.json',baseline),('comparisons.json',responses),('agreement.json',agreement)]:prior.save(output/name,value)
    buffer=io.StringIO();writer=csv.DictWriter(buffer,fieldnames=list(summaries[0]));writer.writeheader();writer.writerows(summaries)
    (output/'summary.csv').write_text(buffer.getvalue())
    print(json.dumps({k:v for k,v in report.items() if k!='summaries'}))

if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('mode',choices=['prepare','score']);ap.add_argument('--output',type=Path)
    args=ap.parse_args()
    if args.mode=='prepare':prepare()
    else:score(args.output or OUTPUT/'scored')
