# JEV実分類を使う一次検索評価

2026-10-03。ユーザーのJEV $5クレジット利用承認に基づき、固定ダミーを実際に分類する。Foundry再順位付け、Azure配置、RBAC変更、Cosmos性能計測はこの試験の対象外。

## 送信前の固定と上限

`evaluation/jev-plan.issue14.json` にdataset/分類体系/正規化辞書のSHA-256、jev-1.13.0、プロフィール/質問分類プロンプト版、送信本文とhash、対象断片/人物/質問への対応を固定する。ラベル・分類fixture・正解IDをJEVへ送らず、原本根拠本文と型付き質問だけを送る。非公開/参加不可/撤回/旧版の4名は分割ごとに送信前除外。同じ本文でも調整/確認の間で応答を共有しない。

計画は330回（承認済み334断片、322人物/事例全体、16質問を、同一分割・同一本文・同一質問でまとめる）。型付きNoulは既存のHasDomainEvidenceに従って文書に根拠語のある問いだけを送る。質問側は必要分野・Noulを実分類する。実際の未評価Noulは0に変換しない。

最大350回、並列1、自動retry 0、総額$1以内。失敗時は停止し、明示的な手動診断の正常応答を `--recovery-response` で取り込める。追加試行も350回と費用上限に含め、元の失敗は最大64k入力で予約し続ける。公式の最大64k入力トークンを各送信の予約上限にし、330×64,000×$0.042/M = **$0.88704** を保守的に予約する。単価確認日2026-10-03、入力のみ課金・出力無料。[公式価格/モデル](https://docs.typesafe.ai/models)、[公式API](https://docs.typesafe.ai/api)。これは実請求額ではなく上限見積もりで、残クレジットはユーザー申告の$5。円換算と為替日は今回設定しない。

ゲート `evaluation/gates.issue14-jev.json` は同じ90%一次基準、構造化90%、ウォーム15秒、冷起動上限なし、承認予算8,000円を維持し、今回の送信計画hashを固定する。構造化/再順位付け/検索全体の待ち時間は今回のJEV分類結果だけでは合否を付けない。

## 実行・秘密・再送防止

```sh
# 生成は送信しない。既存計画の上書きは拒否。
dotnet run --project src/Dse.Evaluation -- --write-jev-plan evaluation/jev-plan.issue14.json
python3 scripts/jev_evaluation.py evaluation/jev-plan.issue14.json --out .artifacts/issue14-jev-live

# 計画とゲートをコミットしてから、承認済みクレジットで送信。
python3 scripts/jev_evaluation.py evaluation/jev-plan.issue14.json --out .artifacts/issue14-jev-live --execute --credit-budget-usd 1 --approval-reference user-20261003-issue14-jev-credit

# 同じ記録を用いて調整/確認と小/大を比較。追加送信なし。
dotnet run --project src/Dse.Evaluation -- --jev-plan evaluation/jev-plan.issue14.json --jev-capture .artifacts/issue14-jev-live/responses.json --large --out .artifacts/issue14-jev-tuning-large
dotnet run --project src/Dse.Evaluation -- --jev-plan evaluation/jev-plan.issue14.json --jev-capture .artifacts/issue14-jev-live/responses.json --large --split final --gates evaluation/gates.issue14-jev.json --out .artifacts/issue14-jev-final-large
```

実行キーはgit無視の`.env`の`TYPESAFE_API_KEY`または`TYPE_SAFE_API_KEY`。キーをCLI引数・ログ・Gitへ出さない。エンドポイントとモデルを固定し、HTTP redirect、SDK retry、別モデルfallbackを使わない。既存Key Vaultのユーザー権限不足をRBAC追加や資格情報取得の迂回で解消しない。

送信前のpendingを`responses.jsonl`へfsyncし、提供元usageと分類・Confidence・Noulを各応答で記録する。完了済みは再送しない。クラッシュ/失敗/不明な送信は予約を保持し、次回も自動再送を拒否する。途中応答を成功した全集合の結果として採点しない。送信/応答本文hash、model、キー、確率合計、最大Choice、Confidence、usageを検証する。

## 比較の読み方

L（語一致のみ）とJEVを使うA〜Eを、同じ本文・同じ語抽出・同じ候補上限で比較する。LにはProbability/Noulを一切使わない。語は原本本文と質問から既存ProfileExtraction.Termsで取得し、以前のfixture手書き語と区別する。A〜Eでは合成分類を全て実JEV応答へ差し替える。Noul検索条件も質問の実応答から閾値で選ぶ。

一次Recall/HitRate、Noul増分、正解なし質問の取得候補数、上限損失を人物/事例・分割・Nで別記する。最終表示順位をまだ作らないため、最終Precision/nDCG/HitRate@Kは今回も未測定。JEVの分類応答時間は取得時に記録するが、検索時には保存済み分布を使うため、検索説明全体の15秒SLOとは別。

今回の正解あり独立質問は分割ごとに2問で、20問の最終品質評価は引き続き未達。過去の合成分類の40%/100%を語一致のみのベースライン値として流用しない。実行結果は別runへ保存し、本番採用設定は別途判断する。

## 今回の実測

[実JEV比較結果・記録・Todo照合](validation-results/20261003-issue-14-jev/results.md)。330件を完了、手動追加4回、合計334試行。確認集合の人物RecallはL=80%、D/E=100%だが、調整集合ではL/E=100%、D=80%。確認集合の改善を一般化せず、本番採用・LLM再順位付けは保留する。

## 質問数の拡張案（未承認）

[拡張案v2](../evaluation/expansion-v2/README.md)に、調整/確認それぞれ正解あり20問＋正解なし4問、計48問と原本120資料を追加した。ラベルは未承認で追加有料送信0回。既存の実測と区別し、人手レビュー後に新計画・ゲートを固定する。
