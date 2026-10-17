#!/usr/bin/env python3
"""Freeze an unresolved adoption evidence packet; never approve production use.

This revision inventories saved reports and code-default candidates only. Actual
acceptance, a human adoption decision and production application are separate.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

from issue34_saved_measurements import DATASET, build_record, exclusive_write, finite_nonnegative, read_json

ROOT = Path(__file__).resolve().parents[1]
ROLES = ('candidate', 'issue30', 'issue31', 'issue32', 'issue33', 'issue34')
HASH_SLOTS = {'classificationPromptSha256', 'rerankPromptSha256', 'promptImplementationSha256',
              'taxonomySha256', 'normalizationImplementationSha256', 'retrievalRulesImplementationSha256',
              'effectiveRuntimeConfigSha256'}
TEXT_SLOTS = {'classificationModelId', 'classificationModelVersion', 'rerankModelId', 'rerankModelVersion',
              'classificationPromptVersion', 'rerankPromptVersion', 'taxonomyVersion',
              'normalizationVersion', 'retrievalRulesVersion'}
NUMBER_SLOTS = {'evidenceBytesPerCandidate': (64, 8000), 'personLimit': (1, 50), 'caseLimit': (1, 50),
                'rescueSlots': (0, 50), 'questionTopCategories': (1, 5), 'questionThreshold': (0, 1),
                'documentThreshold': (0, 1), 'noulThreshold': (0, 1)}


def require(condition):
    if not condition: raise ValueError('ADOPTION_PACKET_REFUSED')


def digest(value, length=64):
    require(type(value) is str and re.fullmatch('[a-f0-9]{' + str(length) + '}', value))


def candidate_settings(value):
    require(type(value) is dict and set(value) == {'schemaVersion', 'sourceCommit', 'datasetSha256',
        'configurationOrigin', 'configurationValidated', 'settings'})
    require(value['schemaVersion'] == 'issue35-candidate-v1')
    require(value['configurationOrigin'] == 'code-default-candidate-not-effective-runtime')
    require(value['configurationValidated'] is False)
    digest(value['sourceCommit'], 40); digest(value['datasetSha256'])
    settings = value['settings']
    require(type(settings) is dict and set(settings) == HASH_SLOTS | TEXT_SLOTS | set(NUMBER_SLOTS))
    for name, setting in settings.items():
        if setting is None: continue
        if name in HASH_SLOTS: digest(setting)
        elif name in TEXT_SLOTS:
            require(type(setting) is str and re.fullmatch(r'[A-Za-z0-9._-]{1,160}', setting))
        else:
            finite_nonnegative(setting)
            low, high = NUMBER_SLOTS[name]
            require(low <= setting <= high)
            if name not in ('questionThreshold', 'documentThreshold', 'noulThreshold'):
                require(type(setting) is int)
    if all(settings[k] is not None for k in ('rescueSlots', 'personLimit', 'caseLimit')):
        require(settings['rescueSlots'] <= min(settings['personLimit'], settings['caseLimit']))
    return sorted(name for name, setting in settings.items() if setting is None)


def build_packet(root, manifest):
    root = root.resolve()
    require(type(manifest) is dict and set(manifest) == {'schemaVersion', 'artifacts'})
    require(manifest['schemaVersion'] == 'issue35-adoption-input-v1')
    descriptors = manifest['artifacts']
    require(type(descriptors) is dict and set(descriptors) == set(ROLES))
    documents, evidence, paths, hashes = {}, [], set(), set()
    for role in ROLES:
        item = descriptors[role]
        require(type(item) is dict and set(item) == {'path', 'sha256'})
        digest(item['sha256'])
        require(type(item['path']) is str and bool(item['path']) and not Path(item['path']).is_absolute())
        path = (root / item['path']).resolve()
        require(path.is_relative_to(root) and path not in paths and item['sha256'] not in hashes)
        document, actual = read_json(path)
        require(actual == item['sha256'])
        paths.add(path); hashes.add(actual)
        documents[role] = document
        evidence.append({'role': role, 'path': path.relative_to(root).as_posix(), 'sha256': actual})
    candidate = documents['candidate']
    unknown_slots = candidate_settings(candidate)
    capture = documents['issue30']
    require(type(capture) is dict and capture.get('realAcceptance') == 'pending')
    fixture, dataset_hash = read_json(DATASET)
    regenerated = build_record(capture, fixture, descriptors['issue30']['sha256'], dataset_hash)
    require(documents['issue34'] == regenerated)
    require(candidate['sourceCommit'] == regenerated['provenance']['sourceCommit'])
    require(candidate['datasetSha256'] == regenerated['provenance']['inputSha256'])
    rerank, semantics, authorization = (documents['issue31'], documents['issue32'], documents['issue33'])
    for value in (rerank, semantics):
        require(type(value) is dict and value.get('version') == 'offline-quality-v1' and value.get('qualityClaim') is False)
    conditions, false_cases = rerank.get('conditionCount'), rerank.get('falsePositiveCases')
    require(type(conditions) is int and conditions >= 0 and type(false_cases) is list)
    require(all(type(row) is dict and type(row.get('questionId')) is str for row in false_cases))
    for key in ('humanGoldApproved', 'complete', 'gatePassed'): require(type(semantics.get(key)) is bool)
    agreement = semantics.get('agreementFinal')
    require(type(agreement) is dict and type(agreement.get('denominator')) is int and agreement['denominator'] >= 0)
    if agreement.get('value') is not None:
        finite_nonnegative(agreement['value']); require(agreement['value'] <= 1)
    require(type(authorization) is dict and authorization.get('schemaVersion') == 'issue33-local-evidence-v1')
    require(authorization.get('evidenceKind') == 'local_tests' and authorization.get('realAcceptance') == 'pending')
    for key in ('targetedTestsPassed', 'allDotnetTestsPassed'):
        require(type(authorization.get(key)) is int and authorization[key] > 0)
    latency, cost = regenerated['latency'], regenerated['cost']
    dependencies = {
        '30': {'status': 'failed' if latency['warmGate'] == 'failed' else 'pending',
               'warmGate': latency['warmGate'], 'coldHttpObservedCount': latency['cold3']['observedCount']},
        '31': {'status': 'pending', 'conditionCount': conditions,
               'historicalFalsePositiveQuestionCount': len({row['questionId'] for row in false_cases})},
        '32': {'status': 'pending', 'humanGoldApproved': semantics['humanGoldApproved'],
               'reportedComplete': semantics['complete'], 'reportedGatePassed': semantics['gatePassed'],
               'agreement': agreement.get('value'), 'denominator': agreement['denominator']},
        '33': {'status': 'pending', 'evidenceKind': 'local_tests',
               'targetedTestsPassed': authorization['targetedTestsPassed'],
               'allDotnetTestsPassed': authorization['allDotnetTestsPassed']},
        '34': {'status': 'pending', 'measurementState': regenerated['state'], 'monthlyGate': cost['monthlyGate']},
    }
    blockers = ['ISSUE30_ACCEPTANCE_PENDING', 'ISSUE31_INDEPENDENT_COMPARISON_PENDING',
                'ISSUE32_SEMANTIC_ACCEPTANCE_PENDING', 'ISSUE33_REAL_SERVICE_ACCEPTANCE_PENDING',
                'ISSUE34_FULL_MEASUREMENT_PENDING', 'CURRENT_PRICES_FX_TAX_INVOICE_UNVERIFIED',
                'EFFECTIVE_RUNTIME_CONFIG_UNVERIFIED', 'HUMAN_ADOPTION_DECISION_REQUIRED']
    if latency['warmGate'] == 'failed': blockers.append('WARM_GATE_FAILED')
    if latency['cold3']['observedCount'] != 3: blockers.append('COLD_HTTP_EVIDENCE_INCOMPLETE')
    if unknown_slots: blockers.append('CANDIDATE_SETTINGS_INCOMPLETE')
    return {'schemaVersion': 'issue35-adoption-packet-v1', 'evidenceKind': 'offline_inventory',
        'decision': 'pending-human-decision', 'adoptionStatus': 'not_adopted', 'productionApplicationAllowed': False,
        'candidate': {'sourceCommit': candidate['sourceCommit'], 'datasetSha256': candidate['datasetSha256'],
                      'configurationDocumentSha256': descriptors['candidate']['sha256'], 'unknownSettingSlots': unknown_slots,
                      'effectiveConfigurationVerified': False},
        'dependencies': dependencies,
        'monthly': {'searchAssumption': cost['monthlySearchAssumption'], 'approvedBudgetJpy': cost['approvedMonthlyBudgetJpy'],
                    'estimatedTotalJpy': None, 'invoiceActualJpy': None, 'gate': 'pending',
                    'unknownComponents': cost['unknownComponents']},
        'blockingConditions': blockers, 'evidence': evidence,
        'limitations': ['No new measurements, model calls, ledger reads, price lookups, or runtime changes.',
            'Reported fixture success and deferred optimization are not service acceptance or adoption approval.',
            'Historical comparison results are not independent evidence for the current candidate.',
            'Settings describe a code-default candidate; hashes do not prove the effective deployed configuration.',
            'This packet cannot authorize production or deployment, even when a reported timing gate passes.']}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--root', type=Path, default=ROOT)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        manifest, _ = read_json(args.manifest)
        packet = build_packet(args.root, manifest)
        exclusive_write(args.output, packet)
    except (OSError, ValueError, TypeError, KeyError, OverflowError, RecursionError):
        print('ADOPTION_PACKET_REFUSED', file=sys.stderr)
        return 2
    print(json.dumps({'decision': packet['decision'], 'adoptionStatus': packet['adoptionStatus'],
                      'productionApplicationAllowed': False}))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
