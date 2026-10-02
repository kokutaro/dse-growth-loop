import { useEffect, useState } from 'react';

type Api = <T>(path: string, method?: string, body?: unknown, key?: string, etag?: string) => Promise<T>;
type Section = { field: string; text: string; basis: string; evidenceIds: string[] };
type State = { etag: string; sessionEtag: string; status: string; sourceVersion: number; sections: Section[]; scopeIds: string[]; evidenceIds?: string[];
  publicationJob?: { status: string; lastErrorCode?: string }; canApprove: boolean; processingMode?: string; consents: Record<string, { publication: boolean; analysis: boolean }>;
  outcome?: { resolution: string; result: string }; feedback: { evaluatorId: string; value: number | null }[] };
const names: Record<string, string> = { challenge: '課題の要約', assumptions: '前提', attempts: '試したこと', result: '結果の要約', reuseConditions: '再利用条件', unresolvedPoints: '未解決点' };

export function Knowledge({ api, sid, owner, sessionEtag }: { api: Api; sid: string; owner: boolean; sessionEtag: string }) {
  const path = `/sessions/${sid}/knowledge`;
  const [state, setState] = useState<State>(), [sections, setSections] = useState<Section[]>([]);
  const [resolution, setResolution] = useState('unresolved'), [result, setResult] = useState(''), [value, setValue] = useState(''), [comment, setComment] = useState('');
  const [scopeText, setScopeText] = useState('');
  const [publication, setPublication] = useState(false), [analysis, setAnalysis] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('');
  async function load(reset = false) {
    const current = await api<State>(path); setState(current);
    if (reset) { setSections(current.sections); setScopeText(current.scopeIds.join(', ')); setPublication(false); setAnalysis(false); }
  }
  useEffect(() => { void load(true).catch(() => setError('要約を取得できませんでした。')); }, [api, path]);
  useEffect(() => { if (state?.status === 'absent') void load(false).catch(() => setError('最新の保存版を取得できませんでした。')); }, [sessionEtag]);
  async function act(operation: string, body: unknown = {}) {
    if (!state) return; setBusy(true); setError('');
    try {
      const current = state.status === 'absent' ? await api<State>(path) : state;
      await api(`${operation === 'recover' ? `/sessions/${sid}/knowledge-recovery` : `${path}/${operation}`}`, 'POST', body, crypto.randomUUID(), operation === 'draft' ? current.sessionEtag : current.etag);
      await load(true);
    } catch (e) { setError(`保存・操作が完了していません。状態を再読込して再試行してください。${e instanceof Error ? e.message : ''}`); }
    finally { setBusy(false); }
  }
  if (!state) return <div><p role="status">評価・要約を読み込み中…</p>{error && <p role="alert">{error}</p>}<button onClick={() => void load(true).catch(() => setError('要約を取得できませんでした。'))}>評価・要約を再読込</button></div>;
  return <section aria-label="評価と公開知識">
    <h4>解決評価と知識化</h4>
    <p>解決状態と相談の有用性を別に保存します。評価は因果効果、AIの学習更新完了、人事評価を表しません。尺度は暫定案です。</p>
    <button onClick={() => void load(true)} disabled={busy}>評価・要約を再読込</button>
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
    <p data-testid="saved-feedback">保存した評価：{state.feedback.map(f => `${f.evaluatorId}：${f.value ?? '未回答'}`).join(' / ') || '未回答'}</p>
    <p data-testid="knowledge-status">要約状態：{state.status} · 版 {state.sourceVersion} · {state.processingMode === 'fixture' ? 'ダミーAI' : state.processingMode === 'live' ? '実AI' : '下書きなし'}</p>
    {state.publicationJob && <p>検索への反映：{state.publicationJob.status} {state.publicationJob.lastErrorCode ?? ''}</p>}
    {owner && state.status === 'absent' && <button disabled={busy || !state.outcome} onClick={() => void act('draft')}>AI下書きを作成</button>}
    {state.sections.length > 0 && <>
      <p>利用できる根拠ID：{state.evidenceIds?.join(', ')}</p>
      <p>公開先：{state.scopeIds.join(', ')}。原会話の閲覧許可、知識の公開同意、事業分析の同意は別です。根拠IDは参加者だけに表示します。</p>
      <form onSubmit={e => { e.preventDefault(); void act('edit', { sections, scopeIds: scopeText.split(',').map(s => s.trim()).filter(Boolean) }); }}>
        {owner && <label>公開先scope（会話許可範囲内・カンマ区切り）<input required value={scopeText} onChange={e => setScopeText(e.target.value)} /></label>}
        {sections.map((section, i) => <div key={section.field}>
          <label>{names[section.field]}<textarea required maxLength={1500} value={section.text} readOnly={!owner} onChange={e => setSections(prev => prev.map((s, n) => n === i ? { ...s, text: e.target.value } : s))} /></label>
          <label>{names[section.field]}の情報源<select value={section.basis} disabled={!owner} onChange={e => setSections(prev => prev.map((s, n) => n === i ? { ...s, basis: e.target.value } : s))}>
            <option value="participant_report">参加者の報告</option><option value="inference">推測（要確認）</option><option value="unknown">不明</option></select></label>
          {owner && <label>{names[section.field]}の根拠ID（カンマ区切り）<input value={section.evidenceIds.join(', ')} onChange={e => setSections(prev => prev.map((s, n) => n === i ? { ...s, evidenceIds: e.target.value.split(',').map(id => id.trim()).filter(Boolean) } : s))} /></label>}
          <small>根拠：{section.evidenceIds.join(', ') || 'なし（不明）'}</small>
        </div>)}
        {owner && <button disabled={busy}>要約を保存</button>}
      </form>
      {owner && state.status === 'draft' && <button disabled={busy} onClick={() => void act('request-review', { version: state.sourceVersion })}>レビューを依頼</button>}
      {['review_requested', 'published'].includes(state.status) && <>
        <label><input type="checkbox" checked={publication} onChange={e => setPublication(e.target.checked)} />この版と公開先を確認して公開に同意</label>
        <label><input type="checkbox" checked={analysis} onChange={e => setAnalysis(e.target.checked)} />この版の要約を事業分析に使うことに同意</label>
        <button disabled={busy} onClick={() => void act('consent', { version: state.sourceVersion, publication, analysis })}>同意を保存</button>
        <p data-testid="knowledge-consents">版ごとの確認：{Object.entries(state.consents).map(([person, c]) => `${person} 公開${c.publication ? '同意' : '不同意'}・分析${c.analysis ? '同意' : '不同意'}`).join(' / ') || '確認待ち'}</p>
      </>}
      {state.canApprove && state.status === 'review_requested' && <button disabled={busy} onClick={() => void act('approve', { version: state.sourceVersion })}>この版を承認</button>}
      {state.status === 'published' && <><button disabled={busy} onClick={() => void act('withdraw')}>公開を撤回</button><button disabled={busy} onClick={() => void act('recover')}>検索への反映を再試行</button></>}
      <p>編集中に変更した要約は再承認が必要です。検索への反映が失敗しても承認Jobは保持され、再試行できます。</p>
    </>}
  </section>;
}
