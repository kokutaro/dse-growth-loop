# Issue #33 query-only 検索から表示・共有までの回帰

2026-10-17。最新main `e61f7a3` を基点に、既存の認可境界試験を拡張した。Azureを停止したまま、実AI・実Cosmos・追加認証・台帳照合は行っていない。Issue #33全体の完了・実環境受入はpending。

## 今回の追加範囲

既存 `SearchBoundaryIntegrationTests` は条件指定済みの検索を主に検証していた。query-onlyの自然文から、実際の `NaturalSearchService` → `AiService` の分類codec → 投影取得・原本照合 → 再順位付けcodec/parser → 最終再照合 → HTTP応答の経路を追加した。transportだけはHTTPクライアントも資格情報も持たないインプロセスfake。Data storeは固定ダミーのMemoryで、設定のLive分岐は実サービスの利用を意味しない。

| 固定ケース | 確認する結果 |
| --- | --- |
| scope不一致、撤回、参加不可、旧版/building投影、旧根拠と複数職歴の混合集合 | query-onlyで分類・再順位付け各1回。禁止候補がAI入力と実HTTP応答に含まれず、正しい4entityと根拠だけが残る |
| 分類処理中のscope変更・撤回・参加不可・active/access版変更・根拠版/公開範囲変更（7ケース） | 分類入力は質問のみで原本・候補は0。変更された人物が次の再順位付け入力・表示候補・根拠から除外される |
| 分類完了後に原本読取が失敗 | 再順位付け送信0、応答consume0、成功retrieval監査0。IOExceptionを成功応答へ変えない |
| 検索候補から共有確定へ（既存6ケースを接続） | 正常候補を自然文検索サービスで選び直してから共有へ渡す。参加者のscope不足・撤回・session変更・根拠版/公開範囲変更で共有0。正常時の原子的保存・冪等拒否も維持 |

以前からある再順位付け中の撤回/版変更/根拠本文変更、架空候補ID・架空根拠ID、未送信の切詰め末尾引用、原本再照合障害の回帰も維持した。検索失敗・rerank失敗・根拠切詰め不整合は既存の別status/例外で識別する。変更はfake hookからMemory正本へ直接注入し、実サービスの分散競合を再現したとはしない。テスト集合は `tests/Dse.Tests/SearchBoundaryIntegrationTests.cs` の `FixedCatalog` と各Theoryに固定し、成功済み実AI質問の再送や新しい人承認goldは使わない。

## ローカル検証

- 対象回帰：`dotnet test tests/Dse.Tests --filter 'FullyQualifiedName~SearchBoundaryIntegrationTests|FullyQualifiedName~SharedCommitTests'`、30件成功。
- 全.NET回帰：`dotnet test tests/Dse.Tests --no-restore`、596件成功（HTTP統合追加後の最終実行）。
- `dotnet restore DseGrowthLoop.slnx --locked-mode` とRelease solution build成功。既存ASPIRE010警告1件。
- AGENTS.mdに従う `aspire run` の最初の試行は、既存MessagePack 2.5.302のNU1902により停止。[公式advisory](https://github.com/advisories/GHSA-qhrr-8q5h-9q3h)の修正版2.5.303をAppHostに固定しlockを更新した。監査を無効化せず、同更新後のローカルDemo/Mock API/WebはRunning・Healthyを確認した。Azureの稼働とは別である。

## 変更しない境界と残条件

認可・検索・AI・監視の本番ロジックは変更していない。初回検索の遅延はユーザーの06:06:41 UTCの判断で既知課題として残し、段階別計測・最適化・追加実測は行わない。Issue #30の正式warm p95未達とcold02の証拠制約をこのローカル回帰で上書きしない。

実Entra JWT・実Cosmos・複数replica・外部からの撤回/版競合の受入は未実施。必要な最小実試験の計画・承認は別途であり、今回のfake成功を実サービスの入力/表示/共有0の証明としない。#34は#30/#33、#35は#30〜#34に依存する。今回は#33の不足していたローカル経路を先に進め、追加有料実測や監視基盤改修は行わない。
