import { useCallback, useEffect, useRef, useState } from 'react';
import { friendlyError, type Api } from './workspace-api';

type IdentityView = { roomId: string; reviewedVersion: string; fieldSet: string; etag: string; selfConsent: boolean; state: string; participants: { aliasId: string; aliasLabel: string; sharedName: string | null }[] };
export function IdentityDisclosure({ api, roomId }: { api: Api; roomId: string }) {
  const [view, setView] = useState<IdentityView>(), [checked, setChecked] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('');
  const generation = useRef(0), mounted = useRef(false), changing = useRef(false);
  const path = `/workspace/rooms/${roomId}/identity-consent`;
  const load = useCallback(async () => {
    if (changing.current) return;
    const run = ++generation.current;
    try {
      const current = await api<IdentityView>(path);
      if (!mounted.current || run !== generation.current) return;
      setView(previous => { if (previous?.reviewedVersion !== current.reviewedVersion || previous?.selfConsent !== current.selfConsent) setChecked(false); return current; });
      setError('');
    } catch (e) {
      if (!mounted.current || run !== generation.current) return;
      setView(undefined); setChecked(false); setError(friendlyError(e));
    }
  }, [api, path]);
  useEffect(() => {
    mounted.current = true; setView(undefined); setChecked(false); void load();
    const timer = setInterval(() => void load(), 5000);
    return () => { mounted.current = false; generation.current++; clearInterval(timer); };
  }, [load]);
  async function consent(granted: boolean) {
    if (!view || changing.current) return;
    const reviewed = view; changing.current = true; generation.current++; setBusy(true); setView(undefined); setChecked(false); setError('');
    try {
      await api<IdentityView>(path, 'POST', { granted, reviewedVersion: reviewed.reviewedVersion, fieldSet: 'profile-display-name-v1' }, crypto.randomUUID(), reviewed.etag);
      // Confirm the current participant/profile version again; a write response alone does not keep names visible.
      changing.current = false; await load();
    } catch (e) { if (mounted.current) { setView(undefined); setChecked(false); setError(friendlyError(e)); } }
    finally { changing.current = false; if (mounted.current) setBusy(false); }
  }
  const shared = view?.state === 'shared' && view.participants.length > 1 && view.participants.every(p => !!p.sharedName);
  return <section aria-label="実名開示の相互同意"><h2>実名開示</h2><fieldset disabled={busy}><legend>この活動だけで表示名を開示する</legend><p>本人プロフィールの表示名だけを共有します。公的な氏名確認ではありません。部署・連絡先は開示しません。全員が現在の参加者と表示名の版に同意した場合だけ、相手に名前を表示します。</p><p><a href="#profile">本人プロフィールの現在の表示名を確認</a></p><p>事例公開・事業分析とは別の同意です。撤回・プロフィール変更・参加者変更の後は、全員の再確認が必要です。</p>{error && <p role="alert">{error}</p>}{view && <><p data-testid="identity-status">{shared ? '双方の同意を確認しました' : view.state === 'unavailable' ? '表示名を確認できません。プロフィールを確認してください。' : view.selfConsent ? 'あなたは同意済みです。相手の確認を待っています。' : '現在は匿名で活動しています'}</p>{shared && <ul aria-label="この活動で共有した表示名">{view.participants.map(p => <li key={p.aliasId}>{p.aliasLabel}：{p.sharedName}</li>)}</ul>}{!view.selfConsent && view.state !== 'unavailable' && <><label><input type="checkbox" checked={checked} onChange={e => setChecked(e.target.checked)} />この活動の参加者に本人プロフィールの表示名を開示することに同意</label><button disabled={!checked} onClick={() => void consent(true)}>実名開示への同意を保存</button></>}{view.selfConsent && <button className="danger" onClick={() => void consent(false)}>実名開示への同意を撤回</button>}</>}<button onClick={() => void load()}>実名開示の状態を再確認</button></fieldset></section>;
}
