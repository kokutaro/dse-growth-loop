"""Versioned concrete-skill agreement. No inference and no conversion of the old eight-field gold."""
import argparse
import hashlib
import json
import re
from pathlib import Path

VERSION = 'specific-skills-eval-v1'
BINDINGS = ('sourceSha256', 'modelVersion', 'promptVersion', 'configSha256')
FIELDS = ('raw', 'normalizationCandidate', 'evidenceId', 'quote', 'participation')
PARTICIPATIONS = ('performed', 'joint', 'assisted', 'consultation', 'aspiration', 'other_person', 'negated', 'unclear')


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(',', ':'))


def validate(value):
    if value.get('version') != 'specific-skills-v1' or value.get('state') not in ('extracted', 'unknown', 'not_evaluated'):
        raise ValueError('Unknown specific-skill contract')
    if value['state'] != 'extracted':
        if value.get('items', 'missing') is not None:
            raise ValueError('Unknown/missing is not an empty set')
        return
    items = value.get('items')
    if not isinstance(items, list):
        raise ValueError('An assessed empty set must be an explicit array')
    if len(items) > 40 or len({canonical(i) for i in items}) != len(items):
        raise ValueError('Duplicate or oversized item set')
    for item in items:
        if set(item) != set(FIELDS) or not all(isinstance(item[k], str) and item[k].strip() for k in FIELDS if k != 'normalizationCandidate'):
            raise ValueError('Every candidate retains raw spelling, evidence, quotation and involvement')
        if item['participation'] not in PARTICIPATIONS or len(item['raw']) > 100 or len(item['quote']) > 8000:
            raise ValueError('Invalid skill or involvement')
        if item['normalizationCandidate'] is not None and (not isinstance(item['normalizationCandidate'], str) or not item['normalizationCandidate'].strip() or len(item['normalizationCandidate']) > 100):
            raise ValueError('Typed normalization candidate required')


def evaluate(gold, predictions):
    if gold.get('version') != VERSION or predictions.get('version') != VERSION:
        raise ValueError('Old eight-field labels cannot be reinterpreted as specific-skills-v1 gold')
    records = gold['records']
    if len({r['id'] for r in records}) != len(records) or len({r['id'] for r in predictions['records']}) != len(predictions['records']):
        raise ValueError('Duplicate IDs')
    actual = {r['id']: r for r in predictions['records']}
    rows = []
    for g in records:
        if g['split'] not in ('tuning', 'final'):
            raise ValueError('Explicit split required')
        validate(g['expected'])
        if g['expected']['state'] == 'not_evaluated':
            raise ValueError('Unreviewed labels cannot stand in for reviewed unknown')
        p = actual.get(g['id'], {})
        bound = (p.get('status') == 'success' and all(isinstance(g.get(k), str) and g[k] and g[k] == p.get(k) for k in BINDINGS)
                 and all(re.fullmatch(r'[0-9a-f]{64}', g.get(k, '')) for k in ('sourceSha256', 'configSha256')))
        value = p.get('specificSkills')
        valid = True
        try:
            validate(value or {})
        except (ValueError, TypeError, KeyError):
            valid = False
        match = bound and valid and value['state'] == g['expected']['state'] and (
            value['items'] is None if g['expected']['items'] is None else
            {canonical(i) for i in value['items'] or []} == {canonical(i) for i in g['expected']['items']})
        rows.append({'id': g['id'], 'split': g['split'], 'bound': bound, 'valid': valid, 'matches': bool(match), 'expected': g['expected'], 'actual': value})
    approval = gold.get('approval', {})
    approved = (approval.get('status') == 'human-approved' and all(approval.get(k) for k in ('reviewer', 'reviewedAt', 'evidenceReference'))
                and approval.get('recordsSha256') == hashlib.sha256(canonical(records).encode()).hexdigest())
    captured = predictions.get('origin') == 'recorded-specific-skills-responses'
    extra = sorted(set(actual) - {r['id'] for r in records})
    return {'version': VERSION, 'callsThisRun': 0, 'qualityClaim': bool(approved and captured and bool(rows) and not extra and all(r['bound'] and r['valid'] for r in rows)),
            'humanGoldApproved': bool(approved), 'independentUnseenTest': predictions.get('independentUnseenTest') is True,
            'gatePassed': None, 'unexpectedPredictionIds': extra, 'gateNotice': 'No acceptance threshold agreed for this new contract; never replace the previous eight-field score.',
            'summaries': [{'split': split, 'correct': sum(r['matches'] for r in rows if r['split'] == split),
                           'denominator': sum(r['split'] == split for r in rows)} for split in ('tuning', 'final')], 'rows': rows}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--gold', required=True); parser.add_argument('--predictions', required=True); parser.add_argument('--out', required=True)
    args = parser.parse_args()
    output = Path(args.out)
    if output.exists():
        raise ValueError('Refuse to overwrite an earlier evaluation')
    output.write_text(json.dumps(evaluate(json.loads(Path(args.gold).read_text()), json.loads(Path(args.predictions).read_text())), ensure_ascii=False, indent=2) + '\n')
