# 取込の段階計測と送信前取消

## 計測と時間の基準

`pipeline-stages-v1` は Job ID / aggregate（generation）ID / 相関ID / fence holder・ETag / AI run・操作を結ぶ運用メタデータ。Job作成時刻、所有権取得後の処理開始時刻、各段階の経過時間を分ける。作成からAI開始までを処理時間と同一視しない。

Jobのclaim、profile構築・公開、AI根拠準備、provider attempt、精算、応答後の原本確認、lease更新、失敗保存を記録する。終了した公開Repositoryのread/query/write呼出（失敗を含む）の件数・時間・失敗件数とキャッシュhitを段階別に集計する。未終了の呼出や無記録を0件・停止済みの証拠として扱わない。AIの既知の試行・再試行は従来の`trace.attemptCount/retryCount`を維持する。SDK内部の再試行数は取得できておらず`null`。公開呼出回数をSDKのwire回数と呼ばない。stageの経過時間は入れ子を含むため単純加算しない。Repository時間はそのstage直下で終了した呼出の合計であり、全体時間は`elapsedMs`と併せて読む。

本文・引用・人物名・認証情報・例外メッセージは診断に含めない。stageは最大256件、超過は`droppedStages`で明示する。Jobの`processingDiagnostics`とAI traceの`preparationMs/pipelineDiagnostics`は任意追加で、旧記録には存在しない。Workerの`--inspect-generation`にも安全な診断項目を表示する。HTTPの新権限や新APIは追加しない。保存中の診断はその時点のsnapshotであり、`running`のstageだけで実行中writerと判断しない。終端ログ、Job/AI実行状態、receipt、gate等と照合する。

120秒のJob期限、60秒のAI期限等の設定は変更しない。タイマーとlease更新待機に既存の`TimeProvider`を使い、System時の時間を保って仮想時間で再生可能にする。期限超過は`PROCESSING_TIMEOUT`、更新失敗は`LEASE_RENEWAL_FAILED`、それ以外の取消は`PROCESSING_CANCELLED`。所有権を失ったJobには古いworkerが失敗状態を書かない。

## 読取り削減と認可

AI送信前だけ、同じ所有fence内で権限・原本・版・親generation・根拠のpoint readを共有する。返すJSONはコピーで、ETagも保持する。期間・関与・根拠の意味を書き換えない。AI後の確認にはこのキャッシュを再利用せず、同一の`SourceSnapshot`だけを重複排除して新しく読む。異なる版のsnapshotは残すため、不一致が合格へ変わることはない。

## 取消と互換性上の注意

安全に解放できるのは、fence取得の結果が確定し、監査保存・予約・AI送信より前の読取り専用準備で取消され、そのフェーズに書込み試行がなく、未完了read/queryが0のときだけ。フェーズのRepositoryは書込み禁止で、Primary/Memory/Cosmos/Localの書込み口にも禁止を伝播する。閉じたフェーズを後から使えない。

取得結果不明、未完了処理、書込み試行、監査・予約後の取消、提供元応答不明、公開batchの結果不明はfail closedを維持する。AI gateが空であっても、監査・予約後にtimeoutした処理のtenant fenceを自動解除しない。未知のAI応答ではslotだけでなくfenceも保持するため、既存の候補提案は`dispatching`のまま503となり得る。これは新しいキーで再送してよい状態ではない。prepared時の`inferenceNotDispatched`だけを根拠にせず、AI run / execution / receiptを照合する。

復旧には引き続き`persistence.md`の停止・照合・期待ETag付き手順が必要。消費済み枠の補充、A再実行、B/C処理、旧検索切替はこの修正で自動化しない。

## ローカルで確認した範囲

仮想時間で120秒/60秒/最大7回の製品Worker経路を実行。各AI前確認の取消、lease更新失敗、未知の提供元結果、未知の原子公開batchを検証する。原子batchの結果不明は、旧状態のままか新状態の全体commitのどちらもあり得るため、未commitに偽装しない。

保存済みHK評価の原応答には現行mainと互換でない古い構造化schemaがある。原本・原応答は保持し、欠けた日付欄だけを`null/unknown`とした別の互換fixtureで後続処理を比較する。この処理はモデルの新しい日付推定ではない。無遅延の3冊で修正前後の21要求wireは一致し、公開Repository読取りは20,867→4,659。100ms/読取りという仮定のA再生は120秒で3回までで停止したが、旧検索を維持しfenceを残さなかった。

性能の完了保証や実AI精度の改善は示していない。本番の各読取り遅延・SDK retry・実際の取消地点は未計測。残るprofile構築等の読取りと遠隔依存の時間は、将来、承認された本番計測で分離して確認する。今回は期限延長・追加AI・本番配備をしていない。
