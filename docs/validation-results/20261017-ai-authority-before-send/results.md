# 共通AI実行器：送信前の設定照合と残Issue照合

2026-10-17、基点main `46f2ecacdafe6d9f60b30102874a75e90527ec57`。#1/#14/#30〜#35の最新GitHub本文と実装・保存証跡を照合した。無理に新しい実測や採用判断を作らず、#33に関連する独立したコード不具合を修正する。

## 再現と変更

AiServiceの入口と会話照合は設定なしのAuthorityProviderを使っていた。根拠なしの質問分類では、SourceGuardによる送信前根拠検査を通らない。現在設定だけが正本のrole/scope/principal/OID/enabledと不一致になった場合、設定付きSourceGuardが最終的に拒否しても、新規HTTP送信とAiRun/使用量更新が既に行われていた。キャッシュ経路もAiRunを作成してから拒否していた。

明示Azure/既定Azure ×5不一致 ×新規/キャッシュの20ケースで修正前の失敗を再現した。新規10ケースは偽HTTP1回、キャッシュ10ケースは本文返却前の保存状態変更で失敗した。テスト用の整合した正本・設定からactorを取得し、設定だけを変更するため、偽actorの入力検証試験とは異なる。

設定付きAuthorityProviderをAI実行器に保持し、tenant fence内で入力組立・AiRun作成・予約・HTTP送信・キャッシュ参照より前に照合する。会話照合も同じProviderを利用する。SourceGuardの送信後再照合、既存の使用量予約/精算、失敗留保、回数上限・キャッシュ規則は再利用する。

## 検証

- 全.NET **746/746**（追加20回帰、LargeEvaluation fixture含む）。
- 関連Python **40/40**：offline-quality14、保存観測15、採用パケット11。実品質測定ではなく既存オフライン回帰の再実行。
- Worker Release build警告0/エラー0、git diff --check成功。
- 20拒否ケースで `AUTHORITY_REVOKED`、追加HTTP0、AiRun/使用量day/gate/予約/認可正本の内容・ETag変更0を確認。正常なキャッシュ生成を先に成功させるケースも含む。tenant fenceの取得/解放ETagはこの変更0の対象外。

[集計](summary.json)、[ソースhash](source-hashes.json)、[修正前20ケース](before-fix.log)、[全.NET](dotnet-tests.log)、[Python](python-tests.log)、[Worker](worker-build.log)。

## 残Issueの受入照合

| Issue | 既存実装・証拠 | 残る条件 |
| --- | --- | --- |
| #30 | 承認済み独立v3のサービス一次Recall/指定救済の保存結果、HTTP観測要約 | HTTP/UIを含む総合受入pending。warm未達とcold02欠落を維持。初回遅延/p95追加測定・最適化は保留 |
| #31 | 同候補hashの保存再順位応答再生、逆順誤推薦・固定障害回帰 | 保存済み条件は1組。別モデル/prompt/根拠長/確率提示の事前固定した少数比較・独立確認が未実施。過去正常応答を再送しない |
| #32 | 8項目scorer、原文hash付きレビュー草案120record（tuning60/final60） | 全ラベルunreviewed・承認pending、再利用可能な8項目構造化予測なし。人の項目別正解確認後に条件固定と必要最小実評価 |
| #33 | 原本/根拠・query-only/再順位/表示/共有のローカル回帰、本修正20件 | 実JWT/実Cosmos・競合の統合受入は未実施。今回の偽HTTPを実送信の安全性証明に置き換えない |
| #34 | 操作別の部分collector、保存観測のschema変換 | 同run操作別RU・全AI・保存/索引/runtime/総費用等は不明。保存観測変換を新規実測としない。費用/試行の作業台帳を再導入しない |
| #35 | 候補hash・保存資料・未達を束ねる未採用パケット | 子の合否・有効配置設定・総月額・人の採用判断が残る。productionApplicationAllowed=falseを維持 |
| #14 | 親の元Todo4/12と子Issue対応表、品質/認可/計測の保存基盤 | 子ゲートと複合受入が未達。延期を制限の明示受入へ読み替えない |
| #1 | 段階ゲート/子の実装証跡。#15/#16も部分完了 | #14/#15/#16等の残条件と利用者の最終受入。親を自動完了しない |

次に追加の実環境変更なしで進める最小の人手作業は、#32の既存レビュー草案で項目別goldを人が作成・確認すること。空欄の一括承認や検索関連度ラベルの流用ではない。対象はdescription/start/end/precision/months/skills/domain/evidenceIdsで、適用不能・不明と分母/同義表現を明示する。新規推論承認や本番採用承認を同時に求めるものではない。

#1/#14/#30〜#35は全条件が満たされないためOpenを維持する。この局所修正を品質ゲートの合格へ昇格しない。今回のAzure操作・外部モデル送信・新規実権限/同意・RBAC/ネットワーク変更・恒久image展開・削除は0回。既存Azure停止状態を変更していない。

