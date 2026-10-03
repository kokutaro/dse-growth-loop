# Issue #14 実JEV分類と語一致検索の比較

2026-10-03。ユーザーがJEV $5クレジットの使用を承認し、最大$1・350試行を予約した。**実APIによる分類を追加し、確認集合では人物Recallが80%から100%へ改善した。ただし、少数の固定ダミー上の一次取得だけの結果で、本番品質・再順位付け品質の達成ではない。**

## 固定・実行範囲

送信前コミット `dc2b0fd` で、[送信計画](../../../evaluation/jev-plan.issue14.json)、[確認ゲート](../../../evaluation/gates.issue14-jev.json)、ラベル/本文/分割/hash、jev-1.13.0、文書 `profile-classify-v1`／質問 `search-query-v1`、taxonomy-v1／normalization-v1、全探索条件を固定した。入力は原本の架空本文と型付き質問で、正解ID・人手ラベル・合成分類を送っていない。非公開・参加不可・撤回・旧版の固定4名は分割ごとに除外した。調整と確認で応答を共有しない。

330件は334断片・322人物/事例全体・16質問の対応を同一本文/型付き質問で重複排除した数。A〜Fの全541条件と語一致のみLのN=20/50/100を、同じ原本語抽出で比較した。分類の全Probability・別Confidence・Noulは実応答へ差し替え、Lには分類値を使用しない。合成fixture段階の40%を語一致ベースラインとして流用していない。

大調整・小確認・大確認を各544条件×4質問＝2,176行評価。確認結果を見て閾値を変更せず、全条件CSVと事前の代表条件を報告する。検索は評価器のLegacy Retrieverの全体N・20%救済枠で、本番retrieval-v2の人物/事例別枠・固定RescueSlotsの実行ではない。Fは最大200候補の評価器限定で公開APIを無制限化しない。

## 実測結果

代表条件は文書閾値0.2／質問Top-3／Noul0.5／N=20。Recallの分母は各分割で人物・事例とも正解あり1質問（人物の正解5名、事例の正解1件）。小/大は同じ質問の規模違いで独立標本として合算しない。

| 一次検索条件 | 大調整：人物Recall | 小確認：人物Recall | 大確認：人物Recall | 大確認：事例Recall |
|---|---:|---:|---:|---:|
| L：語一致のみ | 100% | 80% | 80% | 100% |
| A：全体Top-1 | 80% | 80% | 80% | 100% |
| B：全体の複数Probability | 80% | 80% | 80% | 100% |
| C：断片Probability | 80% | 80% | 80% | 100% |
| D：C＋Noul | 80% | 100% | 100% | 100% |
| E：D＋語一致 | 100% | 100% | 100% | 100% |
| F：権限内全件、最大200 | 100% | 100% | 100% | 100% |

一次90%ゲートは確認の人物L/A/B/Cが不合格、D/Eが合格。事例は全条件合格。HitRate@Nは正解あり各1質問で全て100%だが、上位5件をLLMで順位付けしたHitRate@5ではない。[確認の集計・合否](final-large-report.json)、[全条件CSV](final-large-summary.csv)、[質問別CSV](final-large-questions.csv)を参照。

確認のLは正解 `f-person-006` を、Cは副業務の正解 `f-person-002` を取得できない。後者は「主業務は機械設計。副業務として倉庫制御サービスのリリース自動化と運用引き継ぎを担当した。」という原本で、JEVのsoftware Probabilityは0.01、handover Noulは0.88だった。D/Eでは引き継ぎNoulから救済できた。[原本付き比較例](comparison-examples.json)。N=20のD/Eの上限による正解損失は0だが、Noul追加枠には正解1名と非該当3名が入った。

正解なし質問ではLの候補0に対し、D/Eは人物20名・事例3件を返す。最終表示は未実施なので、これを推薦誤り件数とは扱わない。調整のDは80%のままで、語を併用するEは100%。**Noulの改善例はあるが、分類検索だけが語一致より常に優れるとは言えない。** 語一致を併用するEを次の再順位付け比較候補に残し、本番採用設定は変更しない。Eの全探索条件には80%の行もあり、全閾値合格ではない。

## 試行・費用・待ち時間

| 記録 | 値 |
|---|---:|
| 完了した固有リクエスト | 330 |
| 初回応答の検証失敗／手動追加試行 | 4／4 |
| 総外部試行 | 334 |
| 成功応答の入力／出力トークン | 186,664／33,140 |
| 成功usageによる費用推計 | $0.007839888 |
| 失敗4件を各64k入力で予約した保守的費用 | $0.018591888 |
| 総334試行の最大入力予約額 | $0.897792（上限$1以内） |
| 分類HTTP p50／p95 | 192.61ms／238.03ms（334標本） |

入力$0.042/M・出力無料、確認日2026-10-03。[公式モデル・単価](https://docs.typesafe.ai/models)。失敗のusageは未取得のため費用0とせず、256,000入力トークン相当を残した。実請求・残クレジットとは未照合。為替換算や月額推計は今回は行わず、承認予算8,000円を維持する。分類HTTP時間は、保存した分布を使う検索のcold/warm時間や15秒SLOとは別で合否対象外。

確率合計の検証で4件停止し、正常応答を各1回の手動診断で取得した。後半2件の記録では合計0.99、最大Choice0.55で、微小な丸め差ではなかった。初回失敗率4/330＝1.21%はこの試験の値で、先頭の失敗は詳細原因を残していないため全4件を同一原因と断定しない。確率の補正や検証閾値変更は行っていない。

停止後に回復取込と追加試行/不確定費用の記録を実装したが、送信本文・モデル・分類プロンプト・正解・ゲート・検索条件は変更しなかった。正常応答の自動再送は0。初回失敗と復旧履歴は[追記journal](responses.jsonl)、復旧済み全集合は[再現用応答](responses.json)、手動診断は `diagnostic*.json`、[予約/承認記録](approval.json)へ保存した。各診断も334試行に含む。元の最大予約$0.88704は初期330件、追加4回後の上限は$0.897792。

## 再現と検証

追加送信なしで再現できる。出力先は空の新しいパスを指定する。キー不要。

```sh
dotnet restore --locked-mode
dotnet build --no-restore -c Release
dotnet run --project src/Dse.Evaluation --no-build -c Release -- --jev-plan evaluation/jev-plan.issue14.json --jev-capture docs/validation-results/20261003-issue-14-jev/responses.json --large --out .artifacts/jev-replay-tuning
dotnet run --project src/Dse.Evaluation --no-build -c Release -- --jev-plan evaluation/jev-plan.issue14.json --jev-capture docs/validation-results/20261003-issue-14-jev/responses.json --split final --gates evaluation/gates.issue14-jev.json --out .artifacts/jev-replay-small
dotnet run --project src/Dse.Evaluation --no-build -c Release -- --jev-plan evaluation/jev-plan.issue14.json --jev-capture docs/validation-results/20261003-issue-14-jev/responses.json --large --split final --gates evaluation/gates.issue14-jev.json --out .artifacts/jev-replay-large
```

[manifest](manifest.json)に元の全候補JSONを含むhash、行数、分類HTTP集計、失敗記録を保存。巨大な全候補JSONは上記で再生成し、Gitには全条件CSV・失敗例・代表条件reportを残す。回復処理の使用量検証追加後にも確認大集合を再実行し、全候補JSONのhash一致を確認する。

locked-mode restore／Release全ビルド、.NET全308件（行カバレッジ89.98%、JevEvaluation93.82%）、Python/Bicep46件、契約17件、評価fixture5件が成功。MSBuildのIPC・Azure CLI設定書込はsandbox制限で初回失敗し、許可されたローカル実行で成功。契約試験は既存venvで依存を満たした。既存ASPIRE010警告1件。UI変更はなく、ローカルPlaywrightは未実行。

## Issue Todoの最終照合

| 元Todo | 状態 | 確認した範囲・残作業 |
|---|---|---|
| 正解/分割/版と事前合否の固定 | 一部完了 | 今回のJEV条件とゲートは送信前固定。後続LLMモデル/プロンプトは残る |
| A〜F比較、Fの評価器限定/上限 | 完了 | 実JEV分布と原本語でA〜Fを実行。F最大200、試行最大350/$1 |
| 一次探索を先に行い、有料全組合せを避ける | 完了 | 一次544条件、分類応答の再利用で追加送信0。K=3/5の基盤は実装済み |
| 同じ候補でLLM/根拠量/確率/順序/再試行比較 | 未完了 | 保存応答比較器のみ、実LLM未実施 |
| 一次/最終指標、Noul/打切り/正解なし集計 | 一部完了 | 実分類による一次指標は保存。Precision@K/nDCG@K/最終HitRate@Kは未測定 |
| Cosmos RU/索引/量・AI token・cold/warm・費用 | 一部完了 | JEV試行/usage/費用/HTTP時間は記録。実Cosmos・検索全体時間は未測定 |
| 根拠/構造化/認可と原因別失敗 | 一部完了 | 入力除外と候補混入0、capture/一次失敗例。実LLM構造化/引用照合は未測定 |
| 採用/不採用・再現・実測・単価/月額 | 一部完了 | 本記録で不採用理由と実分類証跡を保存。為替・月額・本番採用は残る |
| 複数職歴救済/不正AI入力・表示0/架空ID0 | 一部完了 | 分類入力の固定除外・全構成一次混入0。実LLM入力/表示と架空ID照合は未実施 |
| 構造化90%/根拠100%/20問中18問と一次合否 | 未完了 | 今回の一次合否は報告。独立した正解あり20問と最終品質は残る |
| ウォーム15秒（承認済み）と冷起動別報告 | 未完了 | 今回は分類HTTPのみで検索SLOを評価しない |
| 承認予算と実利用量による月額比較 | 一部完了 | 送信前見積もり・実JEV usage・失敗予約を保存。月額利用量は未確定 |

A〜F比較を追加チェックし、探索項目と合わせて2/12項目完了。残りは未完了のまま。Issue #14はopenを維持し、PRは自動closeしない。
