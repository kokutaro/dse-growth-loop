# Issue 30 副業務の実Jev分類計画

2026-10-03。ユーザーの実行指示により、`jev-1.13.0`で共同実施の文脈を残す副業務20件と、同じ根拠形式の元職歴対照20件を分類した。試行上限44／費用上限$0.12を送信前に固定し、42試行で40件を完了した。

- [固定送信計画](jev-plan.json)：新しい入力40件。既存の同一成功リクエストを再送しない。
- [fixture](fixture.json)：既存人物・質問・正解ラベルを保持し、同じ人物へ派生検索断片20件を追加。
- [対応表](bindings.json)：元職歴と副業務の各requestId。
- [送信前manifest](manifest.json)：元データ・応答・実装のhashと上限。
- [実測結果・費用・追加課金なしの再現](../../../docs/validation-results/20261003-issue-30-jev/results.md)。

分類入力は実装同様、本文に加えて原本根拠と空の候補配列を持つ。fixtureの根拠・版IDを用いたHTTP分類とオフライン取得評価であり、実DB正本検証やクラウドへの投影は実行していない。確認集合は観察済みなので、独立評価と扱わない。

実測済みの計画を再送しない。保存応答からの再採点は次のコマンドで行う（新しい空の出力先を指定）。

```sh
dotnet run --project src/Dse.Evaluation --no-build -- \
  --issue30-live-score docs/validation-results/20261003-issue-30-jev/capture \
  .artifacts/issue30-live-replay-new
```
