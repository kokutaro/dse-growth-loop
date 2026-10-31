export type SkillElement = { label: string; action: string | null; object: string | null; conditions: string[]; kind: string; subjectScope: string; participation?: string; basis?: string; conceptId: string | null; conceptState: string; slices: { text: string; quoteOffset: number; length: number }[] };
export type SkillAnalysis = { version: string; quoteState: string; semanticSupport: string; experienceState: string; subjectScope: string; kind: string; reason: string | null; elements: SkillElement[] };
export type SpecificSkills = {
  version: string;
  guardVersion?: string;
  rejectedItems?: { item: { raw: string; evidenceId: string; quote: string; participation?: string; basis?: string }; reason: string }[];
  state: "extracted" | "unknown" | "not_evaluated";
  items: { raw: string; normalizationCandidate: string | null; evidenceId: string; quote: string; participation?: string; basis?: string; analysis?: SkillAnalysis }[] | null;
};
const contextNames: Record<string,string> = { experience: "経験として抽出", knowledge: "知識・学習", qualification: "資格", not_experience: "未実施・希望等", uncertain: "文脈未確定" };
export function SkillDetails({ value }: { value?: SpecificSkills }) {
  return <details className="specific-skills"><summary>具体スキル・原文と文脈</summary>
    <p>分野の大分類や分類確率とは別の情報です。正規化候補は未確定で、熟練度を表しません。</p>
    {!value || value.state === "not_evaluated" ? <p>未評価（旧スキルIDは移行していません）</p> : value.state === "unknown" ? <p>情報不足（候補は未確定）</p> : value.items?.length === 0 ? <p>{value.rejectedItems?.length ? "候補はすべて保留中（該当なしの確定ではありません）" : "抽出済み・該当する具体スキル候補なし"}</p> : <ul>{value.items?.map((s, i) => <li key={i}>
      <strong>{s.raw}</strong> ／ 正規化候補：{s.normalizationCandidate ?? "不明"} ／ {contextNames[s.basis ?? ""] ?? "旧版の抽出（新方式で未評価）"}
      <p>{!s.basis ? "旧版の候補です。新方式での抽出前に経験認定へ変更しません" : s.analysis?.experienceState === "eligible" ? "経験内容の候補（本人未確認）" : "知識・未実施等の文脈を保持。経験タグには採用しません"}</p>
      {s.analysis && <><p>引用整合性：{s.analysis.quoteState} ／ 意味支持：{s.analysis.semanticSupport} ／ 種別：{s.analysis.kind}</p>
        <ul>{s.analysis.elements.map((e,j)=><li key={j}>技能要素：{e.label} ／ 条件：{e.conditions.join("、") || "原文引用で確認（自動分解なし）"} ／ 共通概念：{e.conceptId ?? "未対応"}（{e.conceptState}）</li>)}</ul></>}
      <blockquote>{s.quote}<cite>根拠ID：{s.evidenceId}</cite></blockquote>
    </li>)}</ul>}
    {!!value?.rejectedItems?.length && <div className="quarantined-skills"><p>確認が必要な候補：{value.rejectedItems.length}件。本人の経験タグ・検索には採用しません。</p><ul>{value.rejectedItems.map((r, i) => <li key={i}>
      <span>{r.item.raw}</span> ／ 保留理由：{r.reason}<blockquote>{r.item.quote}<cite>根拠ID：{r.item.evidenceId}</cite></blockquote>
    </li>)}</ul></div>}
  </details>;
}
