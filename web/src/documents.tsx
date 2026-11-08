import { useEffect, useRef, useState } from "react";
import { getToken, type AppConfig } from "./auth";
import {apiFetch as fetch,workflowHref,workflowStorageKey} from './workflow-context';

type Accepted = { documentId: string; jobId: string; version: number };
type Job = { status: string; lastErrorCode: string | null; resultId: string | null };
type Evidence = { id: string; location: { label: string }; text: string };
type UploadContext = { personId: string; departmentId: string; scopeIds: string[]; maxBytes: number;canReviewSkills?:boolean };
type SavedDocument={id:string;title:string;sourceVersion:number;status:string;currentVersionId:string;etag:string;ingestionJobId?:string};
const statuses: Record<string, string> = { queued: "処理待ち", processing: "処理中", ready: "機械抽出済み", failed: "失敗", needs_review: "確認待ち", cancelled: "取り消し", budget_stopped: "保留" };
const warningLabels: Record<string, string> = { IMAGES_NOT_OCR: "画像の文字は抽出していません。", EMPTY_DOCUMENT: "抽出できる本文がありません。", EXTERNAL_LINKS_IGNORED: "外部リンク先は取得していません。", FORMULA_CACHED_VALUES_ONLY: "数式は保存済みの値です。再計算していません。", FORMULA_CACHE_MISSING: "保存値のない数式セルがあります。", AUXILIARY_PARTS_NOT_EXTRACTED: "ヘッダー・フッター・注釈は抽出対象外です。", EXCEL_1900_LEAP_DAY: "Excel固有の日付1900-02-29を含みます。", INVALID_DATE_SERIAL: "変換できない日付は元の値を表示します。" };
export function Documents({ config, person }: { config: AppConfig; person?: string }) {
  const [context, setContext] = useState<UploadContext>();
  const [saved,setSaved]=useState<SavedDocument[]>([]),[savedCursor,setSavedCursor]=useState<string|null>(null);
  const [documentState,setDocumentState]=useState(''),[revisionFile,setRevisionFile]=useState<File>(),[changeReason,setChangeReason]=useState('');
  const [fragments,setFragments]=useState<{id:string;description:string;contentOnly:boolean;historyOnly:boolean}[]>([]);
  const documentEtag=useRef(''),revisionKey=useRef(crypto.randomUUID()),withdrawKey=useRef(crypto.randomUUID());
  const activeDocument=useRef<Accepted|undefined>(undefined),documentView=useRef('');
  const documentKey=(value:Accepted|undefined)=>value?`${value.documentId}:${value.version}:${value.jobId}`:'';
  const [accepted, setAccepted] = useState<Accepted>();
  const [job, setJob] = useState<Job>();
  const [evidence, setEvidence] = useState<Evidence[]>([]);
  const [warnings, setWarnings] = useState<string[]>([]);
  const [pipeline, setPipeline] = useState<{ aiState: string; processingMode: string; sourceVersion: number; resultId: string; moreRuns?: boolean; runs?: { id: string; operation: string; status: string; errorCode?: string; trace: { modelId: string | null; promptVersion: string; taxonomyVersion: string; dataVersion: number; evidenceIds: string[]; inputTokens: number; outputTokens: number; attemptCount: number } }[]; job?: Job & { id: string; etag: string } }>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [title, setTitle] = useState("");
  const [type, setType] = useState("profile");
  const [file, setFile] = useState<File>();
  const key = useRef(crypto.randomUUID());
  const etag = useRef("");
  const uploadStorageKey = useRef<string | undefined>(undefined);
  const generation = useRef(0), mounted = useRef(true);
  const current = (version: number) => mounted.current && version === generation.current;
  async function request(path: string, init?: RequestInit) {
    const headers = new Headers(init?.headers);
    if (config.mode === "Demo" && person) headers.set("X-Demo-Person", person);
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
    const version = ++generation.current; uploadStorageKey.current = undefined;
    setContext(undefined); setAccepted(undefined);activeDocument.current=undefined; setJob(undefined); setEvidence([]); setWarnings([]); setPipeline(undefined);setSaved([]);setFragments([]);
    try {
      const resolved: UploadContext = await (await request("/api/v1/upload-context")).json();
      if (!current(version)) return;
      if (!resolved.personId || !/^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$/.test(resolved.personId)) throw new Error("本人の登録先を確認できませんでした。");
      const storageKey = workflowStorageKey(`dse-upload-job:v2:${config.mode}:${config.tenantId || 'demo'}:${resolved.personId}`);
      uploadStorageKey.current = storageKey; setContext(resolved); setError("");
      // Legacy generic/client-person keys cannot establish the authenticated owner.
      sessionStorage.removeItem("dse-upload-job");
      if (person) sessionStorage.removeItem(`dse-upload-job:${config.tenantId}:${person}`);
      const saved = sessionStorage.getItem(storageKey);
      if (saved) {
        const value: Accepted = JSON.parse(saved);
        if (/^[A-Za-z0-9._:-]+$/.test(value.documentId) && /^(?:[A-Za-z0-9._:-]+)?$/.test(value.jobId)) { activeDocument.current=value;setAccepted(value); await refresh(value); }
      }
    }
    catch (e) { if (current(version)) setError(String(e)); }
  }
  useEffect(() => {
    mounted.current = true;
    uploadStorageKey.current = undefined;
    setContext(undefined); setAccepted(undefined);activeDocument.current=undefined; setJob(undefined); setEvidence([]); setWarnings([]); setPipeline(undefined);setSaved([]);setFragments([]);
    setTitle(""); setFile(undefined); setBusy(false); setError(""); key.current = crypto.randomUUID();
    if (config.mode === "Demo") void loadContext();
    return () => { mounted.current = false; generation.current++; uploadStorageKey.current = undefined; };
  }, [config, person]);
  async function listDocuments(more=false){
    const version=generation.current;
    const page=await (await request('/api/v1/documents?pageSize=20'+(more&&savedCursor?'&continuationToken='+encodeURIComponent(savedCursor):''))).json();
    if(!current(version))return;
    setSaved(xs=>more?[...xs,...page.items]:page.items);setSavedCursor(page.continuationToken);
  }
  async function refresh(value = accepted) {
    const version = generation.current;
    if (!value || !uploadStorageKey.current) return;
    const selected=()=>current(version)&&documentKey(activeDocument.current)===documentKey(value);if(!selected())return;
    try {
      let jobState:Job={status:'ready',lastErrorCode:null,resultId:null};
      if(value.jobId){const response = await request(`/api/v1/jobs/${value.jobId}`);if(!selected())return;etag.current=response.headers.get('ETag')??'';jobState=await response.json();}
      if (!selected()) return;
      setJob(jobState);
      const docResponse=await request(`/api/v1/documents/${value.documentId}`);const doc=await docResponse.json();if(!selected())return;documentEtag.current=docResponse.headers.get('ETag')??'';
      setWarnings(doc.warnings ?? []);
      const view=`${doc.id}:${doc.sourceVersion}:${doc.activeVersion}:${doc.accessVersion}`;if(documentView.current!==view){documentView.current=view;setFragments([]);}
      setDocumentState(doc.status);if(doc.status==='withdrawn'){setEvidence([]);setPipeline(undefined);setJob({status:'cancelled',lastErrorCode:null,resultId:null});return;}
      if (doc.enrichmentJobId) { const next = await (await request(`/api/v1/documents/${value.documentId}/pipeline`)).json(); if (!selected()) return; setPipeline(next); }
      if (jobState.status === "ready" && doc.activeVersion!==null) {
        const all: Evidence[] = []; let token: string | null = null;
        do {
          const page = await (await request(`/api/v1/documents/${value.documentId}/evidence?pageSize=100${token ? `&continuationToken=${encodeURIComponent(token)}` : ""}`)).json();
          if (!selected()) return;
          all.push(...page.items); token = page.continuationToken;
        } while (token);
        setEvidence([...new Map(all.map(row => [row.id, row])).values()]);
      } else setEvidence([]);
      setError("");
    } catch (e) { if (selected()) { setEvidence([]); setError(String(e)); } }
  }
  async function chooseDocument(doc:SavedDocument){
    if(!uploadStorageKey.current)return;const value={documentId:doc.id,jobId:doc.ingestionJobId??'',version:doc.sourceVersion};activeDocument.current=value;setAccepted(value);setPipeline(undefined);setFragments([]);setEvidence([]);setChangeReason('');setRevisionFile(undefined);revisionKey.current=crypto.randomUUID();withdrawKey.current=crypto.randomUUID();sessionStorage.setItem(uploadStorageKey.current,JSON.stringify(value));await refresh(value);
  }
  async function revise(event:React.SubmitEvent){
    event.preventDefault();if(!accepted||!revisionFile||!context||!uploadStorageKey.current)return;const version=generation.current,storageKey=uploadStorageKey.current;setBusy(true);setError('');
    try{if(revisionFile.size>context.maxBytes)throw new Error('ファイルが上限を超えています。');const body=new FormData();body.set('file',revisionFile);body.set('reason',changeReason);
      const next:Accepted=await(await request(`/api/v1/documents/${accepted.documentId}/versions`,{method:'POST',headers:{'If-Match':documentEtag.current,'Idempotency-Key':revisionKey.current},body})).json();
      if(!current(version)||storageKey!==uploadStorageKey.current)return;
      activeDocument.current=next;setAccepted(next);sessionStorage.setItem(storageKey,JSON.stringify(next));setEvidence([]);setPipeline(undefined);setFragments([]);revisionKey.current=crypto.randomUUID();await refresh(next);await listDocuments();
    }catch(e){if(current(version)){setEvidence([]);setFragments([]);setError(String(e));}}finally{if(current(version))setBusy(false);}
  }
  async function withdraw(){
    if(!accepted||!changeReason.trim())return;const version=generation.current;setBusy(true);setError('');
    try{await request(`/api/v1/documents/${accepted.documentId}/withdraw`,{method:'POST',headers:{'Content-Type':'application/json','If-Match':documentEtag.current,'Idempotency-Key':withdrawKey.current},body:JSON.stringify({reason:changeReason})});if(!current(version))return;setEvidence([]);setFragments([]);await refresh();await listDocuments();}
    catch(e){if(current(version)){setEvidence([]);setFragments([]);setError(String(e));}}finally{if(current(version))setBusy(false);}
  }
  useEffect(() => {
    if (!accepted || !job || !["queued", "processing"].includes(job.status) && !["queued", "processing"].includes(pipeline?.job?.status ?? "")) return;
    // Poll only while this view has an active job; stop after five minutes or when hidden.
    const started = Date.now(); let running = false;
    const timer = setInterval(async () => {
      if (Date.now() - started > 300000) { clearInterval(timer); return; }
      if (document.hidden || running) return;
      running = true; try { await refresh(accepted); } finally { running = false; }
    }, 3000);
    return () => clearInterval(timer);
  }, [accepted?.jobId, job?.status, pipeline?.job?.status]);
  async function upload(event: React.SubmitEvent) {
    event.preventDefault(); setBusy(true); setError("");
    const version = generation.current, storageKey = uploadStorageKey.current;
    try {
      if (!file || !context || !storageKey) throw new Error("ファイルと登録先を確認してください。");
      if (file.size > context.maxBytes) throw new Error("ファイルが上限を超えています。");
      const body = new FormData(); body.set("file", file); body.set("title", title); body.set("documentType", type);
      const value: Accepted = await (await request("/api/v1/documents", { method: "POST", headers: { "Idempotency-Key": key.current }, body })).json();
      if (!current(version) || storageKey !== uploadStorageKey.current) return;
      sessionStorage.setItem(storageKey, JSON.stringify(value));
      activeDocument.current=value;setAccepted(value); setPipeline(undefined); setEvidence([]); setJob({ status: "queued", lastErrorCode: null, resultId: null }); await refresh(value);await listDocuments();
    } catch (e) { if (current(version)) setError(String(e)); } finally { if (current(version)) setBusy(false); }
  }
  async function retry() {
    const version = generation.current, storageKey = uploadStorageKey.current;
    if (!accepted || !storageKey) return;
    setBusy(true);
    try {
      const result = await (await request(`/api/v1/jobs/${accepted.jobId}/retry`, { method: "POST", headers: { "Content-Type": "application/json", "If-Match": etag.current, "Idempotency-Key": crypto.randomUUID() }, body: JSON.stringify({ reason: "画面から手動再実行" }) })).json();
      if (!current(version) || storageKey !== uploadStorageKey.current) return;
      const next = { ...accepted, jobId: result.jobId };activeDocument.current=next; sessionStorage.setItem(storageKey, JSON.stringify(next)); setAccepted(next); await refresh(next);
    } catch (e) { if (current(version)) setError(String(e)); } finally { if (current(version)) setBusy(false); }
  }
  async function original() {
    const version = generation.current;
    if (!accepted || !uploadStorageKey.current) return;
    try {
      const response = await request(`/api/v1/documents/${accepted.documentId}/original`);
      const blob = await response.blob(); if (!current(version)) return;
      const url = URL.createObjectURL(blob); const link = document.createElement("a");
      link.href = url; link.download = `${accepted.documentId}.${response.headers.get("Content-Type")?.includes("spreadsheet") ? "xlsx" : "docx"}`;
      link.click(); setTimeout(() => URL.revokeObjectURL(url), 10000);
    } catch (e) { if (current(version)) setError(String(e)); }
  }
  return <section className="panel documents" aria-labelledby="documents-title">
    <h2 id="documents-title">文書を取り込む</h2>
    <p>ダミーのWord（docx）・Excel（xlsx）を登録できます。画像OCRは対象外です。構造化・分類・検索投影の状態を下に表示します。</p>
    <p>Excelは全シートのテキストと出典を取り込みます。Google スプレッドシートはExcel形式で書き出して登録してください。</p>
    {!context ? <button onClick={loadContext}>登録先を確認する</button> : <p>対象者：{context.personId} ／ 部署：{context.departmentId} ／ 閲覧範囲：{context.scopeIds.join("、")} ／ 上限：{context.maxBytes / 1024 / 1024} MiB</p>}
    {context&&<section aria-label="保存した資料"><h3>保存した資料</h3><p>資料一覧を更新すると、保存した資料を開けます。</p><button disabled={busy} onClick={()=>void listDocuments().catch(e=>setError(String(e)))}>資料一覧を更新</button><ul>{saved.map(doc=><li key={doc.id}><button disabled={busy} onClick={()=>void chooseDocument(doc)}>{doc.title}（第{doc.sourceVersion}版{doc.status==='withdrawn'?'・撤回済み':''}）</button></li>)}</ul>{savedCursor&&<button disabled={busy} onClick={()=>void listDocuments(true).catch(e=>setError(String(e)))}>次の資料を読む</button>}</section>}
    <form onSubmit={upload}>
      <label htmlFor="document-title">文書名</label><input id="document-title" value={title} required maxLength={300} onChange={e => { setTitle(e.target.value); key.current = crypto.randomUUID(); }} />
      <label htmlFor="document-type">文書種別</label><select id="document-type" value={type} onChange={e => { setType(e.target.value); key.current = crypto.randomUUID(); }}><option value="profile">職歴・スキル</option><option value="report">部署週報</option><option value="case">事例</option></select>
      <label htmlFor="document-file">原本ファイル</label><input key={`${config.mode}:${config.tenantId}:${person ?? ''}`} id="document-file" type="file" accept=".docx,.xlsx" required onChange={e => { setFile(e.target.files?.[0]); key.current = crypto.randomUUID(); }} />
      <button disabled={busy || !context} type="submit">{busy ? "処理しています…" : "アップロード"}</button>
    </form>
    {error && <p role="alert">{error}</p>}
    {accepted && <div><p role="status">{statuses[job?.status ?? "queued"]} ／ AI構造化：{pipeline?.job?.status === "ready" ? "検索投影済み" : pipeline?.job ? (statuses[pipeline.job.status] ?? pipeline.job.status) : "未実施"}</p><p>文書ID：{accepted.documentId} ／ 版：{accepted.version}</p>
      {pipeline && <div><p>{pipeline.processingMode === "live" ? "実AI処理" : pipeline.processingMode === "fixture" ? "fixture（fake AI）" : "処理方式未確定"} ／ 抽出版：{pipeline.sourceVersion}</p>
        <p>構造化・分類ジョブ：{pipeline.job?.id} ／ 結果：{pipeline.resultId || "未確定"}</p>
        {pipeline.runs && pipeline.runs.length > 0 && <details><summary>各段階の成否・根拠・モデル版</summary>{pipeline.runs.map(run => <p key={run.id}>{run.operation === "structure" ? "構造化" : run.operation === "extractSkills" ? "具体スキル抽出" : "分類"}：{run.status} {run.errorCode} ／ モデル：{run.trace.modelId ?? "未呼出し"} ／ プロンプト：{run.trace.promptVersion} ／ 分類体系：{run.trace.taxonomyVersion} ／ データ版：{run.trace.dataVersion} ／ 試行：{run.trace.attemptCount} ／ tokens：{run.trace.inputTokens}/{run.trace.outputTokens} ／ 根拠：{run.trace.evidenceIds.join("、")}</p>)}{pipeline.moreRuns && <p>最初の100件を表示しています。</p>}</details>}
        {pipeline.job?.lastErrorCode && <p>AI処理詳細：{pipeline.job.lastErrorCode}</p>}
        {pipeline.job && ["failed", "needs_review", "budget_stopped", "queued", "cancelled"].includes(pipeline.job.status) && <button disabled={busy} onClick={async () => {
          setBusy(true); try { await request(`/api/v1/jobs/${pipeline.job!.id}/retry`, { method: "POST", headers: { "Content-Type": "application/json", "If-Match": pipeline.job!.etag, "Idempotency-Key": crypto.randomUUID() }, body: JSON.stringify({ reason: "構造化・分類の手動再実行" }) }); await refresh(); } catch (e) { setError(String(e)); } finally { setBusy(false); }
        }}>構造化・分類を再実行</button>}
      </div>}
      {job?.lastErrorCode && <p>詳細：{job.lastErrorCode}</p>}
      <button onClick={() => refresh()}>進捗を更新</button> <button onClick={original}>原本を取得</button>
      {job && ["failed", "needs_review", "queued", "budget_stopped"].includes(job.status) && <button disabled={busy} onClick={retry}>再実行・再通知</button>}
      {warnings.map(w => <p key={w}>{warningLabels[w] ?? w}</p>)}
      {documentState==='withdrawn'?<p role="status">この資料は撤回済みです。以前の原文は検索・候補確認に使いません。</p>:<section aria-label="資料の訂正と撤回"><h3>資料の訂正・撤回</h3><p>訂正すると旧版を検索から外し、新版を通常の取込処理へ送ります。過去の版と承認履歴は残します。</p><form onSubmit={e=>void revise(e)}><label>訂正・撤回の理由<input value={changeReason} maxLength={500} required onChange={e=>{setChangeReason(e.target.value);revisionKey.current=crypto.randomUUID();withdrawKey.current=crypto.randomUUID();}}/></label><label>訂正版の原本<input type="file" accept=".docx,.xlsx" onChange={e=>{setRevisionFile(e.target.files?.[0]);revisionKey.current=crypto.randomUUID();}}/></label><button disabled={busy||!revisionFile||!changeReason.trim()}>訂正版を取り込む</button><button type="button" disabled={busy||!changeReason.trim()} onClick={()=>void withdraw()}>この資料を撤回する</button></form>
      {context?.canReviewSkills&&<><button disabled={busy} onClick={async()=>{try{const page=await(await request(`/api/v1/documents/${accepted.documentId}/skill-fragments`)).json();setFragments(page.items);}catch(e){setError(String(e));}}}>技能候補の元になる断片を確認</button>{fragments.length>0?<ul>{fragments.map(f=><li key={f.id}><p>{f.description} ／ {f.contentOnly?'知識内容のみ':'経験候補・本人未確認'}</p>{!f.historyOnly&&<a href={workflowHref('/skill-dictionary.html?fragment='+encodeURIComponent(f.id))}>この断片の保存済み抽出を確認する</a>}</li>)}</ul>:<p>技能候補の確認では、現在の原文と保存済み抽出だけを読みます。</p>}</>}
      </section>}
      {evidence.length > 0 && <h3>出典プレビュー</h3>}{evidence.map(span => <article key={span.id}><h4>{span.location.label}</h4><p className="evidence-text">{span.text}</p></article>)}
    </div>}
  </section>;
}
