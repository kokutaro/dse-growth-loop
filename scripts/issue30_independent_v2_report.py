#!/usr/bin/env python3
"""Score frozen independent-v2 service capture offline; HTTP gates remain separate."""
import argparse
import hashlib
import json
import statistics
from pathlib import Path

from issue30_grouped_report import evaluate

ROOT = Path(__file__).resolve().parents[1]
RUN = 'issue30-independent-20261004-v2'


def score(data, report, run=RUN, manual_codes=("CLASSIFICATION_MANUAL_REQUIRED",), require_expected_unknown=False):
    plan = report['plan']
    if not report['completed'] or plan['evaluationId'] != run or not plan['independentHoldout'] or plan['observedRegressionSet']:
        raise ValueError('Wrong or incomplete independent capture')
    questions = {q['id']: q for q in data['questions']}
    types = {u['search']['entityId']: u['search']['entityType'] for u in data['units']}
    refs = {r['id']: r['text'] for u in data['units'] for r in u['search']['sourceRefs']}
    keys = [(r['questionId'], r['arm']) for r in report['rows']]
    expected = {(q, arm) for q in questions for arm in ('all', 'targeted')}
    if len(keys) != len(expected) or set(keys) != expected:
        raise ValueError('Exactly 24 distinct questions in each arm required')
    arms = {}
    for arm in ('all', 'targeted'):
        rows = [r for r in report['rows'] if r['arm'] == arm]
        scored = []
        grounding = []
        for row in rows:
            value = evaluate(row, questions, types)
            value['status'] = row.get('rerankingStatus') or row.get('error')
            q = questions[row['questionId']]
            gold = set(value['gold'])
            primary = set(row['primary'])
            value['primaryRecall'] = len(gold & primary) / len(gold) if gold else None
            value['lostPrimaryGold'] = sorted(gold - primary)
            value['lostFinalGold'] = sorted(gold - set(value['displayRanking'][:5]))
            classification = row.get('queryClassification') or {}
            value['manualUnknown'] = not gold and value['status'] in manual_codes and (not require_expected_unknown or q.get('draftDomain') == 'unknown') and classification.get('status') == 'Succeeded' and classification.get('choice') in ('unknown', 'other')
            value['targetViolation'] = arm == 'targeted' and any(types[i] != q['entityType'] for i in row['ranking'])
            audit = row.get('retrievalAudit')
            if audit:
                merged = {i.split(':', 1)[1].removeprefix(run+'-') for i in audit['mergedIds']}
                probability = set(audit['routes']['probability'])
                rescued = [i for i in audit['mergedIds'] if i.startswith(q['entityType']+':') and i not in probability][:audit['rules']['rescueSlots']]
                value['lostBeforeCap'] = sorted(gold - merged)
                value['lostAtCap'] = sorted((gold & merged) - primary)
                value['rescueGold'] = sorted({i.split(':', 1)[1].removeprefix(run+'-') for i in rescued} & gold)
            for assessment in row.get('assessments') or []:
                ids = [i.removeprefix(run+'-') for i in assessment['evidenceIds']]
                reason = assessment['reason']
                grounding.append(bool(reason) and bool(ids) and all(i in refs for i in ids) and any(reason in refs.get(i, '') for i in ids))
            scored.append(value)
        metrics = {}
        for typ in ('person', 'knowledge'):
            positive = [r for r in scored if r['entityType'] == typ and r['gold']]
            metrics[typ] = {'positiveQuestions': len(positive), **{key: statistics.mean(r[key] for r in positive)
                for key in ('primaryRecall', 'groupHitAt5', 'groupPrecisionAt5', 'groupRecallAt5', 'combinedHitAt5', 'combinedPrecisionAt5')}}
        support_lost = [r['questionId'] for r in scored if r['entityType'] == 'person' and any(i.endswith('-person-support') for i in r['lostPrimaryGold'])]
        failed = [r['questionId'] for r in scored if r['status'] not in ('succeeded', 'all_unsuitable', 'no_candidates') and not r['manualUnknown']]
        false_count = sum(r['noGoldFalse'] for r in scored)
        violations = sum(r['targetViolation'] for r in scored)
        passed = all(m['primaryRecall'] >= .9 and m['groupHitAt5'] >= .9 and m['groupPrecisionAt5'] >= .9 for m in metrics.values()) and not support_lost and not failed and not false_count and not violations and all(grounding)
        arms[arm] = {'serviceQualityPassed': passed, 'metrics': metrics, 'noGoldFalseQuestions': false_count,
            'targetViolations': violations, 'manualUnknownQuestions': [r['questionId'] for r in scored if r['manualUnknown']], 'allUnsuitableQuestions': [r['questionId'] for r in scored if r['status'] == 'all_unsuitable'], 'multipleCareerSupportLost': support_lost, 'failedQuestions': failed,
            'groundedRecommendations': sum(grounding), 'recommendationsChecked': len(grounding), 'rows': scored}
    audits = report['aiAudits']
    usage = {'billableAttempts': sum(a['trace']['attemptCount'] for a in audits),
        'inputTokens': sum(a['trace']['inputTokens'] for a in audits), 'outputTokens': sum(a['trace']['outputTokens'] for a in audits),
        'estimatedAiJpy': sum(a['trace']['estimatedCostJpy'] for a in audits),
        'cacheHits': sum(bool(a['trace'].get('cachedFromRunId')) for a in audits)}
    return {'independentHoldout': True, 'holdoutTuned': False, 'serviceQualityPassed': all(a['serviceQualityPassed'] for a in arms.values()),
        'overallGate': 'pending-authenticated-http-and-timing', 'arms': arms, 'usage': usage,
        'ledgerBeforeSearch': report['ledgerBefore'], 'ledgerAfterSearch': report['ledgerAfter'],
        'durationScope': 'Managed Identity service calls; not HTTP or UI timing', 'rescueScope': 'Rescue IDs inferred from the frozen per-type reserve rule and captured ordered merged/probability IDs'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    dataset = ROOT/'evaluation/issue-30/independent-v2/approved/dataset.json'
    report = json.loads(args.capture.read_text())
    if hashlib.sha256(dataset.read_bytes()).hexdigest() != report['plan']['datasetSha256']:
        raise ValueError('Approved dataset checksum mismatch')
    result = score(json.loads(dataset.read_text()), report)
    result['sha256'] = {str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in (dataset, args.capture, Path(__file__))}
    if args.output.exists(): raise ValueError('Never overwrite scored evidence')
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2)+'\n')
    print(json.dumps({k: result[k] for k in ('serviceQualityPassed', 'overallGate', 'usage')}, indent=2))


if __name__ == '__main__':
    main()
