#!/usr/bin/env python3
"""Freeze changed-person candidate wire inputs without network or AI calls."""
import argparse
from decimal import Decimal
import hashlib
import json
from pathlib import Path
import llm_rerank_evaluation as llm

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT/'docs/validation-results/20261004-issue-30-person-limits/replay/questions.json'
DATASET = ROOT/'evaluation/expansion-v2/dataset.json'

def load(path): return json.loads(path.read_text())
def reference(path):
    path = path.resolve()
    return str(path.relative_to(ROOT)) if path.is_relative_to(ROOT) else str(path)

def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def save(path, value): path.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n')

def prepare(output):
    if output.exists(): raise ValueError('Preserve existing review plan; output already exists')
    rows = [r for r in load(SOURCE) if r['personLimit']==15 and r['changedFromTen']]
    primary = [{'setting': {'variant': 'E'}, 'questionId': r['retrieval']['questionId'],
                'candidateSha256': r['retrieval']['candidateSha256'], 'candidates': r['retrieval']['selected']} for r in rows]
    if len(primary)!=8: raise ValueError('Expected exactly eight changed observed questions')
    tasks = llm.plan(primary, load(DATASET))
    previous = load(ROOT/'evaluation/issue-30/full-llm/all-tasks.json')
    if any(llm.payload(t, 'dse-luna6-eval')==llm.payload(p, 'dse-luna6-eval') for t in tasks for p in previous):
        raise ValueError('Prior identical request exists; resolve exact saved-response reuse before planning new send')
    output.mkdir(parents=True)
    wire_hashes = {};total_tokens = 0;maximum_input = 0;cost = Decimal(0)
    for task in tasks:
        body = llm.payload(task, 'dse-luna6-eval')
        bound = len(llm.encode(body))+512
        if bound>16000: raise ValueError('Wire input exceeds existing cap')
        task['inputTokenBound'] = bound
        maximum_input = max(maximum_input, bound);total_tokens += bound+llm.OUTPUT_BOUND
        cost += llm.task_reservation(task)
        name = task['id']+'.request.json';save(output/name, body)
        wire_hashes[name] = digest(output/name)
    save(output/'tasks.json', tasks)
    plan = {'modelVersion': llm.MODEL_RESPONSE, 'promptVersion': llm.PROMPT, 'parseRetries': 3,
            'captureCodeSha256': digest(ROOT/'scripts/llm_rerank_evaluation.py'), 'requests': tasks,
            'minimumRequestIntervalSeconds': 1, 'stopOnHttp429': True}
    save(output/'plan.json', plan)
    save(output/'manifest.json', {'version':'issue30-person-rerank-ready-v1','executionEnabled':False,'approvalStatus':'pending',
         'observedRegression':True,'independentHoldout':False,'modelCalls':0,'changedQuestions':8,'wireRequests':24,
         'automaticRetries':0,'maximumAttemptsPerTask':1,'maximumInputTokensPerCall':maximum_input,'maximumTotalTokens':total_tokens,
         'maximumCostUsd':str(cost),'maximumCostJpy':str(cost*200),'fxJpyPerUsd':200,'requestedAiBudgetJpy':10,
         'compatibleExecutor':'llm.capture(task, deployment, path, send, approval, max_attempts=1); legacy batch always reserves and executes up to four attempts, so it must not be used',
         'sha256': {**{str(p.relative_to(ROOT)):digest(p) for p in (SOURCE,DATASET,Path(__file__),ROOT/'scripts/llm_rerank_evaluation.py',ROOT/'evaluation/issue-30/full-llm/all-tasks.json')},
                    **{reference(output/name):digest(output/name) for name in ['tasks.json','plan.json',*wire_hashes]}}})

def verify(output):
    manifest = load(output/'manifest.json')
    for name, expected in manifest['sha256'].items():
        path = Path(name) if Path(name).is_absolute() else ROOT/name
        if digest(path)!=expected: raise ValueError('Frozen input changed: '+name)
    tasks = load(output/'tasks.json')
    for task in tasks:
        if load(output/(task['id']+'.request.json'))!=llm.payload(task,'dse-luna6-eval'):
            raise ValueError('Wire payload changed')
        llm.task_reservation(task)
    cost = sum((llm.task_reservation(task) for task in tasks), Decimal(0))
    if manifest["maximumCostUsd"] != str(cost) or manifest["maximumTotalTokens"] != sum(t["inputTokenBound"]+llm.OUTPUT_BOUND for t in tasks) or manifest["automaticRetries"] != 0 or manifest["maximumAttemptsPerTask"] != 1:
        raise ValueError("Frozen budget does not match tasks")
    return manifest

def capture_plan(output, directory):
    manifest = verify(output)
    approval = load(output/'execution-approval.json')
    if approval.get('manifestSha256') != digest(output/'manifest.json') or Decimal(str(approval.get('approvedBudgetJpy', 0))) < Decimal(manifest['maximumCostJpy']):
        raise ValueError('Explicit matching budget approval required')
    if directory.exists() and (directory/'approval.json').exists() and load(directory/'approval.json') != approval:
        raise ValueError('Approval changed')
    directory.mkdir(parents=True, exist_ok=True);save(directory/'approval.json', approval)
    send = llm.sender('https://dse-foundry-ypbunss3xh66m.openai.azure.com/')
    results = []
    for task in load(output/'tasks.json'):
        paid = sum((Decimal(r['costUsd']) for r in results), Decimal(0))
        if (paid+llm.task_reservation(task))*200 > Decimal(str(approval['approvedBudgetJpy'])):
            raise ValueError('Approved cost limit reached')
        result = llm.capture(task, 'dse-luna6-eval', directory/(llm.sha(task['id'].encode())+'.json'), send,
                             approval['approvalReference'], max_attempts=1)
        results.append(result);save(directory/'comparisons.json', results)
        print(task['id'], result['status'], result.get('failure'), result['costUsd'], flush=True)
        if result['status'] != 'success': raise ValueError('Stopped on failed task; no automatic resend')
    fixture = {q['id']:q for q in load(DATASET)['questions']}
    scores=[]
    from issue30_full_llm_evaluation import measure
    for task, result in zip(load(output/'tasks.json'), results):
        ids=[c['candidateId'] for c in task['input']['candidates']]
        ranked=[i['entityId'] for i in result['items']]
        scores.append({'questionId':task['questionId'],'order':task['order'],'attempt':task['attempt'],
                       **measure(fixture[task['questionId']]['judgments'],ranked,ids,5)})
    save(directory/'scored.json',scores)
    save(directory/'summary.json',{'completed':True,'independentHoldout':False,'conditions':len(results),
         'calls':sum(len(r['wireAttempts']) for r in results),'inputTokens':sum(r['inputTokens'] for r in results),
         'outputTokens':sum(r['outputTokens'] for r in results),'costUsd':str(sum((Decimal(r['costUsd']) for r in results),Decimal(0))),
         'noGoldFalseRecommendations':sum(r['falseRecommendation'] or 0 for r in scores),
         'positiveHitAt5All':all(r['hitRate']==1 for r in scores if not r['noGold']),
         'noIrrelevantDisplayed':all(not r['irrelevantRecommended'] for r in scores)})

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('action',choices=['prepare','verify','capture']);parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--capture-output',type=Path)
    args=parser.parse_args()
    if args.action=='prepare':prepare(args.output)
    if args.action=='capture':
        if args.capture_output is None:parser.error('--capture-output required')
        capture_plan(args.output,args.capture_output)
    else: print(json.dumps(verify(args.output),ensure_ascii=False,indent=2))

if __name__=='__main__':main()
