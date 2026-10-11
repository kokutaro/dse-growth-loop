#!/usr/bin/env python3
"""Offline warm24/cold3 record validation. No HTTP, Azure, model calls or approval."""
import argparse
from datetime import datetime, timezone
import json
import math
from pathlib import Path

from azure_validation import RG, confirmed_stopped, environment_id, revision_inventory, rows
from issue30_http_report import quantile
from issue30_independent_v3_http_report import SCOPE, response_evidence, score as score_warm

ROOT = Path(__file__).resolve().parents[1]


def duration(value):
    if type(value) not in (int, float) or not math.isfinite(value) or value < 0:
        raise ValueError('Finite nonnegative duration required')
    return value


def utc(value):
    if not isinstance(value, str):
        raise ValueError('UTC timestamp required')
    result = datetime.fromisoformat(value.replace('Z', '+00:00'))
    if result.tzinfo is None or result.utcoffset().total_seconds() != 0:
        raise ValueError('UTC timestamp required')
    return result.astimezone(timezone.utc)


def timing(values):
    return {'count': len(values), 'p50Ms': quantile(values, .5),
            'p95Ms': quantile(values, .95), 'maxMs': max(values)}


def cold_quality(fixture, question, record):
    """Minimum existing response contract; no new holdout or model requests."""
    try:
        observation=record['uiObservation']
        if observation['questionId'] != question['id']:
            return False
        candidates,grounded=response_evidence(fixture,record['body'],observation)
        ui=observation['ui']
        if (ui['alerts'] or '検索失敗' in ui['status'] or not isinstance(ui['status'],str)
                or record['body'].get('rerankingStatus') not in ('succeeded','all_unsuitable','no_candidates')
                or observation.get('uiCompletionUpperBoundMs') != record.get('uiCompletionUpperBoundMs')):
            return False
        ranked=[c['entityId'].removeprefix(SCOPE+'-') for c in candidates if c['entityType']==question['entityType']][:5]
        gold=set(question['positiveIds'])
        hit=len(set(ranked)&gold)
        return all(grounded) and (bool(ranked) and hit/len(ranked)>=.9 and hit/len(gold)>=.9 if gold else not candidates)
    except (KeyError,ValueError,TypeError,StopIteration,AttributeError):
        return False


def score_cold(fixture, planned_ids, records, subscription, warm_run_ids=()):
    rows(records)
    questions = {q['id']: q for q in fixture['questions']}
    if (len(planned_ids) != 3 or len(set(planned_ids)) != 3 or not set(planned_ids) <= set(questions)
            or any(questions[q]['draftDomain'] == 'unknown' for q in planned_ids)
            or len(records) != 3 or [r['questionId'] for r in records] != planned_ids):
        raise ValueError('Three ordered, preplanned in-scope cold questions required')
    cycles, snapshots, times, ui_times, failed = set(), set(), [], [], []
    quality_failed=[]
    response_ids = set(warm_run_ids)
    previous_finish = None
    target = {'subscription': subscription, 'resourceGroup': RG,
              'app': 'dse-api-vnet', 'environment': 'dse-environment-vnet-jpe'}
    expected_env = environment_id(subscription, target['environment']).lower()
    for record in records:
        if (record['request'] != {'query': questions[record['questionId']]['text'], 'target': 'all'}
                or type(record['attempt']) is not int or record['attempt'] != 1
                or type(record['preSearchRequestCount']) is not int or record['preSearchRequestCount'] != 0):
            raise ValueError('First API request, unchanged query and single attempt required')
        snapshot = record['zeroSnapshot']
        if (not isinstance(snapshot, dict) or not isinstance(snapshot.get('target'), dict)
                or snapshot.get('mode') != 'replicas' or any(snapshot['target'].get(k) != v for k, v in target.items())):
            raise ValueError('New API/environment zero snapshot required')
        checks = snapshot.get('checks', [])
        rows(checks)
        if len(checks) < 4:
            raise ValueError('Missing revision/actual replica inventory')
        revision_inventory(checks[1].get('data'))
        identity = checks[0].get('data') or {}
        if not isinstance(identity, dict):
            raise ValueError('App/environment identity required')
        observed_env = identity.get('environment') or identity.get('managedEnvironment')
        if (identity.get('name') != target['app'] or not isinstance(observed_env, str)
                or observed_env.lower() != expected_env or not confirmed_stopped(checks)):
            raise ValueError('All revisions inactive, Stopped and actual replica lists empty required')
        cycle, snapshot_id = record['cycleId'], snapshot.get('runId')
        if (not isinstance(cycle, str) or not cycle or cycle in cycles
                or not isinstance(snapshot_id, str) or not snapshot_id or snapshot_id in snapshots):
            raise ValueError('Independent cycle and zero snapshot for each cold required')
        cycles.add(cycle); snapshots.add(snapshot_id)
        zero_time, start, finish = utc(snapshot['finishedAt']), utc(record['startedAtUtc']), utc(record['finishedAtUtc'])
        if not zero_time < start <= finish or (previous_finish is not None and zero_time < previous_finish):
            raise ValueError('Sequential zero/start/finish timestamps required')
        previous_finish = finish
        network_ms = duration(record['networkMs'])
        if not math.isclose(network_ms, (finish - start).total_seconds() * 1000, abs_tol=1, rel_tol=0):
            raise ValueError('HTTP duration does not match start/finish')
        times.append(network_ms)
        ui_ms = record.get('uiCompletionUpperBoundMs')
        if ui_ms is not None:
            if duration(ui_ms) < network_ms:
                raise ValueError('UI completion cannot precede HTTP completion')
            ui_times.append(ui_ms)
        status = record.get('httpStatus')
        if status is not None and (type(status) is not int or not 100 <= status <= 599):
            raise ValueError('HTTP status required; use null and errorCode for transport failure')
        if status is None and record.get('errorCode') not in ('TIMEOUT', 'NETWORK_ERROR', 'CANCELLED'):
            raise ValueError('Transport failure must be retained explicitly')
        body = record.get('body')
        response_id = body.get('runId') if isinstance(body, dict) else None
        if isinstance(response_id, str) and response_id:
            if response_id in response_ids:
                raise ValueError('Response runId reused across cold cycles or from warm')
            response_ids.add(response_id)
        quality=status == 200 and ui_ms is not None and cold_quality(fixture,questions[record['questionId']],record)
        if not quality:
            quality_failed.append(record['questionId'])
        if status != 200 or ui_ms is None or not quality:
            failed.append(record['questionId'])
    return {'coldMeasurementComplete': not failed, 'coldQualityPassed':not quality_failed,
            'qualityFailedQuestions':quality_failed, 'plannedQuestionIds': planned_ids,
            'networkTiming': timing(times), 'uiTiming': timing(ui_times) if ui_times else None,
            'failedQuestions': failed, 'coldSloPassed': None, 'liveAcceptance': 'pending', 'billedCost': None,
            'evidenceKind': 'supplied-records-not-independently-attested',
            'timingDefinition': 'Linear quantiles; all three attempts retained, separate from warm24; cold has no acceptance threshold.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', required=True, type=Path)
    parser.add_argument('--subscription', required=True)
    parser.add_argument('--cold-question-ids', nargs=3, required=True)
    args = parser.parse_args()
    fixture = json.loads((ROOT / 'evaluation/issue-30/independent-v3/approved/dataset.json').read_text())
    try:
        warm_http = json.loads((args.directory / 'http-warm.json').read_text())
        warm = score_warm(fixture, warm_http,
                          json.loads((args.directory / 'ui-warm-observations.json').read_text()))
        warm_run_ids = {record['body']['runId'] for record in warm_http
                        if isinstance(record.get('body'), dict)
                        and isinstance(record['body'].get('runId'), str) and record['body']['runId']}
        cold = score_cold(fixture, args.cold_question_ids, json.loads((args.directory / 'http-cold.json').read_text()),
                          args.subscription, warm_run_ids)
        result = {'schemaVersion': 1, 'warm': warm, 'cold': cold,
                  'liveAcceptance': 'pending', 'approval': 'not_granted_by_this_report'}
        # Exclusive output preserves earlier runs; no raw HAR, tokens or ARM templates are exported.
        with (args.directory / 'timing-summary.json').open('x', encoding='utf-8') as file:
            json.dump(result, file, ensure_ascii=False, indent=2, allow_nan=False)
            file.write('\n')
        print(json.dumps({'warmSloPassed': warm['warmSloPassed'], 'coldMeasurementComplete': cold['coldMeasurementComplete'],
                          'liveAcceptance': result['liveAcceptance']}))
        return 0 if warm['qualityPassed'] and warm['warmSloPassed'] and cold['coldMeasurementComplete'] else 1
    except (OSError, ValueError, TypeError, KeyError):
        parser.exit(2, 'Invalid or incomplete timing records, or output already exists; no live acceptance granted.\n')


if __name__ == '__main__':
    raise SystemExit(main())
