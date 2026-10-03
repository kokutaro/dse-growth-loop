#!/usr/bin/env python3
"""Issue 14の比較器用合成入力。実Jev分類・人による正解承認ではない。"""
import copy
import hashlib
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def generate(dataset_path):
    dataset = json.loads(dataset_path.read_text())
    groups = {}
    for unit in dataset['units']:
        groups.setdefault((unit['split'], unit['search']['entityType'], unit['search']['entityId']), []).append(unit)
    overall = []
    for units in groups.values():
        summary = copy.deepcopy(units[0])
        search = summary['search']
        search['id'] = search['entityId'] + '-overall-projection'
        search['unitId'] = search['entityId'] + '-overall'
        search['summary'] = '\n'.join(u['search']['summary'] for u in units)
        search['sourceRefs'] = [ref for u in units for ref in u['search']['sourceRefs']]
        summary['scenario'] = 'synthetic-overall-distribution'
        # この指定回帰例の全体値は比較用の仮定。期間比/断片平均から推定しない。
        if units[0]['scenario'] == 'multiple-careers':
            for p in search['classifications']:
                p['probability'] = .7 if p['categoryId'] == 'mechanical' else .3 if p['categoryId'] == 'software' else 0.
        overall.append(summary)
    queries = []
    for q in dataset['questions']:
        no_match = q['difficulty'] == 'no-match'
        probabilities = {'unknown': 1.} if no_match else {
            'software': .8, 'mechanical': .1, 'operations': .05, 'sales': .03, 'unknown': .02}
        queries.append(dict(questionId=q['id'], distribution=[dict(axis='domain', categoryId=c, probability=p)
            for c, p in probabilities.items()], terms=['量子暗号'] if no_match else ['ソフトウェア', '引き継ぎ']))
    return dict(version='primary-experiments-v1', origin='synthetic-experiment-inputs',
                datasetSha256=hashlib.sha256(dataset_path.read_bytes()).hexdigest(), overallUnits=overall, queries=queries)


def main():
    value = generate(ROOT / 'evaluation/dataset.json')
    (ROOT / 'evaluation/experiment-inputs.json').write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


if __name__ == '__main__':
    main()
