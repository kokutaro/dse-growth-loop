import { useEffect, useRef, useState } from 'react';
import { friendlyError, WorkspaceError } from './workspace-api';

type Api = <T>(path: string, method?: string, body?: unknown, key?: string, etag?: string) => Promise<T>;
type Section = { field: string; text: string; basis: string; evidenceIds: string[] };
type AnalysisMetadata = { departmentId: string | null; resolution: string | null; reportedHours: number | null; recurrences: number | null; feedback: number | null };
type State = { occurrenceDepartmentVerified: boolean; reviewedVersion: string; etag: string; sessionEtag: string; status: string; sourceVersion: number; sections: Section[]; scopeIds: string[]; evidenceIds?: string[];
  publicationJob?: { status: string; lastErrorCode?: string }; canApprove: boolean; processingMode?: string; consents: Record<string, { publication: boolean; analysis: boolean }>;
  analysisMetadata?: AnalysisMetadata | null; outcome?: { resolution: string; result: string }; feedback: { evaluatorId: string; value: number | null }[]; participantLabels?: Record<string, string>; scopeLabels?: Record<string, string>; evidenceLabels?: Record<string, string> };
const names: Record<string, string> = { challenge: '課題の要約', assumptions: '前提', attempts: '試したこと', result: '結果の要約', reuseConditions: '再利用条件', unresolvedPoints: '未解決点' };

export function Knowledge({ api, sid, owner, sessionEtag, onChanged, participantLabel, anonymous = false }: { anonymous?: boolean; participantLabel?: (id: string) => string; onChanged?: () => void; api: Api; sid: string; owner: boolean; sessionEtag: string }) {
  const path = anonymous ? `/workspace/rooms/${sid}/knowledge` : `/sessions/${sid}/knowledge`;
  const label = (id: string) => state?.participantLabels?.[id] ?? participantLabel?.(id) ?? (anonymous ? "参加者" : id);
  const [state, setState] = useState<State>(), [sections, setSections] = useState<Section[]>([]);
  const [resolution, setResolution] = useState('unresolved'), [result, setResult] = useState(''), [value, setValue] = useState(''), [comment, setComment] = useState('');
  const [scopeText, setScopeText] = useState('');
  const [metadata, setMetadata] = useState<AnalysisMetadata>({ departmentId: null, resolution: null, reportedHours: null, recurrences: null, feedback: null });
  const [publication, setPublication] = useState(false), [analysis, setAnalysis] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('');
  const metadataValues = (value?: AnalysisMetadata | null) => [value?.departmentId ?? null, value?.resolution ?? null, value?.reportedHours ?? null, value?.recurrences ?? null, value?.feedback ?? null];
  const dirty = !!state && (JSON.stringify(sections) !== JSON.stringify(state.sections) ||
    JSON.stringify(scopeText.split(',').map(s => s.trim()).filter(Boolean)) !== JSON.stringify(state.scopeIds) ||
    JSON.stringify(metadataValues(metadata)) !== JSON.stringify(metadataValues(state.analysisMetadata)));
  const missingEvidence = anonymous && sections.some(section => section.basis !== 'unknown' && !section.evidenceIds.length);
  const loadGeneration = useRef(0), active = useRef(true);
  async function load(reset = false) {
    const generation = ++loadGeneration.current;
    let current: State;
    try { current = await api<State>(path); } catch (e) { if (anonymous && active.current && generation === loadGeneration.current) { setState(undefined); setPublication(false); setAnalysis(false); } throw e; }
    if (!active.current || generation !== loadGeneration.current) return;
    setState(current);
    if (reset) { setSections(current.sections); setScopeText(current.scopeIds.join(', ')); setPublication(false); setAnalysis(false); setMetadata(current.analysisMetadata ?? { departmentId: null, resolution: null, reportedHours: null, recurrences: null, feedback: null }); }
  }
  useEffect(() => { active.current = true; setState(undefined); setPublication(false); setAnalysis(false); void load(true).catch(() => { if (active.current) setError('要約を取得できませんでした。'); }); return () => { active.current = false; loadGeneration.current++; }; }, [api, path]);
  useEffect(() => { if (state?.status === 'absent') void load(false).catch(() => setError('最新の保存版を取得できませんでした。')); }, [sessionEtag]);
  async function act(operation: string, body: unknown = {}) {
    if (dirty && ['consent', 'approve', 'request-review'].includes(operation)) { setError('未保存の編集があります。保存した版を双方で確認してから同意・承認してください。'); return; }
    if (operation === 'edit' && missingEvidence) { setError('参加者の報告・推測には、この活動の確認記録を選んでください。根拠がない場合は情報源を「不明」にしてください。'); return; }
    if (!state) return;
    if (['consent', 'approve', 'request-review'].includes(operation)) body = { ...(body as object), reviewedVersion: state.reviewedVersion };
    setBusy(true); setError('');
    try {
      const current = state.status === 'absent' ? await api<State>(path) : state;
      await api(`${operation === 'recover' ? `${anonymous ? "/workspace/rooms" : "/sessions"}/${sid}/knowledge-recovery` : `${path}/${operation}`}`, 'POST', body, crypto.randomUUID(), operation === 'draft' ? current.sessionEtag : current.etag);
      if (['outcome', 'edit', 'consent', 'approve', 'withdraw'].includes(operation)) onChanged?.();
      await load(true);
    } catch (e) { if (anonymous && e instanceof WorkspaceError && [401, 403, 404].includes(e.status)) { setState(undefined); setPublication(false); setAnalysis(false); } setError(anonymous ? `保存・操作が完了していません。${friendlyError(e)}${e instanceof WorkspaceError && e.code === 'CONSENT_REQUIRED' ? ' CONSENT_REQUIRED' : ''}` : `保存・操作が完了していません。状態を再読込して再試行してください。${e instanceof Error ? e.message : ''}`); }
    finally { setBusy(false); }
  }
  if (!state) return <div><p role="status">評価・要約を読み込み中…</p>{error && <p role="alert">{error}</p>}<button onClick={() => void load(true).catch(() => setError('要約を取得できませんでした。'))}>評価・要約を再読込</button></div>;
  return <section aria-label="評価と公開知識">
    <h4>解決評価と知識化</h4>
    <p>解決状態と相談の有用性を別に保存します。評価は因果効果、AIの学習更新完了、人事評価を表しません。尺度は暫定案です。</p>
    <button onClick={() => void load(true).catch(() => setError("要約を取得できませんでした。"))} disabled={busy}>評価・要約を再読込</button>
    {error && <p role="alert">{error}</p>}
    {owner && <form onSubmit={e => { e.preventDefault(); void act('outcome', { resolution, result }); }}>
      <label>解決状態<select value={resolution} onChange={e => setResolution(e.target.value)}><option value="resolved">解決</option><option value="partial">部分解決</option><option value="unresolved">未解決</option></select></label>
      <label>相談の結果<textarea required maxLength={4000} value={result} onChange={e => setResult(e.target.value)} /></label>
      <button disabled={busy}>結果を保存</button>
    </form>}
    <p data-testid="saved-outcome">保存した結果：{state.outcome ? `${state.outcome.resolution} · ${state.outcome.result}` : '未登録'}</p>
    <form onSubmit={e => { e.preventDefault(); void act('feedback', { value: value ? Number(value) : null, comment }); }}>
      <label>相談の有用性<select value={value} onChange={e => setValue(e.target.value)}><option value="">未回答</option>
        {['役立たなかった', 'あまり役立たなかった', 'どちらともいえない', '役立った', 'とても役立った'].map((label, i) => <option key={i} value={i + 1}>{i + 1}：{label}</option>)}</select></label>
      <label>評価コメント（任意）<textarea maxLength={2000} value={comment} onChange={e => setComment(e.target.value)} /></label><button disabled={busy}>評価を保存</button>
    </form>
    <p data-testid="saved-feedback">保存した評価：{state.feedback.map(f => `${label(f.evaluatorId)}：${f.value ?? '未回答'}`).join(' / ') || '未回答'}</p>
    <p data-testid="knowledge-status">要約状態：{state.status} · 版 {state.sourceVersion} · {state.processingMode === 'fixture' ? 'ダミーAI' : state.processingMode === 'live' ? '実AI' : '下書きなし'}</p>
    {state.publicationJob && <p>検索への反映：{state.publicationJob.status} {state.publicationJob.lastErrorCode ?? ''}</p>}
    {owner && state.status === 'absent' && <button disabled={busy || !state.outcome} onClick={() => void act('draft')}>AI下書きを作成</button>}
    {state.sections.length > 0 && <>
      {!anonymous && <p>利用できる根拠ID：{state.evidenceIds?.join(', ')}</p>}
      <p>公開先：{anonymous ? state.scopeIds.map(id => state.scopeLabels?.[id] ?? "許可された共有範囲").join(" / ") : state.scopeIds.join(", ")}。原会話の閲覧許可、知識の公開同意、事業分析の同意は別です。</p>
      <form onSubmit={e => { e.preventDefault(); void act('edit', { sections, scopeIds: scopeText.split(',').map(s => s.trim()).filter(Boolean), analysisMetadata: metadata }); }}>
        {owner && !anonymous && <label>公開先scope（会話許可範囲内・カンマ区切り）<input required disabled={busy} value={scopeText} onChange={e => setScopeText(e.target.value)} /></label>}
        {sections.map((section, i) => <div key={section.field}>
          <label>{names[section.field]}<textarea required maxLength={1500} value={section.text} readOnly={!owner || busy} onChange={e => setSections(prev => prev.map((s, n) => n === i ? { ...s, text: e.target.value } : s))} /></label>
          <label>{names[section.field]}の情報源<select value={section.basis} disabled={!owner || busy} onChange={e => setSections(prev => prev.map((s, n) => n === i ? { ...s, basis: e.target.value } : s))}>
            <option value="participant_report" disabled={anonymous && !section.evidenceIds.length}>参加者の報告</option><option value="inference" disabled={anonymous && !section.evidenceIds.length}>推測（要確認）</option><option value="unknown">不明</option></select></label>
          {owner && anonymous && <details><summary>{names[section.field]}の確認記録を選ぶ</summary><p>この活動で利用を許可された記録を選びます。原文や内部識別情報は表示しません。</p>{state.evidenceIds?.length ? state.evidenceIds.map((token, n) => <label key={token}><input type="checkbox" disabled={busy} checked={section.evidenceIds.includes(token)} onChange={e => setSections(prev => prev.map((s, index) => index === i ? { ...s, evidenceIds: e.target.checked ? [...s.evidenceIds, token] : s.evidenceIds.filter(id => id !== token) } : s))} />{state.evidenceLabels?.[token] ?? `この活動の記録${n + 1}`}</label>) : <p>利用できる確認記録がありません。情報源は「不明」として保存してください。</p>}</details>}
          {owner && !anonymous && <label>{names[section.field]}の根拠ID（カンマ区切り）<input disabled={busy} value={section.evidenceIds.join(', ')} onChange={e => setSections(prev => prev.map((s, n) => n === i ? { ...s, evidenceIds: e.target.value.split(',').map(id => id.trim()).filter(Boolean) } : s))} /></label>}
          <small>根拠：{anonymous ? (section.evidenceIds.length ? "この活動の確認済み記録" : "なし（不明）") : section.evidenceIds.join(", ") || "なし（不明）"}</small>
        </div>)}
        <fieldset><legend>公開・分析に使う申告情報（任意）</legend><p>この情報も公開先と要約の版に含めて双方が確認します。空欄は不明として扱います。氏名・実際の部署名を入力しないでください。</p>
          <p>発生部署：{state.occurrenceDepartmentVerified ? '課題作成時点の主部署をダミー正本で確認済み' : '所属履歴が不足しているため不明'}。現在の異動先へ過去の課題を付け替えません。</p>
          {!anonymous && <label>確認済み部署コード<input readOnly value={metadata.departmentId ?? ''} /></label>}
          <label>集計に公開する結果区分<select value={metadata.resolution ?? ''} disabled={!owner || busy} onChange={e => setMetadata(m => ({ ...m, resolution: e.target.value || null }))}><option value="">不明</option><option value="resolved">解決</option><option value="partial">部分解決</option><option value="unresolved">未解決</option></select></label>
          <label>申告工数（時間）<input type="number" min="0" max="100000" step="0.01" readOnly={!owner || busy} value={metadata.reportedHours ?? ''} onChange={e => setMetadata(m => ({ ...m, reportedHours: e.target.value === '' ? null : Number(e.target.value) }))} /></label>
          <label>申告した発生回数<input type="number" min="1" max="10000" step="1" readOnly={!owner || busy} value={metadata.recurrences ?? ''} onChange={e => setMetadata(m => ({ ...m, recurrences: e.target.value === '' ? null : Number(e.target.value) }))} /></label>
          <label>公開する申告評価<select disabled={!owner || busy} value={metadata.feedback ?? ''} onChange={e => setMetadata(m => ({ ...m, feedback: e.target.value === '' ? null : Number(e.target.value) }))}><option value="">未評価</option>{[1, 2, 3, 4, 5].map(v => <option key={v} value={v}>{v}</option>)}</select></label>
        </fieldset>
        {missingEvidence && <p role="status">参加者の報告・推測には確認記録が必要です。記録を選ぶか、情報源を「不明」にしてください。</p>}
        {owner && <button disabled={busy || missingEvidence}>要約を保存</button>}
      </form>
      {dirty && <p role="status">未保存の編集があります。入力を保存すると同意と承認を取り消し、保存した版を双方で再確認します。</p>}
      {owner && state.status === 'draft' && <button disabled={busy || dirty} onClick={() => void act('request-review', { version: state.sourceVersion })}>レビューを依頼</button>}
      {['review_requested', 'published'].includes(state.status) && <>
        <label><input type="checkbox" disabled={busy || dirty} checked={publication} onChange={e => setPublication(e.target.checked)} />この版と公開先を確認して公開に同意</label>
        <label><input type="checkbox" disabled={busy || dirty} checked={analysis} onChange={e => setAnalysis(e.target.checked)} />この版の要約を事業分析に使うことに同意</label>
        <p>分析同意には、課題作成時点の確認済み主部署と、この版の解決状態・申告工数・発生回数・申告評価が含まれます。申告情報だけを編集した場合も、同意と承認を取り消し、双方で再確認します。</p>
        <button disabled={busy || dirty} onClick={() => void act('consent', { version: state.sourceVersion, publication, analysis })}>同意を保存</button>
        <p data-testid="knowledge-consents">版ごとの確認：{Object.entries(state.consents).map(([person, c]) => `${label(person)} 公開${c.publication ? '同意' : '不同意'}・分析${c.analysis ? '同意' : '不同意'}`).join(' / ') || '確認待ち'}</p>
      </>}
      {state.canApprove && state.status === 'review_requested' && <button disabled={busy || dirty} onClick={() => void act('approve', { version: state.sourceVersion })}>この版を承認</button>}
      {state.status === 'published' && <><button disabled={busy} onClick={() => void act('withdraw')}>公開を撤回</button><button disabled={busy} onClick={() => void act('recover')}>検索への反映を再試行</button></>}
      <p>編集中に変更した要約は再承認が必要です。検索への反映が失敗しても承認Jobは保持され、再試行できます。</p>
    </>}
  </section>;
}
