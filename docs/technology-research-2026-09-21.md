# 技術調査・選定記録

確認日：2026-09-21 / 対応設計版：0.2。一次情報を優先した。以下の「事実」は公開資料の確認結果、「判断」は本デモに対する設計判断である。Jevの日本語分類精度はユーザー試験で目標達成済み。試験値は未受領なので捏造せず、検索Recall・再順位付け・費用は未検証として区別する。

今回の更新：AI Search＋PostgreSQL＋埋め込み検索を初期構成から外し、Cosmos DB Serverlessの属性索引＋Jev Probability/Noul＋Foundry再順位付けへ変更した。

## 1. Jev：分類・意思決定を担う新しい選択肢

| 確認事項 | 事実・出典 | 本案件への判断 |
|---|---|---|
| 提供段階 | 2026-09-15に早期アクセス発表。[公式発表](https://typesafe.ai/blog/introducing-system-one-models-and-jev) | 日本語分類はユーザー確認済み。契約、版変更時の回帰、可用性は別途確認 |
| 接続先 | 公式APIは`POST https://api.typesafe.ai/v1/systemone`。[API](https://docs.typesafe.ai/api) | プロバイダー依存はアダプター内に閉じ込める |
| 紹介サイトとの区別 | ユーザー提示サイトは独立した紹介・仲介APIを説明している。[紹介サイトの利用案内](https://www.jevtypesafeai.com/get-jev) | 「Jevへ送信可」を第三者仲介サービスへの送信許可に拡張しない |
| Choice | 選択結果に加えて各選択肢の`probabilities`を返す。[Choice](https://docs.typesafe.ai/primitives/choice) | 全分布を保存。非最上位でも閾値を超えるものを検索に使う。職歴単位と質問側の複数分類も使う |
| Score | 順序のある2〜10段階を評価する。[Score](https://docs.typesafe.ai/primitives/score) | 習熟度の行動基準を定義した場合だけ使用。能力の精密な点数や年数を推定させない |
| Noul | yes側の値を0〜1で返す。[Noul](https://docs.typesafe.ai/primitives/noul) | 経験有無の補助検索経路を設ける。未判定を否定にせず、閾値と救済効果を独立評価 |
| Confidence | 分布の集中度に基づく情報で、各選択肢のProbabilityとは別。[Confidence](https://docs.typesafe.ai/confidence) | 診断用に保存するが、低Confidenceを理由に複数分野の候補を除外しない |
| 既知の弱点 | 算術・日付・長い無関係な入力・誘導的内容に注意が必要。[Jev 1.13の制約](https://docs.typesafe.ai/model-jaggedness/jev-1.13) | 集計、期間計算、認可はコード。入力を必要箇所に絞り、誘導文を評価に含める |

現行モデルは`jev-1.13.0`。テキスト入力のみで、日本語等の非英語は英語同等の品質とはされていない。公開コンテキスト上限は要求全体64k、stateと最大の単一質問の合計32k。最新エイリアスは動くため固定版を使う。入力課金は公開値で100万トークン当たり0.042米ドル、出力は無課金だが、契約・提供条件の変更を実装前に再確認する。レート制限は変動すると説明されており、定常性能の保証値として設計しない。[Models](https://docs.typesafe.ai/models)

顧客入出力をモデル学習に使わない方針と、保持期間・越境処理・ZDRは分けて確認する。Enterprise向けZDRは個別条件。本デモのダミー送信許可を将来の実データに転用しない。[Legal](https://docs.typesafe.ai/legal)、[Privacy Policy](https://typesafe.ai/legal/privacy-policy)

## 2. Foundry：最新カタログから比較開始点を置く

2026-09-21確認時、公式一覧にGPT-6 Astra、GPT-5.6 Sol/Terra/Lunaと構造化出力対応が掲載されている。生成の初期候補をLuna、複雑な要約の比較候補をTerra、品質上限比較をAstraとした。これは本案件での価格・速度・精度の順位を実測した結果ではない。モデル利用はリージョン・デプロイ方式・クォータの確認後に確定する。[Foundryモデル一覧](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure)

Foundryの主な役割は、取得済みの広い候補集合を職歴・事例本文から再順位付けし、根拠付きで説明すること。固定書式で抽出できない情報の構造化、解決要約、事業仮説作成にも使う。埋め込みモデルは初期構成に含めない。

## 3. 検索：複数Probabilityで広く拾い、LLMで絞る

| 項目 | 公式情報 | 採用方針 |
|---|---|---|
| 属性検索 | 等価・配列要素一致・数値範囲を索引で検索できる。[索引仕様](https://learn.microsoft.com/en-us/azure/cosmos-db/index-overview) | 分類IDとProbability閾値、検索語、期間を使う |
| 部分文字列検索 | `CONTAINS`は等価条件と索引利用効率が異なる。[索引利用](https://learn.microsoft.com/en-us/azure/cosmos-db/index-overview#index-usage) | 正規化語を事前保存し、本文部分一致を主経路にしない |
| 複数分類 | Choiceは非最上位の確率も返す。[Choice](https://docs.typesafe.ai/primitives/choice) | 保存時に単一分類へ潰さない。検索閾値と最終提示基準を分離 |
| 経験の有無 | Noulはyes/noの確率を返す。[Noul](https://docs.typesafe.ai/primitives/noul) | 補助経路として和集合に追加。経験期間や習熟度とは別項目 |

この方式の採用判断はユーザーと合意済み。これから検証するのは一次Recall、最終Precision/順位、候補数と費用・待ち時間の関係。分類が正しいことだけで検索の取りこぼしゼロとはしない。分類体系外の検索失敗が残る場合だけ、ベクトル検索等を将来比較する。

## 4. データストア：NoSQLの選択は目的から判断

- Cosmos DB Serverlessは消費RUと保存量を課金し、予約した処理能力の固定費を持たない。単一リージョンに制限される。断続的なデモ利用に合う。[Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless)
- 今回は日本語本文のネイティブ全文検索ではなく、事前に整理した属性の索引検索。全文検索の対応言語を採用可否の理由にしない。
- 必要な整合性はETagと同一論理パーティション内の更新で確保し、全体の横断トランザクションは要求しない。[競合制御](https://learn.microsoft.com/en-us/azure/cosmos-db/database-transactions-optimistic-concurrency)
- Azure Table StorageはPartitionKey/RowKey以外の二次索引を標準で持たず、複数属性検索ではアプリ側の索引管理が必要。今回はCosmos DBを選ぶ。[Table Storage検索設計](https://learn.microsoft.com/en-us/azure/storage/tables/table-storage-design-for-query)
- 安価になるかは検索RU、書込・索引更新、背景処理、保存量で決まる。NoSQL一般がRDBより安いという主張ではない。

## 5. 実装・実行基盤

- .NET 10はLTSで2028年11月までサポート。新規バックエンドの初期候補とする。パッチ版とSDKは実装時に固定。[.NETサポート](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)
- Word/ExcelのOpen XML形式はOpen XML SDKで機械的に扱う。LLMへ全文を丸投げする前に、構造と根拠位置を保つ。[Open XML SDK](https://learn.microsoft.com/en-us/office/open-xml/about-the-open-xml-sdk)
- Container Apps Jobsは定時実行と手動実行に使える。スケジュールはUTC評価である。[Jobs](https://learn.microsoft.com/ja-jp/azure/container-apps/jobs)
- 非同期通知はAzure Queue Storage。DB内ジョブと別の永続ストアなので、通知漏れ・再配送・重複をアプリ側で扱い、頻繁なDB巡回を避ける。[Queue Storage](https://learn.microsoft.com/en-us/azure/storage/queues/storage-queues-introduction)

## 6. 未検証事項・実装開始のチェックリスト

- Azureサブスクリプションでの候補モデル利用可否、クォータ、必要な申請。
- 選定リージョンでのCosmos DB Serverless、ネットワーク接続、データ配置。
- Jevアカウントの利用可否、利用条件、現行モデル、APIエラーとレート制限。
- ユーザー分類試験の設定と結果を受領して回帰用に固定。検索の正解集合・関連度を別に用意。
- Probability閾値、職歴単位化、Noul追加で救える候補数と、上限適用で落ちる候補数。
- Foundry再順位付けへの入力根拠、順序バイアス、分割処理、拒否・失敗時の縮退。
- 全体費用：基盤固定費を含む稼働時間別見積。公開トークン単価だけで総額を語らない。
- 本人未確認スキルの表示、訂正、再取込競合の操作テスト。
- 自動投稿の適合率、不要会話での誤投稿、重複抑制、AI障害時の継続。
- 共有チャット全参加者への閲覧権限と、公開撤回直後の遮断。
- 結果の存在するデモと、未検証の仮説・推定を区別する説明。

構成は検証方針として合意済みだが、統合システムの性能・費用・実データ運用の安全性は未確認。具体的な試験は[検索検証計画](retrieval-validation-plan.md)に従う。
