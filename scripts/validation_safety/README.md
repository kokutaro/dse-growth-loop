# 移行時helperの出典保存

移行時の `post-pilot-stop-watchdog.py` と `queued-worker-stop-watchdog.py` の原本バイトは、`provenance.json` の `sourceText` に変更せず保存した。`sourceSha256` と元パスを記録し、UTF-8バイトへ戻したSHAが元の値と一致することを回帰で確認する。過去ZIPとMac上の移行原本も置き換えない。

このディレクトリの `.py` は実行を拒否するstubへ置き換えた。引数なしではARCHIVED情報だけを返し、`--execute-after-approval` を含む引数付き呼出しはexit2、Azure呼出し0、stopConfirmed=false。`entryPointSha256` はこの無効化入口のhash。原本には空の再取得revisionを停止成功にする既知の問題があり、原本データを復元・実行してはならない。原本を読み込む回帰はすべてMock専用で、運用経路ではない。

Issue30の新しい停止経路は `../issue30_safety_supervisor.py`。旧helperの75秒は外側ループの判定値であり、全CLI/revision処理の総期限ではない。新経路は親process監視、signal、単調時計の共通期限、残り時間を渡したCLI timeout、全revision/実replica確認を備える。既定plan-only。実Azure実行には別途承認済みの範囲/時間/費用が必要。

Mac sleep、host電源断、SIGKILL、Azure/認証の不通では停止を保証できない。別hostの停止担当が必要。helperは復元/起動/AI呼出しを行わない。
