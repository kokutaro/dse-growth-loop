#!/usr/bin/env python3
"""Offline-only Issue 31/32 replay and human-gold scoring; never sends requests."""
import argparse
import csv
import hashlib
import importlib.util
import json
from pathlib import Path
import unicodedata

VERSION = 'offline-quality-v1'
FIELDS = ('description', 'start', 'end', 'precision', 'months', 'skills', 'domain', 'evidenceIds')
CONDITIONS = ('modelVersion', 'promptVersion', 'evidenceBytes', 'includeProbabilities')
BINDINGS = ('sourceSha256', 'modelVersion', 'promptVersion', 'configSha256')


def load(path):
    return json.loads(Path(path).read_text(encoding='utf-8'))


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def canonical(value):
    if isinstance(value, str):
        return ' '.join(unicodedata.normalize('NFKC', value).split())
    return value


def mean(values):
    values = [v for v in values if v is not None]
    return {'value': sum(values) / len(values) if values else None, 'denominator': len(values)}


def save(output, report, rows):
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError('Existing run cannot be overwritten; choose a new output directory')
    output.mkdir(parents=True, exist_ok=True)
    (output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    if rows:
        with (output / 'rows.csv').open('w', encoding='utf-8', newline='') as f:
            writer = csv.DictWriter(f, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows({k: json.dumps(v, ensure_ascii=False) if isinstance(v, (dict, list)) else v
                             for k, v in row.items()} for row in rows)


def rerank(scored_path, responses_path, dataset_path):
    scored, responses, dataset = map(load, (scored_path, responses_path, dataset_path))
    if scored['datasetSha256'] != digest(dataset_path):
        raise ValueError('Dataset hash differs from previously validated evaluator report')
    questions = {q['id']: q for q in dataset['questions']}
    captures = {}
    for r in responses:
        key = (r['questionId'], r['candidateSha256'], *(r[c] for c in CONDITIONS), r['order'], r['attempt'])
        if key in captures:
            raise ValueError('Duplicate captured condition')
        captures[key] = r
    question_hashes = {}
    for r in responses:
        question_hashes.setdefault(r['questionId'], set()).add(r['candidateSha256'])
    if any(len(hashes) != 1 for hashes in question_hashes.values()):
        raise ValueError('Condition comparison requires identical primary candidate hash per question')
    rows = []
    seen_rows = set()
    for r in scored['comparisons']:
        key = (r['questionId'], r['candidateSha256'], *(r[c] for c in CONDITIONS), r['order'], r['attempt'])
        capture = captures.get(key)
        if capture is None:
            # Deterministic primary baseline has no paid capture and is not compared as a model condition.
            if r['modelVersion'] == 'primary-order-no-ai':
                continue
            raise ValueError('Missing captured response for scored condition')
        if r['k'] not in (3, 5) or (key, r['k']) in seen_rows:
            raise ValueError('Duplicate or unknown scored K')
        seen_rows.add((key, r['k']))
        ranking = [i['entityId'] for i in capture['items']]
        if ranking != r['ranking'] or capture['status'] != r['status']:
            raise ValueError('Scored ranking/status differs from captured response')
        judgments = {j['entityId']: j for j in questions[r['questionId']]['judgments']}
        displayed = ranking[:r['k']]
        if any(i not in judgments for i in displayed):
            raise ValueError('Scored item has no gold judgment')
        incorrect = [i for i in displayed if judgments[i]['grade'] < 2 or judgments[i]['insufficient']]
        wires = capture.get('wireAttempts', [])
        rows.append({**{k: r[k] for k in ('questionId', 'entityType', 'candidateSha256', *CONDITIONS,
                                         'order', 'attempt', 'k', 'status', 'failureCause')},
                     'ranking': displayed, 'falsePositiveIds': incorrect,
                     'falsePositiveItems': len(incorrect), 'displayedItems': len(displayed),
                     'hitRate': r['scores']['finalHitRate'], 'precision': r['scores']['precision'],
                     'ndcg': r['scores']['ndcg'], 'zeroGold': r['scores']['relevantCount'] == 0,
                     'wireAttempts': len(wires), 'estimatedWireAttempts': sum(w.get('usageEstimated', False) for w in wires),
                     'inputTokens': sum(w.get('inputTokens', 0) for w in wires),
                     'outputTokens': sum(w.get('outputTokens', 0) for w in wires),
                     'cachedTokens': sum(w.get('cachedTokens', 0) for w in wires),
                     'reasoningTokens': sum(w.get('reasoningTokens', 0) for w in wires),
                     'failure': capture.get('failure')})
    if any(not all((key, k) in seen_rows for k in (3, 5)) for key in captures):
        raise ValueError('Every capture must have exactly K3 and K5 scored rows')
    groups = {}
    for r in rows:
        key = (*[r[c] for c in CONDITIONS], r['entityType'], r['order'], r['attempt'], r['k'])
        groups.setdefault(key, []).append(r)
    summaries = []
    for group in groups.values():
        summaries.append({**{k: group[0][k] for k in (*CONDITIONS, 'entityType', 'order', 'attempt', 'k')},
                          'questions': len(group), 'successfulQuestions': sum(r['status'] == 'success' for r in group),
                          'hitRate': mean(r['hitRate'] for r in group), 'precision': mean(r['precision'] for r in group),
                          'ndcg': mean(r['ndcg'] for r in group),
                          'falsePositiveItems': sum(r['falsePositiveItems'] for r in group),
                          'falsePositiveQuestions': sum(r['falsePositiveItems'] > 0 for r in group),
                          'zeroGoldFalseRecommendation': mean(float(r['displayedItems'] > 0) for r in group if r['zeroGold'] and r['status'] == 'success'),
                          'failedQuestions': sum(r['status'] != 'success' for r in group)})
    pairs = []
    by_question = {}
    for r in rows:
        key = (r['questionId'], r['candidateSha256'], *(r[c] for c in CONDITIONS), r['k'])
        by_question.setdefault(key, []).append(r)
    for group in by_question.values():
        for i, a in enumerate(group):
            for b in group[i+1:]:
                if a['status'] != 'success' or b['status'] != 'success':
                    continue
                union = set(a['ranking']) | set(b['ranking'])
                pairs.append({**{k: a[k] for k in ('questionId', 'candidateSha256', *CONDITIONS, 'k')},
                              'a': [a['order'], a['attempt']], 'b': [b['order'], b['attempt']],
                              'exactOrderMatch': a['ranking'] == b['ranking'],
                              'jaccard': len(set(a['ranking']) & set(b['ranking'])) / len(union) if union else 1,
                              'aFalsePositiveIds': a['falsePositiveIds'], 'bFalsePositiveIds': b['falsePositiveIds']})
    return {'version': VERSION, 'scope': 'new offline aggregation of historical validated captures; no new model evaluation',
            'qualityClaim': False, 'callsThisRun': 0,
            'inputs': {str(p): digest(p) for p in (scored_path, responses_path, dataset_path)},
            'conditionCount': len({tuple(r[c] for c in CONDITIONS) for r in rows}),
            'summaries': summaries, 'pairs': pairs,
            'falsePositiveCases': [r for r in rows if r['falsePositiveItems']],
            'historicalUsageInSelectedSuccessfulCaptures': {
                k: sum(r[k] for r in rows if r['k'] == 5) for k in
                ('wireAttempts', 'estimatedWireAttempts', 'inputTokens', 'outputTokens', 'cachedTokens', 'reasoningTokens')},
            'historicalFailuresOutsideSelectedCaptures': 'Not zero; see original final-summary 130 HTTP429 and one interrupted record. Do not sum duplicated capture directories.',
            'blockers': ['Only one captured model/prompt/evidence/probability condition: alternative conditions need a frozen plan and separate approval.',
                         'Historical captures are regression evidence, not an independent confirmation set.']}, rows


def fault_matrix():
    """Exercise the existing capture protocol with fixed callbacks, not a transport."""
    spec = importlib.util.spec_from_file_location('offline_rerank_protocol', Path(__file__).with_name('llm_rerank_evaluation.py'))
    protocol = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(protocol)
    task = {'questionId': 'fixed-fault-q', 'candidateSha256': 'fixed-fault-hash', 'order': 'original', 'attempt': 1,
            'input': {'question': '架空課題', 'candidates': [{'candidateId': 'a', 'evidence': [{'id': 'e', 'text': '架空の対応'}]}]}}
    item = {'candidateId': 'a', 'reason': '対応', 'evidenceIds': ['e']}
    cases = [('valid_empty', '{"items":[]}', None, 'success', None, 1),
             ('invalid_json', 'invalid', None, 'failed', 'parse_failure', 4),
             ('duplicate_json_key', '{"items":[],"items":[]}', None, 'failed', 'parse_failure', 4),
             ('invented_candidate', json.dumps({'items': [{**item, 'candidateId': 'fake'}]}), None, 'failed', 'grounding_failure', 1),
             ('invented_evidence', json.dumps({'items': [{**item, 'evidenceIds': ['fake']}]}), None, 'failed', 'grounding_failure', 1),
             ('unsupported_quote', json.dumps({'items': [{**item, 'reason': '本人は責任者'}]}), None, 'failed', 'grounding_failure', 1),
             ('refused', '{}', 'content_filter', 'refused', 'refused', 1),
             ('truncated', '{}', 'length', 'truncated', 'truncated', 1),
             ('http429', '{}', 429, 'failed', 'http_429', 1),
             ('http401', '{}', 401, 'failed', 'http_401', 1)]
    rows = []
    for name, content, finish, status, failure, count in cases:
        sent = []
        def fake_send(payload):
            sent.append(payload)
            if isinstance(finish, int):
                # No socket/client is constructed; HTTPError is only a fixed local exception.
                raise protocol.urllib.error.HTTPError('https://offline.invalid', finish, 'redacted', {}, None)
            return {'model': protocol.MODEL_RESPONSE,
                    'usage': {'prompt_tokens': 100, 'completion_tokens': 20},
                    'choices': [{'finish_reason': finish or 'stop', 'message': {'content': content}}]}
        result = protocol.execute(task, 'offline-dummy', fake_send)
        passed = (result['status'] == status and result['failure'] == failure and len(sent) == count)
        rows.append({'case': name, 'status': result['status'], 'failure': result['failure'],
                     'fakeAttempts': len(sent), 'expectedAttempts': count, 'passed': passed,
                     'estimatedAttempts': sum(w['usageEstimated'] for w in result['wireAttempts']),
                     'inputTokens': result['inputTokens'], 'outputTokens': result['outputTokens'],
                     'reservationUsd': result['costUsd']})
    if not all(r['passed'] for r in rows):
        raise ValueError('Fixed fault protocol regression failed')
    return {'version': VERSION, 'origin': 'fixed-fake-protocol-regression', 'qualityClaim': False,
            'callsThisRun': 0, 'cases': rows, 'passed': True,
            'protocolSha256': digest(Path(__file__).with_name('llm_rerank_evaluation.py'))}, rows


def valid_hash(value):
    return isinstance(value, str) and len(value) == 64 and all(c in '0123456789abcdef' for c in value)


def semantic(gold_path, predictions_path):
    gold, predictions = map(load, (gold_path, predictions_path))
    if gold.get('version') != VERSION or gold.get('fields') != list(FIELDS):
        raise ValueError('Unknown field definition/version')
    approval = gold.get('approval', {})
    # Hash the reviewed record payload independently of its approval metadata.
    record_hash = hashlib.sha256(json.dumps(gold['records'], ensure_ascii=False, sort_keys=True,
                                          separators=(',', ':')).encode()).hexdigest()
    approved = (approval.get('status') == 'human-approved' and all(isinstance(approval.get(k), str) and approval[k].strip()
                for k in ('reviewer', 'reviewedAt', 'evidenceReference')) and approval.get('recordsSha256') == record_hash)
    expected = {r['id']: r for r in gold['records']}
    actual = {r['id']: r for r in predictions['records']}
    if len(expected) != len(gold['records']) or len(actual) != len(predictions['records']):
        raise ValueError('Duplicate record IDs')
    if any(r.get('split') not in ('tuning', 'final') for r in gold['records']):
        raise ValueError('Explicit tuning/final split required')
    extra = sorted(set(actual) - set(expected))
    recorded = predictions.get('origin') == 'recorded-structured-responses'
    rows = []
    for ident, g in expected.items():
        p = actual.get(ident, {})
        reusable = bool(p) and all(g.get(k) == p.get(k) and g.get(k) not in (None, '') for k in BINDINGS)
        if not valid_hash(g.get('sourceSha256')) or not valid_hash(g.get('configSha256')):
            reusable = False
        for field in FIELDS:
            rule = g.get('expected', {}).get(field, {})
            state = rule.get('state')
            if state not in ('known', 'unknown', 'not_applicable', 'unreviewed'):
                raise ValueError('Every major field must explicitly declare its expected state')
            present = field in p.get('fields', {})
            value = p.get('fields', {}).get(field)
            reason, match = 'unreviewed_gold', None
            if state != 'unreviewed':
                if state == 'not_applicable':
                    reason, match = 'excluded_not_applicable', None
                elif not reusable:
                    reason, match = 'capture_not_reusable_or_missing', False
                elif not present:
                    reason, match = 'missing_field', False
                elif state == 'unknown':
                    match = value is None
                    reason = 'correct_explicit_unknown' if match else 'invented_unknown_value'
                elif field in ('skills', 'evidenceIds'):
                    match = isinstance(value, list) and len(value) == len(set(value)) and set(value) == set(rule['value'])
                    reason = 'match' if match else 'wrong_set_or_evidence'
                else:
                    aliases = rule.get('acceptedValues', [rule['value']])
                    match = any(type(value) is type(a) and canonical(value) == canonical(a) for a in aliases)
                    reason = 'match' if match else 'semantic_mismatch'
            rows.append({'id': ident, 'split': g['split'], 'field': field, 'expectedState': state,
                         'reusable': reusable, 'present': present, 'matches': match, 'reason': reason,
                         'expected': rule.get('value'), 'actual': value})
    summaries = []
    for split in ('tuning', 'final'):
        for field in FIELDS:
            subset = [r for r in rows if r['split'] == split and r['field'] == field]
            summaries.append({'split': split, 'field': field, 'agreement': mean(float(r['matches']) if r['matches'] is not None else None for r in subset),
                              'unreviewed': sum(r['expectedState'] == 'unreviewed' for r in subset)})
    final = [r for r in rows if r['split'] == 'final']
    agreement = mean(float(r['matches']) if r['matches'] is not None else None for r in final)
    final_summaries = [s for s in summaries if s['split'] == 'final']
    complete = (recorded and not extra and bool(final) and all(r['expectedState'] != 'unreviewed' for r in rows)
                and all(r['reusable'] for r in rows))
    passed = (approved and complete and agreement['value'] is not None and agreement['value'] >= .9
              and all(s['agreement']['value'] is not None and s['agreement']['value'] >= .9 for s in final_summaries))
    return {'version': VERSION, 'scope': 'field semantics; independent of JSON validity and retrieval recall',
            'callsThisRun': 0, 'qualityClaim': False, 'humanGoldApproved': approved,
            'reviewedRecordsSha256': record_hash, 'recordedStructuredOrigin': recorded, 'complete': complete, 'gatePassed': passed,
            'agreementFinal': agreement, 'summaries': summaries, 'unexpectedPredictionIds': extra,
            'failures': [r for r in rows if r['matches'] is False],
            'inputs': {str(p): digest(p) for p in (gold_path, predictions_path)}}, rows


def draft(dataset_path):
    dataset = load(dataset_path)
    records = []
    for unit in dataset['units']:
        source = unit['search']['sourceRefs']
        body = '\n'.join(s['text'] for s in source)
        records.append({'id': unit['search']['unitId'], 'split': unit['split'], 'source': source,
                        'sourceSha256': hashlib.sha256(body.encode()).hexdigest(),
                        'modelVersion': None, 'promptVersion': None, 'configSha256': None,
                        'expected': {f: {'state': 'unreviewed'} for f in FIELDS}})
    return {'version': VERSION, 'fields': list(FIELDS), 'origin': 'human-review-draft-no-gold-labels',
            'datasetSha256': digest(dataset_path), 'approval': {'status': 'pending', 'reviewer': None,
             'reviewedAt': None, 'evidenceReference': None, 'recordsSha256': None}, 'records': records}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    rr = sub.add_parser('rerank')
    rr.add_argument('--scored', required=True); rr.add_argument('--responses', required=True)
    rr.add_argument('--dataset', required=True); rr.add_argument('--out', required=True)
    se = sub.add_parser('semantic')
    se.add_argument('--gold', required=True); se.add_argument('--predictions', required=True); se.add_argument('--out', required=True)
    faults = sub.add_parser('faults'); faults.add_argument('--out', required=True)
    dr = sub.add_parser('draft')
    dr.add_argument('--dataset', required=True); dr.add_argument('--out', required=True)
    args = parser.parse_args()
    if args.command == 'rerank':
        report, rows = rerank(args.scored, args.responses, args.dataset); save(args.out, report, rows)
    elif args.command == 'semantic':
        report, rows = semantic(args.gold, args.predictions); save(args.out, report, rows)
    elif args.command == 'faults':
        report, rows = fault_matrix(); save(args.out, report, rows)
    else:
        report = draft(args.dataset); save(args.out, report, [])
    print('Offline only: no new inference, Azure reads, or cost. Report: ' + str(Path(args.out) / 'report.json'))


if __name__ == '__main__':
    main()
