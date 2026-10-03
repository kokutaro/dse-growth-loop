#!/usr/bin/env python3
"""New, model-unseen dummy cohort. Labels remain pending until human review."""
import hashlib
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'evaluation/issue-30/independent'
SCENARIOS=[
 ('software','cache','人気商品へのアクセスが集中してキャッシュ更新時にDB負荷が跳ね上がる問題を抑える',
  'ソフトウェア開発で、キャッシュ期限切れの同時再計算を単一処理にまとめた。更新中は古い値を返し、負荷試験でDBへの集中を抑えた。'),
 ('software','timezone','海外拠点の予約時刻が夏時間の切替日にずれる問題を防ぐ',
  'ソフトウェア開発で、予約日時を地域のタイムゾーン識別子とともに保持した。夏時間で存在しない時刻と二度現れる時刻を区別し、切替日のテストを追加した。'),
 ('mechanical','thermal','温度変化で軸と穴のはめ合いがきつくなる問題を設計で防ぐ',
  '機械設計で、軸とハウジングの材料ごとの熱膨張量を計算した。最高使用温度でも必要すきまが残る寸法公差に変更し、温度試験で干渉がないことを確認した。'),
 ('mechanical','fatigue','繰り返し荷重でブラケットの角から亀裂が発生する問題を改善する',
  '機械設計で、ブラケットの角の応力集中を解析した。曲率半径と補強リブを見直し、繰り返し荷重の耐久試験で亀裂発生を抑えた。'),
 ('sales','discount','担当者ごとの無秩序な値引きで粗利が下がる商談運用を改める',
  '営業で、粗利下限と値引き額に応じた承認権限を定めた。見積時に例外を上長へ回す運用を導入し、案件別の粗利を週次で確認した。'),
 ('sales','channel','代理店同士が同じ見込み顧客に重複提案して揉める問題を整理する',
  '営業で、代理店の商談登録に受付日時と有効期限を設けた。顧客への提案責任者を決め、重複案件の調整手順を代理店契約に反映した。'),
 ('manufacturing','calibration','検査ゲージのずれによって不良品を合格にしてしまう問題を減らす',
  '製造現場で、検査ゲージを始業時に基準器と照合した。異常時は直前の正常確認以降の製品を隔離し、再検査と校正の履歴を残した。'),
 ('manufacturing','traceability','不良が発覚した際に対象ロットを特定できない問題を改善する',
  '製造現場で、原材料ロットと加工設備、時刻、製品番号を紐付けて記録した。模擬回収を実施し、影響する製品だけを追跡できることを確認した。'),
 ('operations','permit','交代勤務の切替時に保守作業の許可と危険区域の情報が抜ける問題を防ぐ',
  '業務運用で、保守作業の許可番号、立入禁止区域、未解除の電源遮断を交代時の確認表にまとめた。双方が確認してから担当を交代し、未完了許可を追跡した。'),
 ('operations','spares','交換部品の欠品で設備復旧が遅れる問題を改善する',
  '業務運用で、故障頻度と調達日数から交換部品の発注点を定めた。使用時に在庫を減算し、発注点を下回った部品を補充して保管場所も統一した。')]
NAMES={'software':'ソフトウェア','mechanical':'機械設計','sales':'営業','manufacturing':'製造','operations':'業務運用'}
MAIN={'software':'機械設計','mechanical':'営業','sales':'ソフトウェア開発','manufacturing':'営業','operations':'機械設計'}

def generate():
 if OUT.exists():raise ValueError('Dataset already exists; preserve review history')
 units=[];questions=[];sources=[]
 for domain,topic,problem,action in SCENARIOS:
  group='i30i-'+domain+'-'+topic
  for kind in ['person','knowledge']:
   question=problem+('経験のある担当者を探したい。' if kind=='person' else '実施済みの事例を探したい。')
   for role in ['direct','support','negative']:
    entity=f'{group}-{kind}-{role}'
    text=action if role=='direct' else ('主業務は'+MAIN[domain]+'。副業務として次の対応を共同実施した。'+action if kind=='person' else '次の改善の実施に共同で参加した。'+action)
    if role=='negative':text=NAMES[domain]+'の資料に「'+question+'」という相談を記録した。'+('本人は案内を配布しただけで、設計・実装・改善を実施していない。' if kind=='person' else '相談を載せた案内資料であり、対策の実施・検証結果の記録はない。')
    search={'id':entity+'-projection','entityId':entity,'entityType':kind,'tenantId':'demo-tenant','unitId':entity+'-unit','title':'架空'+entity,
       'summary':text,'scopeIds':['issue30-independent'],'status':'active','available':True,'taxonomyVersion':'taxonomy-v1',
       'classifications':[],'experienceSignals':[],'normalizedTerms':[],
       'sourceRefs':[{'id':entity+'-evidence','documentTitle':'sources.md','location':'#'+entity+'-evidence','text':text}],'confirmationState':'fixture'}
    units.append({'search':search,'groupId':group,'split':'final','smoke':False,'sourceVersion':1,'currentActiveVersion':1,'scenario':role,'draftDomain':domain})
    sources.extend(['<a id="'+entity+'-evidence"></a>','## '+entity,'',text,''])
   questions.append({'id':f'{group}-{kind}-query','text':question,'entityType':kind,'difficulty':'independent-action','split':'final','smoke':False,
       'request':{'categories':[{'axis':'domain','categoryId':domain}],'signalIds':[],'terms':[],'probabilityThreshold':.2,'noulThreshold':.5,'limit':20},
       'draftDomain':domain,'scenarioGroupId':group,'positiveIds':[f'{group}-{kind}-direct',f'{group}-{kind}-support'],'hardNegativeId':f'{group}-{kind}-negative'})
 for n,(kind,text,domain) in enumerate([
  ('person','コード署名鍵の漏えい時に署名を失効し再発行した経験者を探したい。','software'),
  ('knowledge','光学センサーの迷光を遮光構造で減らした実施済み事例を探したい。','mechanical'),
  ('person','深海生物の分類学の研究で新種を記載した研究者を探したい。','unknown'),
  ('knowledge','古代碑文を解読して年代を確定した学術調査の事例を探したい。','unknown')],1):
  questions.append({'id':f'i30i-no-answer-{n:02}','text':text,'entityType':kind,'difficulty':'no-gold','split':'final','smoke':False,
   'request':{'categories':[{'axis':'domain','categoryId':domain}],'signalIds':[],'terms':[],'probabilityThreshold':.2,'noulThreshold':.5,'limit':20},
   'draftDomain':domain,'scenarioGroupId':f'i30i-no-answer-{n:02}','positiveIds':[]})
 for q in questions:
  q['judgments']=[{'entityId':u['search']['entityId'],'grade':3 if u['search']['entityId'] in q['positiveIds'] and u['scenario']=='direct' else 2 if u['search']['entityId'] in q['positiveIds'] else 0,
   'insufficient':False,'reason':'要求された具体的対応を実施した原本記録。' if u['search']['entityId'] in q['positiveIds'] else '要求された対応の実施記録がない。',
   'evidenceRefs':[u['search']['entityId']+'-evidence']} for u in units if u['search']['entityType']==q['entityType']]
 data={'version':'issue30-independent-v1','origin':'synthetic','humanReviewStatus':'pending','labelAuthor':'AI draft; human approval required before independent scoring',
       'baseVersion':'new scenarios; prior evaluation-v1/v2 scenarios excluded','units':units,'questions':questions}
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
 '## 固定する検証条件','', '人物10／事例15、救済2、Top-3、質問閾値0.1、文書確率0.2、Noul0.5。既存本番実装のプロンプト・根拠予算1,200バイトを使う。',
 '一次Recallは人物／事例各90%以上、最終HitRate@5とPrecision@5は90%以上、正解なし誤推薦0、根拠ID／原文整合100%。ウォームp95は15秒以下、coldは別報告。',
 '独立確認結果を見て閾値・モデル・プロンプト・ラベルを調整しない。未達なら原因を記録し、次の調整・別独立集合へ進む。']
 (OUT/'review.md').write_text('\n'.join(review)+'\n')
 old=json.load(open(ROOT/'evaluation/expansion-v2/dataset.json'))
 assert not {q['text'] for q in questions}&{q['text'] for q in old['questions']}
 assert not {u['search']['summary'] for u in units}&{u['search']['summary'] for u in old['units']}
 (OUT/'pre-model-manifest.json').write_text(json.dumps({'status':'pending-human-review','modelCalls':0,'questions':24,'positiveQuestions':20,'noGoldQuestions':4,'units':60,
   'exactQuestionAndSourceOverlapWithV2':0,'sha256':{f:hashlib.sha256((OUT/f).read_bytes()).hexdigest() for f in ['dataset.json','sources.md','review.md']},
   'generatorSha256':hashlib.sha256(Path(__file__).read_bytes()).hexdigest()},ensure_ascii=False,indent=2)+'\n')
 print('Prepared independent draft: 24 questions, 60 sources, human review pending, model calls 0.')
if __name__=='__main__':generate()
