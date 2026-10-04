#!/usr/bin/env python3
"""Prepare new synthetic labels for human review; no model or network calls."""
import hashlib
import json
from pathlib import Path
import generate_issue30_independent_v2 as base
ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT/'evaluation/issue-30/independent-v3'
SCENARIOS = [
 ('software','event-dedup','同じ決済イベントが再送されて売上を二重計上する問題を防ぐ', 'ソフトウェア開発で、受信した決済イベントの一意IDを処理済み台帳と照合した。売上更新と処理済み記録を同じトランザクションで保存し、同一イベントの再送試験で計上が一度だけになることを確認した。'),
 ('software','unicode-cut','絵文字を含む文章の切り出しで表示が壊れる問題を改善する', 'ソフトウェア開発で、文章の切り出し位置を表示上の文字の境界で判定する方式に変更した。結合文字と複数コードポイントの絵文字を含む入力で、保存した本文と表示結果を比較した。'),
 ('mechanical','thermal-joint','温度変化で伸びる配管が支持部を押して損傷する問題を防ぐ', '機械設計で、配管の使用温度範囲と伸び量を計算した。固定支持点と滑り支持点の配置を見直し、伸縮継手を設けて温度変化時の支持部の変位を測定した。'),
 ('mechanical','cable-bend','可動部のケーブルが繰り返し曲げられて断線する問題を減らす', '機械設計で、可動部のケーブル経路と曲げ半径を見直した。ケーブル支持具の位置を変更し、最大ストロークで干渉がないことと繰り返し動作後の導通を確認した。'),
 ('sales','quote-scope','顧客へ提示した見積の作業範囲が曖昧で受注後に認識違いが起きる問題を防ぐ', '営業で、顧客への見積に対象作業、納品物、対象外作業を添付した。顧客との商談で前提条件を読み合わせ、合意した版を契約担当へ渡して追加要望を変更見積として管理した。'),
 ('sales','channel-owner','代理店と自社が同じ顧客へ別々に提案して商談が衝突する問題を改善する', '営業で、代理店経由の商談と自社商談を顧客IDで照合した。提案前に主担当を合意し、代理店との連絡窓口と顧客への次回提案日を案件台帳へ記録した。'),
 ('manufacturing','lot-label','製造途中で部品のロット表示が混ざり追跡できなくなる問題を防ぐ', '製造現場で、部品を工程へ投入する際にロット番号と作業容器の表示を照合した。容器間の移し替えでラベルを再発行し、模擬回収で完成品から投入ロットを追跡できることを確認した。'),
 ('manufacturing','furnace-drift','熱処理炉の温度の偏りで製品の硬さがばらつく問題を減らす', '製造現場で、熱処理炉の複数位置に測定器を配置して温度分布を確認した。製品の配置と加熱条件を見直し、同じロットの測定位置ごとの硬さを比較した。'),
 ('operations','room-booking','社内会議室の予約が重なり利用者同士の調整が繰り返される問題を改善する', '業務運用で、社内会議室の受付窓口を一本化した。予約時に利用時間と準備時間を確認し、キャンセル期限と予約変更の連絡先を案内して重複予約件数を集計した。'),
 ('operations','return-assets','退職時に会社貸与物の回収漏れが発生する問題を防ぐ', '業務運用で、退職予定者ごとに会社貸与物の一覧と返却先を確認した。所属部署と総務が返却チェックを分担し、最終出勤日に未返却品を照合して受領記録を保存した。')]
NO_GOLD = [
 ('i30p-no-answer-01','画像の文字領域を検出して傾き補正した経験者を探したい。'),
 ('i30p-no-answer-02','油圧回路の急激な圧力上昇を測定して抑えた実施済み事例を探したい。'),
 ('i30p-no-answer-03','古代貨幣の刻印を比較して鋳造地を研究した担当者を探したい。'),
 ('i30p-no-answer-04','中世音楽の写譜を比較して旋律の変遷を研究した事例を探したい。')]

def generate():
    old_out, old_scenarios = base.OUT, base.SCENARIOS
    try:
        base.OUT = OUT; base.SCENARIOS = SCENARIOS
        base.generate()
    finally:
        base.OUT, base.SCENARIOS = old_out, old_scenarios
    path = OUT/'dataset.json'
    data = json.loads(path.read_text().replace('i30n-', 'i30p-').replace('issue30-independent-v2', 'issue30-independent-v3'))
    data['baseVersion'] = 'new synthetic scenarios after v2 observed regression; pending human review'
    for qid, text in NO_GOLD:
        next(q for q in data['questions'] if q['id']==qid)['text'] = text
    previous = [json.loads((ROOT/p).read_text()) for p in ['evaluation/expansion-v2/dataset.json','evaluation/issue-30/independent/dataset.json','evaluation/issue-30/independent-v2/dataset.json']]
    assert not {q['text'] for q in data['questions']} & {q['text'] for d in previous for q in d['questions']}
    assert not {u['search']['summary'] for u in data['units']} & {u['search']['summary'] for d in previous for u in d['units']}
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2)+'\n')
    sources = OUT/'sources.md'; sources.write_text(sources.read_text().replace('i30n-', 'i30p-'))
    review = ['# Issue #30 独立 v3 の原文・ラベル案', '', '人による確認待ち。AI分類・再順位付けは未実行（0回）。v2の観測後修正をこの集合では調整しない。', '',
      '[架空原本60件](sources.md)・[全正解ラベル案](dataset.json)・[実行・稼働上限案](execution-plan.md)。', '',
      '人物・事例各10問と正解なし4問。同じシナリオの直接実施を3、共同実施・副業務を2、案内配布のみと他シナリオを0とする案。実社員の無作為標本ではなく、架空シナリオの確認集合。以前の質問・対応本文と完全一致は0件。', '',
      '| 質問ID | 質問 | 正解ラベル案 |', '|---|---|---|']
    review += [f"| `{q['id']}` | {q['text']} | "+('`direct`=3、`support`=2、他=0' if q['positiveIds'] else '全候補0')+' |' for q in data['questions']]
    review += ['', '正解なし01・02は分野内だが該当原本なし、03・04は分類体系の対象外の想定。対象外は検索未実行と区別する。ラベル変更があれば実AI送信前に再固定する。', '',
      '人物15／事例15／救済2、質問Top3・閾値0.1、文書0.2、Noul0.5、根拠1,200バイト、`search-query-v2`／`search-rerank-v2`、Jev 1.13.0／Luna6を固定する。一次Recall・種別ごとのHit@5・Precision@5は各90%以上、正解なし誤推薦0、根拠整合100%、warm HTTP p95は15秒以下。coldは別報告。']
    (OUT/'review.md').write_text('\n'.join(review)+'\n')
    manifest = {'status':'pending-human-review','modelCalls':0,'questions':24,'units':60,'exactOverlapWithPreviousQuestionsAndSources':0,
      'sha256':{f:hashlib.sha256((OUT/f).read_bytes()).hexdigest() for f in ['dataset.json','sources.md','review.md']},
      'generatorSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    (OUT/'pre-model-manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n')
if __name__=='__main__': generate()
