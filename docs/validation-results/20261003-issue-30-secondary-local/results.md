# Issue 30：副業務の検索断片追加・ローカル検証

2026-10-03。保存済み応答での[改善候補比較](../20261003-issue-30-fragment-replay/results.md)を受け、プロフィール投影に副業務の検索断片追加を実装した。**ダミーのOffice原本から元経験1件と副業務SearchUnitを作成し、同じ人物に統合して検索できることを確認した。** 実Jev/Foundry呼び出し・Azure操作・本番適用は0。

## 実装

- `Profile:SecondaryWorkFragments`を追加。AppHostとPlaywrightのMock環境で有効、設定省略時は無効。
- 明示された副業務・兼務の記載を原文から機械抽出。共同実施・補助・否定の文脈を保持し、単一の参照元EvidenceSpanに完全一致する場合だけ追加する。
- 元経験を保持し、派生断片を独立分類してSearchUnitだけを追加。経験正本・本人編集欄・月数を増やさない。同じ人物IDへ統合する。
- 元断片と同じ認可・有効版・根拠を使い、全投影を同一トランザクションで公開する。分類入力が同一ならキャッシュを利用する。
- 元経験＋派生検索断片の20件上限を分類前に確認。超過時は既存有効版を保ち、新しい世代を部分公開しない。

## 確認結果

| 確認 | 結果 |
|---|---|
| .NET全テスト | 327件成功、失敗・スキップ0 |
| 今回追加した.NETテスト | 12条件成功（副業務の原文抽出、設定ON/OFF、検索、保存分類再利用、訂正・再取込、撤回、件数上限、本人追記の区別） |
| 行カバレッジ | 全体87.64%、ProfilePipeline 95.83%、ProfileExtraction 97.36%（生成正規表現を含む別クラスは100%） |
| プロフィールPlaywright | 4件成功（副業務追加、docx、xlsx、公開撤回・再公開） |
| 共通契約 | 17件成功 |
| ソリューション・UIビルド | 成功。既存ASPIRE010とViteのbundleサイズ警告あり |

機能未実装の段階で副業務追加を期待する2条件が失敗することを確認し、実装後に成功した。テストランナーのローカル通信ソケット制限は許可されたローカル実行で解消した。契約検証は依存を導入済みの仮想環境を利用した。

ブラウザ試験は[架空原本](../../../web/tests/fixtures/secondary-work.docx)を実際にアップロードし、経験の編集欄が1件、経験月数が48か月、検索応答に「共同実施した」を保った派生断片と原文根拠があることを検証した。これはfake AIによる機能試験で、実Jevの分類精度・Recallではない。

## ローカルで試す

```sh
dotnet run --project src/Dse.AppHost
```

画面の文書アップロードで[secondary-work.docx](../../../web/tests/fixtures/secondary-work.docx)を本人資料として登録し、検索投影の完了後にプロフィールを更新する。検索語に`副業務として次の対応を共同実施した`を入力し、候補と原文を確認する。

AppHostはMockで動作する。fixture原本の再生成は`python3 web/tests/fixtures/generate-secondary.py`。既存プロフィールを一括再分類する操作は今回実行していない。

## 再現コマンド

```sh
dotnet build --no-restore -m:1 -nr:false
dotnet test tests/Dse.Tests --no-restore -m:1 -nr:false --collect:"XPlat Code Coverage"
npm --prefix web run build
(cd web && npm run test:e2e -- tests/profiles.spec.ts)
.artifacts/issue-9-contract-venv/bin/python -m unittest discover -s tests/contracts
```

契約の仮想環境がない場合は[READMEのセットアップ](../../../README.md)を使う。[機械可読検証記録](validation.json)には実装・テスト・fixtureのhashと計測範囲を保存する。

## 未検証の範囲

見出しがない任意の職歴文、複数根拠にまたがる副業務、複数副業務の個別分割は対象外。今回の分類入力は共同実施等の前置きを残すため、先行の保存応答で前置きを除いた評価とは異なる。実Jevでの新規分類・独立した承認済み確認集合・実Cosmos・最終LLM品質は未測定。先行の人物Recall100%を今回の実AI結果として報告しない。
