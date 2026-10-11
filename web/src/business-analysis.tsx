import { useEffect, useRef, useState } from 'react';
import { friendlyError, WorkspaceError, type Api } from './workspace-api';

type Group = { id: string; decision?: string; metrics: { independentChallenges: number; distinctDepartments: number; recurringChallenges: number | null; unresolvedRatio: number | null; reportedHours: number | null; missingMetadata: string[] }; hypothesis?: Record<string, unknown>; evidenceIds: string[] };
type Run = { runId: string; status: string; etag: string; processingMode: string; sourceCount: number; groups: Group[]; limits?: { maxInputs: number; timeoutSeconds: number; maxConcurrencyPerTenant: number }; recoveryRequired?: boolean; decision?: string; exclusions?: { reason: string }[] };
const labels: Record<string, string> = { candidate: '候補', investigating: '調査中', on_hold: '保留', rejected: '却下' };
const failures: Record<string, string> = { DEPARTMENT_METADATA_MISSING: '公開要約の部署情報が不足しています。', DEMO_THRESHOLD_NOT_MET: '独立した課題数・部署数の条件を満たしていません。', Budget: '予算の設定により処理を止めました。', Stopped: 'AI停止の設定により処理を止めました。', Configuration: '分析の接続設定が未完成です。', UNGROUNDED_HYPOTHESIS: '根拠を確認できない仮説を採用しませんでした。', TIMEOUT_OR_CANCELLED: '制限時間または中断により処理を止めました。' };
type Source = { title: string; version: number; sections: { field: string; text: string; basis: string }[] };
const sectionNames: Record<string, string> = { challenge: '課題', assumptions: '前提', attempts: '試したこと', result: '結果', reuseConditions: '再利用条件', unresolvedPoints: '未解決点' };
function AnalysisEvidence({ api, runId, knowledgeId, label }: { api: Api; runId: string; knowledgeId: string; label: string }) {
  const [source, setSource] = useState<Source>(), [busy, setBusy] = useState(false), [error, setError] = useState('');
  async function open() {
    setSource(undefined); setError(''); setBusy(true);
    try { setSource(await api<Source>(`/business-analysis/${encodeURIComponent(runId)}/sources/${encodeURIComponent(knowledgeId)}`)); }
    catch (e) { setError(friendlyError(e)); }
    finally { setBusy(false); }
  }
  return <div><button disabled={busy} onClick={() => void open()}>{label}</button>{error && <p role="alert">{error}</p>}{source && <section aria-label={`${label}の本文`}><h4>{source.title} · 公開版 {source.version}</h4>{source.sections.map(s => <div key={s.field}><strong>{sectionNames[s.field] ?? '要約'}</strong><p>{s.text}</p><small>{s.basis === 'inference' ? '推測・要確認' : s.basis === 'participant_report' ? '参加者の報告' : '不明'}</small></div>)}</section>}</div>;
}

type Capabilities = { allowed: boolean; enabled: boolean; configVersion: string; mode: 'fixture' | 'live'; reason?: string; automaticRetry: false; requestOwnerKey?: string | null; currentTime?: string | null };
export function BusinessAnalysisPanel({ api }: { api: Api }) {
  const [capabilities, setCapabilities] = useState<Capabilities>();
  useEffect(() => {
    let active = true; setCapabilities(undefined);
    void api<Capabilities>('/business-analysis/capabilities').then(value => { if (active) setCapabilities(value); }).catch(() => { if (active) setCapabilities(undefined); });
    return () => { active = false; };
  }, [api]);
  if (!capabilities?.allowed) return null;
  return <details className="panel"><summary>{capabilities.mode === 'fixture' ? '許可済み課題の分析（ローカル検証）' : '許可済み課題の分析'}</summary><BusinessAnalysis key={capabilities.requestOwnerKey ?? 'unconfigured'} api={api} capabilities={capabilities} updateCapabilities={setCapabilities} /></details>;
}
function BusinessAnalysis({ api, capabilities, updateCapabilities }: { api: Api; capabilities: Capabilities; updateCapabilities: (value: Capabilities) => void }) {
  const [run, setRun] = useState<Run>(), [busy, setBusy] = useState(false), [error, setError] = useState('');
  const pending = useRef<{ runId: string; cutoff: string; configVersion: string } | undefined>(undefined);
  const requestVersion = useRef(0);
  const lastRequest = useRef<typeof pending.current>(undefined);
  const [resumeBlocked, setResumeBlocked] = useState(false), [hasPending, setHasPending] = useState(false);
  const storageKey = capabilities.requestOwnerKey && /^abr-[a-f0-9]{64}$/.test(capabilities.requestOwnerKey)
    ? `dse-business-analysis-request:v1:${capabilities.requestOwnerKey}` : undefined;
  function remember(request: NonNullable<typeof pending.current>) {
    if (!storageKey) throw new Error('本人の分析保存先を確認できません。');
    sessionStorage.setItem(storageKey, JSON.stringify(request)); pending.current = request; lastRequest.current = request; setHasPending(true);
  }
  function accept(result: Run) {
    setRun(result);
    if (result.status !== 'processing' && !result.recoveryRequired) {
      pending.current = undefined; setHasPending(false); if (storageKey) sessionStorage.removeItem(storageKey);
    }
  }
  function keepUnconfirmed() {
    try { if (lastRequest.current) remember(lastRequest.current); }
    catch { setResumeBlocked(true); }
  }
  useEffect(() => {
    setRun(undefined); setBusy(false); setHasPending(false); setError(''); pending.current = undefined; lastRequest.current = undefined; requestVersion.current++;
    setResumeBlocked(!storageKey);
    if (storageKey) {
      try {
        const saved = sessionStorage.getItem(storageKey);
        if (saved) {
          const request = JSON.parse(saved);
          if (typeof request.runId !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}$/.test(request.runId) ||
              typeof request.cutoff !== 'string' || !Number.isFinite(Date.parse(request.cutoff)) || typeof request.configVersion !== 'string') throw new Error();
          pending.current = request; lastRequest.current = request; setHasPending(true);
        }
      } catch { setResumeBlocked(true); setError('未確定の分析記録を確認できません。管理者に保存状態の確認を依頼してください。'); }
    }
    return () => { requestVersion.current++; };
  }, [api, storageKey]);
  useEffect(() => {
    if (!run || busy || run.status === 'invalidated') return;
    let active = true;
    const timer = setInterval(() => {
      const version = requestVersion.current;
      void api<Run>(`/business-analysis/${encodeURIComponent(run.runId)}`).then(value => {
        if (active && version === requestVersion.current) accept(value);
      }).catch(() => { if (active && version === requestVersion.current) { keepUnconfirmed(); setRun(undefined); setError('許可状態を確認できないため、結果を閉じました。同じ分析の結果を確認してください。'); } });
    }, 5000);
    return () => { active = false; clearInterval(timer); };
  }, [api, run?.runId, run?.status, busy]);
  async function start() {
    if (busy || (!capabilities.enabled && !pending.current) || resumeBlocked || !storageKey || run?.status === 'processing' || run?.recoveryRequired) return;
    const version = ++requestVersion.current;
    const resuming = !!pending.current;
    const request = pending.current ?? { runId: `analysis-${crypto.randomUUID()}`, cutoff: new Date().toISOString(), configVersion: capabilities.configVersion };
    setBusy(true); setError(''); setRun(undefined);
    try {
      remember(request);
      let result: Run;
      if (resuming) {
        try { result = await api<Run>(`/business-analysis/${encodeURIComponent(request.runId)}`); }
        catch (e) {
          if (!(e instanceof WorkspaceError) || e.status !== 404) throw e;
          if (!capabilities.enabled) throw new WorkspaceError(409, capabilities.reason ?? 'AI_BUDGET_STOPPED');
          result = await api<Run>('/business-analysis', 'POST', request, request.runId);
        }
      } else result = await api<Run>('/business-analysis', 'POST', request, request.runId);
      if (version === requestVersion.current) accept(result);
    } catch (e) {
      if (version !== requestVersion.current) return;
      if (e instanceof WorkspaceError && e.status === 400) {
        try {
          const current = await api<Capabilities>('/business-analysis/capabilities');
          if (version === requestVersion.current && current.allowed && current.requestOwnerKey === capabilities.requestOwnerKey && typeof current.configVersion === 'string' && typeof current.currentTime === 'string' && Number.isFinite(Date.parse(current.currentTime))) {
            // Keep the run/key even if a response was misclassified: GET or server replay checks prevent duplicate drafting.
            remember({ ...request, cutoff: current.currentTime, configVersion: current.configVersion });
            updateCapabilities(current); setError('要求の内容を受理できませんでした。同じ分析IDを現在の日時・設定で再確認できます。'); return;
          }
        } catch { /* Preserve the uncertain request when current configuration cannot be confirmed. */ }
      }
      if (version === requestVersion.current) setError(friendlyError(e));
    }
    finally { if (version === requestVersion.current) setBusy(false); }
  }
  async function refresh() {
    if (!run || busy) return;
    const version = ++requestVersion.current; setBusy(true); setError('');
    const id = run.runId; setRun(undefined);
    try { const result = await api<Run>(`/business-analysis/${encodeURIComponent(id)}`); if (version === requestVersion.current) accept(result); }
    catch (e) { if (version === requestVersion.current) { keepUnconfirmed(); setError(friendlyError(e)); } }
    finally { if (version === requestVersion.current) setBusy(false); }
  }
  async function decide(status: string, groupId: string) {
    if (!run || busy) return;
    const version = ++requestVersion.current; setBusy(true); setError('');
    try { const result = await api<Run>(`/business-analysis/${encodeURIComponent(run.runId)}/decision`, 'PATCH', { status, groupId }, crypto.randomUUID(), run.etag); if (version === requestVersion.current) setRun(result); }
    catch (e) { if (version === requestVersion.current) { keepUnconfirmed(); setRun(undefined); setError(friendlyError(e)); } }
    finally { if (version === requestVersion.current) setBusy(false); }
  }
  return <section aria-label="事業仮説の手動分析">
    <h2>共通する課題から、次に調べる仮説へ</h2>
    <p>双方が事業分析を許可した公開要約だけを使います。個別の会話は入力に含めません。候補は人が調査して判断し、市場規模・売上・ROIを確定しません。</p>
    <button className="primary" disabled={busy || (!capabilities.enabled && !hasPending) || resumeBlocked || !storageKey || run?.status === 'processing' || run?.recoveryRequired} onClick={() => void start()}>{busy ? '分析を確認中…' : hasPending ? '同じ分析の結果を確認する' : capabilities.mode === 'fixture' ? '今すぐ分析（ローカル）' : '今すぐ分析'}</button>
    {!storageKey && <p role="status">本人の分析保存先を確認できないため、新しい分析は開始していません。</p>}
    {!capabilities.enabled && <p role="status">現在、分析の実行は有効になっていません。管理者による接続・停止状態の確認が必要です。</p>}
    {error && <p role="alert">{error}</p>}
    {run && <div className="panel">
      <p role="status">{run.status === 'invalidated' ? '許可状態が変わったため、この結果は無効になりました。再分析してください。' : `許可済み要約 ${run.sourceCount} 件 · ${run.processingMode === 'fixture' ? '架空データ・Mock の検証結果' : run.processingMode}`}</p>
      {run.status !== 'invalidated' && <>
        {run.limits && <p>上限：要約 {run.limits.maxInputs} 件・{run.limits.timeoutSeconds} 秒・同時実行 {run.limits.maxConcurrencyPerTenant} 件。失敗時の自動再試行はありません。</p>}
        {run.status === 'processing' && <p role="status">分析は完了していません。中断された処理は自動で再実行しません。保存状態を確認してください。</p>}
        {run.status === 'partial_failure' && <p role="alert">分析の一部を完了できませんでした。表示される候補は成功した範囲のみです。失敗した処理を自動で再実行しません。</p>}
        {run.recoveryRequired && <p role="alert">中断した処理の停止確認と復旧が必要です。確認が終わるまで、新しい分析を開始できません。</p>}
        {run.status === 'completed' && !run.groups.length && <p>独立した課題数や部署などの条件を満たす候補はありません。欠損の情報は推測で補いません。</p>}
        {!!run.exclusions?.length && <ul aria-label="分析で除外した理由">{[...new Set(run.exclusions.map(item => item.reason))].map(reason => <li key={reason}>{failures[reason] ?? '処理または根拠を確認できないため、この候補を除外しました。'}</li>)}</ul>}
        {run.groups.map((group, groupIndex) => <article className="card" key={group.id}>
          <h3>共通課題の候補</h3>
          <p>独立した課題：{group.metrics.independentChallenges} 件 · 部署：{group.metrics.distinctDepartments} · 継続した課題：{group.metrics.recurringChallenges ?? '不明'} 件</p>
          <p>未解決率：{group.metrics.unresolvedRatio === null ? '不明' : `${Math.round(group.metrics.unresolvedRatio * 100)}%`} · 申告工数：{group.metrics.reportedHours === null ? '不明' : `${group.metrics.reportedHours} 時間`}</p>
          <dl>{Object.entries({ audience: '対象', problem: '共通する課題', existingWorkaround: '現在の回避策', valueHypothesis: '価値の仮説', counterEvidence: '反証', nextCustomerCheck: '次に確認すること' }).map(([field, label]) => <div key={field}><dt>{label}</dt><dd>{typeof group.hypothesis?.[field] === 'string' ? String(group.hypothesis[field]) : '未確認'}</dd></div>)}</dl>
          <p>根拠として使った公開要約</p>{group.evidenceIds.map((id, index) => <AnalysisEvidence key={`${run.runId}:${id}`} api={api} runId={run.runId} knowledgeId={id} label={`候補${groupIndex + 1}の公開要約${index + 1}を確認`} />)}
          {!!group.metrics.missingMetadata.length && <p>未解決率・工数などの公開情報に欠損がある場合は「不明」と表示します。</p>}
          <label>人による判断<select aria-label="事業仮説の判断" disabled={busy || !['completed', 'partial_failure'].includes(run.status)} value={group.decision ?? 'candidate'} onChange={e => void decide(e.target.value, group.id)}>{Object.entries(labels).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></label>
        </article>)}
      </>}
      <button disabled={busy} onClick={() => void refresh()}>許可と最新の結果を確認</button>
    </div>}
  </section>;
}
