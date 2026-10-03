#!/usr/bin/env python3
"""Issue 30: frozen changed-candidate plan and offline scoring. No network in this file."""
import argparse
from decimal import Decimal
import hashlib
import json
import math
from pathlib import Path
import llm_rerank_evaluation as llm

ROOT = Path(__file__).resolve().parents[1]
PLAN = ROOT / 'evaluation/issue-30/llm'
SOURCE = ROOT / 'docs/validation-results/20261003-issue-30-case-limits'
OUTPUT = ROOT / 'docs/validation-results/20261003-issue-30-llm'
OLD = ROOT / 'docs/validation-results/20261003-issue-14-luna6'

def load(path): return json.loads(path.read_text())
def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')

def tasks_for(rows, dataset):
    primary = [{'setting': {'variant': 'E'}, 'questionId': r['retrieval']['questionId'],
                'candidateSha256': r['retrieval']['candidateSha256'], 'candidates': r['retrieval']['selected']}
               for r in rows if r['caseLimit'] == 15 and r['changedFromTen']]
    if len(primary) != 3: raise ValueError('Unexpected changed cohort')
    return llm.plan(primary, dataset)

def prepare():
    if PLAN.exists(): raise ValueError('Plan already exists')
    rows = load(SOURCE / 'questions.json')
    dataset = load(ROOT / 'evaluation/expansion-v2/dataset.json')
    tasks = tasks_for(rows, dataset)
    prior = load(ROOT / 'evaluation/expansion-v2/luna6/plan.json')['requests']
    responses = load(OLD / 'comparisons.json')
    requests, reused = [], []
    for task in tasks:
        match = next((t for t in prior if t['order'] == task['order'] and t['attempt'] == task['attempt']
                      and llm.payload(t, 'dse-luna6-eval') == llm.payload(task, 'dse-luna6-eval')), None)
        if match:
            response = next((r for r in responses if r['questionId'] == match['questionId']
                             and r['order'] == match['order'] and r['attempt'] == match['attempt'] and r['status'] == 'success'), None)
            if response:
                # The new candidate hash contains classifications that are not sent to this evaluator.
                reused.append({'taskId': task['id'], 'priorCandidateSha256': response['candidateSha256'],
                               'payloadSha256': llm.sha(llm.encode(llm.payload(task, 'dse-luna6-eval'))),
                               'result': {**response, 'candidateSha256': task['candidateSha256'], 'origin': 'exact_prior_payload_reuse'}})
                continue
        requests.append(task)
    plan = {'modelVersion': llm.MODEL_RESPONSE, 'promptVersion': llm.PROMPT, 'parseRetries': 3,
            'captureCodeSha256': digest(ROOT / 'scripts/llm_rerank_evaluation.py'),
            'minimumRequestIntervalSeconds': 1, 'stopOnHttp429': True, 'requests': requests}
    reservation = sum((llm.task_reservation(t) * 4 for t in requests), Decimal(0))
    if reservation > Decimal('.10'): raise ValueError('Budget exceeded')
    save(PLAN / 'plan.json', plan); save(PLAN / 'all-tasks.json', tasks); save(PLAN / 'reused.json', reused)
    inputs = [SOURCE / 'questions.json', SOURCE / 'report.json', ROOT / 'evaluation/expansion-v2/dataset.json',
              OLD / 'comparisons.json', ROOT / 'evaluation/expansion-v2/luna6/plan.json',
              ROOT / 'scripts/llm_rerank_evaluation.py', Path(__file__).resolve(),
              PLAN / 'plan.json', PLAN / 'all-tasks.json', PLAN / 'reused.json']
    save(PLAN / 'manifest.json', {'version': 'issue30-llm-v1', 'approval': 'user-20261003-issue30-execute-llm',
         'creditUsd': '.10', 'maximumNewWireAttempts': len(requests)*4, 'maximumReservationUsd': str(reservation),
         'conditions': len(tasks), 'newRequests': len(requests), 'reusedResponses': len(reused),
         'k': 5, 'evidenceBytes': 1000, 'includeProbabilities': False, 'productionApplied': False,
         'gates': {'allChangedQuestionsHaveRelevantTop5': True, 'allThreeAddedGoldKeptTop5': True,
                   'allThreeAddedNegativesExcluded': True, 'noIrrelevantRecommendation': True, 'validGroundingAndSchema': True},
         'scope': 'Observed regression; three changed positive questions, no new no-gold or independent questions',
         'sha256': {str(p.relative_to(ROOT)): digest(p) for p in inputs}})
    print(json.dumps({'conditions':len(tasks),'newRequests':len(requests),'reused':len(reused),'reservationUsd':str(reservation)}))

def measure(gold, ranked, added_gold, added_other, k=5):
    grades = {j['entityId']: 0 if j['insufficient'] else j['grade'] for j in gold}
    relevant = {i for i, g in grades.items() if g >= 2}
    shown = ranked[:k]
    if len(set(ranked)) != len(ranked) or any(i not in grades for i in ranked): raise ValueError('Invalid ranking')
    dcg = lambda values: sum((2**g-1)/math.log2(i+2) for i,g in enumerate(values))
    ideal = dcg(sorted(grades.values(), reverse=True)[:k])
    return {'ranking': ranked, 'displayed': shown, 'precisionAt5': sum(i in relevant for i in shown)/len(shown) if shown else None,
            'hitRateAt5': int(any(i in relevant for i in shown)), 'recallAt5': len(set(shown)&relevant)/len(relevant),
            'ndcgAt5': dcg([grades[i] for i in shown])/ideal,
            'addedGoldKept': sorted(set(shown)&set(added_gold)), 'addedGoldLost': sorted(set(added_gold)-set(shown)),
            'addedOtherRecommended': sorted(set(ranked)&set(added_other)),
            'irrelevantRecommended': [i for i in ranked if i not in relevant]}

def score(output):
    if output.exists(): raise ValueError('Output already exists')
    manifest = load(PLAN / 'manifest.json')
    for name, expected in manifest['sha256'].items():
        if digest(ROOT / name) != expected: raise ValueError('Frozen input changed: '+name)
    tasks = load(PLAN / 'all-tasks.json'); plan = load(PLAN / 'plan.json')
    if tasks != tasks_for(load(SOURCE / 'questions.json'), load(ROOT / 'evaluation/expansion-v2/dataset.json')):
        raise ValueError('Changed cohort mismatch')
    live = load(OUTPUT / 'capture/comparisons.json') if plan['requests'] else []
    reused = load(PLAN / 'reused.json')
    responses = live + [r['result'] for r in reused]
    if len(responses) != 9 or len({(r['questionId'],r['order'],r['attempt']) for r in responses}) != 9:
        raise ValueError('Incomplete or duplicate conditions')
    fixture = load(ROOT / 'evaluation/expansion-v2/dataset.json')
    sources = {r['retrieval']['questionId']:r for r in load(SOURCE/'questions.json') if r['caseLimit']==15 and r['changedFromTen']}
    questions = {q['id']:q for q in fixture['questions']}
    results = []
    for task in tasks:
        response = next(r for r in responses if (r['questionId'],r['order'],r['attempt'])==(task['questionId'],task['order'],task['attempt']))
        if response['candidateSha256'] != task['candidateSha256'] or response['modelVersion'] != llm.MODEL_RESPONSE or response['promptVersion'] != llm.PROMPT:
            raise ValueError('Response binding mismatch')
        if response['status'] != 'success': raise ValueError('Model failure: '+str(response.get('failure')))
        final = response['wireAttempts'][-1]
        parsed = llm.parse(final['body'], task)
        if [{'entityId':i['candidateId'],'reason':i['reason'],'evidenceIds':i['evidenceIds']} for i in parsed] != response['items']:
            raise ValueError('Raw response mismatch')
        source = sources[task['questionId']]
        ranked = [i['entityId'] for i in response['items']]
        results.append({'questionId':task['questionId'],'order':task['order'],'attempt':task['attempt'],
            'candidateSha256':task['candidateSha256'],'origin':response.get('origin','new_live_capture'),
            **measure(questions[task['questionId']]['judgments'], ranked, source['addedRelevant'], source['addedOther'])})
    baseline = [{'questionId':i, **measure(questions[i]['judgments'], [c['entityId'] for c in r['retrieval']['selected']],
                 r['addedRelevant'],r['addedOther'])} for i,r in sources.items()]
    summaries=[]
    for order, attempt in [('original',1),('reversed',1),('original',2)]:
        group=[r for r in results if r['order']==order and r['attempt']==attempt]
        summaries.append({'order':order,'attempt':attempt,'questions':len(group),'hitRateAt5':sum(r['hitRateAt5'] for r in group)/len(group),
            'meanPrecisionAt5':sum(r['precisionAt5'] for r in group if r['precisionAt5'] is not None)/sum(r['precisionAt5'] is not None for r in group),
            'meanNdcgAt5':sum(r['ndcgAt5'] for r in group)/len(group),
            'addedGoldKept':sum(len(r['addedGoldKept']) for r in group),'addedGoldLost':sum(len(r['addedGoldLost']) for r in group),
            'addedOtherRecommended':sum(len(r['addedOtherRecommended']) for r in group),'irrelevantRecommended':sum(len(r['irrelevantRecommended']) for r in group)})
    attempts=[a for r in live for a in r['wireAttempts']]
    if len(attempts)>manifest['maximumNewWireAttempts']: raise ValueError('Attempt budget exceeded')
    report={'version':manifest['version'],'newRequests':len(live),'reusedResponses':len(reused),'wireAttempts':len(attempts),
        'parseRetries':sum(r['parseRetries'] for r in live), 'costUsd':str(sum((Decimal(r['costUsd']) for r in live),Decimal(0))),
        'inputTokens':sum(a['inputTokens'] for a in attempts),'outputTokens':sum(a['outputTokens'] for a in attempts),
        'cachedTokens':sum(a['cachedTokens'] for a in attempts),'validSchemaAndGrounding':len(results),'productionApplied':False,
        'scope':manifest['scope'],'summaries':summaries,
        'sameOrderRepeatMatches':sum(next(r for r in results if r['questionId']==i and r['order']=='original' and r['attempt']==1)['ranking']==
            next(r for r in results if r['questionId']==i and r['order']=='original' and r['attempt']==2)['ranking'] for i in sources),
        'reverseOrderMatches':sum(next(r for r in results if r['questionId']==i and r['order']=='original' and r['attempt']==1)['ranking']==
            next(r for r in results if r['questionId']==i and r['order']=='reversed')['ranking'] for i in sources),
        'gatesPassed':all(r['hitRateAt5']==1 and not r['addedGoldLost'] and not r['addedOtherRecommended'] and not r['irrelevantRecommended'] for r in results)}
    if Decimal(report['costUsd'])>Decimal(manifest['creditUsd']): raise ValueError('Credit exceeded')
    save(output/'report.json',report);save(output/'questions.json',results);save(output/'baseline.json',baseline)
    save(output/'comparisons.json',responses)
    print(json.dumps(report,ensure_ascii=False))

if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('mode',choices=['prepare','score']);ap.add_argument('--output',type=Path)
    args=ap.parse_args()
    if args.mode=='prepare':prepare()
    else:score(args.output or OUTPUT/'scored')
