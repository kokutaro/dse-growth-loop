#!/usr/bin/env python3
"""New, model-unseen dummy cohort. Labels remain pending until human review."""
import hashlib
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'evaluation/issue-30/independent-v2'
SCENARIOS=[
 ('software','deadlock','同時更新のロック順序の違いで取引処理が停止する問題を防ぐ',
  'ソフトウェア開発で、複数テーブルの更新順序を統一した。デッドロック検出時はトランザクション全体を有限回再試行し、同時更新試験で処理完了を確認した。'),
 ('software','pagination','更新が続く一覧でページ移動時に項目が重複または欠落する問題を防ぐ',
  'ソフトウェア開発で、一覧を更新日時と一意IDの組で並べた。最後に表示したキーから次のページを取得する方式へ変更し、追加と削除が並行する試験で重複と欠落がないことを確認した。'),
 ('mechanical','resonance','駆動周波数付近の共振で装置の振動が増幅する問題を抑える',
  '機械設計で、装置の固有振動数を打撃試験で測定した。支持部の剛性を変更して駆動周波数から離し、運転時の振動振幅を測定した。'),
 ('mechanical','corrosion','異種金属の接触部が屋外で腐食する問題を防ぐ',
  '機械設計で、アルミ部品と鋼製締結部の接触を絶縁ワッシャーで分離した。排水経路を追加し、塩水噴霧試験で接触部の腐食が抑えられることを確認した。'),
 ('sales','renewal','契約更新の連絡が遅れて継続顧客を失う問題を改善する',
  '営業で、契約終了日の九十日前に更新担当へ通知する運用を導入した。利用状況を顧客と確認し、更新意思と次回連絡日を案件台帳で管理した。'),
 ('sales','tender','入札で必要な提出書類の不足により失格となる問題を防ぐ',
  '営業で、入札要領から必須添付書類と提出期限を一覧化した。提出前に別担当者が書類の有効期限と署名を照合し、受領証を保管する運用を導入した。'),
 ('manufacturing','moisture','樹脂材料の吸湿で成形品に気泡が発生する問題を減らす',
  '製造現場で、成形前の樹脂材料の含水率を測定した。乾燥温度と時間を材料ごとに設定し、開封後の保管時間を制限して気泡不良率を確認した。'),
 ('manufacturing','setup','品種切替時の段取り作業で生産停止が長引く問題を改善する',
  '製造現場で、段取り作業を設備停止中の作業と事前準備に分けた。治具を事前に配置し、取付位置を標準化して切替時の停止時間を計測した。'),
 ('operations','visitor','来訪者の退館記録が欠けて災害時の所在確認ができない問題を防ぐ',
  '業務運用で、来訪者の入館証を受付で発行し退館時に回収した。未返却の入館証と受入担当者を一覧にし、避難訓練で所在確認の手順を検証した。'),
 ('operations','expense','出張精算の費目誤りで差戻しが続く問題を減らす',
  '業務運用で、出張精算の費目ごとに必要な証憑と入力例を示した。申請前に日付と費目の組を確認するチェックを追加し、差戻し理由を月次で集計した。')]

NAMES={'software':'ソフトウェア','mechanical':'機械設計','sales':'営業','manufacturing':'製造','operations':'業務運用'}
MAIN={'software':'機械設計','mechanical':'営業','sales':'ソフトウェア開発','manufacturing':'営業','operations':'機械設計'}

def generate():
 if OUT.exists():raise ValueError('Dataset already exists; preserve review history')
 units=[];questions=[];sources=[]
 for domain,topic,problem,action in SCENARIOS:
  group='i30n-'+domain+'-'+topic
  for kind in ['person','knowledge']:
   question=problem+('経験のある担当者を探したい。' if kind=='person' else '実施済みの事例を探したい。')
   for role in ['direct','support','negative']:
    entity=f'{group}-{kind}-{role}'
    text=action if role=='direct' else ('第一の職歴では'+MAIN[domain]+'を担当。第二の職歴では部署の定例報告を担当。副業務として次の対応を共同実施した。'+action if kind=='person' else '次の改善の実施に共同で参加した。'+action)
    if role=='negative':text=NAMES[domain]+'の資料に「'+question+'」という相談を記録した。'+('本人は案内を配布しただけで、設計・実装・改善を実施していない。' if kind=='person' else '相談を載せた案内資料であり、対策の実施・検証結果の記録はない。')
    search={'id':entity+'-projection','entityId':entity,'entityType':kind,'tenantId':'demo-tenant','unitId':entity+'-unit','title':'架空'+entity,
       'summary':text,'scopeIds':['issue30-independent-v2'],'status':'active','available':True,'taxonomyVersion':'taxonomy-v1',
       'classifications':[],'experienceSignals':[],'normalizedTerms':[],
       'sourceRefs':[{'id':entity+'-evidence','documentTitle':'sources.md','location':'#'+entity+'-evidence','text':text}],'confirmationState':'fixture'}
    units.append({'search':search,'groupId':group,'split':'final','smoke':False,'sourceVersion':1,'currentActiveVersion':1,'scenario':role,'draftDomain':domain})
    sources.extend(['<a id="'+entity+'-evidence"></a>','## '+entity,'',text,''])
   questions.append({'id':f'{group}-{kind}-query','text':question,'entityType':kind,'difficulty':'independent-action','split':'final','smoke':False,
       'request':{'categories':[{'axis':'domain','categoryId':domain}],'signalIds':[],'terms':[],'probabilityThreshold':.2,'noulThreshold':.5,'limit':20},
       'draftDomain':domain,'scenarioGroupId':group,'positiveIds':[f'{group}-{kind}-direct',f'{group}-{kind}-support'],'hardNegativeId':f'{group}-{kind}-negative'})
 for n,(kind,text,domain) in enumerate([
  ('person','音声認識の専門用語の誤変換を辞書学習で減らした経験者を探したい。','software'),
  ('knowledge','真空容器の微小漏れをヘリウム検査で特定した実施済み事例を探したい。','mechanical'),
  ('person','古典文学の写本の系統を比較研究した担当者を探したい。','unknown'),
  ('knowledge','遠方銀河の赤方偏移を観測して距離を推定した研究事例を探したい。','unknown')],1):
  questions.append({'id':f'i30n-no-answer-{n:02}','text':text,'entityType':kind,'difficulty':'no-gold','split':'final','smoke':False,
   'request':{'categories':[{'axis':'domain','categoryId':domain}],'signalIds':[],'terms':[],'probabilityThreshold':.2,'noulThreshold':.5,'limit':20},
   'draftDomain':domain,'scenarioGroupId':f'i30n-no-answer-{n:02}','positiveIds':[]})
 for q in questions:
  q['judgments']=[{'entityId':u['search']['entityId'],'grade':3 if u['search']['entityId'] in q['positiveIds'] and u['scenario']=='direct' else 2 if u['search']['entityId'] in q['positiveIds'] else 0,
   'insufficient':False,'reason':'要求された具体的対応を実施した原本記録。' if u['search']['entityId'] in q['positiveIds'] else '要求された対応の実施記録がない。',
   'evidenceRefs':[u['search']['entityId']+'-evidence']} for u in units if u['search']['entityType']==q['entityType']]
 data={'version':'issue30-independent-v2','origin':'synthetic','humanReviewStatus':'pending','labelAuthor':'AI draft; human approval required before independent scoring',
       'baseVersion':'new scenarios; prior evaluation-v1/v2 scenarios excluded','units':units,'questions':questions}
 old=json.loads((ROOT/'evaluation/expansion-v2/dataset.json').read_text())
 previous=json.loads((ROOT/'evaluation/issue-30/independent/dataset.json').read_text())
 assert not {q['text'] for q in questions}&{q['text'] for q in previous['questions']}
 assert not {u['search']['summary'] for u in units}&{u['search']['summary'] for u in previous['units']}
 assert not {q['text'] for q in questions}&{q['text'] for q in old['questions']}
 assert not {u['search']['summary'] for u in units}&{u['search']['summary'] for u in old['units']}
 OUT.mkdir(parents=True);(OUT/'dataset.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n');(OUT/'sources.md').write_text('# 独立集合の架空原本\n\n'+'\n'.join(sources))
 review=['# Issue 30 独立集合の正解ラベル案','', '状態：人による確認待ち。AI分類・LLM応答は未取得。','',
 '新規10シナリオ、人物／事例各10問、正解なし4問。各種別30候補、計60原本。旧集合の課題・対応文は再利用していない。',
 '分類体系と一般的な主業務・副業務書式は共通。シナリオ内容の独立性であり実社員の無作為抽出ではない。',
 '各問の同シナリオdirectを3、supportを2、negativeと他シナリオを0とする案。正解なし4問は全候補0。',
 '副業務の共同実施は正解だが、案内配布のみは非正解。ラベルに不一致があればモデル実行前に訂正する。','',
 '[全原文](sources.md)・[全候補の判定](dataset.json)を確認してください。','',
 '| 質問ID | 質問 | 正解（候補ID末尾） |','|---|---|---|']
 review += [f"| `{q['id']}` | {q['text']} | "+('`direct`=3、`support`=2' if q['positiveIds'] else '正解なし')+' |' for q in questions]
 review += ['','正解IDは質問IDの`-query`を`-direct`／`-support`に置換した値。各原文の対応はsources.mdに明記。','',
 '## 固定する検証条件','', '人物15／事例15、救済2、Top-3、質問閾値0.1、文書確率0.2、Noul0.5。既存本番実装のプロンプト・根拠予算1,200バイトを使う。',
 '一次Recallは人物／事例各90%以上、最終HitRate@5とPrecision@5は90%以上、正解なし誤推薦0、根拠ID／原文整合100%。ウォームp95は15秒以下、coldは別報告。',
 '独立確認結果を見て閾値・モデル・プロンプト・ラベルを調整しない。未達なら原因を記録し、次の調整・別独立集合へ進む。']
 (OUT/'review.md').write_text('\n'.join(review)+'\n')
 (OUT/'pre-model-manifest.json').write_text(json.dumps({'status':'pending-human-review','modelCalls':0,'questions':24,'positiveQuestions':20,'noGoldQuestions':4,'units':60,
   'exactQuestionAndSourceOverlapWithV2':0,'sha256':{f:hashlib.sha256((OUT/f).read_bytes()).hexdigest() for f in ['dataset.json','sources.md','review.md']},
   'generatorSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()},ensure_ascii=False,indent=2)+'\n')
 print('Prepared independent draft: 24 questions, 60 sources, human review pending, model calls 0.')
if __name__=='__main__':generate()
