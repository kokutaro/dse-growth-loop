# 保存済み分析：分析主体の設定整合回帰

2026-10-17。基点main `528e11793081ac6386061e94d50ba5aae89f6c91`。#15/#16の既存分析serviceを照合し、復旧修正に続いて保存結果・根拠・判断の認可不足を修正する。

## 再現と修正

既存の保存結果/根拠/判断の検査では、AuthorityProviderに設定を渡さない呼出しが残っていた。分析主体が資料の同意者も兼ねると参加者の設定検査で遮断されるため、主体だけの設定不一致を見落とした。資料の同意者とは別の認可済みdummy分析主体を作り、role・scope・principal・OID・enabledを設定だけで不一致にすると、結果・根拠・新しい判断・既存判断receiptの20ケースすべてで拒否されず利用できた。

serviceに設定付きAuthorityProviderを保持し、開始・現行性検査・結果消費を統一した。根拠取得と判断も、保存行を読む前に同じ正本照合を行う。正本のgrantと現在設定に不一致があれば `AUTHORITY_REVOKED` で拒否する。正本に存在しない偽principalも先に拒否し、正当な別所有者には引き続き `NOT_FOUND` を返す。

## ローカル検証

全.NET **726/726** 成功（追加21回帰、既存LargeEvaluation fixture1件を含む）。Worker Release buildは警告0・エラー0、`git diff --check` も成功。LocalRecordStore・偽IAiTransportを使用し、実HTTP/JWT/実Azure認可の受入ではない。dummy分析主体は試験fixtureだけに明示登録し、通常Demoの主体やクラウドの残り92名は有効化しない。

- 5種類の設定不一致×結果/根拠/判断/receiptの4経路で、本文消費callbackが0回、保存run/判断receipt/認可正本の内容・ETagが無変更、追加のモデル送信0であることを確認する。
- 設定を元へ戻すと、資料の同意状態を変えず同じ保存結果を取得できることを確認する。これは設定不一致の拒否であり、失効した正本のepochを復活させる処理ではない。
- 正当な別分析者が他所有者の結果・根拠を消費せず、判断も保存できないことを確認する。

[集計](summary.json)、[ソースhash](source-hashes.json)、[全.NETログ](dotnet-tests.log)、[修正前20ケース](readers-before-fix.log)を保存する。

## 残る受入

コードとローカル回帰を完了しても、実BusinessAnalysisLiveDrafter/Manual Job・復旧、認可/同意の実運用、人による安全レビュー・業務判断、#16の先行Issue/全体E2E/隔離復元は未完了のまま。#15/#16はOpenを維持する。

この追補のAzure操作・外部モデル送信・新規認可/同意・RBAC/ネットワーク変更・恒久image展開・削除は0回。初回検索遅延/p95や費用/試行作業台帳は扱わない。
