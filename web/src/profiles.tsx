import { SkillDetails, type SpecificSkills } from "./specific-skills";
import { useEffect, useRef, useState } from "react";
import { authSessionSignal, getToken, type AppConfig } from "./auth";

type Period = { start: string | null; end: string | null; precision: string; months: number | null };
type Decisions = { choices: Record<string, { probabilities: Record<string, number>; confidence: number }>; nouls: Record<string, { state: number; yesProbability: number | null }> };
type Experience = { workbookSectionKind?: "project" | "context"; id: string; documentId: string; description: string; period: Period; confirmationState: string; correctionState: string; pendingExtraction?: { description: string }; evidenceRefs: { id: string }[]; decisions?: Decisions; specificSkills?: SpecificSkills };
type Person = { id: string; displayName: string; available: boolean; publicationState?: string; confirmationState: string; sourceVersion: number; activeVersion: number | null; projectionJobId?: string; totalExperienceMonths?: number | null };
const isCalendarMonth = (period: Period) => period.precision === "month" && (!period.start || period.start.length === 7) && (!period.end || period.end.length === 7);
export function Profiles({ config, person: demoPerson }: { config: AppConfig; person?: string }) {
  const [person, setPerson] = useState<Person>(); const [etag, setEtag] = useState("");
  const [experiences, setExperiences] = useState<Experience[]>([]); const [changed, setChanged] = useState<Set<string>>(new Set());
  const [message, setMessage] = useState(""); const [error, setError] = useState(""); const [busy, setBusy] = useState(false);
  const lifetime = useRef(new AbortController());
  useEffect(() => {
    const controller = new AbortController();
    lifetime.current = controller;
    setPerson(undefined); setEtag(""); setExperiences([]); setChanged(new Set()); setBusy(false); setError(""); setMessage("");
    return () => controller.abort();
  }, [config.mode, config.tenantId, config.clientId, config.scope, demoPerson]);
  const currentSignal = () => AbortSignal.any([lifetime.current.signal, authSessionSignal()]);
  async function request(path: string, signal: AbortSignal, init?: RequestInit) {
    signal.throwIfAborted();
    const headers = new Headers(init?.headers);
    if (config.mode === "Demo" && demoPerson) headers.set("X-Demo-Person", demoPerson);
    if (config.mode === "Azure") headers.set("Authorization", `Bearer ${await getToken(config)}`);
    for (let attempt = 0; ; attempt++) {
      signal.throwIfAborted();
      const response = await fetch(path, { ...init, headers, signal });
      signal.throwIfAborted();
      if (response.ok) return response;
      const body = await response.json().catch(() => ({}));
      // ACCESS_BUSY rejects before the access fence is acquired. Reuse the same
      // request and idempotency key for writes; never retry a stale If-Match (412).
      if (response.status === 503 && body.code === "ACCESS_BUSY" && body.retryable === true && attempt < 4) {
        await new Promise<void>((resolve, reject) => {
          signal.throwIfAborted();
          const cancel = () => { clearTimeout(timer); reject(signal.reason); };
          const timer = setTimeout(() => { signal.removeEventListener("abort", cancel); resolve(); }, 250 * 2 ** attempt);
          signal.addEventListener("abort", cancel, { once: true });
        });
        continue;
      }
      throw new Error(response.status === 412 ? "別の更新があります。プロフィールを更新してから訂正し直してください。" : `${body.code ?? "接続エラー"}（${response.status}）`);
    }
  }
  async function load(signal = currentSignal()) {
    setBusy(true); setError("");
    try {
      const context = await (await request("/api/v1/upload-context", signal)).json();
      const response = await request(`/api/v1/people/${context.personId}`, signal);
      const value: Person = await response.json();
      const all: Experience[] = []; let cursor: string | null = null;
      do {
        const page = await (await request(`/api/v1/people/${value.id}/experiences?pageSize=100${cursor ? `&continuationToken=${encodeURIComponent(cursor)}` : ""}`, signal)).json();
        all.push(...page.items); cursor = page.continuationToken;
      } while (cursor);
      signal.throwIfAborted();
      setPerson(value); setEtag(response.headers.get("ETag") ?? ""); setExperiences(all); setChanged(new Set());
    } catch (e) { if (!signal.aborted) setError(String(e)); } finally { if (!signal.aborted) setBusy(false); }
  }
  function edit(id: string, update: Partial<Experience>) {
    setExperiences(rows => rows.map(row => row.id === id ? { ...row, ...update } : row)); setChanged(old => new Set([...old, id]));
  }
  function periodDate(row: Experience, key: "start" | "end", value: string) {
    const period = { ...row.period, [key]: value || null };
    const calendarMonth = isCalendarMonth(row.period);
    period.precision = calendarMonth ? "month" : period.start ? "day" : "unknown";
    if (period.start && period.end) {
      const start = new Date(period.start), end = new Date(period.end);
      period.months = (end.getUTCFullYear() - start.getUTCFullYear()) * 12 + end.getUTCMonth() - start.getUTCMonth() + (calendarMonth ? 1 : -(end.getUTCDate() < start.getUTCDate() ? 1 : 0));
    } else period.months = null;
    edit(row.id, { period });
  }
  async function save(action: "correct" | "confirm", publicationState = person?.publicationState ?? "published") {
    if (!person) return;
    const signal = currentSignal();
    setBusy(true); setError(""); setMessage("");
    try {
      const response = await request(`/api/v1/people/${person.id}`, signal, { method: "PATCH", headers: { "Content-Type": "application/json", "If-Match": etag, "Idempotency-Key": crypto.randomUUID() }, body: JSON.stringify({
        displayName: person.displayName, available: person.available, action, publicationState,
        experienceEdits: experiences.filter(e => changed.has(e.id) || action === "confirm").map(e => ({ experienceId: e.id, description: e.description, period: e.period })),
      }) });
      const value: Person = await response.json(); signal.throwIfAborted();
      setPerson(value); setEtag(response.headers.get("ETag") ?? ""); setChanged(new Set());
      setMessage(publicationState === "withdrawn" ? "公開を撤回しました。検索から除外しました。" : "保存しました。検索投影の更新後に検索へ反映されます。");
      await load(signal);
    } catch (e) { if (!signal.aborted) setError(String(e)); } finally { if (!signal.aborted) setBusy(false); }
  }
  return <section className="panel profiles" aria-labelledby="profiles-title">
    <h2 id="profiles-title">自分のプロフィール・経験</h2>
    <p>根拠のあるAI抽出値は本人未確認でも検索対象です。訂正は再取込より優先されます。</p>
    <button disabled={busy} onClick={() => void load()}>プロフィールを更新</button>
    {error && <p role="alert">{error}</p>}{message && <p>{message}</p>}
    {person && <>
      <p>データ版：{person.sourceVersion} ／ {person.activeVersion ? `有効版：${person.activeVersion}` : "検索投影待ち・非公開"} ／ {person.confirmationState}</p>
      <label>表示名<input value={person.displayName} maxLength={100} onChange={e => setPerson({ ...person, displayName: e.target.value })} /></label>
      <label className="checkbox"><input type="checkbox" checked={person.available} onChange={e => setPerson({ ...person, available: e.target.checked })} />相談への参加が可能</label>
      <p>公開状態：{person.publicationState === "withdrawn" ? "撤回済み" : "公開"} ／ 日付から算出した経験月数（重複除外）：{person.totalExperienceMonths ?? "不明"}</p>
      <p>Probabilityは分類への適合度で、経験年数・経験割合・熟練度ではありません。Confidenceは分類の確信度です。</p>
      {experiences.map(row => <article className="profile-experience" data-testid={row.documentId} key={row.id}>
        {row.workbookSectionKind === "context" && <p>案件外の原文文脈（自己PR・記入例・補足等）。案件経験や具体技能の検索には公開しません。</p>}
        <label>経験の説明<textarea value={row.description} maxLength={8000} onChange={e => edit(row.id, { description: e.target.value })} /></label>
        <label>{isCalendarMonth(row.period) ? "開始月" : "開始日"}<input type={isCalendarMonth(row.period) ? "month" : "date"} value={row.period.start ?? ""} onChange={e => periodDate(row, "start", e.target.value)} /></label>
        <label>{isCalendarMonth(row.period) ? "終了月（この月を含む）" : "終了日"}<input type={isCalendarMonth(row.period) ? "month" : "date"} value={row.period.end ?? ""} onChange={e => periodDate(row, "end", e.target.value)} /></label>
        <p>月数：{row.period.months ?? "不明"} ／ {row.confirmationState} ／ 根拠：{row.evidenceRefs.map(r => r.id).join("、")}</p>
        <SkillDetails value={row.specificSkills} />
        {row.pendingExtraction && <p>再取込との差分確認：{row.pendingExtraction.description}（現在の本人訂正を保持。確認または訂正を保存すると差分を解消します）</p>}
        {row.decisions && <details><summary>業務・技術分野の大分類と補助判定</summary>
          {Object.entries(row.decisions.choices).map(([axis, choice]) => <p key={axis}>{axis}：{Object.entries(choice.probabilities).map(([k, v]) => `${k} ${(v * 100).toFixed(1)}%`).join(" ／ ")} ／ Confidence {choice.confidence}</p>)}
          {Object.entries(row.decisions.nouls).map(([id, noul]) => <p key={id}>{id}：{noul.state === 2 ? "未評価" : noul.state === 1 ? "情報不足" : `経験ありの確率 ${noul.yesProbability}（0は経験なしの判定）`}</p>)}
        </details>}
      </article>)}
      <button disabled={busy} onClick={() => save("correct")}>訂正を保存</button>{" "}
      <button disabled={busy} onClick={() => save("confirm")}>内容を本人確認</button>{" "}
      <button disabled={busy} onClick={() => save("correct", person.publicationState === "withdrawn" ? "published" : "withdrawn")}>{person.publicationState === "withdrawn" ? "再公開する" : "公開を撤回"}</button>
    </>}
  </section>;
}
