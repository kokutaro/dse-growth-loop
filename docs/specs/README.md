# 実装契約 v1

更新日：2026-09-23。対象：[Issue #3](https://github.com/kokutaro/dse-growth-loop/issues/3)、親計画 [#1](https://github.com/kokutaro/dse-growth-loop/issues/1)。全件ダミーの開発契約。**この仕様を追加しただけでは、現APIに書込・認可・AI処理は実装されない。**

2026-10-11の追加ランタイム契約は [匿名ワークスペース](openapi-workspace-v1.json)、[手動分析](openapi-business-analysis-v1.json)、[公開知識](openapi-knowledge-v1.json)。独立表示名同意のprivate保存stateはSession schemaに定義し、匿名DTOへは出さない。新Live traceは未測定RUをnullとして区別する。実環境の受入は別の検証である。

## 契約の入口

| 資料 | 内容 |
|---|---|
| [JSON Schema](contracts.schema.json) | Draft 2020-12。20エンティティ、本人紐付け、未保存UI状態、要求・応答型。`$defs`を名前で参照する |
| [正常・障害状態例](examples.json) / [不正入力例](invalid-examples.json) | すべて架空。`schema`の型で`value`を検証する。相互参照のある実投入seedではない |
| [データ・整合性](data-and-consistency.md) | 正本、版、パーティション、原子的更新、通知・投影回復 |
| [状態遷移](transitions.json) / [遷移例](transition-examples.json) | 許可辺・主体・必要条件。列挙されない遷移は拒否 |
| [API仕様](api.md) / [OpenAPI 3.1](openapi.json) | 入出力、上限、ページング、冪等性、ETag、エラーと画面表示 |
| [権限・公開](authorization.md) | oidと架空人物、役割行列、共有前照合、公開と分析の承認 |
| [分類体系](taxonomy-v1.json) / [正規化辞書](normalization-v1.json) | Choice、Noul、期間の分離と既存fixture互換 |
| [決定ログ](decisions.md) | 採用契約と利用者確認待ちの暫定方針 |

JSON Schemaで構造・入力範囲を検証し、[追加不変条件の検証器](../../tests/contracts/contract_rules.py)で分布合計、期間、参加者確認などの複数フィールド条件を検証する。DBの参照整合性、競合制御、JWT、ACL、トランザクション、モデル品質はこの検証器の対象外であり、後続実装の試験が必要。OpenAPIは配布しやすい自己完結形式とし、埋込型がJSON Schemaと一致することをテストする。変更時は両方を更新する。

## 検証の再現

Python 3.12以降と既存.NET SDKを使用。依存を一時venvへ導入する。テスト本体はネットワーク・Azure・有料AIを使用しない。

```sh
python3 -m venv /tmp/dse-contracts-venv
/tmp/dse-contracts-venv/bin/pip install -r tests/contracts/requirements.txt
dotnet test tests/Dse.Tests --collect:"XPlat Code Coverage"
/tmp/dse-contracts-venv/bin/python -m unittest discover -s tests/contracts
/tmp/dse-contracts-venv/bin/coverage run --source=tests/contracts -m unittest discover -s tests/contracts
/tmp/dse-contracts-venv/bin/coverage report --include='*/contract_rules.py' --fail-under=80
```

CIでは検証器だけを対象にカバレッジをレポートする。`.NET`テストは実際の`DemoData.Create`を`TestResults/contract-fixture.json`へ書き出すため、契約テストより先に実行する。既存fixtureをコピーして互換性を主張しない。

## 後続Issueの参照先

- #5：共通メタデータ、パーティション、本人紐付け、ETag、認可再照合。
- #6：文書版・根拠・取込状態、Blob/DB/Queue境界、通知漏れ。
- #8/#10：分類体系・AI台帳・停止、全Probability/Confidence、本人訂正保護。
- #7/#11/#13：参加承諾・会話連番・自然文検索・全員閲覧可能な共有。
- #12：成果/評価、版単位の公開承認・撤回、明示分析許可を実装。[公開手順](../knowledge-publication.md)と[実行API](openapi-knowledge-v1.json)を参照。
- #15：承認・全員分析同意のある要約を用いた事業仮説抽出は後続。
- #4/#14/#16：正常・不正例を再利用し、認可・障害・実サービスの試験へ拡張する。

追加フィールドや状態を無断で意味変更しない。破壊的変更は`schemaVersion`とAPIメジャー版を更新し、移行・旧版読取・再投影方針を同じPRに記載する。

Issue #13のMessageサブタイプ `messageKind=ai_case`、`ChatAssistanceRules`、事例ごとの評価、出典・評価APIを正本Schema/OpenAPIへ追加した。`kind=message` は既存Messageの識別子として維持する。[設定と検証境界](../chat-assistance.md)を参照。

- [手動ダミー部署正本のAPI](openapi-organization-v1.json)：期間検証、operator限定変更、本人所属確認。権限・退職・復元は[停止下のローカルCLI](../organization-authority.md)。
