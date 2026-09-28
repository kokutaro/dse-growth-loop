import { useCallback, useEffect, useRef, useState } from "react";
import { HubConnectionBuilder, HttpTransportType, LogLevel } from "@microsoft/signalr";
import { getToken, type AppConfig } from "./auth";

type Row = { id: string; etag: string; sessionId: string; title: string; summary: string; ownerId: string;
  personId: string; status: string; aiAssistance: string; membershipVersion: number; lastSequence: number;
  sequence: number; clientMessageId: string; body: string; createdAt: string; participantIds: string[] };
type Page = { items: Row[]; continuationToken: string | null; lastSequence?: number };
type Api = <T>(path: string, method?: string, body?: unknown, key?: string, etag?: string) => Promise<T>;
class ApiError extends Error { constructor(public status: number, public code: string) { super(code); } }

export function Conversations({ config, candidates }: { config: AppConfig; candidates: { entityId: string; title: string; entityType: string }[] }) {
  const [person, setPerson] = useState("demo-person-01");
  return <section className="panel conversations" aria-labelledby="conversations-title">
    <h2 id="conversations-title">課題と相談チャット</h2>
    <p>参加を承諾した人だけが会話を閲覧できます。AI支援が停止中でも会話を続けられます。</p>
    {config.mode === "Demo" && <label>ローカルテスト主体
      <select value={person} onChange={e => setPerson(e.target.value)}>
        {["01", "02", "03"].map(id => <option key={id} value={`demo-person-${id}`}>架空人物 {id}</option>)}
      </select>
    </label>}
    <Workspace key={person} config={config} person={person} candidates={candidates} />
  </section>;
}
function Workspace({ config, person, candidates }: { config: AppConfig; person: string; candidates: { entityId: string; title: string; entityType: string }[] }) {
  const [principal, setPrincipal] = useState("");
  const [rows, setRows] = useState<Row[]>([]), [selected, setSelected] = useState<Row>();
  const [title, setTitle] = useState(""), [summary, setSummary] = useState(""), [error, setError] = useState(""), [busy, setBusy] = useState(false);
  const createKey = useRef(crypto.randomUUID());
  const api = useCallback<Api>(async (path, method = "GET", body, key, etag) => {
    const headers: Record<string, string> = {};
    if (config.mode === "Demo") headers["X-Demo-Person"] = person;
    else headers.Authorization = `Bearer ${await getToken(config)}`;
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (key) headers["Idempotency-Key"] = key;
    if (etag) headers["If-Match"] = etag;
    const response = await fetch(`/api/v1${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (!response.ok) throw new ApiError(response.status, (await response.json().catch(() => ({}))).code ?? "CONNECTION_FAILED");
    return response.json();
  }, [config, person]);
  async function load() {
    let token: string | null = null; const found: Row[] = [];
    do {
      const page: Page = await api(`/challenges?pageSize=100${token ? `&continuationToken=${encodeURIComponent(token)}` : ""}`);
      found.push(...page.items); token = page.continuationToken;
    } while (token);
    setRows(found);
    setPrincipal((await api<{ principalId: string }>("/conversation-context")).principalId);
  }
  useEffect(() => { if (config.mode === "Demo") void load().catch(() => setError("課題を取得できませんでした。再読み込みしてください。")); }, [api]);
  async function create(event: React.SubmitEvent) {
    event.preventDefault(); setBusy(true); setError("");
    try {
      const row = await api<Row>("/challenges", "POST", { title, summary }, createKey.current);
      createKey.current = crypto.randomUUID(); setTitle(""); setSummary(""); setSelected(row); await load();
    } catch (e) { setError(`課題を登録できませんでした。同じ入力で再試行できます。${e instanceof Error ? e.message : ""}`); }
    finally { setBusy(false); }
  }
  return <>
    <form onSubmit={create}>
      <label>課題名<input required maxLength={300} value={title} onChange={e => { setTitle(e.target.value); createKey.current = crypto.randomUUID(); }} /></label>
      <label>困っていること<textarea required maxLength={8000} value={summary} onChange={e => { setSummary(e.target.value); createKey.current = crypto.randomUUID(); }} /></label>
      <button disabled={busy}>課題を作成</button>
    </form>
    <button type="button" onClick={() => { setError(""); void load().catch(() => setError("課題を取得できませんでした。")); }}>課題を読み込む</button>
    {error && <p role="alert">{error}</p>}
    <ul className="challenge-list">{rows.map(row => <li key={row.id}><button type="button" onClick={() => setSelected(row)}>{row.title}</button></li>)}</ul>
    {selected && principal && <><button type="button" onClick={() => setSelected(undefined)}>相談画面を閉じる</button>
      <Chat key={selected.sessionId} challenge={selected} principal={principal} api={api} config={config} person={person} candidates={candidates} /></>}
  </>;
}
type Pending = { clientMessageId: string; body: string; key: string; state: "saving" | "save_failed" };
function Chat({ challenge, principal, api, config, person, candidates }: { challenge: Row; principal: string; api: Api; config: AppConfig; person: string; candidates: { entityId: string; title: string; entityType: string }[] }) {
  const sid = challenge.sessionId, path = `/sessions/${sid}`;
  const [session, setSession] = useState<Row>(), [participations, setParticipations] = useState<Row[]>([]);
  const [messages, setMessages] = useState<Row[]>([]), [text, setText] = useState(""), [target, setTarget] = useState("");
  const [error, setError] = useState(""), [connection, setConnection] = useState("未接続"), [busy, setBusy] = useState(false);
  const storageKey = `dse-outbox:${config.tenantId}:${principal}:${sid}`;
  const [pending, setPending] = useState<Pending[]>(() => {
    try { return (JSON.parse(sessionStorage.getItem(storageKey) ?? "[]") as Pending[]).map(x => ({ ...x, state: "save_failed" })); } catch { return []; }
  });
  useEffect(() => { sessionStorage.setItem(storageKey, JSON.stringify(pending)); }, [pending, storageKey]);
  const last = useRef(0), generation = useRef(0), syncing = useRef(false);
  const hub = useRef<ReturnType<HubConnectionBuilder["build"]>>(undefined);
  const sync = useCallback(async () => {
    if (syncing.current) return;
    syncing.current = true; const run = generation.current;
    try {
      const invites = await api<Page>(`${path}/participations`);
      if (run !== generation.current) return;
      setParticipations(invites.items);
      const current = await api<Row>(path);
      if (run !== generation.current) return;
      setSession(current);
      // Subscribe before replay: hints during replay are harmless, periodic replay covers every lost hint.
      if (hub.current?.state === "Connected") {
        try { await hub.current.invoke("Join", sid); }
        catch { setConnection("通知接続を回復中・履歴はDBから取得します"); }
      }
      const after = last.current; let token: string | null = null;
      do {
        const page: Page = await api(`${path}/messages?afterSequence=${after}&pageSize=100${token ? `&continuationToken=${encodeURIComponent(token)}` : ""}`);
        if (run !== generation.current) return;
        setMessages(previous => [...new Map([...previous, ...page.items].map(x => [x.sequence, x])).values()].sort((a, b) => a.sequence - b.sequence));
        last.current = page.lastSequence ?? last.current;
        // Only DB history acknowledges pending sends, never a hint or local storage.
        setPending(previous => previous.filter(x => !page.items.some(m => m.clientMessageId === x.clientMessageId && m.body === x.body)));
        token = page.continuationToken;
      } while (token);
    } catch (e) {
      if (run !== generation.current) return;
      if (e instanceof ApiError && [401, 403, 404].includes(e.status)) {
        setSession(undefined); setMessages([]); last.current = 0;
        setConnection("参加承諾待ち、または閲覧権限がありません");
      } else setConnection("履歴を取得できません。再接続時に再取得します");
    } finally { syncing.current = false; }
  }, [api, path, sid]);
  useEffect(() => {
    let disposed = false;
    const transport = new HubConnectionBuilder().withUrl("/api/hubs/conversations", {
      transport: HttpTransportType.LongPolling,
      headers: config.mode === "Demo" ? { "X-Demo-Person": person } : {},
      accessTokenFactory: config.mode === "Azure" ? () => getToken(config) : undefined,
    }).configureLogging(LogLevel.None).withAutomaticReconnect([0, 2000, 5000, 10000]).build();
    hub.current = transport;
    transport.on("MessageCommitted", () => { void sync(); });
    transport.onreconnecting(() => { if (!disposed) setConnection("切断・再接続中（未保存の入力は保持）"); });
    transport.onreconnected(() => { if (!disposed) { setConnection("接続済み"); void sync(); } });
    transport.onclose(() => { if (!disposed) setConnection("切断中・履歴はDBから回復できます"); });
    async function connect() {
      if (disposed || transport.state !== "Disconnected") return;
      try { await transport.start(); if (!disposed) { setConnection("接続済み"); await sync(); } }
      catch { if (!disposed) setConnection("切断中・再接続を待っています"); }
    }
    void sync(); void connect();
    const timer = window.setInterval(() => { void sync(); void connect(); }, 10000);
    const stop = () => { void transport.stop(); };
    window.addEventListener("pagehide", stop);
    return () => { disposed = true; generation.current++; clearInterval(timer); window.removeEventListener("pagehide", stop); void transport.stop(); hub.current = undefined; };
  }, [config, person, sync]);
  async function act(action: () => Promise<unknown>) {
    setBusy(true); setError("");
    try { await action(); await sync(); }
    catch (e) { setError(`操作を完了できませんでした。状態を更新して再試行してください。${e instanceof Error ? e.message : ""}`); await sync(); }
    finally { setBusy(false); }
  }
  async function send(item: Pending) {
    setPending(previous => previous.map(x => x.clientMessageId === item.clientMessageId ? { ...x, state: "saving" } : x));
    try {
      const current = await api<Row>(path);
      await api<Row>(`${path}/messages`, "POST", { clientMessageId: item.clientMessageId, body: item.body }, item.key, current.etag);
      setPending(previous => previous.filter(x => x.clientMessageId !== item.clientMessageId));
      await sync();
    } catch {
      setPending(previous => previous.map(x => x.clientMessageId === item.clientMessageId ? { ...x, state: "save_failed" } : x));
    }
  }
  const own = participations.find(x => config.mode === "Azure" || x.personId === person);
  const owner = principal === challenge.ownerId;
  return <article className="chat">
    <h3>{challenge.title}</h3><p>{challenge.summary}</p>
    <p aria-live="polite" data-testid="chat-connection">{connection}</p>
    <button type="button" onClick={() => void sync()}>会話を更新</button>
    {error && <p role="alert">{error}</p>}
    <ul>{participations.map(p => <li key={p.id}>{p.personId}：{({ requested: "依頼中", accepted: "参加中", declined: "辞退", left: "退会済み" } as Record<string, string>)[p.status]}</li>)}</ul>
    {!owner && own?.status === "requested" && <div>
      <button disabled={busy} onClick={() => void act(() => api(`${path}/participation`, "PATCH", { decision: "accepted" }, crypto.randomUUID(), own.etag))}>参加を承諾</button>
      <button disabled={busy} onClick={() => void act(() => api(`${path}/participation`, "PATCH", { decision: "declined" }, crypto.randomUUID(), own.etag))}>辞退する</button>
    </div>}
    {session && <>
      {owner && session.status === "open" && <form onSubmit={e => { e.preventDefault(); void act(() => api(`${path}/participations`, "POST", { personId: target }, crypto.randomUUID(), session.etag)); }}>
        <label>相談する人物ID<input required value={target} onChange={e => setTarget(e.target.value)} list="chat-candidates" /></label>
        <datalist id="chat-candidates">{candidates.filter(x => x.entityType === "person").map(x => <option key={x.entityId} value={x.entityId}>{x.title}</option>)}</datalist>
        <small>検索候補のIDを指定します。候補を表示しただけでは参加者になりません。</small><button disabled={busy}>相談を依頼</button>
      </form>}
      <label className="checkbox"><input type="checkbox" checked={session.aiAssistance === "enabled"} disabled={busy || session.status !== "open"}
        onChange={e => void act(() => api(path, "PATCH", { aiAssistance: e.target.checked ? "enabled" : "stopped", status: session.status }, crypto.randomUUID(), session.etag))} />自動支援 ON</label>
      <p className="note">AI投稿は今後実装します。この設定は保存され、人同士の会話には影響しません。</p>
      <ol aria-label="保存済みの会話">{messages.map(message => <li key={message.sequence}>
        <small>#{message.sequence} · {message.ownerId} · {new Date(message.createdAt).toLocaleString()} · 保存済み</small><p className="message-body">{message.body}</p>
      </li>)}</ol>
      <form onSubmit={e => { e.preventDefault(); if (!text.trim()) return; const item: Pending = { clientMessageId: crypto.randomUUID(), key: crypto.randomUUID(), body: text, state: "saving" }; setPending(previous => [...previous, item]); setText(""); void send(item); }}>
        <label>メッセージ<textarea maxLength={8000} required value={text} onChange={e => setText(e.target.value)} disabled={session.status !== "open"} /></label>
        <button disabled={session.status !== "open"}>送信</button>
      </form>
      {!owner && own?.status === "accepted" && <button disabled={busy} onClick={() => void act(() => api(`${path}/participation`, "PATCH", { decision: "left" }, crypto.randomUUID(), own.etag))}>退会する</button>}
      {owner && session.status === "open" && <button disabled={busy} onClick={() => void act(() => api(path, "PATCH", { aiAssistance: "stopped", status: "closed" }, crypto.randomUUID(), session.etag))}>相談を終了</button>}
      {session.status === "closed" && <p>この相談は終了しました。</p>}
    </>}
    <ul aria-label="未保存のメッセージ">{pending.map(item => <li key={item.clientMessageId}><p className="message-body">{item.body}</p>
      <span>{item.state === "saving" ? "保存中…" : "未保存（結果未確認）"}</span>
      {item.state === "save_failed" && <button onClick={() => void send(item)}>同じIDで再送</button>}</li>)}</ul>
    <p className="note">接続中はAPIの稼働時間に含まれます。相談画面を閉じると切断します。未保存の再送待ちはこのタブに保持されます。</p>
  </article>;
}
