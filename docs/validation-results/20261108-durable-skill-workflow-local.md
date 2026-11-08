# 通常処理を使う永続デモのローカル検証

## 概要

main `0da09703bf2910a6f28ec8abcfd2ab610f8711ad` を基点に、架空データだけで通常取込・承認済み技能ID投影・自然文検索・訂正・撤回を接続した。実装は [永続デモ](../durable-demo-workflow.md)、調査時の範囲は [方針記録](20261108-durable-skill-workflow-plan.md) を参照。ここに記載する成功はローカル試験で、実モデルの品質や共有Azure経路の実機成功とは区別する。

## 成功した確認

| 対象 | 今回の結果 |
|---|---|
| Mac/.NET 10、全体・Debug | 1,270成功、失敗0、スキップ0。coverage付き |
| JSON Schema/OpenAPI・実レコード契約 | 43成功 |
| 認証・context・ブラウザ保存キーのunit | 10成功 |
| TypeScript/Vite | 成功 |
| 辞書・旧メモリデモ・永続通常デモのブラウザ | 12成功 |
| 通常人物画面・公式の各ケース隔離runner | 10成功 |
| 自然文AI選択ID/版の条件確認後の保持 | 1成功。API応答fixtureで、外部AIなし |

通常画面では、以前の架空原文のGit候補2件を同じIDへ承認し、通常 `/api/search` で2資料に一致、画面から1資料を撤回して1件へ減ることを確認した。同じ原本を新規取込し、通常Worker・ProfilePipeline・資料断片の収集を通して検索へ追加。訂正で旧版が検索から消え、再収集後は第2版のEvidenceRefだけで戻ることも確認した。根拠のある知識は人物経験の候補へ昇格しない。共有辞書は版0のまま。

永続ストア再開、catalog/conversation/Blob/Queue通知の領域分離、実認可の失効、通常領域とのfence競合、失効後の引用抑止、固定辞書版・統合後の再照合、承認バッチ失敗時の非部分更新を.NETテストで確認した。旧4案の再収集・承認と履歴原本の訂正・撤回も拒否する。

実 `AiService`・codec・実行ガードを使うオフラインtransport試験では、同じ自然文検索2回で模擬送信は計2回、記録4件のうち2件はcache hitだった。実際のHTTP送信・資格情報・Foundry推論は使っていない。追加の有料AI呼出しは0回。

## 新規主要ファイルの行カバレッジ

| ファイル | 実測 |
|---|---|
| WorkflowRecordStore.cs | 64/64、100% |
| DemoWorkflows.cs | 121/125、96.8% |
| SkillProjection.cs | 58/62、93.5% |
| DocumentLifecycle.cs | 66/67、98.5% |
| DemoWorkflowEndpoints.cs | 21/21、100% |

## 失敗の扱いと自己レビュー

TDDの失敗段階はfeatureブランチのコミットに保存している。通常画面を複数specまとめてraw runnerで動かした初回は、共有したuploadレート制限と前ケースの経験月数による失敗があった。既存の公式runnerに戻してケースを隔離し、人物画面10件の成功を確認した。訂正のテストではAI traceの版と文書受付版を混同しない待機へ修正し、現在の文書版・EvidenceRefも直接検証した。

自己レビューでは、技能承認だけで別memberへ私有資料を公開しない境界、旧履歴原本の変更拒否、画面の遅い進捗応答が別の文書・版へ反映されないこと、自然文の条件確認で技能IDが欠落しないことを追加・修正した。独立したレビュー承認、CI、共有反映はこのローカル記録には含めない。

## PR初回CIで確認した競合と修正

同じhead `29e584ee` のpush側verifyは全体成功、PR側 [42441804377](https://github.com/kokutaro/dse-growth-loop/actions/runs/42441804377) は辞書画面と既存の手動分析画面が失敗した。traceを取得して確認した結果、新しい資料一覧の初期GETと、候補収集の画面再読込中にテストが送ったGET、既存分析POSTがいずれも `ACCESS_BUSY` / 503だった。分析要求の結果が不明なままPOSTを再送していない。

資料一覧を必要時の取得へ変えて初期読込の競合を減らし、テストの確認GETも候補収集・画面再読込が完了してから送るよう修正した。修正後、ローカルの辞書・通常デモ12件、既存の手動分析6件、Web buildが成功した。追加の有料AI呼出しは0回。PR・プレビューの新しいheadでの結果は別途確認する。

head `6f3f41f7` のPR側 [verify 42442576531](https://github.com/kokutaro/dse-growth-loop/actions/runs/42442576531) は全14ジョブ成功（Linux Release .NET 1,269件、契約43件、認証/context 10件を含む）。同じheadのpush側 [42442572069](https://github.com/kokutaro/dse-growth-loop/actions/runs/42442572069) は辞書画面の既存1ケースで失敗した。traceは、試験の辞書監査GETと画面の初期GETが競合し、画面に `ACCESS_BUSY` が返ったことを示した。監査GETを画面遷移の前へ移し、再収集後の監査も画面再読込の完了を待つよう修正した。失敗を単純な成功扱いや無条件の再実行で隠していない。

同headの [preview 42442711921](https://github.com/kokutaro/dse-growth-loop/actions/runs/42442711921) は実際に配備済み。入口画面の永続処理の説明と、operator権限のないプレビュー利用者の開始拒否をブラウザで確認した。権限追加はしておらず、一連の操作の実機成功ではない。終了後のARM読み取りで対象revisionは `ScaledToZero`・replicas 0だった。共有API/Workerは基点 `0da09703` のまま。

## 独立レビューでの遅延応答の修正

別のレビュー担当が `ada6ba03` を確認し、資料断片GETの応答が資料切替・版更新後のパネルへ混入し得るP2を指摘した。応答を保留するブラウザ回帰試験で資料切替・版更新の2ケースを修正前に失敗させた。その後、要求時の認証世代・選択文書キー・原文view・断片読取世代を、成功応答と例外の反映前に照合するよう修正した。A→B→Aで元の資料へ戻った場合も、選択し直す前の応答は無効になる。

修正後の辞書・通常デモ画面は16件成功、Web buildも成功。試験は資料切替、同じ資料への戻り、原文版更新、古い例外の4ケースを含む。対象は画面の非同期表示で、サーバー側の認可・同意確認を変更していない。独立した再レビューの判断、最新CIと実配備は別途記録する。

## 未実施・制約

- 追加の実AI品質試験、Azure Queue→常用Workerの領域付き実機取込、共有画面の一連確認は未実施。成功済みAI応答の不要な再送はしていない。
- 既存サービスの権限・設定・予算を使い、新規IAM・Azureリソース・共有辞書や実ユーザー情報を変更していない。現在の領域は本人専用で、別利用者との共同編集・招待や、全領域の通知自動探索は含まない。
- CIは既存設定でLargeEvaluationを1件除外するため、同じheadの通常.NET件数はローカル全体より1件少なくなる。CI結果は実行後に別途記録する。

生ログは同名サブディレクトリの `logs/`、画面証跡は [承認](../../web/screenshots/20261108-030820-durable-workflow/01-approved.png)、[通常検索](../../web/screenshots/20261108-030820-durable-workflow/02-normal-search.png)、[撤回後](../../web/screenshots/20261108-030820-durable-workflow/03-withdrawn-search.png)、[通常取込](../../web/screenshots/20261108-030820-durable-workflow/04-normal-ingestion.png)、[訂正版](../../web/screenshots/20261108-030820-durable-workflow/05-normal-correction.png)。
