# 自然文検索・根拠付き再順位付け

Issue #11で追加したローカル検証用の検索経路。ライブRecall・順位品質・待ち時間・費用の合否はIssue #14で測定する。以下の既定値は品質を保証する値ではない。ローカル起動は`dotnet run --project src/Dse.AppHost`（Mock、停止解除、出力上限3000を設定済み）。直接APIを起動する場合は`Ai__Mode=Mock`、`AiUsage__Stopped=false`、`AiUsage__MaxOutputTokens=3000`を指定する。

## APIと画面

既存の`POST /api/search`とその最大`Limit=100`、`retrieval-v1`、救済枠・応答は維持する。既存の[OpenAPI v1](specs/openapi.json)にある自然文検索は計画契約で未実装。本実装は条件の編集と人物・事例別の結果を含むため、別の[/api/v2契約](specs/openapi-search-v2.json)として公開する。旧手動検索のクライアントは移行不要。

- `POST /api/v2/search/conditions`：`{ "query": "ソフトウェアの引き継ぎを改善したい" }`。Jevのdomain全分布・複数上位分類、補助Noul、正規化語を返す。`unknown/other`、停止・失敗時は`conditions=null`と`manual_required:*`を返し、手動入力へ戻る。Confidenceによって副分類を捨てない。
- `POST /api/v2/search`：`{ "query": "...", "conditions": { "categories": [{ "axis": "domain", "categoryId": "software" }], "signalIds": ["handover"], "terms": ["引き継ぎ"], "limit": 20 } }`。編集済み条件で一次取得し、Foundryで根拠を読む。`conditions`、`primary`、`assessments`、`runId`、`stage`、`rerankingStatus`、`aiRunId`を200で返す。公開閾値はサーバーの版付き設定で上書きする。
- `GET /api/v2/retrieval-runs/{runId}`：同じテナント・scope内の検索実行者のみ閲覧できる。監査には根拠本文や自然文そのものは保存せず、条件・版・ID・処理診断を返す。

検索は`{ "query": "..." }`のみでも1回の同期API呼出しで分類→一次取得→再順位付けを完結する。条件生成APIは編集用の任意のプレビューで、複数リクエストは必須ではない。分類が使えなければ422 `CLASSIFICATION_MANUAL_REQUIRED`を返し、手動条件付きで再検索する。永続バックグラウンド処理やpollがないので202は返さない。v1業務更新の`Idempotency-Key`契約はこの新しいv2同期検索には適用しない。自動再送はせず、既存AIアダプターの主体・入力・モデル・版ごとの5分キャッシュと永続予算台帳を適用する。キャッシュ期限後の再検索は新しいAI処理になり得る。ライブ実行の承認境界は[AIアダプター](ai-adapters.md)と同じ。

画面は自然文→条件生成→複数分類/技術要素・補助条件/語の編集→検索→人物/事例カード→出典の流れ。空の自然文では従来の手動検索を使う。未確認表示・fixture/実AI処理の出典表示を保持し、適合理由は根拠引用として表示する。役割・期間等を推測せず、相談時に確認する不足情報を表示する。

## 版付き設定と一次取得

`Retrieval`設定（環境変数では`Retrieval__*`）を固定し、変更時は`Version`を更新する。設定全体を各RetrievalRunへ保存する。

| 項目 | 既定値 | 意味 |
|---|---:|---|
| Version | retrieval-v2 | 一次取得ルール版 |
| QuestionTopCategories | 3 | 質問側の上位カテゴリ数 |
| QuestionThreshold | 0.1 | 質問側Probabilityの下限 |
| DocumentThreshold | 0.2 | 文書側Probabilityの下限 |
| NoulThreshold | 0.5 | 質問補助条件/文書Noulの下限 |
| RescueSlots | 2 | 人物/事例それぞれの語・Noul救済枠 |
| PersonLimit / CaseLimit | 10 / 10 | 人物/事例別の候補上限（各最大50） |
| EvidenceBytesPerCandidate | 1200 | 候補ごとの根拠本文UTF-8バイト予算 |

同一配列要素の分類ID＋Probability、Noul、正規化語をORで広く取得する。ACL・有効版・参加可否はAIとは独立したサーバー判定。人物は職歴数にかかわらず1候補。人物と事例の枠を別に確保し、各枠を`min(要求Limit, 設定Limit)`に制限する（合計最大100）。一次取得5,000単位/1,000 RU超過は503 `SEARCH_LIMIT_EXCEEDED`であり、途中結果の正常返却はしない。

RetrievalRunには変換済み条件、分類/正規化/ルール版、各投影のsource/access/projection版、経路別候補ID、統合前の単位ID、統合後/上限後のIDと件数、AI入力ID、切詰め、RU、時間、AiRun参照を記録する。CosmosのRUは一次クエリ・正本照合・AI前後の照合の実測合計（監査保存自体は合計に含まない）。Demoは0で`ruMeasured=false`、実測値と扱わない。

一次検索の再現は保存済み条件・設定と同じ版の不変投影を使う。`beforeMergeIds`/`dataVersions`から対象投影を指定し、当時の認可内集合でRetrieverを実行する。現在の公開APIで撤回済みデータを再表示するための仕組みではない。「権限内全件」の品質評価は[専用管理オフライン経路](offline-evaluation.md)に限定する。AI順位の再現性は一次検索と区別する。

## 根拠・失敗・認可

AIには各候補の最初の安定順の一致断片を渡し、正本から再取得した本文・出典、関連職歴の役割・期間（未記載ならnull）を使う。タグ/Probabilityだけでは判定しない。一次カードでは他の一致断片も保持する。最初の断片に限定することによる順位損失は後続の品質評価対象。

UTF-8バイト数はトークン数の保守的な上界として扱い、Unicode文字の途中では切らない。根拠ごとの元/送信バイト数（省略は0）をAiRunとRetrievalRunへ記録する。要求全体には既存のAI入力/出力トークン上限と台帳を適用する。

入力資料は命令として扱わない。構造化応答の候補IDと根拠IDは送信集合・候補内に限定し、理由は送信した引用本文に実在する連続引用だけを採用する。自由な主張の意味一致を保証したとは扱わず、裏付けのない熟練度・年数等を表示しない。質問・手動語・根拠本文に資格情報を示す入力があれば送信前に拒否する。秘密を含む入力を本文ログへ出さない。未記名の文字列が秘密かを自動判定できることは保証せず、ダミー限定・ログイン情報入力禁止を画面に明示する。

| 状態 | 表示/動作 |
|---|---|
| succeeded | 根拠付き再順位付け。成功応答から省略された候補は不適合 |
| all_unsuitable | 一次候補はあったがAIの適合候補は0件 |
| no_candidates | 一次候補0件。AI呼出しなし |
| stopped / budget_stopped | 一次候補と「再順位付け未実施」 |
| refused / truncated | AI拒否/打切りを区別。一次候補へ戻す |
| ungrounded_output / not_performed:* | 不正ID・根拠不一致/障害。AIの主張を表示しない |
| source_revoked | 送信前/表示前の有効版・認可変化。無効候補とその理由を除外 |

送信時は既存AccessFenceとSourceGuardを使い、表示時も正本を再照合し、レスポンスのシリアライズまで撤回更新をフェンスで保護する。AI待機中の更新は表示前に再確認する。モデルの自動切替はない。

## Issue #11の検証対応

| Todoの要件 | 実装・検証 |
|---|---|
| 自然文、複数分類/技術要素、Noul、手動復帰、機密入力拒否 | NaturalSearchTestsの変換・unknown/失敗・資格情報拒否・queryのみ完結、検索E2E |
| OR/配列相関/独立認可、人物/事例の版付き枠、再現/監査 | 既存RetrievalTests/QueryTests/SourceGuardTests、NaturalSearchTestsの枠独占防止・固定一次ID・監査、生成監査契約検証 |
| 正本本文/役割/期間/出典、トークン上界/切詰め、出力/注入/拒否/0件/予算 | 既存AiTests/SourceGuardTests、NaturalSearchTestsのUnicode切詰め・捏造/根拠不一致・資料内命令・全件不適合/0件・状態別フォールバック |
| 表示前有効版/認可、停止表示、自然文UI/出典/未確認/段階 | NaturalSearchTestsの検索中撤回・member制限・履歴所有者分離、ApiTestsの本番認証、検索6シナリオE2E |
| API互換性/公開上限、fake AI E2E/ライブ評価分離 | v2 OpenAPIと実応答の契約検証、従来API/取得5,000単位・1,000RU/Limit 100の回帰、[オフライン評価](offline-evaluation.md)とIssue #14 |

2026-10-02：.NET 239件、Playwright 18件、契約14件、設定スクリプト41件、評価fixture4件、認証単体4件が成功。今回の試験はローカルfake AI/fixtureであり、ライブ品質の合格や実RU/費用の測定結果として扱わない。
