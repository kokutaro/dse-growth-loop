#!/usr/bin/env python3
"""Score the observed 24-question regression; never grants independent acceptance."""
import argparse
import hashlib
import json
import statistics
from pathlib import Path
from issue30_grouped_report import evaluate
ROOT = Path(__file__).resolve().parents[1]
BASE = 'issue30-independent-20261004-v2'

def score(data, report):
    plan = report['plan']
    if not report['completed'] or not plan['observedRegressionSet'] or plan['independentHoldout'] or not plan['evaluationId'].startswith('issue30-hardening-') or plan['arms'] != ['all']:
        raise ValueError('Complete observed regression in the all arm required')
    questions = {q['id']: q for q in data['questions']}
    keys = [(r['questionId'], r['arm']) for r in report['rows']]
    if len(keys) != 24 or set(keys) != {(q, 'all') for q in questions}:
        raise ValueError('Exactly one row for each approved question required')
    types = {u['search']['entityId']: u['search']['entityType'] for u in data['units']}
    refs = {r['id']: r['text'] for u in data['units'] for r in u['search']['sourceRefs']}
    rows, grounding = [], []
    for raw in report['rows']:
        row = evaluate(raw, questions, types)
        row['status'] = raw.get('rerankingStatus') or raw.get('error')
        gold = set(row['gold'])
        row['primaryRecall'] = len(gold.intersection(raw['primary'])) / len(gold) if gold else None
        row['lostPrimaryGold'] = sorted(gold - set(raw['primary']))
        row['outOfScope'] = not gold and row['status'] == 'CLASSIFICATION_OUT_OF_SCOPE' and not raw['ranking']
        row['durationMs'] = raw['durationMs']
        row['primaryReads'] = sum(c['operation'] == 'primary_read' for c in (raw.get('retrievalAudit') or {}).get('cosmosCharges', []))
        for assessment in raw.get('assessments') or []:
            ids = [i.removeprefix(BASE+'-') for i in assessment['evidenceIds']]
            reason = assessment['reason']
            grounding.append(bool(reason) and bool(ids) and all(i in refs for i in ids) and any(reason in refs.get(i, '') for i in ids))
        rows.append(row)
    metrics = {}
    for typ in ('person', 'knowledge'):
        positive = [r for r in rows if r['entityType'] == typ and r['gold']]
        metrics[typ] = {key: statistics.mean(r[key] for r in positive) for key in ('primaryRecall', 'groupHitAt5', 'groupPrecisionAt5', 'groupRecallAt5')}
    failed = [r['questionId'] for r in rows if r['status'] not in ('succeeded', 'all_unsuitable', 'no_candidates') and not r['outOfScope']]
    support_lost = [r['questionId'] for r in rows if any(i.endswith('-person-support') for i in r['lostPrimaryGold'])]
    false = sum(r['noGoldFalse'] for r in rows)
    passed = all(m['primaryRecall'] >= .9 and m['groupHitAt5'] >= .9 and m['groupPrecisionAt5'] >= .9 for m in metrics.values()) and not failed and not support_lost and not false and all(grounding)
    audits = report['aiAudits']
    if any(not a['jobId'].startswith(plan['evaluationId']+'-') for a in audits):
        raise ValueError('Audit from another evaluation')
    attempts = sum(a['trace']['attemptCount'] for a in audits)
    if attempts > 48: raise ValueError('48-call stage bound exceeded')
    return {'observedRegressionSet': True, 'independentHoldout': False, 'independentAcceptanceGranted': False,
        'serviceQualityPassed': passed, 'httpSloMeasured': False, 'metrics': metrics, 'failedQuestions': failed,
        'multipleCareerSupportLost': support_lost, 'noGoldFalseQuestions': false,
        'outOfScopeQuestions': [r['questionId'] for r in rows if r['outOfScope']],
        'groundedRecommendations': sum(grounding), 'recommendationsChecked': len(grounding),
        'usage': {'billableAttempts': attempts, 'estimatedAiJpy': sum(a['trace']['estimatedCostJpy'] for a in audits),
            'inputTokens': sum(a['trace']['inputTokens'] for a in audits), 'outputTokens': sum(a['trace']['outputTokens'] for a in audits),
            'schemaMismatches': sum(a.get('errorCode') == 'AI_SCHEMAMISMATCH' for a in audits)}, 'rows': rows}

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    dataset = ROOT/'evaluation/issue-30/independent-v2/approved/dataset.json'
    report = json.loads(args.capture.read_text())
    if report['plan']['datasetSha256'] != hashlib.sha256(dataset.read_bytes()).hexdigest(): raise ValueError('Changed labels')
    result = score(json.loads(dataset.read_text()), report)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n')
    print(json.dumps({k: result[k] for k in ('serviceQualityPassed', 'independentAcceptanceGranted', 'usage')}, indent=2))
if __name__ == '__main__': main()
