# Issue #34 の同run操作収集器

2026-10-11。`OperationMeasurements` は `AsyncLocal` を使い、同じHTTP要求・処理内のストア操作を同runにまとめる。並列要求は別run、同要求内の並列読取は同run。operation ID、run/request/correlation IDは収集器が生成するランダム識別子で、社員・原本・テナントのIDではない。

## 実装した収集範囲

- `CosmosRecordStore.ReadAsync`：SDK responseのRU/HTTP状態、期間、失敗。フェンス、台帳、原本・根拠を固定ラベルで分類する。404や失敗にもRUがある場合は残す。
- `CommitAsync`：transactional batch全体の `RequestCharge` のみ。各item RUを加算しない。フェンス、予約/精算、検索/AI監査の混在batchも1操作なので二重計上しない。混在batchの役割別RUへ分割した実測値とはしない。
- `QueryAsync`：呼び出した各ページを1操作。SDK exceptionのchargeが返る場合も捕捉する。
- Memory/Local：外側の `OperationMeasuredRecordStore` でread/commit/queryと処理期間を収集し、RUはnull。Local内部Memoryを重ねてwrapしない。CosmosはSDK箇所で収集済みなのでwrapしない。
- API middleware：HTTP開始からendpoint終了までの期間とHTTP状態、下流store操作を含む。phaseは `service`。画面描画・HTTP転送終了・warm/coldの真偽を推定しない。`X-DSE-Measurement-Run` は生成run IDだけを返し、許可された測定時にHTTPとoperator exportを対応づけられる。
- `CosmosProjectionRepository` は `IRecordStore` を通らない直接SDK経路なので、ページloopにも `Start(ProjectionPage, cosmosSdk:true)` / `ObserveResponse` / `Fail` hookが必要。rootがその接続を担当する。

```csharp
// SDKを呼ぶ既存処理の周囲。元の例外・再試行方針は変更しない。
using var operation = OperationMeasurements.Start(MeasurementKind.ProjectionPage, cosmosSdk: true);
try
{
    var page = await iterator.ReadNextAsync(ct);
    operation.ObserveResponse(page.RequestCharge, 200);
    // 既存の読取・検索処理
}
catch (Exception ex) { operation.Fail(ex); throw; }
```

DI接続はDemoの登録lambdaから `new OperationMeasuredRecordStore(store)` を返す。APIで `AddSingleton<OperationMeasurementBuffer>()` と `UseMiddleware<OperationMeasurementsMiddleware>()` を認証・identity・endpointより外側へ置く。公開export endpointは収集器から作らない。operator exportを接続する場合は既存の管理者認可を適用し、一般利用者へ他requestの報告を返さない。Worker/Jobは別のscopeを明示的に開始するまで収集しない。同runをJobの再起動に跨いで永続化する実装ではない。

## 輸出データと不明値

Domainの `MeasuredOperation` は既存 [測定schema](evaluation/issue-34/measurement-record.schema.json) の `operations` 各行に合わせたDTO。本文、question、principal、tenant/partition/record ID、URL/path、token、秘密、SDK diagnostics、任意のexception messageを保持しない。HTTP/ストアの失敗コードは固定enum由来のラベルだけ。stream/JSONを複製して観測ログへ書かない。

`OperationMeasurementExport` は `issue34-operation-export-v1` という**部分輸出**。既存 `issue34-measurement-v1` のprovenance/bounds/storage/latency/costを完備するものではない。実行承認や費用設定も付与しない。

- `ObservedSdkRequestUnits` は観測したpublic SDK response chargeの小計。SDK応答が1件もなければnull。DemoもnullなのでAzure実測0 RUへ変えない。
- `CompleteRequestUnits` はSDK内部再試行のcharge等が未分離なのでnull。`CompleteMeasurement` と `AcceptanceGatePassed` はfalse、stateは `incomplete`。
- `attempt=1` はこのpublic SDK応答の1観測を示し、SDK内wire attemptの数を1と証明しない。別のストア呼び出しで再試行した場合は別operation ID。SDK内部retryの総数/各課金はunknown。
- 失敗chargeが未知・非有限・負ならnull。transport前に止まった操作もnullであり、請求0とは断定しない。
- `Ai`、JPY、保存/索引/バックアップ、runtime、ネットワーク/ログ、価格/FX/税/契約、請求照合、UI完了/温冷母集団は未収集。既存AI台帳のread/commitが見えることと、全モデルの送信/usage/失敗留保が測れることを分ける。
- required categoryを `scope.Export(expectedKinds...)` へ渡すと、未観測カテゴリーを `MissingExpectedKinds` へ残す。未観測が0件/0円という意味ではない。保留中のoperationはpending/nullのまま輸出できる。
- scopeの既定上限は10,000操作で、超過を `DroppedOperations` に計上する。API bufferは最大64runかつ20,000操作に制限したプロセス内の一時資料。古いreportは破棄され、プロセス停止で失われる。受入測定時は管理者が許可された範囲で保存し、実行計画のsource/config/input/service hashと照合する必要がある。

## ローカル回帰と実環境の残り

`MeasurementTests` はFake/Mockだけで、read/batch/query、失敗RU、batch単一計上、並列6request×4操作の隔離、欠落/保留/上限分類、Demo null、取消、固定カテゴリー、HTTP phase、nested scope/Buffer上限、未知chargeを検証する。rootの統合ビルド・全体回帰で実行する。

```sh
dotnet test tests/Dse.Tests --filter FullyQualifiedName~MeasurementTests
```

Azure SDKの応答/exceptionから取り出すhookをローカル実装したが、実CosmosのRU値・内部retry課金・本番認可/全AI・保存量/価格/実請求・warm24/cold3・停止/復元は今回未計測。実測は費用上界・最新台帳・独立停止/復元・本人認証の準備と承認を揃えてから別途行う。資料準備やFakeテストだけで#34を完了扱いにしない。
