import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { initializeAuth, endAuth, type AppConfig } from './auth';
import { IdentityDisclosure } from './identity-disclosure';
import { Profiles } from './profiles';
import { Documents } from './documents';
import { Knowledge } from './knowledge';
import { BusinessAnalysisPanel } from './business-analysis';
import { friendlyError, readPages, workspaceApi, WorkspaceError, type Api, type Challenge, type RoomRow, type InvitationRow, type MessageRow, type WorkspaceCase, type WorkspaceCapabilities } from './workspace-api';
import './workspace.css';

const menus = [['home', '⌂', 'ホーム'], ['tasks', '▤', '自分の課題・相談'], ['invitations', '◇', '参加依頼'], ['rooms', '◫', '活動ルーム'], ['knowledge', '▧', '事例・ナレッジ']];
type Draft = { id: string; title: string; detail: string; category: string; key: string; pending?: boolean; uncertain?: boolean; posted?: string };
type Activity = { challenge: Challenge; session?: RoomRow; participation?: InvitationRow; owner: boolean };
type Case = WorkspaceCase;
const blankDraft = (id: string): Draft => ({ id, title: '', detail: '', category: 'ソフトウェア', key: crypto.randomUUID() });
const href = (page: string) => `#${page}`;
const decodeQuery = (value: string | undefined) => { try { return value ? decodeURIComponent(value) : '引き継ぎ'; } catch { return '引き継ぎ'; } };
function Go({ to, children, primary = false }: { to: string; children: ReactNode; primary?: boolean }) { return <a className={`button ${primary ? 'primary' : ''}`} href={href(to)} onClick={to === "compose" ? e => { e.preventDefault(); location.hash = "compose/new-" + crypto.randomUUID(); } : undefined}>{children}</a>; }
function Pill({ children, teal = false }: { children: ReactNode; teal?: boolean }) { return <span className={`state-label ${teal ? 'teal' : ''}`}>{children}</span>; }
function Head({ title, lead }: { title: string; lead?: string }) { return <><h1>{title}</h1>{lead && <p className="lead">{lead}</p>}</>; }
function Section({ title, to, children }: { title: string; to?: string; children: ReactNode }) { return <section><div className="section-head"><h2>{title}</h2>{to && <a href={href(to)}>すべて見る</a>}</div>{children}</section>; }
function Empty({ text }: { text: string }) { return <div className="empty"><p>{text}</p><Go to="compose" primary>課題・相談を投稿する</Go></div>; }
function Privacy() { return <div className="privacy"><strong>この活動の表示：匿名</strong>本文の自由入力を完全に自動匿名化するものではありません。氏名・連絡先・秘密を入力しないでください。参加後も匿名名で活動します。実名開示と事例公開は、それぞれ別の双方同意が必要です。</div>; }
function status(a: Activity) { return a.session?.status === 'closed' ? '完了' : a.session && a.session.participantCount > 1 ? '活動中' : '参加待ち'; }
function ActivityCard({ activity }: { activity: Activity }) { return <article className="card"><div className="status-line"><Pill teal={status(activity) === '活動中'}>{status(activity)}</Pill><span className="tag">{activity.owner ? '相談した活動' : '協力している活動'}</span></div><h3>{activity.challenge.title}</h3><p>{activity.challenge.summary}</p><p>{activity.session && activity.session.participantCount > 1 ? activity.session.participants.map(p => p.aliasLabel).join(" / ") + " · この活動の参加者のみ" : (activity.session?.participants.map(p => p.aliasLabel).join(" / ") || "匿名の投稿者") + " · 協力者の参加待ち"}</p><div className="card-foot"><small>匿名名で会話</small><Go to={`room/${activity.challenge.id}`}>ルームを開く</Go></div></article>; }
function InviteCard({ activity }: { activity: Activity }) { return <article className="card"><Pill>回答待ち</Pill><h3>{activity.challenge.title}</h3><p>{activity.challenge.summary}</p><span className="tag">投稿者は匿名</span><div className="card-foot"><small>できる範囲で協力</small><Go to={`invitation/${activity.challenge.id}`}>内容を確認</Go></div></article>; }
function CaseCard({ item, query = "引き継ぎ" }: { item: Case; query?: string }) { return <article className="card"><span className="tag">参考事例</span><h3>{item.title}</h3><p>{item.summary}</p><div className="card-foot"><small>{item.processingMode === "fixture" ? "ローカルの架空データ" : "公開を確認した事例"}</small><Go to={`case/${item.id}/${encodeURIComponent(query)}`}>事例を読む</Go></div></article>; }

export function WorkspaceApp() {
  const [config, setConfig] = useState<AppConfig>();
  const [error, setError] = useState('');
  const [ended, setEnded] = useState(false);
  useEffect(() => {
    let active = true;
    void fetch('/api/config').then(async response => {
      if (!response.ok) throw new Error();
      const value = await response.json(); await initializeAuth(value);
      if (active) setConfig(value);
    }).catch(() => { if (active) setError('接続設定を読み込めませんでした。ページを再読み込みしてください。'); });
    return () => { active = false; };
  }, []);
  async function logout() { setEnded(true); try { await endAuth(); } catch { setError('Microsoft のログアウトを完了できませんでした。再ログイン時にアカウントを確認してください。'); } }
  return <><a className="skip" href="#stage" onClick={event => { event.preventDefault(); document.getElementById("stage")?.focus({ preventScroll: true }); window.scrollTo({ top: 0, behavior: 'instant' }); }}>本文へ移動</a><header className="topbar"><a href="#home" className="brand"><span className="mark" aria-hidden="true">∞</span><span>DSE <b>Growth Loop</b></span></a><div className="header-right"><span className="preview-label">{config?.mode === 'Demo' ? 'ローカル・ダミーデータ' : '接続確認'}</span>{config?.mode === 'Azure' && !ended && <button onClick={() => void logout()}>ログアウト</button>}<a className="button avatar" href="#profile" aria-label="マイページを開く">私</a></div></header>{error && <p role="alert">{error}</p>}{ended ? <main id="stage" tabIndex={-1}><h1>接続を終了しました</h1><button onClick={() => location.reload()}>ログインし直す</button></main> : config ? <Workspace config={config} /> : <main id="stage" tabIndex={-1}><p role="status">接続設定を読み込み中…</p></main>}</>;
}
function Workspace({ config }: { config: AppConfig }) {
  const [route, setRoute] = useState(location.hash.slice(1) || 'home');
  const [person, setPerson] = useState('demo-person-01');
  const [activities, setActivities] = useState<Activity[]>([]);
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [cases, setCases] = useState<Case[]>([]);
  const [error, setError] = useState(''), [notice, setNotice] = useState(''), [loading, setLoading] = useState(false);
  const [filter, setFilter] = useState('すべて');
  const [capabilities, setCapabilities] = useState<WorkspaceCapabilities>();
  const [identitySession] = useState(() => crypto.randomUUID());
  const requestVersion = useRef(0);
  const api = useMemo(() => workspaceApi(config, person), [config, person]);
  const [page, id, caseQuery] = route.split('/');
  const stage = useRef<HTMLElement>(null);
  useEffect(() => {
    function change() { setRoute(location.hash.slice(1) || 'home'); setNotice(''); setError(''); }
    addEventListener('hashchange', change); return () => removeEventListener('hashchange', change);
  }, []);
  useEffect(() => {
    stage.current?.focus({ preventScroll: true });
    window.scrollTo({ top: 0, behavior: 'instant' });
    document.title = `DSE Growth Loop｜${menus.find(m => m[0] === page)?.[2] ?? (page === 'profile' ? 'マイページ' : '相談')}`;
  }, [route]);
  const load = useCallback(async () => {
    const version = ++requestVersion.current; setLoading(true); setError('');
    try {
      const capability = await api<WorkspaceCapabilities>('/workspace/capabilities');
      if (version !== requestVersion.current) return;
      setCapabilities(capability);
      if (!(capability.contractVersion === "anonymous-workspace-v1" && capability.available && capability.structuredIdentifiersRemoved && capability.roomLocalAliases && capability.opaqueOperationIds && capability.legacyApiIsolation === "operator-or-development")) { setActivities([]); setCases([]); return; }
      const challenges = await readPages<Challenge>(api, '/workspace/challenges');
      const rooms = await readPages<RoomRow>(api, '/workspace/rooms');
      const invitations = await readPages<InvitationRow>(api, '/workspace/invitations');
      const publicCases = await api<{ items: Case[] }>('/workspace/knowledge');
      const next: Activity[] = challenges.map(challenge => ({ challenge, owner: challenge.isOwner, session: rooms.find(r => r.challenge.id === challenge.id), participation: invitations.find(i => i.challengeId === challenge.id) }));
      for (const invitation of invitations) if (!next.some(a => a.challenge.id === invitation.challengeId)) next.push({ challenge: { id: invitation.challengeId, roomId: invitation.roomId, title: invitation.title, summary: invitation.summary, status: invitation.status, isOwner: false, etag: invitation.etag }, owner: false, participation: invitation });
      for (const room of rooms) if (!next.some(a => a.challenge.id === room.challenge.id)) next.push({ challenge: room.challenge, owner: room.isOwner, session: room, participation: room.participation });
      if (version === requestVersion.current) { setActivities(next); setCases(publicCases.items); }
    } catch (e) { if (version === requestVersion.current) { setActivities([]); setCases([]); setCapabilities(undefined); setError(friendlyError(e)); } }
    finally { if (version === requestVersion.current) setLoading(false); }
  }, [api]);
  useEffect(() => { void load(); return () => { requestVersion.current++; }; }, [load]);
  // Consume the draft URL after a confirmed API creation, including Back/Forward revisits.
  const draftKey = id || 'new';
  const draft = drafts[`${person}:${draftKey}`] ?? blankDraft(draftKey);
  useEffect(() => {
    if (page !== 'compose') return;
    if (draft.posted) {
      const destination = `room/${draft.posted}`;
      history.replaceState(null, '', href(destination)); setRoute(destination);
      setNotice('この相談は投稿済みです。現在の活動を表示しました。');
    } else if (id && !id.startsWith('new-') && !drafts[`${person}:${id}`]) {
      history.replaceState(null, '', '#tasks'); setRoute('tasks'); setNotice('この下書きは見つかりません。');
    }
  }, [page, id, draft.posted, drafts, person]);
  const mine = activities.filter(a => a.owner);
  const invites = activities.filter(a => !a.owner && a.participation?.status === 'requested');
  const rooms = activities.filter(a => a.session);
  const myDrafts = Object.entries(drafts).filter(([key, d]) => key.startsWith(person + ':') && !d.posted && (d.title || d.detail));
  const selected = activities.find(a => a.challenge.id === id);
  const menuPage = ({ compose: 'tasks', invitation: 'invitations', room: 'rooms', case: 'knowledge' } as Record<string, string>)[page] ?? page;
  const go = (to: string) => { location.hash = to; };
  function updateDraft(value: Draft) { setDrafts(old => ({ ...old, [`${person}:${draftKey}`]: value })); }
  function saveDraft() {
    const key = id || crypto.randomUUID();
    setDrafts(old => ({ ...old, [`${person}:${key}`]: { ...draft, id: key }, ...(id ? {} : { [`${person}:new`]: blankDraft('new') }) }));
    go('tasks');
  }
  const blocked = !(capabilities?.contractVersion === "anonymous-workspace-v1" && capabilities.available && capabilities.structuredIdentifiersRemoved && capabilities.roomLocalAliases && capabilities.opaqueOperationIds && capabilities.legacyApiIsolation === "operator-or-development");
  return <div className="shell"><aside><p className="rail-title">経験が、誰かの次の一歩に。</p><nav id="menu" aria-label="メインメニュー">{menus.map(([to, icon, label]) => <a key={to} href={href(to)} aria-current={menuPage === to ? 'page' : undefined}><span className="navicon" aria-hidden="true">{icon}</span><span>{label}</span>{to === 'invitations' && invites.length > 0 && <span className="count">{invites.length}</span>}</a>)}</nav><div className="rail-bottom"><p className="rail-note">困ったときも、力を貸せるときも。<br />課題を起点に、匿名でつながる。</p>{config.mode === 'Demo' && <label>ローカル検証の役割<select aria-label="ローカル検証の役割" value={person} onChange={e => { requestVersion.current++; setCapabilities(undefined); setPerson(e.target.value); setActivities([]); setCases([]); setError(''); go('home'); }}><option value="demo-person-01">相談者（架空）</option><option value="demo-person-02">協力者（架空）</option><option value="demo-person-03">別の協力者（架空）</option></select></label>}<a href="/validation.html">既存機能の検証画面</a></div></aside><main id="stage" tabIndex={-1} ref={stage}><p className="demo-notice">{blocked ? '匿名契約と現在の権限を確認しています。確認できるまで相談・依頼・会話は開きません。' : config.mode === 'Demo' ? 'ローカル検証 · 架空のデータを匿名専用 API に保存します。下書きはこのページ内だけに保持し、再読込で消えます。' : '匿名専用 API で現在の閲覧権限と本文を確認します。氏名・連絡先・機密情報を入力しないでください。'}</p><div role="status" aria-live="polite" id="toast">{notice}</div>{error && <p role="alert">{error}</p>}<section className="stage">
      {blocked && page !== 'profile' ? <><Head title={menus.find(m => m[0] === menuPage)?.[2] ?? '相談'} /><div className="panel"><h2>匿名で安心して協力できるよう、接続を準備しています</h2><p>相手ごとの匿名名、実名開示の双方同意、公開する要約の確認が必要です。</p><Go to="profile">マイページへ</Go></div></> : <>
      {page === 'home' && <><Head title="ホーム" lead="困りごとを相談し、経験を持ち寄る。" /><div className="welcome"><div><p className="eyebrow">相談からはじめる</p><h2>いま、ひとりで抱えている課題はありますか？</h2><p>小さな相談も、匿名名で投稿できます。</p></div><Go to="compose" primary>課題・相談を投稿する</Go></div><Section title="次にすること"><div className="list">{myDrafts.map(([, d]) => <article key={d.id} className="list-row"><div><Pill>下書き</Pill><h3>{d.title || 'タイトル未入力の相談'}</h3><p>内容を整えて、相談を届けましょう。</p></div><Go to={`compose/${d.id}`}>続きを書く</Go></article>)}{invites.length > 0 && <article className="list-row"><div><h3>参加依頼が {invites.length} 件あります</h3><p>協力できるか、内容を見て決められます。</p></div><Go to="invitations">依頼を確認</Go></article>}{!myDrafts.length && !invites.length && <div className="list-row"><div><h3>最初の一歩は、どちらからでも。</h3><p>課題を相談する。自分の経験を登録して、誰かを助ける。</p></div><Go to="profile">経験を登録する</Go></div>}</div></Section><Section title="進行中の活動" to="rooms"><div className="cards">{rooms.filter(a => a.session?.status !== 'closed').map(a => <ActivityCard key={a.challenge.id} activity={a} />)}</div>{rooms.length === 0 && <p className="subtle">現在、進行中の活動はありません。</p>}</Section><Section title="届いている参加依頼" to="invitations"><div className="cards">{invites.slice(0, 2).map(a => <InviteCard key={a.challenge.id} activity={a} />)}</div>{!invites.length && <p className="subtle">回答待ちの依頼はありません。</p>}</Section><Section title="役立つ事例" to="knowledge"><div className="cards">{cases.slice(0, 2).map(c => <CaseCard key={c.id} item={c} />)}</div>{!cases.length && <p className="subtle">公開済みの参考事例はありません。</p>}</Section></>}
      {page === 'tasks' && <><div className="page-head"><Head title="自分の課題・相談" /><Go to="compose" primary>課題・相談を投稿する</Go></div><div className="tabs" aria-label="相談の絞り込み">{['すべて', '下書き', '参加待ち', '活動中', '完了'].map(f => <button key={f} className={filter === f ? 'active' : ''} aria-pressed={filter === f} onClick={() => setFilter(f)}>{f}</button>)}</div><div className="list">{['すべて', '下書き'].includes(filter) && myDrafts.map(([, d]) => <article className="list-row" key={d.id}><div><Pill>下書き</Pill><h3>{d.title || 'タイトル未入力の相談'}</h3></div><Go to={`compose/${d.id}`}>続きを書く</Go></article>)}{mine.filter(a => filter === 'すべて' || status(a) === filter).map(a => <ActivityCard key={a.challenge.id} activity={a} />)}{!mine.length && !myDrafts.length && <Empty text="まだ相談を投稿していません。" />}</div></>}
      {page === 'compose' && !draft.posted && (!id || id.startsWith('new-') || !!drafts[`${person}:${id}`]) && <Composer demo={config.mode === "Demo"} key={`${person}:${draftKey}`} draft={draft} update={updateDraft} saveDraft={saveDraft} api={api} onPosted={async challenge => { updateDraft({ ...draft, posted: challenge.id, pending: false }); await load(); if (location.hash === href(route)) go(`room/${challenge.id}`); }} />}
      {page === 'invitations' && <><Head title="参加依頼" lead="相談内容を確認し、できる範囲で協力できます。" /><div className="cards">{invites.map(a => <InviteCard key={a.challenge.id} activity={a} />)}</div>{!invites.length && <Empty text="回答待ちの参加依頼はありません。" />}</>}
      {page === 'invitation' && (selected ? <Invitation activity={selected} api={api} onChanged={async accepted => { await load(); go(accepted ? `room/${selected.challenge.id}` : 'invitations'); }} /> : <Missing loading={loading} />)}
      {page === 'rooms' && <><Head title="活動ルーム" lead="相談した活動も、協力している活動も、ここから。" /><div className="cards">{rooms.map(a => <ActivityCard key={a.challenge.id} activity={a} />)}</div>{!rooms.length && <Empty text="参加している活動はありません。" />}</>}
      {page === 'room' && (selected?.session ? <Room key={`${person}:${id}`} activity={selected} api={api} person={config.mode === "Demo" ? person : identitySession} config={config} onChanged={load} /> : <Missing loading={loading} />)}
      {page === 'knowledge' && <CaseLibrary api={api} initial={cases} />}
      {page === 'case' && <CaseDetail api={api} id={id} key={`${person}:${id}`} />}
      {page === 'profile' && <><Head title="マイページ" lead="自分の経験と、協力できる範囲を登録します。" /><div className="notice">このページは自分用です。実名開示・事例公開の同意は活動ごとに確認します。</div><Profiles key={`profile:${person}`} config={config} person={person} /><Documents key={`documents:${person}`} config={config} person={person} /><BusinessAnalysisPanel key={`analysis:${person}`} api={api} /></>}
      {!['home', 'tasks', 'compose', 'invitations', 'invitation', 'rooms', 'room', 'knowledge', 'case', 'profile'].includes(page) && <Missing loading={false} />}
      </>}
    </section><div className="workspace-refresh">{<button onClick={() => void load()} disabled={loading}>{loading ? '最新の状態を読み込み中…' : '最新の状態を読み込む'}</button>}</div><p className="privacy-foot">参加後も匿名名で活動。実名開示と事例公開は、それぞれ別の相互同意が必要です。本人プロフィールの表示名を共有するには、この活動で全員の現行同意が必要です。</p></main></div>;
}
function Missing({ loading }: { loading: boolean }) { return <><Head title={loading ? '読み込み中…' : '対象を開けません'} /><p>{loading ? '最新の参加状態を確認しています。' : '参加状態が変わったか、対象が見つかりません。最新の状態を読み込んでください。'}</p><Go to="home">ホームへ</Go></>; }
function Composer({ demo, draft, update, saveDraft, api, onPosted }: { demo: boolean; draft: Draft; update: (d: Draft) => void; saveDraft: () => void; api: Api; onPosted: (r: Challenge) => void }) {
  const [busy, setBusy] = useState(!!draft.pending), [error, setError] = useState(''), [discard, setDiscard] = useState(false);
  const submitting = useRef(!!draft.pending);
  async function post() {
    if (submitting.current || draft.posted) return;
    submitting.current = true; setBusy(true); setError(''); update({ ...draft, pending: true, uncertain: false });
    try { const row = await api<Challenge>('/workspace/challenges', 'POST', { title: draft.title.trim(), summary: draft.detail.trim() }, draft.key); onPosted(row); }
    catch (e) { const confirmedRejection = e instanceof WorkspaceError && [400, 401, 403, 404, 409, 412, 422, 429].includes(e.status); setError(friendlyError(e)); update({ ...draft, pending: false, uncertain: !confirmedRejection }); submitting.current = false; setBusy(false); }
  }
  return <><Head title={draft.id === 'new' || draft.id.startsWith('new-') ? '課題・相談を投稿する' : '下書きの編集'} lead="まだ整理できていなくても大丈夫です。相談したい点から書いてみましょう。" /><form className="panel form-width" onSubmit={e => { e.preventDefault(); void post(); }}><label htmlFor="title">課題のタイトル</label><input id="title" required maxLength={150} disabled={busy || !!draft.pending || !!draft.uncertain} value={draft.title} onChange={e => update({ ...draft, title: e.target.value, key: crypto.randomUUID() })} placeholder="例：調査記録を引き継ぐ方法を相談したい" /><label htmlFor="detail">困っていること・相談したいこと</label><textarea id="detail" required maxLength={2000} disabled={busy || !!draft.pending || !!draft.uncertain} value={draft.detail} onChange={e => update({ ...draft, detail: e.target.value, key: crypto.randomUUID() })} placeholder="状況、試したこと、相談したいこと" /><small>氏名・部署名・顧客名・機密情報は入力しないでください。</small><Privacy />{error && <p role="alert">{error}</p>}{draft.uncertain && <p role="status">投稿の結果を確認できていません。内容と同じキーを保持して再試行します。</p>}<div className="actions"><button className="primary" disabled={busy}>{busy ? '保存を確認中…' : draft.uncertain ? '同じ内容で投稿を再確認' : demo ? '匿名名で投稿する（ローカル）' : '匿名名で投稿する'}</button><button type="button" disabled={busy || !!draft.pending || !!draft.uncertain} onClick={saveDraft}>下書きにする</button><button type="button" disabled={busy || !!draft.pending || !!draft.uncertain} className="danger" aria-expanded={discard} onClick={() => setDiscard(true)}>未保存の入力を破棄</button></div>{discard && <div className="notice" role="group" aria-label="入力の破棄確認"><p>未保存の入力を破棄しますか？</p><button type="button" onClick={() => setDiscard(false)}>入力を続ける</button>{' '}<button type="button" className="danger" onClick={() => { update(blankDraft(draft.id)); setDiscard(false); }}>破棄する</button></div>}<small>投稿は匿名専用 API に保存されます。下書きは戻る・進むやメニュー移動でも保持します。再読込すると下書きは消えます。参加候補は活動内の匿名名で確認します。通知の外部送信は行いません。</small></form></>;
}
function Invitation({ activity, api, onChanged }: { activity: Activity; api: Api; onChanged: (accepted: boolean) => Promise<void> }) {
  const [busy, setBusy] = useState(false), [error, setError] = useState('');
  const key = useRef(crypto.randomUUID());
  async function decide(decision: string) {
    setBusy(true); setError('');
    try {
      const { items } = await api<{ items: InvitationRow[] }>('/workspace/invitations');
      const own = items.find(p => p.id === activity.participation?.id);
      if (!own || own.status !== 'requested') { await onChanged(own?.status === 'accepted'); return; }
      await api(`/workspace/invitations/${own.id}`, 'PATCH', { decision }, key.current, own.etag);
      await onChanged(decision === 'accepted');
    } catch (e) { setError(friendlyError(e)); }
    finally { setBusy(false); }
  }
  return <><Go to="invitations">参加依頼一覧へ</Go><Head title={activity.challenge.title} /><div className="panel"><Pill>{activity.participation?.status === 'requested' ? '回答待ち' : '回答済み'}</Pill><h2>相談したいこと</h2><p className="preserve-lines">{activity.challenge.summary}</p><Privacy /><p>「参加する」で活動を開始します。依頼者の追加承認は不要です。実名・所属の開示への同意にはなりません。</p>{error && <p role="alert">{error}</p>}<div className="actions"><button className="primary" disabled={busy || activity.participation?.status !== 'requested'} onClick={() => void decide('accepted')}>参加する</button><button disabled={busy || activity.participation?.status !== 'requested'} onClick={() => void decide('declined')}>今回は辞退する</button></div></div></>;
}
function Room({ activity, api, person, config, onChanged }: { activity: Activity; api: Api; person: string; config: AppConfig; onChanged: () => Promise<void> }) {
  const path = `/workspace/rooms/${activity.session!.id}`;
  const [session, setSession] = useState(activity.session!);
  const [revoked, setRevoked] = useState(false);
  const [messages, setMessages] = useState<MessageRow[]>([]);
  const [text, setText] = useState(''), [error, setError] = useState(''), [busy, setBusy] = useState(false), [confirmClose, setConfirmClose] = useState(false);
  const pending = useRef<{ body: string; key: string; clientMessageId: string } | undefined>(undefined);
  const active = useRef(true), lock = useRef(false);
  const [pendingState, setPendingState] = useState(false);
  const request = useRef(0);
  const storageKey = `dse-workspace-outbox:${config.tenantId}:${person}:${activity.session!.id}`;
  useEffect(() => {
    try { const saved = JSON.parse(sessionStorage.getItem(storageKey) ?? 'null'); if (saved?.body && saved?.key && saved?.clientMessageId) { pending.current = saved; setText(saved.body); setPendingState(true); } } catch { /* Invalid local pending data is not a saved message. */ }
  }, [storageKey]);
  const refresh = useCallback(async () => {
    const generation = ++request.current;
    try {
      const current = await api<RoomRow>(path);
      const all = await readPages<MessageRow>(api, `${path}/messages?afterSequence=0`);
      if (!active.current || generation !== request.current) return;
      setSession(current); setMessages(all); setRevoked(false);
      if (pending.current && all.some(m => m.clientMessageId === pending.current?.clientMessageId && m.body === pending.current?.body)) {
        pending.current = undefined; setPendingState(false); setText(''); sessionStorage.removeItem(storageKey);
      }
    } catch (e) {
      if (!active.current || generation !== request.current) return;
      setMessages([]); setRevoked(true);
      setError(friendlyError(e));
    }
  }, [api, path, storageKey]);
  useEffect(() => {
    active.current = true; void refresh();
    const timer = setInterval(() => void refresh(), 10000);
    return () => { active.current = false; request.current++; clearInterval(timer); };
  }, [refresh]);
  async function send() {
    if (lock.current || session.status === 'closed') return;
    lock.current = true; setBusy(true); setError('');
    const message = pending.current ?? { body: text.trim(), clientMessageId: crypto.randomUUID(), key: crypto.randomUUID() };
    pending.current = message; setPendingState(true); sessionStorage.setItem(storageKey, JSON.stringify(message));
    try {
      const current = await api<RoomRow>(path);
      await api(`${path}/messages`, 'POST', { body: message.body, clientMessageId: message.clientMessageId }, message.key, current.etag);
      if (active.current) { await refresh(); }
    } catch (e) { if (active.current) { if (e instanceof WorkspaceError && [400, 422].includes(e.status)) { pending.current = undefined; setPendingState(false); sessionStorage.removeItem(storageKey); } setError(friendlyError(e)); } }
    finally { lock.current = false; if (active.current) setBusy(false); }
  }
  const [candidates, setCandidates] = useState<{ token: string; aliasLabel: string; matchingRoutes: string[] }[]>([]);
  async function findCandidates() {
    setBusy(true); setError(''); setCandidates([]);
    try { const result = await api<{ items: typeof candidates }>(`/workspace/challenges/${activity.challenge.id}/candidates`); setCandidates(result.items); }
    catch (e) { setError(friendlyError(e)); } finally { setBusy(false); }
  }
  async function invite(candidateToken: string) {
    if (lock.current) return; lock.current = true; setBusy(true); setError('');
    try {
      const current = await api<RoomRow>(path);
      await api(`${path}/invitations`, 'POST', { candidateToken }, crypto.randomUUID(), current.etag);
      setCandidates([]); await refresh();
    } catch (e) { setError(friendlyError(e)); }
    finally { lock.current = false; setBusy(false); }
  }
  async function close() {
    setBusy(true); setError('');
    try { const current = await api<RoomRow>(path); await api(path, 'PATCH', { status: 'closed', aiAssistance: current.aiAssistance }, crypto.randomUUID(), current.etag); await refresh(); await onChanged(); setConfirmClose(false); }
    catch (e) { setError(friendlyError(e)); } finally { setBusy(false); }
  }
  if (revoked) return <><Missing loading={false} /><p role="alert">{error}</p><button onClick={() => void refresh()}>参加状態を再確認</button></>;
  return <><Go to="rooms">活動ルーム一覧へ</Go><Head title={session.challenge.title} /><div className="status-line"><Pill teal={session.status !== 'closed'}>{session.status === 'closed' ? '完了' : session.participantCount > 1 ? '活動中' : '参加待ち'}</Pill><span className="tag">{session.isOwner ? '相談した活動' : '協力している活動'}</span></div><Privacy /><div className="split"><div className="panel"><div className="chat-header"><h2>活動の会話</h2><button disabled={busy} onClick={() => { setError(''); void refresh(); }}>会話を更新</button></div><p className="subtle">{session.participantCount > 1 ? session.participants.map(p => p.aliasLabel).join(" / ") + " · 参加者だけが閲覧できます。" : session.participants.map(p => p.aliasLabel).join(" / ") + " · 協力者の参加待ち"}</p>{error && <p role="alert">{error}</p>}<ul className="chat-messages" aria-label="保存済みの会話">{messages.map(m => <li key={m.id}><strong>{m.authorType === 'ai' ? 'AI支援' : m.authorAliasLabel}</strong><p>{m.body}</p><small>保存済み</small></li>)}</ul>{!messages.length && <p className="subtle">まだメッセージはありません。相談の状況を共有しましょう。</p>}<form className="room-message" onSubmit={e => { e.preventDefault(); void send(); }}><label htmlFor="message">メッセージ</label><textarea id="message" required maxLength={8000} value={text} readOnly={pendingState} disabled={busy || session.status === 'closed'} onChange={e => setText(e.target.value)} /><div className="actions"><button className="primary" disabled={busy || session.status === 'closed' || !text.trim()}>{busy ? '保存を確認中…' : pendingState ? '同じ内容で保存を再確認' : config.mode === 'Demo' ? '送信する（ローカル）' : '送信する'}</button></div>{pendingState && <p role="status">未保存（結果未確認）。同じキーで再試行し、重複を防ぎます。</p>}</form></div><div><div className="panel"><h2>今回の相談</h2><p className="preserve-lines">{session.challenge.summary}</p><IdentityDisclosure api={api} roomId={session.id} />{session.isOwner && session.status !== 'closed' && <><h2>活動を終える</h2><button disabled={busy} onClick={() => setConfirmClose(true)}>活動を完了する</button>{confirmClose && <div className="notice"><p>活動を完了すると会話は閲覧のみになります。</p><button disabled={busy} onClick={() => setConfirmClose(false)}>続ける</button>{' '}<button disabled={busy} onClick={() => void close()}>完了を確定</button></div>}</>}</div>{session.isOwner && session.status !== 'closed' && <details className="panel"><summary>匿名で協力を依頼する</summary><p>経験が関連する協力候補を、この活動内の匿名名で確認します。</p><button disabled={busy} onClick={() => void findCandidates()}>匿名の候補を確認</button>{candidates.map(candidate => <article key={candidate.token}><h3>{candidate.aliasLabel}</h3><p>{candidate.matchingRoutes.join(' / ')}</p><button disabled={busy} onClick={() => void invite(candidate.token)}>{candidate.aliasLabel}に参加依頼</button></article>)}</details>}</div></div><details className="panel"><summary>振り返りと事例公開</summary><p>実名開示とは別に、要約の版と公開範囲について双方の同意が必要です。要約を編集すると公開同意と承認が取り消されます。</p><Knowledge anonymous api={api} sid={activity.session!.id} owner={session.isOwner} sessionEtag={session.etag} onChanged={() => void onChanged()} /></details></>;
}
function CaseLibrary({ api, initial }: { api: Api; initial: Case[] }) {
  const [query, setQuery] = useState(''), [items, setItems] = useState(initial), [error, setError] = useState(''), [busy, setBusy] = useState(false);
  const generation = useRef(0);
  useEffect(() => () => { generation.current++; }, []);
  useEffect(() => setItems(initial), [initial]);
  async function search() {
    if (!query.trim()) { generation.current++; setItems(initial); setError(''); return; }
    const run = ++generation.current; setBusy(true); setError(''); setItems([]);
    try { const value = await api<{ items: Case[] }>(`/workspace/knowledge?query=${encodeURIComponent(query.trim())}`); if (run === generation.current) setItems(value.items); }
    catch { if (run === generation.current) setError('事例を取得できませんでした。再試行してください。'); }
    finally { if (run === generation.current) setBusy(false); }
  }
  return <><Head title="事例・ナレッジ" lead="協力して得た知見を、次の相談へ。" /><form className="search" onSubmit={e => { e.preventDefault(); void search(); }}><label htmlFor="case-search">事例を検索</label><input id="case-search" maxLength={200} value={query} disabled={busy} onChange={e => setQuery(e.target.value)} placeholder="例：引き継ぎ、レビュー" /><button disabled={busy}>事例を探す</button></form>{error && <p role="alert">{error}</p>}<div className="cards">{items.map(c => <CaseCard key={c.id} item={c} />)}</div>{!items.length && !busy && <p className="subtle">一致する事例はありません。別の言葉でお試しください。</p>}<p className="subtle">公開を許可された現在の版だけを表示します。原会話と個人プロフィールへのリンクは表示しません。</p></>;
}
function CaseDetail({ api, id }: { api: Api; id: string }) {
  const [row, setRow] = useState<Case>(), [error, setError] = useState('');
  useEffect(() => {
    let active = true; setRow(undefined); setError('');
    async function load() {
      try { const item = await api<Case>(`/workspace/knowledge/${id}`); if (active) { setRow(item); setError(''); } }
      catch { if (active) { setRow(undefined); setError('この事例は公開が撤回されたか、現在は取得できません。'); } }
    }
    void load(); const timer = setInterval(() => void load(), 5000);
    return () => { active = false; clearInterval(timer); };
  }, [id, api]);
  return <><Go to="knowledge">事例一覧へ</Go>{error ? <p role="alert">{error}</p> : row ? <><Head title={row.title} /><article className="panel case-body"><h2>取り組んだこと</h2>{row.sections.map(section => <p key={section.field}>{section.text}</p>)}<div className="privacy"><strong>公開同意は実名開示の同意ではありません</strong>次の相談に知見を役立てましょう。</div><Go to="compose" primary>この知見を参考に相談する</Go></article></> : <p role="status">事例を読み込み中…</p>}</>;
}
