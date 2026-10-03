# Issue 30 保存応答比較

[比較計画](replay-plan.json)に従い、既存の承認済み架空データと実Jev応答を再生する。AI送信・Azure変更・本番適用は行わない。

```sh
dotnet run --project src/Dse.Evaluation -- --issue30-replay .artifacts/issue30-replay-new
```

出力先は新しい空の場所を指定する。既に観察済みの確認集合を使うため、出力は独立品質評価ではなく回帰結果として扱う。

[結果・制限・再現手順](../../docs/validation-results/20261003-issue-30-fragment-replay/results.md)を参照。副業務の断片追加は、同じ分割の保存リクエスト全体に完全一致する原本引用だけで実施する。一致応答がない場合は停止し、人工的な分布を作らない。

## 実分類後の事例上限比較

[事例上限計画](case-limit-plan.json)で人物10・救済2を固定し、事例10・15・20件を比較する。新しい推論は行わず、前回の生Jev応答と採点結果を再検証して候補を再生成する。

```sh
dotnet run --project src/Dse.Evaluation -- --issue30-case-limits .artifacts/issue30-case-limits-new
```

[結果と測定の範囲](../../docs/validation-results/20261003-issue-30-case-limits/results.md)を参照。本番設定の変更は含まない。

## 変更候補3問の実LLM評価

事例上限15件で候補hashが変わった3問の後段LLMを評価した。[固定計画](llm/README.md)と[実測結果](../../docs/validation-results/20261003-issue-30-llm/results.md)を参照。再採点は追加送信なし。

```sh
python3 scripts/issue30_llm_evaluation.py score --output .artifacts/issue30-llm-new
```

## 固定48問全体の実LLM回帰

人物10／事例15で全48問・3条件を比較した。[事前計画と再利用](full-llm/README.md)、[全体結果](../../docs/validation-results/20261003-issue-30-full-llm/results.md)を参照。

```sh
python3 scripts/issue30_full_llm_evaluation.py score --output .artifacts/issue30-full-llm-new
```
