#!/usr/bin/env python3
"""Replay captured ranking against the grouped UI; never calls models or changes old evidence."""
import argparse
import hashlib
import json
import re
import statistics
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def evaluate(row, questions, types):
    q = questions[row['questionId']]
    ranking = row['ranking']
    if len(set(ranking)) != len(ranking) or any(i not in types for i in ranking):
        raise ValueError('Duplicate or unknown recommendation ID')
    gold = {j['entityId'] for j in q['judgments'] if j['grade'] >= 2 and not j.get('insufficient', False)}
    display = [i for i in ranking if types[i] == q['entityType']]
    def metrics(ids):
        top = ids[:5]
        return {'hit': int(bool(gold.intersection(top))) if gold else None,
                'precision': len(gold.intersection(top))/len(top) if top else 0,
                'recall': len(gold.intersection(top))/len(gold) if gold else None}
    group, combined = metrics(display), metrics(ranking)
    return {'questionId': row['questionId'], 'entityType': q['entityType'], 'gold': sorted(gold),
            'displayRanking': display, 'combinedRanking': ranking,
            'groupHitAt5': group['hit'], 'groupPrecisionAt5': group['precision'], 'groupRecallAt5': group['recall'],
            'combinedHitAt5': combined['hit'], 'combinedPrecisionAt5': combined['precision'],
            'noGoldFalse': not gold and bool(ranking), 'status': row.get('rerankingStatus', row.get('error'))}

def cap_diagnosis(audits):
    rows = []
    for audit in audits:
        if audit.get('kind') != 'retrievalRun': continue
        terms = [re.sub(r'\\+u([0-9a-fA-F]{4})', lambda m: chr(int(m[1], 16)), t)
                 for t in audit.get('conditions', {}).get('terms', [])]
        if not any('担当者ごとの無秩序な値引き' in t and '経験のある担当者' in t for t in terms): continue
        merged = [i for i in audit['mergedIds'] if i.startswith('person:')]
        support = next(i for i in merged if i.endswith('i30i-sales-discount-person-support'))
        rows.append({'runId': audit['id'], 'rules': audit['rules'], 'personMergedRank': merged.index(support)+1,
                     'personMergedCount': len(merged), 'selected': support in audit['selectedIds'],
                     'routes': [k for k, v in audit['routes'].items() if support in v]})
    if not rows: raise ValueError('Missing observed discount query audits')
    return rows

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        raise ValueError('Output must be empty')
    dataset = ROOT/'evaluation/issue-30/independent/dataset.json'
    capture = ROOT/'docs/validation-results/20261003-issue-30-hardening-production/production-report.json'
    data, report = json.loads(dataset.read_text()), json.loads(capture.read_text())
    if not report['completed']: raise ValueError('Incomplete capture')
    questions = {q['id']: q for q in data['questions']}
    types = {u['search']['entityId']: u['search']['entityType'] for u in data['units']}
    arms = {}
    for arm in ('all', 'targeted'):
        rows = [r for r in report['rows'] if r['arm'] == arm]
        if len(rows) != len(questions) or {r['questionId'] for r in rows} != set(questions):
            raise ValueError('Expected exactly one row per question')
        evaluated = [evaluate(r, questions, types) for r in rows]
        summary = {}
        for typ in ('person', 'knowledge'):
            positive = [r for r in evaluated if r['entityType'] == typ and r['gold']]
            summary[typ] = {'positiveQuestions': len(positive), **{key: statistics.mean(r[key] for r in positive)
                for key in ('groupHitAt5', 'groupPrecisionAt5', 'groupRecallAt5', 'combinedHitAt5', 'combinedPrecisionAt5')}}
        arms[arm] = {'metrics': summary, 'rows': evaluated, 'noGoldFalseQuestions': sum(r['noGoldFalse'] for r in evaluated)}
    audit_path = ROOT/'docs/validation-results/20261003-issue-30-independent-production/audits.json'
    diagnosis = {'source': str(audit_path.relative_to(ROOT)), 'scope': 'Prior observed production audit; rank diagnostic only, not replayed projections or post-hardening measurement', 'rows': cap_diagnosis(json.loads(audit_path.read_text()))}
    output = {'version': 'issue30-grouped-display-v1', 'modelCalls': 0, 'independentHoldout': False,
              'measurementScope': 'Counterfactual grouped UI replay of observed production rankings; no new AI quality measurement',
              'sha256': {str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in (dataset, capture, audit_path, Path(__file__))}, 'arms': arms}
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output/'cap-diagnosis.json').write_text(json.dumps(diagnosis, ensure_ascii=False, indent=2)+'\n')
    (args.output/'grouped-report.json').write_text(json.dumps(output, ensure_ascii=False, indent=2)+'\n')

if __name__ == '__main__': main()
