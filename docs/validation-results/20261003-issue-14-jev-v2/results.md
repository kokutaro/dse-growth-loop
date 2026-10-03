# Issue #14 追加20問による実JEV一次検索評価

2026-10-03。ユーザーが[追加質問・正解ラベル](../../../evaluation/expansion-v2/review.md)をレビュー済みとして承認。調整20問と確認20問を用意し、各分割に正解なし4問を追加した。**確認の実JEV検索Eは人物Recall95%、事例Recall90%、正解あり20問中19問で一次候補に正解を含んだ。** これはHitRate@N=20で、未実施のLLM最終HitRate@5とは別。

## 分割・固定順序

[dataset evaluation-v2](../../../evaluation/expansion-v2/dataset.json)は全件架空、120資料／48質問。正解ありは各分割人物10問・事例10問、5分野×4課題。各質問の同種別30候補全件を関連度判定した。20問は同じ質問の表現違いや小/大の複製ではなく、異なる作業課題として用意した。各課題には直接候補3・補助候補2と、質問語を含むが実践のない案内候補0を置く。旧v1は回帰・前回実測として維持し、今回の分母に加算しない。

合成集合の実務代表性や、同分野・同じ文型の質問間の統計的独立性は未検証。全課題に語が一致する案内候補を置いた構成なので、普遍的な実務性能として外挿しない。現在のLは既存 `ProfileExtraction.Terms` の文区切り・限定語を用いる完全一致検索であり、形態素解析/BM25等を用いる検索との比較ではない。

1. `e32c9b4` で承認データ、[168件の計画](../../../evaluation/expansion-v2/jev-plan.json)、[ゲート](../../../evaluation/expansion-v2/gates.json)、[選定手順](../../../evaluation/expansion-v2/selection-policy.json)を送信前固定した。モデルjev-1.13.0、文書profile-classify-v1／質問search-query-v1、taxonomy-v1／normalization-v1は前回と同じ。
2. 実分類後、調整24問×544条件＝13,056行を比較。Eで両種別の平均Recall90%以上を満たす84条件のうち、事前手順のN・候補数優先で条件を選んだ。確認の取得/応答は送信計画に含むが、確認の採点結果は選定に参照していない。
3. `f61e786` で[選定結果](../../../evaluation/expansion-v2/selection.json)、[7比較条件](../../../evaluation/expansion-v2/selected-settings.json)、そのhashを含む[最終ゲート](../../../evaluation/expansion-v2/gates.selected.json)を固定してから確認を採点。確認での全条件探索・再調整は行っていない。

選定値は文書閾値0.3／質問Top-1／Noul0.3／N=20。LとA〜Eは同じ値、Fは評価器限定・最大200で権限内全件30候補を渡す基準条件。取得はLegacy Retrieverの全体上限・20%救済枠で、本番retrieval-v2や実Cosmosを実行した結果ではない。CLIに契約パス指定 `--specs` と比較条件指定 `--settings` を追加し、確認時の変更/省略をhashで拒否する。

## 結果

| 条件 | 調整：人物Recall | 調整：事例Recall | 確認：人物Recall | 確認：事例Recall | 確認：正解を含む質問／20 |
|---|---:|---:|---:|---:|---:|
| L：現行語一致のみ | 0% | 0% | 0% | 0% | 0/20 |
| A：全体Top-1 | 60% | 100% | 75% | 90% | 19/20 |
| B：全体複数Probability | 75% | 100% | 90% | 90% | 19/20 |
| C：断片Probability | 75% | 100% | 90% | 90% | 19/20 |
| D：C＋Noul | 90% | 100% | 95% | 90% | 19/20 |
| E：D＋語一致 | 90% | 100% | 95% | 90% | 19/20 |
| F：権限内全件 | 100% | 100% | 100% | 100% | 20/20 |

人物/事例のRecall分母は各10問、各質問の正解は2候補。Eは全40正解候補中37を取得（両種別の平均92.5%）。B〜Eは各種別の平均Recall90%ゲートに合格、Aは人物不合格、Lは両種別不合格。ただし、個々の質問でRecall90%以上という意味ではない。[確認の合否report](final-report.json)、[質問別CSV](final-questions.csv)、[全候補・根拠JSON](final-primary.json)へ保存した。

Eの平均候補数は人物7.83／事例6.42（正解なしを含む各12問）。N=20による正解打切り損失は0。NoulはCに対し正解1名を追加した一方、人物9・事例4の非該当候補も追加した。Eの語一致はRecallを増やしておらず、現行の語抽出が言い換えを拾えない限界が出た。旧v1では語一致人物80%／JEV＋Noul100%だったため、今回の0%を既存の全検索性能へ一般化しない。

確認の正解なし4問で、Eの候補数は8／0／6／6。候補がある3問は最終推薦を行う前に除外判断が必要で、今回の一次検索だけを「正解なしを判定できた」と扱わない。最終表示・構造化・有効根拠ID・Precision@K・nDCG@K・HitRate@5は未測定。

## 取りこぼし

[原本・分布付き失敗例](missed-examples.json)を保存。原因は打切りではなく上限前の取得漏れだった。

- `v2f-software-04-query`：同じ要求でも二重請求しない事例。質問はoperations=0.52、software=0.42で、選定した質問Top-1ではoperationsだけを使った。software Noulも0.19で0.3未満となり、ソフト実装の正解2件を取得できなかった。文書のsoftware=1.0であり、文書分類の欠落とは区別する。Top-3等は次の改善候補だが、確認結果を見て設定を変更していない。
- `v2f-sales-09-query`：提案範囲を合意した経験。副業務の候補はmechanical=0.93／sales=0.07で文書閾値0.3未満。文書sales Noul=0.75でも、質問sales Noul=0.25で検索条件に入らず、補助候補1名を落とした。

Eを次のLLM再順位付けの実験候補とし、本番採用設定は変更しない。質問分布のTop選択、質問Noulの必要性判定、語抽出の改善を別の変更と新しい確認集合で再検証する。

## API試行・費用

固有168件を完了。1件の初回検証が確率合計0.99で失敗し、その1回目手動診断も検証失敗。2回目手動診断で正常応答を取得し、総170試行（追加2回）となった。検証の閾値・モデル・本文は緩和していない。失敗した診断の証跡と64k予約を必須にし、試行/費用を過小申告できないよう回復取込を拡張した。正常応答の自動再送は0。

| 記録 | 値 |
|---|---:|
| 成功入力／出力トークン | 96,720／17,669 |
| 成功usageの推計 | $0.00406224 |
| 失敗2回の128k入力予約を含む保守的費用 | $0.00943824 |
| 全170回の最大入力予約 | $0.45696（本run上限$1以内） |
| 分類HTTP p50／p95 | 190.53ms／260.88ms |
| 時間標本 | 169、失敗した1回目手動診断の時間1件は未保存 |

入力$0.042/M・出力無料、確認日2026-10-03、[公式モデル・単価](https://docs.typesafe.ai/models)。実請求・残クレジットは未照合。先行330件と合わせた試行数は504、成功usage推計$0.011902128、全失敗予約を含めた保守値$0.028030128。ユーザー申告$5クレジットを使い切る規模ではない。円換算・月額・検索全体のcold/warmは未測定で、ウォームp95 15秒ゲートへ分類HTTP時間を流用しない。

[全応答](responses.json)、[送信journal](responses.jsonl)、手動診断 `diagnostic-1.json`／`diagnostic-2.json`、[承認予約](approval.json)、[manifest](manifest.json)を保存。失敗した最初の診断の時間とusageは不明として残し、費用を0にしない。

## 再現・検証

キーも追加課金も不要。新しい空の出力先を指定する。

```sh
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet run --project src/Dse.Evaluation --no-build -c Release -- --data evaluation/expansion-v2 --jev-plan evaluation/expansion-v2/jev-plan.json --jev-capture docs/validation-results/20261003-issue-14-jev-v2/responses.json --large --out .artifacts/v2-replay-tuning
dotnet run --project src/Dse.Evaluation --no-build -c Release -- --data evaluation/expansion-v2 --jev-plan evaluation/expansion-v2/jev-plan.json --jev-capture docs/validation-results/20261003-issue-14-jev-v2/responses.json --settings evaluation/expansion-v2/selected-settings.json --large --split final --gates evaluation/expansion-v2/gates.selected.json --out .artifacts/v2-replay-final
```

調整全条件は[tuning-summary.csv](tuning-summary.csv)と[tuning-questions.csv](tuning-questions.csv)、最終7条件は[final-summary.csv](final-summary.csv)へ保存。巨大な調整全候補JSONはhashをmanifestに残し、上記で再生成する。最終JSONは全168行を保存し、同じ記録からの再実行hash一致も確認する。

.NET309件（行カバレッジ90.18%）、Python/Bicep47件、契約17件、評価fixture5件成功。新CLIの明示specパス、限定条件、最終条件変更/省略拒否、失敗診断の費用予約を回帰確認した。全ビルド・JSON・相対リンク・秘密除外・差分空白を確認。既存ASPIRE010警告1件。

## Issue Todo照合

前回の12項目中A〜F比較と安価な一次探索の2項目を完了のまま維持。追加集合のラベル承認・版固定・正解あり20問・JEV一次指標/費用/合否は今回完了した。残る複合Todoを丸ごとチェックしない。

実LLMモデル/プロンプト比較、最終20問のHitRate@5等、構造化90%／根拠ID100%、実Cosmos/RU/索引/保存量、検索cold/warm、実請求/月額と本番採用は未完了。Issue #14はopen、PRから自動closeしない。
