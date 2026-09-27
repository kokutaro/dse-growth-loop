import { useEffect, useRef, useState } from "react";
import { getToken, type AppConfig } from "./auth";

type Accepted = { documentId: string; jobId: string; version: number };
type Job = { status: string; lastErrorCode: string | null; resultId: string | null };
type Evidence = { id: string; location: { label: string }; text: string };
type UploadContext = { personId: string; departmentId: string; scopeIds: string[]; maxBytes: number };
const statuses: Record<string, string> = { queued: "処理待ち", processing: "処理中", ready: "機械抽出済み", failed: "失敗", needs_review: "確認待ち", cancelled: "取り消し", budget_stopped: "保留" };
const warningLabels: Record<string, string> = { IMAGES_NOT_OCR: "画像の文字は抽出していません。", EMPTY_DOCUMENT: "抽出できる本文がありません。", EXTERNAL_LINKS_IGNORED: "外部リンク先は取得していません。", FORMULA_CACHED_VALUES_ONLY: "数式は保存済みの値です。再計算していません。", FORMULA_CACHE_MISSING: "保存値のない数式セルがあります。", AUXILIARY_PARTS_NOT_EXTRACTED: "ヘッダー・フッター・注釈は抽出対象外です。", EXCEL_1900_LEAP_DAY: "Excel固有の日付1900-02-29を含みます。", INVALID_DATE_SERIAL: "変換できない日付は元の値を表示します。" };
export function Documents({ config }: { config: AppConfig }) {
  const [context, setContext] = useState<UploadContext>();
  const [accepted, setAccepted] = useState<Accepted>();
  const [job, setJob] = useState<Job>();
  const [evidence, setEvidence] = useState<Evidence[]>([]);
  const [warnings, setWarnings] = useState<string[]>([]);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [title, setTitle] = useState("");
  const [type, setType] = useState("profile");
  const [file, setFile] = useState<File>();
  const key = useRef(crypto.randomUUID());
  const etag = useRef("");
  async function request(path: string, init?: RequestInit) {
    const headers = new Headers(init?.headers);
    if (config.mode === "Azure") headers.set("Authorization", `Bearer ${await getToken(config)}`);
    for (let attempt = 0; ; attempt++) {
      const response = await fetch(path, { ...init, headers });
      if (response.ok) return response;
      const body = await response.json().catch(() => ({}));
      // Reads can briefly contend with a search or activation holding the access fence.
      if ((init?.method ?? "GET") === "GET" && response.status === 503 && body.code === "ACCESS_BUSY" && attempt < 3) {
        await new Promise(resolve => setTimeout(resolve, 200 * 2 ** attempt));
        continue;
      }
      throw new Error(`${body.code ?? "接続エラー"}（${response.status}）${response.headers.get("Retry-After") ? ` ${response.headers.get("Retry-After")}秒後に再試行してください。` : ""}`);
    }
  }
  async function loadContext() {
    try {
      setContext(await (await request("/api/v1/upload-context")).json()); setError("");
      const saved = sessionStorage.getItem("dse-upload-job");
      if (saved) {
        const value: Accepted = JSON.parse(saved);
        if (/^[A-Za-z0-9._:-]+$/.test(value.documentId) && /^[A-Za-z0-9._:-]+$/.test(value.jobId)) { setAccepted(value); await refresh(value); }
      }
    }
    catch (e) { setError(String(e)); }
  }
  useEffect(() => { if (config.mode === "Demo") void loadContext(); }, [config]);
  async function refresh(value = accepted) {
    if (!value) return;
    try {
      const response = await request(`/api/v1/jobs/${value.jobId}`); etag.current = response.headers.get("ETag") ?? "";
      const current: Job = await response.json(); setJob(current);
      const doc = await (await request(`/api/v1/documents/${value.documentId}`)).json(); setWarnings(doc.warnings ?? []);
      if (current.status === "ready") {
        const all: Evidence[] = []; let token: string | null = null;
        do {
          const page = await (await request(`/api/v1/documents/${value.documentId}/evidence?pageSize=100${token ? `&continuationToken=${encodeURIComponent(token)}` : ""}`)).json();
          all.push(...page.items); token = page.continuationToken;
        } while (token);
        setEvidence([...new Map(all.map(row => [row.id, row])).values()]);
      } else setEvidence([]);
      setError("");
    } catch (e) { setEvidence([]); setError(String(e)); }
  }
  useEffect(() => {
    if (!accepted || !job || !["queued", "processing"].includes(job.status)) return;
    // Poll only while this view has an active job; stop after five minutes or when hidden.
    const started = Date.now(); let running = false;
    const timer = setInterval(async () => {
      if (Date.now() - started > 300000) { clearInterval(timer); return; }
      if (document.hidden || running) return;
      running = true; try { await refresh(accepted); } finally { running = false; }
    }, 3000);
    return () => clearInterval(timer);
  }, [accepted?.jobId, job?.status]);
  async function upload(event: React.SubmitEvent) {
    event.preventDefault(); setBusy(true); setError("");
    try {
      if (!file || !context) throw new Error("ファイルと登録先を確認してください。");
      if (file.size > context.maxBytes) throw new Error("ファイルが上限を超えています。");
      const body = new FormData(); body.set("file", file); body.set("title", title); body.set("documentType", type);
      const value: Accepted = await (await request("/api/v1/documents", { method: "POST", headers: { "Idempotency-Key": key.current }, body })).json();
      sessionStorage.setItem("dse-upload-job", JSON.stringify(value));
      setAccepted(value); setEvidence([]); setJob({ status: "queued", lastErrorCode: null, resultId: null }); await refresh(value);
    } catch (e) { setError(String(e)); } finally { setBusy(false); }
  }
  async function retry() {
    if (!accepted) return;
    setBusy(true);
    try {
      const result = await (await request(`/api/v1/jobs/${accepted.jobId}/retry`, { method: "POST", headers: { "Content-Type": "application/json", "If-Match": etag.current, "Idempotency-Key": crypto.randomUUID() }, body: JSON.stringify({ reason: "画面から手動再実行" }) })).json();
      const next = { ...accepted, jobId: result.jobId }; sessionStorage.setItem("dse-upload-job", JSON.stringify(next)); setAccepted(next); await refresh(next);
    } catch (e) { setError(String(e)); } finally { setBusy(false); }
  }
  async function original() {
    if (!accepted) return;
    try {
      const response = await request(`/api/v1/documents/${accepted.documentId}/original`);
      const url = URL.createObjectURL(await response.blob()); const link = document.createElement("a");
      link.href = url; link.download = `${accepted.documentId}.${response.headers.get("Content-Type")?.includes("spreadsheet") ? "xlsx" : "docx"}`;
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 10000);
    } catch (e) { setError(String(e)); }
  }
  return <section className="panel documents" aria-labelledby="documents-title">
    <h2 id="documents-title">文書を取り込む</h2>
    <p>ダミーのWord（docx）・Excel（xlsx）を登録できます。画像OCRとAI構造化は未実施です。</p>
    {!context ? <button onClick={loadContext}>登録先を確認する</button> : <p>対象者：{context.personId} ／ 部署：{context.departmentId} ／ 閲覧範囲：{context.scopeIds.join("、")} ／ 上限：{context.maxBytes / 1024 / 1024} MiB</p>}
    <form onSubmit={upload}>
      <label htmlFor="document-title">文書名</label><input id="document-title" value={title} required maxLength={300} onChange={e => { setTitle(e.target.value); key.current = crypto.randomUUID(); }} />
      <label htmlFor="document-type">文書種別</label><select id="document-type" value={type} onChange={e => { setType(e.target.value); key.current = crypto.randomUUID(); }}><option value="profile">職歴・スキル</option><option value="report">部署週報</option><option value="case">事例</option></select>
      <label htmlFor="document-file">原本ファイル</label><input id="document-file" type="file" accept=".docx,.xlsx" required onChange={e => { setFile(e.target.files?.[0]); key.current = crypto.randomUUID(); }} />
      <button disabled={busy || !context} type="submit">{busy ? "処理しています…" : "アップロード"}</button>
    </form>
    {error && <p role="alert">{error}</p>}
    {accepted && <div><p role="status">{statuses[job?.status ?? "queued"]} ／ AI構造化：未実施</p><p>文書ID：{accepted.documentId} ／ 版：{accepted.version}</p>
      {job?.lastErrorCode && <p>詳細：{job.lastErrorCode}</p>}
      <button onClick={() => refresh()}>進捗を更新</button> <button onClick={original}>原本を取得</button>
      {job && ["failed", "needs_review", "queued", "budget_stopped"].includes(job.status) && <button disabled={busy} onClick={retry}>再実行・再通知</button>}
      {warnings.map(w => <p key={w}>{warningLabels[w] ?? w}</p>)}
      {evidence.length > 0 && <h3>出典プレビュー</h3>}{evidence.map(span => <article key={span.id}><h4>{span.location.label}</h4><p className="evidence-text">{span.text}</p></article>)}
    </div>}
  </section>;
}
