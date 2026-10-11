# 移行時helperの出典保存

`post-pilot-stop-watchdog.py` と `queued-worker-stop-watchdog.py` は移行時の原本を変更せず保存した。原本パス/SHA-256は `provenance.json` に記録し、回帰で一致を確認する。既存成果物を置き換えない。

Issue30の新しい停止経路は `../issue30_safety_supervisor.py`。旧helperの75秒は外側ループの判定値であり、全CLI/revision処理の総期限ではない。新経路は親process監視、signal、単調時計の共通期限、残り時間を渡したCLI timeout、全revision/実replica確認を備える。既定plan-only。実Azure実行には別途承認済みの範囲/時間/費用が必要。

Mac sleep、host電源断、SIGKILL、Azure/認証の不通では停止を保証できない。別hostの停止担当が必要。helperは復元/起動/AI呼出しを行わない。
