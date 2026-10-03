#!/usr/bin/env python3
"""Capture approved dummy Jev classifications; no Foundry or Azure provisioning."""
import argparse
import datetime
from decimal import Decimal
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import time
import urllib.error
import urllib.request

ENDPOINT = 'https://api.typesafe.ai/v1/systemone'
MODEL = 'jev-1.13.0'
PRICE = Decimal('0.042')
MAX_CONTEXT_TOKENS = 64000
MAX_REQUESTS = 350


def hash_bytes(value):
    return hashlib.sha256(value).hexdigest()


def reserved_cost(count):
    return Decimal(count) * MAX_CONTEXT_TOKENS * PRICE / 1_000_000


def validate_budget(count, credit):
    credit = Decimal(str(credit))
    if not credit.is_finite() or credit <= 0 or credit > 1 or not 0 < count <= MAX_REQUESTS or reserved_cost(count) > credit:
        raise ValueError('Request count or credit reservation exceeds the approved bound')


def probability(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or not 0 <= value <= 1:
        raise ValueError('Invalid provider probability')
    return value


def validate_response(body, request):
    expected = json.loads(request['requestJson'])
    if body.get('model') != MODEL or expected['model'] != MODEL or not isinstance(body.get('answers'), dict):
        raise ValueError('Provider model or answers mismatch')
    if set(body['answers']) != set(expected['questions']):
        raise ValueError('Provider answer keys mismatch')
    for key, q in expected['questions'].items():
        answer = body['answers'][key]
        if answer.get('type') != q['type']:
            raise ValueError('Provider answer type mismatch')
        if q['type'] == 'choice':
            if set(answer) != {'type', 'choice', 'probabilities', 'confidence'} or set(answer['probabilities']) != set(q['criteria']):
                raise ValueError('Provider choice keys mismatch')
            values = [probability(p) for p in answer['probabilities'].values()]
            if abs(sum(values) - 1) > .000001 or answer['probabilities'].get(answer['choice']) != max(values):
                raise ValueError('Provider distribution mismatch')
            probability(answer['confidence'])
        elif q['type'] == 'noul':
            if set(answer) != {'type', 'noul'}:
                raise ValueError('Provider noul keys mismatch')
            probability(answer['noul'])
        else:
            raise ValueError('Unsupported question type')
    usage = body.get('usage')
    if not isinstance(usage, dict) or any(type(usage.get(k)) is not int for k in ['input_tokens', 'output_tokens']):
        raise ValueError('Provider token usage missing or invalid')
    if not 1 <= usage['input_tokens'] <= MAX_CONTEXT_TOKENS or usage['output_tokens'] < 0:
        raise ValueError('Provider usage exceeds reservation')
    return {'model': body['model'], 'answers': body['answers'], 'usage': usage}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError('Redirects are disabled')


def sender(key):
    opener = urllib.request.build_opener(NoRedirect())
    def send(request):
        data = request['requestJson'].encode('utf-8')
        if len(data) > 16000 or hash_bytes(data) != request['requestSha256']:
            raise ValueError('Changed or oversized request')
        req = urllib.request.Request(ENDPOINT, data=data, headers={
            'Authorization': 'Bearer ' + key, 'Content-Type': 'application/json'}, method='POST')
        with opener.open(req, timeout=60) as response:
            raw = response.read(1_048_577)
            if len(raw) > 1_048_576:
                raise ValueError('Oversized response')
            return json.loads(raw)
    return send


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2, allow_nan=False) + '\n')
    temporary.replace(path)


def capture(plan, plan_hash, output, credit, approval, send, recovery=None):
    requests = plan['requests']
    validate_budget(len(requests), credit)
    if plan['version'] != 'jev-evaluation-v1' or plan['modelVersion'] != MODEL or not approval.strip():
        raise ValueError('Approved pinned plan required')
    if len({r['id'] for r in requests}) != len(requests):
        raise ValueError('Duplicate request IDs')
    for request in requests:
        if hash_bytes(request['requestJson'].encode()) != request['requestSha256']:
            raise ValueError('Request fingerprint mismatch')
    output.mkdir(parents=True, exist_ok=True)
    meta_path = output / 'approval.json'
    if meta_path.exists():
        previous = json.loads(meta_path.read_text())
        if previous['planSha256'] != plan_hash or previous['creditBudgetUsd'] != float(credit) or previous['approvalReference'] != approval:
            raise ValueError('Cannot reuse a run with changed approval or plan')
    else:
        if any(output.iterdir()):
            raise ValueError('Nonempty run has no approval manifest')
        write_json(meta_path, dict(planSha256=plan_hash, modelVersion=MODEL, approvalReference=approval,
            creditBudgetUsd=float(credit), maximumRequests=MAX_REQUESTS, contextTokenReservation=MAX_CONTEXT_TOKENS,
            maximumReservedUsd=float(reserved_cost(len(requests))), priceUsdPerMillionInput=float(PRICE),
            outputPriceUsdPerMillion=0, pricingSource='https://docs.typesafe.ai/models',
            pricingCheckedAt='2026-10-03', startedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(), retries=0))
    journal = output / 'responses.jsonl'
    def manual_attempts(evidence, request_id, request_hash):
        attempt = evidence.get('manualDiagnosticAttempt', 1)
        failed = evidence.get('failedDiagnosticAttempts', [])
        if type(attempt) is not int or attempt not in (1, 2) or len(failed) != attempt - 1:
            raise ValueError('Manual recovery requires every prior failed diagnostic')
        for number, row in enumerate(failed, 1):
            if row.get('requestId') != request_id or row.get('requestSha256') != request_hash or row.get('status') != 'failed' or row.get('manualDiagnosticAttempt') != number or row.get('reservedInputTokens') != MAX_CONTEXT_TOKENS:
                raise ValueError('Failed diagnostic reservation or fingerprint mismatch')
        return attempt
    last = {}
    recovery_count = 0
    if journal.exists():
        for line in journal.read_text().splitlines():
            row = json.loads(line)
            if row['id'] in last and (last[row['id']]['status'] != 'pending' or row['status'] == 'pending'):
                if not (last[row['id']]['status'] == 'failed' and row['status'] == 'succeeded' and row.get('manualRecovery') is True):
                    raise ValueError('Invalid journal transitions')
                recovery_count += manual_attempts(row, row['id'], row['requestSha256'])
            last[row['id']] = row
    allowed = {r['id']: r for r in requests}
    if any(k not in allowed or r['requestSha256'] != allowed[k]['requestSha256'] for k, r in last.items()):
        raise ValueError('Journal does not belong to this plan')
    def append(row):
        with journal.open('a') as stream:
            stream.write(json.dumps(row, ensure_ascii=False, allow_nan=False) + '\n')
            stream.flush(); os.fsync(stream.fileno())
    if recovery is not None:
        recovered_id = recovery.get('requestId')
        previous = last.get(recovered_id)
        if previous is None or recovery.get('status') != 'succeeded':
            raise ValueError('Invalid explicit recovery evidence')
        request = allowed[recovered_id]
        if recovery.get('requestSha256') != request['requestSha256']:
            raise ValueError('Recovery input fingerprint mismatch')
        attempts = manual_attempts(recovery, recovered_id, request['requestSha256'])
        body = validate_response(recovery['body'], request)
        if previous['status'] == 'failed':
            validate_budget(len(requests) + recovery_count + attempts, credit)
            row = dict(id=recovered_id, requestSha256=request['requestSha256'], status='succeeded', body=body,
                durationMs=recovery['durationMs'], manualRecovery=True, attemptCount=1+attempts,
                manualDiagnosticAttempt=attempts, failedDiagnosticAttempts=recovery.get('failedDiagnosticAttempts', []))
            append(row); last[recovered_id] = row; recovery_count += attempts
        elif previous['status'] != 'succeeded' or previous.get('manualRecovery') is not True or previous['body'] != body:
            raise ValueError('Pending or mismatched recovery is not authorized')
    validate_budget(len(requests) + recovery_count, credit)
    if any(r['status'] != 'succeeded' for r in last.values()):
        raise ValueError('Failed/uncertain calls are retained; no automatic resend')
    def save_report():
        responses = [last[r['id']] for r in requests if r['id'] in last]
        input_tokens = sum(r.get('body', {}).get('usage', {}).get('input_tokens', 0) for r in responses)
        output_tokens = sum(r.get('body', {}).get('usage', {}).get('output_tokens', 0) for r in responses)
        report = dict(origin='recorded-jev-classifications', planSha256=plan_hash, modelVersion=MODEL,
            responses=responses, completedRequests=sum(r['status']=='succeeded' for r in responses),
            inputTokens=input_tokens, outputTokens=output_tokens, estimatedCostUsd=float(Decimal(input_tokens)*PRICE/1_000_000),
            maximumReservedUsd=float(reserved_cost(len(last) + recovery_count)),
            retryCount=recovery_count, attemptCount=len(last)+recovery_count,
            uncertainInputTokens=(recovery_count + sum(r['status'] != 'succeeded' for r in responses))*MAX_CONTEXT_TOKENS,
            conservativeCostUsd=float(Decimal(input_tokens + (recovery_count + sum(r['status'] != 'succeeded' for r in responses))*MAX_CONTEXT_TOKENS)*PRICE/1_000_000),
            retries=recovery_count,
            measuredAt=datetime.datetime.now(datetime.timezone.utc).isoformat())
        write_json(output / 'responses.json', report)
        return report
    for request in requests:
        if request['id'] in last:
            continue
        # Persist reservation before the network request: a crash must not cause a resend.
        pending = dict(id=request['id'], requestSha256=request['requestSha256'], status='pending', reservedInputTokens=MAX_CONTEXT_TOKENS)
        append(pending); start = time.monotonic()
        provider = None
        try:
            provider = send(request)
            body = validate_response(provider, request)
            row = dict(id=request['id'], requestSha256=request['requestSha256'], status='succeeded', body=body,
                durationMs=(time.monotonic()-start)*1000)
        except Exception as error:
            row = dict(id=request['id'], requestSha256=request['requestSha256'], status='failed', body={},
                durationMs=(time.monotonic()-start)*1000, errorCode='JEV_CAPTURE_FAILED', reservedInputTokens=MAX_CONTEXT_TOKENS, errorType=type(error).__name__)
            if isinstance(error, urllib.error.HTTPError): row['httpStatus'] = error.code
            if isinstance(error, ValueError) and str(error).startswith('Provider '): row['validationReason'] = str(error)
            if row.get('validationReason') == 'Provider distribution mismatch' and isinstance(provider, dict):
                answer = provider['answers']['domain']; values = answer['probabilities']
                row['probabilitySum'] = sum(values.values()); row['choiceProbability'] = values.get(answer['choice']); row['maximumProbability'] = max(values.values())
            append(row); last[request['id']] = row; save_report()
            raise ValueError('Jev capture stopped; failure recorded without raw response or exception text') from None
        append(row); last[request['id']] = row
        if len(last) % 10 == 0:
            save_report(); print(f'JEV_PROGRESS completed={len(last)}/{len(requests)}', flush=True)
    return save_report()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('plan', type=Path)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--execute', action='store_true')
    parser.add_argument('--recovery-response', type=Path)
    parser.add_argument('--credit-budget-usd', type=float, default=1)
    parser.add_argument('--approval-reference', default='')
    args = parser.parse_args()
    raw = args.plan.read_bytes(); plan = json.loads(raw)
    validate_budget(len(plan['requests']), args.credit_budget_usd)
    print(json.dumps(dict(plannedRequests=len(plan['requests']), model=MODEL, automaticRetries=0,
        maximumReservedUsd=float(reserved_cost(len(plan['requests']))), networkEnabled=args.execute)), flush=True)
    if not args.execute:
        return 0
    sys.path.insert(0, str(Path(__file__).parent))
    from env_config import load_env, ROOT
    configuration = load_env(ROOT / '.env')
    key = configuration.get('TYPESAFE_API_KEY') or configuration.get('TYPE_SAFE_API_KEY')
    if not key:
        raise ValueError('TYPESAFE_API_KEY must be configured securely; no inference sent')
    recovery = json.loads(args.recovery_response.read_text()) if args.recovery_response else None
    report = capture(plan, hash_bytes(raw), args.out, args.credit_budget_usd, args.approval_reference, sender(key), recovery)
    print(json.dumps({k: report[k] for k in ['completedRequests', 'inputTokens', 'outputTokens', 'estimatedCostUsd', 'retries']}))
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (ValueError, OSError, KeyError, TypeError):
        print('Jev evaluation stopped; check the pinned plan, credential setup and redacted run journal.', file=sys.stderr)
        raise SystemExit(1)
