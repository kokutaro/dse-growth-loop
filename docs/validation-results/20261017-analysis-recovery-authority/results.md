# 事業分析オフライン復旧：正本認可・競合回帰

2026-10-17。基点main `2f780b1c0921aed775d57bf6cd5c908a4a68ebfb`。Issue #15/#16の残受入を既存コードと突合し、既存復旧serviceの認可・競合境界を優先する。

## 問題と修正

既存WorkerはCLI入口で現在の正本を照合するが、復旧service自身は渡されたroleに依存していた。入口後の長い照会中に管理者が失効しても、inspect出力や復旧を進められた。access fenceが空であることも読取だけで、検査後に別処理が取得した場合や認可正本が変わった場合に復旧batchを拒否できなかった。

修正前に、偽operator role、inspect/recover走査中の失効、最終検査後のcontrol/person/fence変更の6ケースで、拒否されず処理が進むことを再現した。

停止証拠digestもCLI/serviceの2ケースで末尾改行を許していたことを再現し、文字列全体が64桁hexであることを要求するよう修正した。これは形式検査の強化で、停止証拠の人による確認を代行しない。

既存のBusinessAnalysisRecoveryへ、正本/設定/actorのoperator・analyst・principal・scope・accessEpoch照合を追加した。出力・receipt返却・保存前に再検査し、同一catalog transactionのcontrol/person/fence CASで検査後の変更も拒否する。guard行は意味上同じ値を保存するため権限や同意tokenは変更せず、ETagのみ進む。read-only経路ではETagも維持する。

既存の対象run/slot/pending予約照合、他run保護、uncertain全額留保、停止証拠、冪等receiptを維持する。新規復旧API、権限追加、自動provider再送、fenceの強制解放を追加しない。

## 検証

最終ローカル.NET **705/705** 成功（追加回帰28件、既存LargeEvaluation fixture1件を含む）。Worker Release buildは警告0・エラー0。`git diff --check` 成功。fixtureは復旧対象とは別の専用dummy-operatorを明示的に正本へ登録し、渡すroleだけで管理者を作る旧テスト入力を廃止した。Azureモードの確認もLocalRecordStoreと偽輸送であり、実Azure認可の確認ではない。

[集計JSON](summary.json)、[対象ソースhash](source-hashes.json)、[全.NETログ](dotnet-tests.log)、[修正前の6件再現](authority-race-before-fix.log)、[digest境界の2件再現](digest-before-fix.log)を保存した。旧ログを今回の成功件数へ加算しない。

追加回帰は現在grantの欠落、失効→再有効化後の旧epoch、receipt取得中の失効、Azure binding不一致、fence欠損/不正/保持中、guard値の維持、inspect/receiptの無書込を含む。競合時はrun/slot/対象・他予約/共有gate/当日使用量のETagと内容が変わらないことを確認する。

## 受入の照合とClose判定

| 対象 | コード・ローカルで確認できる範囲 | 残る受入 |
| --- | --- | --- |
| #15 分析/冪等/撤回/集計/判断 | 既存BusinessAnalysisTests、Manual Job計画・CLI試験、今回の復旧回帰を再利用 | 実事業分析・実Job/復旧、共通課題/仮説のライブ品質、人の安全性・業務判断 |
| #16 Job/障害/復旧準備 | 既存IngestionTestsの通知漏れ/reconcile、リース切れ、poison/明示retry、今回の復旧中認可競合 | 先行Issueの合否、全体実E2E、実JWT/Cosmos競合、隔離復元、配置/停止/運用受入 |

前段のmain2f780b1の実AI3操作報告は汎用接続smokeであり、BusinessAnalysisLiveDrafterや実分析の受入ではない。人の判断、新しい分析同意、管理者へのanalyst付与、残り92名の有効化、旧同意移行を代行しない。

今回のAzure操作・有料AI・恒久image展開・削除は0回。初回検索遅延/p95と費用/試行作業台帳は対象外。全条件未達のため #15/#16をCloseせず、コード・ローカル完了部分と残条件をIssueへ証拠付きで反映する。
