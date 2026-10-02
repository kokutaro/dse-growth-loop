# Issue #13 ローカル検証結果

実行日：2026-10-03。全件ダミー、fake AI/Mock transportのみ。Azure展開・有料推論は実施していない。過去のIssue #9の結果を再実行結果として扱わない。[集計](report.json)、[仕様と復旧手順](../../chat-assistance.md)。

## 実行結果

| 確認 | 結果 |
|---|---|
| locked restore / solution build | 成功（既存ASPIRE010警告あり） |
| .NET（LargeEvaluation除外） | 279件成功、失敗・skipなし |
| 最終変更後のChat追加範囲 | 22件成功 |
| Playwright（各suiteを独立APIで実行） | 21件成功：chat-assistance 1 / conversations 5 / knowledge 1 / documents 4 / profiles 3 / search 6 / logout 1 |
| JSON Schema/OpenAPI契約 | 17件成功。実際に保存したAI Message・Session・Job・Feedbackを検証 |
| 設定・Bicep/スクリプト | 41件成功。Bicepローカルコンパイル成功、展開なし |
| 評価fixture | 4件成功 |
| auth unit | 4件成功 |
| frontend build / npm ci | 成功。既存の500 kB chunk警告あり。npm audit 0 vulnerabilities |
| 新規ChatAccess / ChatAssistanceService行coverage | 100% / 99.21%（追加22件のCoverletから集計） |
| git diff --check / 相対文書リンク | 成功 |

## 自動支援の確認範囲

- 同一イベントの再配送・プロセス再生成・Queue11配送でAI投稿は1件。AI自身の投稿にJobを作らない。急速連投は最後の人間投稿へまとめる。
- AI成功後のDB保存前中断と保存後の応答消失は、停止を模擬したfence回復後に同じ一意Messageへ収束する。日次上限1件の予約済み枠も再利用できる。
- 通知前クラッシュはDB Jobのreconcileで回復。Queue timeout後も人間投稿APIは保存済みを返す。
- 未知/不要/AI停止/予算超過/再順位付け失敗/クールダウン/上限では自動投稿しない。固定の不要20例はローカルgateによる誤投稿0件、AI呼出0件。
- 参加者変更後の結果を保存しない。実AiService＋Mock試験で、参加者変更により旧cacheを拒否し、他参加者が閲覧できない事例はAI入力前に拒否（AiRun生成0）する。
- 公開撤回前は再順位付け入力0、再順位付け中に撤回した事例は投稿0。履歴に保存した本文・出典も再読取時に非表示化する。
- UIでON/OFF、事例本文・有効版・引用出典、役立った/不要、相談画面を閉じて開き直した後の履歴を確認。

## 再現コマンド

```sh
dotnet restore --locked-mode -m:1
dotnet build --no-restore -m:1
dotnet test tests/Dse.Tests --filter 'Category!=LargeEvaluation' --collect:'XPlat Code Coverage' -m:1
dotnet test tests/Dse.Tests --filter 'FullyQualifiedName~Chat' --collect:'XPlat Code Coverage' -m:1
python3 -m unittest discover -s tests/contracts
python3 -m unittest discover -s tests/scripts
python3 -m unittest discover -s tests/evaluation
python3 scripts/deploy.py validate
npm --prefix web ci
npm --prefix web run build
npm --prefix web run test:auth
cd web
npm run test:e2e -- tests/chat-assistance.spec.ts --workers=1
```

契約試験は `tests/contracts/requirements.txt` の依存を入れたPythonで実行する。今回は既存 `.artifacts/issue-9-contract-venv/bin/python` を使用した。全UI suiteはCIと同じくそれぞれ新しいAPI/Viteプロセスで実行した。テストの書込回数制限を変更していない。

## 未実施

ライブAIの「不要20例で誤投稿2例以下」、稼働中の15秒目標、Azureコールド起動は未評価。fixtureの20例/0件、E2E所要時間を実AI品質・投稿待ち時間の実測値へ読み替えない。Issue #1の別承認境界に従うため、Issue #13の該当2項目は未チェックのまま残す。
