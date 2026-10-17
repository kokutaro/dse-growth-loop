#!/usr/bin/env python3
"""Offline restoration plan checks only. No restore/activation or Azure calls."""
import argparse
import hashlib
import json
import os
from pathlib import Path

from azure_validation import confirmed_stopped, matches_environment
from issue30_safety_supervisor import ALLOWED, ENV, RG, SUB

TARGET = {'subscription': SUB, 'resourceGroup': RG, 'environment': 'dse-environment-vnet-jpe'}


def validate_snapshot(value):
    if not isinstance(value, dict) or value.get('schemaVersion') != 1 or value.get('target') != TARGET:
        raise ValueError('SNAPSHOT_TARGET_MISMATCH')
    apps = value.get('apps')
    if (not isinstance(apps, list) or len(apps) != len(ALLOWED) or any(not isinstance(a, dict) for a in apps)
            or {a.get('name') for a in apps} != set(ALLOWED)):
        raise ValueError('SNAPSHOT_APPS_MISMATCH')
    for app in apps:
        template = app.get('template')
        if not isinstance(template, dict) or template.get('name') != app['name']:
            raise ValueError('SNAPSHOT_TEMPLATE_MISMATCH')
        expected_id = f'/subscriptions/{SUB}/resourceGroups/{RG}/providers/Microsoft.App/containerApps/{app["name"]}'
        if 'id' in template and (not isinstance(template['id'], str) or template['id'].lower() != expected_id.lower()):
            raise ValueError('SNAPSHOT_RESOURCE_ID_MISMATCH')
        props = template.get('properties') or {}
        if not isinstance(props, dict) or not matches_environment(
                {'name': app['name'], 'environment': props.get('environmentId'),
                 'managedEnvironment': props.get('managedEnvironmentId')}, app['name'], ENV):
            raise ValueError('SNAPSHOT_ENVIRONMENT_MISMATCH')
        config, runtime = props.get('configuration'), props.get('template')
        if not isinstance(config, dict) or config.get('activeRevisionsMode') != 'Single' or not isinstance(runtime, dict):
            raise ValueError('SNAPSHOT_RUNTIME_MISMATCH')
        containers = runtime.get('containers')
        if (not isinstance(containers, list) or not containers
                or any(not isinstance(c, dict) or not isinstance(c.get('image'), str) or not c['image'] for c in containers)):
            raise ValueError('SNAPSHOT_IMAGE_MISSING')
        scale = runtime.get('scale')
        if (not isinstance(scale, dict) or type(scale.get('maxReplicas')) is not int or scale['maxReplicas'] != 1
                or type(scale.get('minReplicas')) is not int or scale['minReplicas'] != (0 if app['name'] == 'dse-api-vnet' else 1)):
            raise ValueError('SNAPSHOT_SCALE_MISMATCH')
    return value


def read_snapshot(path):
    raw = path.read_bytes()
    def reject_constant(_): raise ValueError('INVALID_SNAPSHOT_JSON')
    validate_snapshot(json.loads(raw, parse_constant=reject_constant))
    return raw


def freeze_snapshot(source, destination):
    raw = read_snapshot(source)
    # Exclusive 0600 creation also refuses an existing symlink or prior snapshot.
    descriptor = os.open(destination, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'wb') as file:
        file.write(raw)
    return {'sha256': hashlib.sha256(raw).hexdigest(), 'target': TARGET, 'privateSnapshot': True}


def stop_targets_confirmed(reports):
    if not isinstance(reports, dict) or set(reports) != set(ALLOWED): return False
    for app, report in reports.items():
        if (not isinstance(report, dict) or report.get('mode') != 'replicas'
                or not isinstance(report.get('target'), dict)
                or any(report['target'].get(k) != v for k, v in dict(TARGET, app=app).items())):
            return False
        checks = report.get('checks')
        if not confirmed_stopped(checks): return False
        identity = checks[0].get('data')
        if not isinstance(identity, dict) or not matches_environment(identity, app, ENV): return False
    return True


def review_plan(path, expected_sha256, reports):
    result = {'planValid': False, 'keepStoppedRequired': True, 'executionEnabled': False,
              'restorationConfirmed': False, 'liveAcceptance': 'pending', 'target': TARGET}
    try:
        raw = read_snapshot(path)
        if hashlib.sha256(raw).hexdigest() != expected_sha256:
            result['error'] = 'SNAPSHOT_HASH_MISMATCH'
        elif not stop_targets_confirmed(reports):
            result['error'] = 'STOP_TARGETS_UNCONFIRMED'
        else:
            result.update(planValid=True, snapshotSha256=expected_sha256)
    except (OSError, ValueError, TypeError, KeyError):
        result['error'] = 'SNAPSHOT_INVALID_OR_UNAVAILABLE'
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='action', required=True)
    freeze = sub.add_parser('freeze')
    freeze.add_argument('--source', required=True, type=Path)
    freeze.add_argument('--destination', required=True, type=Path)
    review = sub.add_parser('review')
    review.add_argument('--snapshot', required=True, type=Path)
    review.add_argument('--sha256', required=True)
    review.add_argument('--stop-reports', required=True, type=Path)
    args = parser.parse_args()
    try:
        if args.action == 'freeze': result = freeze_snapshot(args.source, args.destination)
        else: result = review_plan(args.snapshot, args.sha256, json.loads(args.stop_reports.read_text()))
        print(json.dumps(result, allow_nan=False))
        return 0 if args.action == 'freeze' or result['planValid'] else 2
    except (OSError, ValueError, TypeError, KeyError):
        parser.exit(2, 'Snapshot invalid/unavailable or destination exists; keep stopped, no restoration executed.\n')


if __name__ == '__main__':
    raise SystemExit(main())
