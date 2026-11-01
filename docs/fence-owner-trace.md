# Fence取得元の追跡

## 概要

`ownerTrace`（`fence-owner-v1`）を既存fenceの取得・解放commitに含める。追加の取得権限、TTL解除、書込み回数、予算枠は導入しない。診断文脈は認可の証明・代替ではない。

## 記録と確認

- APIは認可済みのサーバールート型と操作相関ID、主体・scope集合のSHA256だけを記録する。実URL、クエリ、本文、メール、認証情報、例外メッセージは記録しない。
- Workerは既存`PipelineDiagnostics`のcorrelationId、Job／aggregate、AI操作を使い、保存Jobのowner／scopeのハッシュを付ける。現在の認可確認は従来の製品処理で行う。
- 取得時刻・解放時刻・保持時間と`acquired`／`released`を記録する。直近の所有元だけを保持し、履歴を無制限に増やさない。
- 結果不明・取消・release結果不明は`FenceLifecycle`警告に相関情報を残す。未確定状態を保存する追加書込みは行わず、永続fenceは従来どおり安全側で閉じる。
- 通常の取得・解放イベントはInformation。現在の本番Warning設定では全履歴を採取しない。永続ownerと不明結果のWarningは利用できるが、完全な時系列計測の配備・設定判断は別途必要。
- 正式なオフライン復旧は既存の停止・receipt照合・期待ETagを必須とし、既存ownerがあれば`recovered_offline`を付ける。過去のholderに所有元を捏造しない。

## 互換性と限界

旧owner情報のない記録も従来どおり拒否／復旧する。ネストした処理は外側のownerを上書きせず、競合writerも取得済みownerを変更しない。新計装はローカルのみで、本番への配備は未実施。過去の残留原因をこの変更で確定したとは扱わない。
