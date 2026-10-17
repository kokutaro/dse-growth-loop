#!/usr/bin/env python3
"""Convert saved numeric Issue30 observations to an incomplete Issue34 record.

Offline only: no service calls, operation collection, ledger reads, prices, or
acceptance decisions. Input summaries are recomputed, never trusted as gates.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
DATASET = ROOT / 'evaluation/issue-30/independent-v3/approved/dataset.json'
PLAN = ROOT / 'docs/evaluation/issue-34/measurement-plan-20261011.json'
LIMIT = 15000
COLD_QUESTIONS = {'cold01': 'i30p-software-event-dedup-person-query',
                  'cold02': 'i30p-software-event-dedup-knowledge-query',
                  'cold03': 'i30p-no-answer-01'}


def require(condition):
    if not condition:
        raise ValueError('INVALID_SAVED_MEASUREMENTS')


def finite_nonnegative(value):
    require(type(value) in (int, float))
    try: require(math.isfinite(value) and value >= 0)
    except OverflowError: raise ValueError('INVALID_SAVED_MEASUREMENTS') from None


def quantile(values, probability):
    values = sorted(values)
    position = (len(values) - 1) * probability
    low = int(position)
    return values[low] + (values[min(low + 1, len(values) - 1)] - values[low]) * (position - low)


def timing(rows, expected):
    measured = [r['networkMs'] for r in rows if r.get('networkMs') is not None]
    complete = len(measured) == expected
    return {'expectedCount': expected, 'observedCount': len(measured),
            'p50Ms': quantile(measured, .5) if complete else None,
            'p95Ms': quantile(measured, .95) if complete else None}


def build_record(capture, fixture, capture_sha256, dataset_sha256):
    require(type(capture) is dict and type(fixture) is dict)
    commit = capture.get('sourceHead')
    require(type(commit) is str and re.fullmatch(r'[a-f0-9]{40}', commit))
    for digest in (capture_sha256, dataset_sha256):
        require(type(digest) is str and re.fullmatch(r'[a-f0-9]{64}', digest))
    questions = fixture.get('questions')
    require(type(questions) is list and len(questions) == 24)
    require(all(type(q) is dict and type(q.get('id')) is str for q in questions))
    require(len({q['id'] for q in questions}) == 24)
    outside = {q['id'] for q in questions if q.get('draftDomain') == 'unknown' and not q.get('positiveIds')}
    require(len(outside) == 2)
    cold_questions = COLD_QUESTIONS
    require(set(cold_questions.values()) <= {q['id'] for q in questions})
    rows = capture.get('measurements')
    require(type(rows) is list and len(rows) <= 27)
    warm, cold, seen, responses = [], [], set(), set()
    unexpected_outcomes = 0
    for row in rows:
        require(type(row) is dict)
        phase, sequence = row.get('phase'), row.get('sequenceOrCycle')
        require(phase in ('warm', 'cold'))
        if phase == 'warm':
            require(type(sequence) is int and 1 <= sequence <= 24)
            expected_question = questions[sequence - 1]['id']
            warm.append(row)
        else:
            require(type(sequence) is str and sequence in cold_questions)
            expected_question = cold_questions[sequence]
            cold.append(row)
        require(row.get('questionId') == expected_question)
        require((phase, sequence) not in seen)
        seen.add((phase, sequence))
        status, duration = row.get('httpStatus'), row.get('networkMs')
        require(status is None or type(status) is int and 100 <= status <= 599)
        if duration is not None:
            finite_nonnegative(duration)
            require(status is not None)
        error = row.get('errorCode')
        require(error is None or type(error) is str and bool(error))
        require(status != 200 or error is None)
        response = row.get('responseRunId')
        require(response is None or type(response) is str and bool(response.strip()))
        if status == 200: require(response is not None)
        if response is not None:
            require(response not in responses)
            responses.add(response)
        if phase == 'warm' and status is not None:
            expected_status = 422 if expected_question in outside else 200
            expected_error = 'CLASSIFICATION_OUT_OF_SCOPE' if expected_status == 422 else None
            if status != expected_status or error != expected_error:
                unexpected_outcomes += 1
    result = json.loads(PLAN.read_text())
    result.update(runId='issue34-offline-' + capture_sha256[:20], state='incomplete',
                  evidenceKind='offline_replay', observedAt=None, operations=[], storage=[])
    result['bounds'] = {key: None for key in result['bounds']}
    result['cost'].update(prices=[], components=[], estimatedMonthlyTotalJpy=None,
        invoiceActualTotalJpy=None, monthlyGate='pending', unknownComponents=[
            'all_cosmos_operations_and_sdk_retries', 'all_ai_usage_failures_and_prices',
            'index_data_backup_storage', 'api_worker_job_runtime', 'blob_queue_acr',
            'requests_network_logging', 'fx_tax_contract_and_invoice_reconciliation'])
    result['provenance'].update(sourceCommit=commit, sourceSha256=None, inputSha256=dataset_sha256,
        configSha256=None, serviceStateSha256=None, candidateSha256=None,
        goldApprovalReference=None, executionApprovalReference=None)
    latency = result['latency']
    latency.update(method='linear_interpolation_ranks_(n-1)*p', warmP95LimitMs=LIMIT)
    latency['warmAll24'] = timing(warm, 24)
    latency['warmSearch22'] = timing([r for r in warm if r['questionId'] not in outside], 22)
    latency['cold3'] = dict(timing(cold, 3), gate='report_only')
    complete_warm = all(latency[k]['p95Ms'] is not None for k in ('warmAll24', 'warmSearch22'))
    latency['warmGate'] = ('failed' if unexpected_outcomes else
        'passed' if complete_warm and all(latency[k]['p95Ms'] <= LIMIT for k in ('warmAll24', 'warmSearch22')) else
        'failed' if complete_warm else 'pending')
    missing_cold = [cycle for cycle in cold_questions if not any(r['sequenceOrCycle'] == cycle and r.get('networkMs') is not None for r in cold)]
    result['limitations'] = [
        'Offline replay of saved numeric observations; no new service call or execution approval.',
        'Warm all24 and fixed searched22 include first-after-startup observations; threshold remains 15000 ms.',
        'Cold means first search after stop/reactivation; container startup-wait inclusion is unverified.',
        'Incomplete cohorts have null p50/p95; missing cold HTTP evidence: ' + (','.join(missing_cold) or 'none'),
        'UI completion, response quality, and the authenticity of saved observations are not revalidated by this converter.',
        'Source commit is reported by the capture; executed source/config/service-state hashes remain unverified.',
        'No service request/correlation IDs or same-run operation/RU evidence are reconstructed from aggregate observations.',
        'AI usage, prices, storage, runtime, full costs and invoice reconciliation remain unknown; no ledger is read.',
        'Warm timing status is separate from overall acceptance; state stays incomplete and monthly gate stays pending.',
        'Observed unexpected warm HTTP outcomes: ' + str(unexpected_outcomes),
    ]
    result['evidenceReferences'] = ['capture-sha256:' + capture_sha256, 'dataset-sha256:' + dataset_sha256]
    return result


def read_json(path):
    require(path.is_file())
    def pairs(items):
        value = {}
        for key, item in items:
            require(key not in value)
            value[key] = item
        return value
    def constant(_): raise ValueError('INVALID_SAVED_MEASUREMENTS')
    with path.open('rb') as source: raw = source.read(16 * 1024 * 1024 + 1)
    require(len(raw) <= 16 * 1024 * 1024)
    return json.loads(raw, object_pairs_hook=pairs, parse_constant=constant), hashlib.sha256(raw).hexdigest()


def exclusive_write(path, value):
    payload = (json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n').encode()
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as output:
            temporary = Path(output.name)
            output.write(payload)
            output.flush()
            os.fsync(output.fileno())
        os.link(temporary, path)
    finally:
        if temporary is not None: temporary.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        capture, digest = read_json(args.capture)
        fixture, dataset_digest = read_json(DATASET)
        result = build_record(capture, fixture, digest, dataset_digest)
        exclusive_write(args.output, result)
    except (OSError, ValueError, TypeError, KeyError, OverflowError, RecursionError):
        print('SAVED_MEASUREMENTS_REFUSED', file=sys.stderr)
        return 2
    print(json.dumps({'state': result['state'], 'warmGate': result['latency']['warmGate'],
                      'monthlyGate': result['cost']['monthlyGate']}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
