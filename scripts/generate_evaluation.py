"""Rebuild deterministic OOXML ingestion fixtures, never call an AI service.

These minimal packages are test inputs, not presentation documents. Text/cell
expectations are checked directly against their XML in tests/evaluation.
"""
import copy
import hashlib
import json
from pathlib import Path
from xml.sax.saxutils import escape
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'evaluation'
CATEGORIES = ['sales', 'operations', 'manufacturing', 'mechanical', 'software', 'unknown']


def save(name, value):
    (OUT / name).write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n')


def package(path, entries):
    with ZipFile(path, 'w') as z:
        for name, value in sorted(entries.items()):
            info = ZipInfo(name, (2026, 9, 26, 0, 0, 0))
            info.compress_type = ZIP_DEFLATED
            z.writestr(info, value.encode())


def office(split, rows):
    """DOCX paragraphs and XLSX inline-string cells are deliberately uncomplicated."""
    folder = OUT / 'documents'
    folder.mkdir(exist_ok=True)
    rel = 'http://schemas.openxmlformats.org/package/2006/relationships'
    office_rel = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
    types = 'http://schemas.openxmlformats.org/package/2006/content-types'
    w = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
    s = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
    paragraphs = ['架空職歴と事例の抽出試験', '全件架空。検索評価用の入力であり実在する社員や実績を含まない。']
    paragraphs += [f'{r[0]} | {r[1]} | {r[2]}' for r in rows if r[3]]
    doc = f'<w:document xmlns:w="{w}"><w:body>' + ''.join(
        f'<w:p><w:r><w:t>{escape(p)}</w:t></w:r></w:p>' for p in paragraphs) + '</w:body></w:document>'
    package(folder / f'{split}.docx', {
        '[Content_Types].xml': f'<Types xmlns="{types}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>',
        '_rels/.rels': f'<Relationships xmlns="{rel}"><Relationship Id="rId1" Type="{office_rel}/officeDocument" Target="word/document.xml"/></Relationships>',
        'word/document.xml': doc,
    })
    cell_rows = [['職歴または事例単位ID', '人物または事例ID', '抽出期待本文']] + [r[:3] for r in rows]
    cells = {f'{chr(65+c)}{i}': v for i, row in enumerate(cell_rows, 1) for c, v in enumerate(row)}
    sheet = f'<worksheet xmlns="{s}"><sheetData>' + ''.join(
        f'<row r="{i}">' + ''.join(f'<c r="{chr(65+c)}{i}" t="inlineStr"><is><t>{escape(v)}</t></is></c>' for c, v in enumerate(row)) + '</row>'
        for i, row in enumerate(cell_rows, 1)) + '</sheetData></worksheet>'
    package(folder / f'{split}.xlsx', {
        '[Content_Types].xml': f'<Types xmlns="{types}"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>',
        '_rels/.rels': f'<Relationships xmlns="{rel}"><Relationship Id="rId1" Type="{office_rel}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml': f'<workbook xmlns="{s}" xmlns:r="{office_rel}"><sheets><sheet name="架空職歴" sheetId="1" r:id="rId1"/></sheets></workbook>',
        'xl/_rels/workbook.xml.rels': f'<Relationships xmlns="{rel}"><Relationship Id="rId1" Type="{office_rel}/worksheet" Target="worksheets/sheet1.xml"/></Relationships>',
        'xl/worksheets/sheet1.xml': sheet,
    })
    return [{'path': f'documents/{split}.docx', 'paragraphs': paragraphs},
            {'path': f'documents/{split}.xlsx', 'sheet': '架空職歴', 'cells': cells}]


def main():
    OUT.mkdir(exist_ok=True)
    units, questions, responses, contracts, expectations = [], [], [], [], []
    templates = {x['schema']: x['value'] for x in json.loads((ROOT / 'docs/specs/examples.json').read_text())}
    for split in ['tuning', 'final']:
        prefix = 't' if split == 'tuning' else 'f'
        # 確認群は別人物・別職歴・別文書。単なる質問の言い換えを跨がせない。
        activity = '受注APIの障害復旧手順を実装し、引き継ぎ時間を短縮した。' if split == 'tuning' else '倉庫制御サービスのリリース自動化と運用引き継ぎを担当した。'
        def add(number, scenario, text, grade, *, kind='person', software=.7, mechanical=.3,
                confidence=.8, signal=None, terms=None, available=True, scope='demo', status='active',
                source_version=1, active_version=1, smoke=True, insufficient=False, unit_suffix='01'):
            entity = f'{prefix}-{kind}-{number:03}'
            uid = f'{entity}-experience-{unit_suffix}' if kind == 'person' else f'{entity}-fragment-{unit_suffix}'
            group = f'{prefix}-document-group-{kind}-{number:03}'
            probs = dict.fromkeys(CATEGORIES, 0.)
            probs.update(software=software, mechanical=mechanical)
            if scenario == 'flat-distribution':
                probs = dict.fromkeys(CATEGORIES, 1/6)
            evidence = f'{uid}-evidence'
            search = dict(id=f'{uid}-projection', entityId=entity, entityType=kind, tenantId='demo-tenant', unitId=uid,
                title=f'架空{entity}', summary=text, scopeIds=[scope], status=status, available=available,
                taxonomyVersion='taxonomy-v1', classifications=[dict(axis='domain', categoryId=c, probability=v) for c,v in probs.items()],
                experienceSignals=[dict(signalId='handover', yesProbability=signal, evidenceState='supported' if signal is not None else 'unknown' if insufficient else 'not_evaluated')],
                normalizedTerms=terms if terms is not None else ['ソフトウェア', '引き継ぎ'],
                sourceRefs=[dict(id=evidence, documentTitle=f'{split}.xlsx', location=f'架空職歴!C{len([u for u in units if u["split"] == split])+2}', text=text)],
                confirmationState='self_corrected' if scenario == 'self-correction' else 'fixture')
            units.append(dict(search=search, groupId=group, split=split, smoke=smoke, sourceVersion=source_version,
                              currentActiveVersion=active_version, scenario=scenario, expectedGrade=grade, insufficient=insufficient))
            classification = copy.deepcopy(templates['ClassificationResult'])
            classification.update(id=f'{uid}-classification', unitId=uid, probabilities=probs,
                choice=max(probs, key=probs.get), confidence=confidence, origin='fixture', aiRunId=None,
                evidenceRefs=[dict(id=evidence, documentVersionId=f'{group}-v{source_version}', sourceVersion=source_version)])
            contracts.append(dict(schema='ClassificationResult', value=classification))
            projection = copy.deepcopy(templates['SearchUnit'])
            projection.update(search, sourceVersion=source_version, activeVersion=source_version,
                classificationResultIds=[classification['id']], evidenceRefs=classification['evidenceRefs'])
            contracts.append(dict(schema='SearchUnit', value=projection))
            return entity

        add(1, 'multiple-careers', '2015-01〜2018-12 機械設計を48か月担当。', 3, software=.05, mechanical=.95, terms=['機械設計'])
        a = add(1, 'multiple-careers', '2019-01〜2021-12 ソフト開発を36か月担当。' + activity, 3, software=.95, mechanical=.05, unit_suffix='02')
        b = add(2, 'secondary-category', '主業務は機械設計。副業務として' + activity, 2, software=.3, mechanical=.7)
        c = add(3, 'low-confidence', '複数部門を兼務し、' + activity, 2, software=.15, mechanical=.85, confidence=.15, signal=.9, terms=[])
        d = add(4, 'flat-distribution', '設計、調達、製造、運用を横断し、' + activity, 2, signal=.9, terms=[])
        add(5, 'unknown-experience', 'ソフト引き継ぎ会議に参加。担当作業と期間は記載なし。', 0, insufficient=True, confidence=None)
        add(6, 'department-only', '部署がソフト開発と引き継ぎを完了。本人は日程調整のみ。', 0)
        add(7, 'self-correction', '本人訂正：ソフト実装担当という旧記載は誤り。担当は会議室予約のみ。', 0, software=.1, mechanical=.9)
        add(8, 'unavailable', activity + ' 現在は参加不可。', 0, available=False)
        add(9, 'outside-scope', activity + ' 他部署限定で公開。', 0, scope='private')
        add(10, 'withdrawn', activity + ' 公開撤回済み。', 0, status='inactive')
        add(11, 'old-version', activity + ' 旧版の記載。現行版は実装を担当していない。', 0, source_version=1, active_version=2)
        e = add(12, 'repeated-report', activity + ' 週報同一内容。', 3)
        for repeat in range(2, 7):
            add(12, 'repeated-report', activity + ' 週報同一内容。', 3, unit_suffix=f'{repeat:02}')
        case = add(1, 'secondary-category', '製造部門の事例。' + activity, 3, kind='knowledge', software=.3, mechanical=.7)
        add(2, 'different-role', 'ソフト製品の引き継ぎ案内チラシを配送。実装や運用設計は対象外。', 0, kind='knowledge')
        add(3, 'reference-only', '引き継ぎ設計に関する用語集。実践例や解決実績はない。', 1, kind='knowledge')
        for number in range(13, 163):
            role = ['契約更新窓口', 'ソフト製品の営業', '端末の梱包発送', '会議の議事録作成', '部署予算の集計'][number % 5]
            add(number, 'hard-negative', f'架空案件{prefix}-{number}でソフトウェアと引き継ぎを扱う{role}。開発・障害復旧・運用設計は担当していない。', 0,
                software=.8, mechanical=.2, smoke=False)
        local = [u for u in units if u['split'] == split]
        for smoke in [True, False]:
            for kind in ['person', 'knowledge']:
                for no_match in [False, True]:
                    qid = f'{prefix}-{"smoke" if smoke else "large"}-{kind}-{"none" if no_match else "software"}'
                    selected = [u for u in local if u['search']['entityType'] == kind and (not smoke or u['smoke'])]
                    by_entity = {}
                    for u in selected:
                        by_entity[u['search']['entityId']] = dict(entityId=u['search']['entityId'], grade=0 if no_match else u['expectedGrade'],
                            insufficient=False if no_match else u['insufficient'], reason='量子暗号実装の記載なし。' if no_match else u['search']['summary'],
                            evidenceRefs=[u['search']['sourceRefs'][0]['id']])
                    request = dict(categories=[dict(axis='domain', categoryId='unknown' if no_match else 'software')],
                        signalIds=[] if no_match else ['handover'], terms=['量子暗号'] if no_match else [],
                        probabilityThreshold=.2, noulThreshold=.5, limit=20)
                    questions.append(dict(id=qid, text='量子暗号実装の経験者・事例を探す。' if no_match else activity + ' 同様の実装経験があり参加可能な候補を探す。',
                        entityType=kind, difficulty='no-match' if no_match else 'multiple-careers' if kind == 'person' else 'secondary-category',
                        split=split, smoke=smoke, request=request, judgments=list(by_entity.values())))
                    # 保存応答は人手レビュー前の合成スタブ。ラベルを実行時に順位へ変換しない。
                    ranking = [] if no_match else [c, d] if kind == 'person' else [case]
                    for n in [20, 50, 100]:
                        responses.append(dict(questionId=qid, n=n, status='success', ranking=ranking))
        expectations.extend(office(split, [(u['search']['unitId'], u['search']['entityId'], u['search']['summary'], u['smoke']) for u in local]))
    for expected in expectations:
        expected['sha256'] = hashlib.sha256((OUT / expected['path']).read_bytes()).hexdigest()
    save('dataset.json', dict(version='evaluation-v1', origin='synthetic', humanReviewStatus='human-approved', units=units, questions=questions))
    save('saved-responses.json', dict(version='evaluation-v1', origin='synthetic-saved-response', modelVersion='fixture-no-model', promptVersion='fixture-rerank-v1', responses=responses))
    save('contract-records.json', contracts)
    save('extraction-expected.json', expectations)
    save('gates.template.json', dict(status='pending', approvedBy='', frozenAt=None, datasetSha256='', replaySha256='',
        primaryRecallMinimum=None, structuredAccuracyMinimum=None, warmP95MsMaximum=None, coldP95MsMaximum=None, aiBudgetYenMaximum=None))
    review = ['# 正解ラベル確認票', '', '全件架空。作成者はAIエージェント。2026-10-03、Issue #14対応時にユーザーがevaluation-v1の全ラベルを承認。', '',
              '関連度: 0 非該当 / 1 参考 / 2 有用 / 3 直接。情報不足は別フラグ。', '',
              '| 質問 | ID | 関連度 | 情報不足 | 根拠 |', '|---|---|---:|---|---|']
    for q in questions:
        for j in q['judgments']:
            review.append(f'| {q["id"]} | {j["entityId"]} | {j["grade"]} | {j["insufficient"]} | {j["reason"]} |')
    (OUT / 'review.md').write_text('\n'.join(review) + '\n')


if __name__ == '__main__':
    main()
