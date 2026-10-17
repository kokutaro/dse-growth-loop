# Azureダミー正本限定初期化CLIの検証

2026-10-17。既存ダミー人物93件があり、authorityControl/department/personAuthorityが0件の環境を対象とする。今回の明示承認は、既存データを保持し、control1・架空部署2・demo-person-01のpersonAuthority1と必要な監査/冪等管理を追加し、少量実AIを再試行する範囲である。実AzureのRBAC・Entra・ネットワーク変更、他人物の有効化、旧参加資格/同意の移行は含まない。

## ローカル検証

- .NET 677/677成功。既存647件と新規限定初期化30件を含む。LargeEvaluationのfixture試験を1件含むが、実AI品質・速度の測定ではない。
- 93人の旧ダミーprofileを持つstoreでplanが無変更であること、applyが対象1名だけを登録し旧行の内容/ETagを保つことを確認。他92名、旧knowledge token、旧Sessionの参加pinは許可しない。
- 停止/承認/設定一致/Mock/MI/接続先の前提が欠けた場合、credentialやstorage生成前に拒否。
- 所有者・scope・有効状態の不一致、profile ETag/承認入力変更後の古いplan、既存正本の存在を拒否。
- 同じキーの再試行は無変更。失効後の再実行や別キーによるリセットを拒否。transaction応答不明時はreceiptで既存結果を確認し、generationを再生成しない。不完全receiptも成功扱いしない。
- Copilot指摘の日時JSON表現差を、保存JSONへ正規化してからhash化するよう修正。JSON保存/再読込を模した2ケースで修正前の誤拒否を再現し、通常再試行・commit応答不明後の再試行とも無変更で確認できることを回帰試験した。

## 実環境との境界

CLI出力はplanHash・固定dummy ID・件数・結果コードに限定し、OID/principal/本文/資格情報/SDK例外本文を出さない。実行時はMI専用で、AIやKeyVaultを登録しない。停止やAPI/Worker/Job設定の一致は作業者がARMから確認するもので、CLIがAzure管理状態を自動検知するとは主張しない。

本記録は初期化CLIのローカル検証である。Azureへの投入、実AI接続・分類・検索/rerankの成否、終了状態は実行後の別記録で報告する。Issue15全体の実環境受入や人による安全性レビューを完了としない。
