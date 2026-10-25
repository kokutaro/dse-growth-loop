export type SkillElement = { label: string; action: string | null; object: string | null; conditions: string[]; kind: string; subjectScope: string; participation: string; conceptId: string | null; conceptState: string; slices: { text: string; quoteOffset: number; length: number }[] };
export type SkillAnalysis = { version: string; quoteState: string; semanticSupport: string; experienceState: string; subjectScope: string; kind: string; reason: string | null; elements: SkillElement[] };
export type SpecificSkills = {
  version: string;
  guardVersion?: string;
  rejectedItems?: { item: { raw: string; evidenceId: string; quote: string; participation: string }; reason: string }[];
  state: "extracted" | "unknown" | "not_evaluated";
  items: { raw: string; normalizationCandidate: string | null; evidenceId: string; quote: string; participation: string; analysis?: SkillAnalysis }[] | null;
};
const involvement: Record<string, string> = { performed: "本人実施", joint: "共同実施", assisted: "補助", consultation: "相談", aspiration: "希望", other_person: "他人の実績", negated: "否定", unclear: "関与不明" };
export function SkillDetails({ value }: { value?: SpecificSkills }) {
  return <details className="specific-skills"><summary>具体スキル・根拠と関与</summary>
    <p>分野の大分類や分類確率とは別の情報です。正規化候補は未確定で、熟練度を表しません。</p>
    {!value || value.state === "not_evaluated" ? <p>未評価（旧スキルIDは移行していません）</p> : value.state === "unknown" ? <p>情報不足（候補は未確定）</p> : value.items?.length === 0 ? <p>{value.rejectedItems?.length ? "候補はすべて保留中（該当なしの確定ではありません）" : "抽出済み・該当する具体スキル候補なし"}</p> : <ul>{value.items?.map((s, i) => <li key={i}>
      <strong>{s.raw}</strong> ／ 正規化候補：{s.normalizationCandidate ?? "不明"} ／ {involvement[s.participation] ?? "関与不明"}
      <p>{s.analysis ? s.analysis.experienceState === "eligible" ? "経験タグの対象（本人未確認）" : ["content_only","non_practice"].includes(s.analysis.experienceState) ? "知識・資格の内容候補。本人の経験タグには採用しません" : "本人関与は保留。経験タグには採用しません" : ["performed", "joint", "assisted"].includes(s.participation) ? "経験タグの対象（本人未確認）" : "本人の経験タグには採用しません"}</p>
      {s.analysis && <><p>引用整合性：{s.analysis.quoteState} ／ 意味支持：{s.analysis.semanticSupport} ／ 種別：{s.analysis.kind}</p>
        <ul>{s.analysis.elements.map((e,j)=><li key={j}>技能要素：{e.label} ／ 条件：{e.conditions.join("、") || "明記なし"} ／ 共通概念：{e.conceptId ?? "未対応"}（{e.conceptState}）</li>)}</ul></>}
      <blockquote>{s.quote}<cite>根拠ID：{s.evidenceId}</cite></blockquote>
    </li>)}</ul>}
    {!!value?.rejectedItems?.length && <div className="quarantined-skills"><p>確認が必要な候補：{value.rejectedItems.length}件。本人の経験タグ・検索には採用しません。</p><ul>{value.rejectedItems.map((r, i) => <li key={i}>
      <span>{r.item.raw}</span> ／ 保留理由：{r.reason}<blockquote>{r.item.quote}<cite>根拠ID：{r.item.evidenceId}</cite></blockquote>
    </li>)}</ul></div>}
  </details>;
}
