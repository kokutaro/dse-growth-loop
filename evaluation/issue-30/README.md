# Issue 30 保存応答比較

[比較計画](replay-plan.json)に従い、既存の承認済み架空データと実Jev応答を再生する。AI送信・Azure変更・本番適用は行わない。

```sh
dotnet run --project src/Dse.Evaluation -- --issue30-replay .artifacts/issue30-replay-new
```

出力先は新しい空の場所を指定する。既に観察済みの確認集合を使うため、出力は独立品質評価ではなく回帰結果として扱う。

[結果・制限・再現手順](../../docs/validation-results/20261003-issue-30-fragment-replay/results.md)を参照。副業務の断片追加は、同じ分割の保存リクエスト全体に完全一致する原本引用だけで実施する。一致応答がない場合は停止し、人工的な分布を作らない。

## 実分類後の事例上限比較

[事例上限計画](case-limit-plan.json)で人物10・救済2を固定し、事例10・15・20件を比較する。新しい推論は行わず、前回の生Jev応答と採点結果を再検証して候補を再生成する。

```sh
dotnet run --project src/Dse.Evaluation -- --issue30-case-limits .artifacts/issue30-case-limits-new
```

[結果と測定の範囲](../../docs/validation-results/20261003-issue-30-case-limits/results.md)を参照。本番設定の変更は含まない。

## 変更候補3問の実LLM評価

事例上限15件で候補hashが変わった3問の後段LLMを評価した。[固定計画](llm/README.md)と[実測結果](../../docs/validation-results/20261003-issue-30-llm/results.md)を参照。再採点は追加送信なし。

```sh
python3 scripts/issue30_llm_evaluation.py score --output .artifacts/issue30-llm-new
```

## 固定48問全体の実LLM回帰

人物10／事例15で全48問・3条件を比較した。[事前計画と再利用](full-llm/README.md)、[全体結果](../../docs/validation-results/20261003-issue-30-full-llm/results.md)を参照。

```sh
python3 scripts/issue30_full_llm_evaluation.py score --output .artifacts/issue30-full-llm-new
```

## 人物上限10／15の比較と表示再採点

人物10／15・事例15・救済2で旧48問の保存分類応答を比較する。新規AI送信なし、本番設定変更なし。人物15の変更候補8問×3条件の実Foundry回帰は全件成功（独立評価とは別）。[実回帰結果](../../docs/validation-results/20261004-issue-30-person-rerank-live/results.md)。[結果と取得漏れの切り分け](../../docs/validation-results/20261004-issue-30-person-limits/results.md)。

```sh
dotnet run --project src/Dse.Evaluation -- --issue30-person-limits .artifacts/issue30-person-limits-new
python3 scripts/issue30_grouped_report.py --output .artifacts/issue30-grouped-new
```

後者は既観測24問×2条件の返却順位を、各表示欄の先頭5件と従来の混在順位で再採点する。新UIの表示を仮定したオフライン比較であり、本番UIやモデルの再実行ではない。

新しい独立集合は[元のレビュー案](independent-v2/review.md)、[新規承認記録](independent-v2/execution-approval.json)、[実行計画](independent-v2/execution-plan.md)を参照。2026-10-04にラベル・AI310円・追加インフラ100円・API一時展開と復帰をユーザーが承認した。承認済みコピーはラベル変更なし。既存のラベル承認は流用していない。

2026-10-04の[新規独立集合の本番検証結果](../../docs/validation-results/20261004-issue-30-independent-v2-production/results.md)を追加。サービス48検索とJWT画面48検索を実施し、分類・一次取得の漏れにより品質ゲート不合格。再検索24問は共有日次上限を維持するため日次リセット後に残し、恒久採用・Issue #30完了は保留する。

2026-10-04の追加承認で、AI・検証インフラ合計2,000円/週（日本時間の月曜0時区切り）、固定162円/USDへ変更した。日次token・入力・同時実行・試行・累計稼働時間の制限を維持し、過去473予約を週次台帳へ引き継いだ。warm24問は解除後に完了した。成功22問のp95 18.622秒で基準15秒は未達。終了時は元設定へ復帰・停止済み。[週次制御の記録](../../docs/validation-results/20261004-issue-30-independent-v2-production/weekly-budget.md)。

2026-10-04の[観測後修正・実AI回帰](../../docs/validation-results/20261004-issue-30-regression/results.md)で、入札の正解取得漏れを解消し、全対象24問の人物・事例Recall100%／スキーマ不一致0を確認した。独立確認への転用はしない。HTTPの15秒基準と新しい独立集合の確認が残る。この時点では新規v3ラベルと累計稼働延長案は承認待ちだった。後続の新規承認と実測を以下に記録する。

2026-10-04の新規承認後、[独立v3の実サービス48検索](../../docs/validation-results/20261004-issue-30-independent-v3-production/results.md)を完了。全対象・対象指定とも人物／事例の一次Recall、種別欄HitRate／Precision／Recallは100%、誤推薦0/4、根拠整合120/120。v3結果を見た調整なし。Mac再ロックにより認証付きHTTP・画面・coldは未実施で、APIを再展開せず元設定・実レプリカ0・Job0を確認した。Issue #30と恒久採用は保留。
