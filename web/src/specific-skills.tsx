export type SpecificSkills = {
  version: string;
  guardVersion?: string;
  rejectedItems?: { item: { raw: string; evidenceId: string; quote: string; participation: string }; reason: string }[];
  state: "extracted" | "unknown" | "not_evaluated";
  items: { raw: string; normalizationCandidate: string | null; evidenceId: string; quote: string; participation: string }[] | null;
};
const involvement: Record<string, string> = { performed: "本人実施", joint: "共同実施", assisted: "補助", consultation: "相談", aspiration: "希望", other_person: "他人の実績", negated: "否定", unclear: "関与不明" };
export function SkillDetails({ value }: { value?: SpecificSkills }) {
  return <details className="specific-skills"><summary>具体スキル・根拠と関与</summary>
    <p>分野の大分類や分類確率とは別の情報です。正規化候補は未確定で、熟練度を表しません。</p>
    {!value || value.state === "not_evaluated" ? <p>未評価（旧スキルIDは移行していません）</p> : value.state === "unknown" ? <p>情報不足（候補は未確定）</p> : value.items?.length === 0 ? <p>{value.rejectedItems?.length ? "候補はすべて保留中（該当なしの確定ではありません）" : "抽出済み・該当する具体スキル候補なし"}</p> : <ul>{value.items?.map((s, i) => <li key={i}>
      <strong>{s.raw}</strong> ／ 正規化候補：{s.normalizationCandidate ?? "不明"} ／ {involvement[s.participation] ?? "関与不明"}
      <p>{["performed", "joint", "assisted"].includes(s.participation) ? "経験タグの対象（本人未確認）" : "本人の経験タグには採用しません"}</p>
      <blockquote>{s.quote}<cite>根拠ID：{s.evidenceId}</cite></blockquote>
    </li>)}</ul>}
    {!!value?.rejectedItems?.length && <div className="quarantined-skills"><p>確認が必要な候補：{value.rejectedItems.length}件。本人の経験タグ・検索には採用しません。</p><ul>{value.rejectedItems.map((r, i) => <li key={i}>
      <span>{r.item.raw}</span> ／ 保留理由：{r.reason}<blockquote>{r.item.quote}<cite>根拠ID：{r.item.evidenceId}</cite></blockquote>
    </li>)}</ul></div>}
  </details>;
}
