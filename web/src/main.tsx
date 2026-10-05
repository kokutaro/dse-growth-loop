import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { initializeAuth, getToken, endAuth, type AppConfig } from "./auth";
import "./style.css";
import { Conversations } from "./conversations";
import { Profiles } from "./profiles";
import { Documents } from "./documents";

type SourceRef = {
  id: string;
  documentTitle: string;
  location: string;
  text: string;
};
type Candidate = {
  entityId: string;
  entityType: string;
  title: string;
  routes: string[];
  units: { summary: string; sourceRefs: SourceRef[]; confirmationState: string; processingMode?: string }[];
};
type SearchResult = {
  candidates: Candidate[];
  matchedUnits: number;
  beforeLimit: number;
  rerankingStatus: string;
  assessments?: { entityId: string; entityType: string; reason: string; evidenceIds: string[]; missingInformation: string[] }[];
  runId?: string;
};
const routeNames: Record<string, string> = {
  probability: "分類一致",
  noul: "経験の補助判定",
  terms: "語一致",
};

function App() {
  const [config, setConfig] = useState<AppConfig>();
  const [conditionsReady, setConditionsReady] = useState(false);
  const [query, setQuery] = useState("");
  const [target, setTarget] = useState("all");
  const [categoryIds, setCategoryIds] = useState("operations");
  const [signalIds, setSignalIds] = useState("handover");
  const [conversionStatus, setConversionStatus] = useState("");
  const [stage, setStage] = useState("");
  const [category, setCategory] = useState("operations");
  const [terms, setTerms] = useState("引き継ぎ");
  const [noul, setNoul] = useState(true);
  const [result, setResult] = useState<SearchResult>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
  const [sessionEnded, setSessionEnded] = useState(false);
  const [logoutError, setLogoutError] = useState(false);
  const searchGeneration = useRef(0);
  function invalidateKnowledge() {
    searchGeneration.current++;
    setResult(undefined); setBusy(false);
    setStage("知識の公開状態が変わりました。検索結果を再取得してください。");
  }
  async function logout() {
    searchGeneration.current++;
    setSessionEnded(true);
    setResult(undefined);
    try { await endAuth(); }
    catch { setLogoutError(true); }
  }
  useEffect(() => {
    fetch("/api/config")
      .then(async (response) => {
        if (!response.ok) throw new Error();
        const value: AppConfig = await response.json();
        await initializeAuth(value);
        setConfig(value);
      })
      .catch(() =>
        setError(
          "接続設定を読み込めませんでした。ページを再読み込みしてください。",
        ),
      );
  }, []);
  async function convert() {
    if (!config) return;
    setBusy(true); setError(""); setResult(undefined); setStage("質問の分類中");
    try {
      const headers: Record<string, string> = { "Content-Type": "application/json" };
      if (config.mode === "Azure") headers.Authorization = `Bearer ${await getToken(config)}`;
      const response = await fetch("/api/v2/search/conditions", { method: "POST", headers, body: JSON.stringify({ query }) });
      if (!response.ok) throw new Error("条件の生成に失敗しました。機密ログイン情報を除き、手動条件を確認してください。");
      const value = await response.json();
      setConditionsReady(!!value.conditions);
      if (value.conditions) {
        setCategoryIds(value.conditions.categories.map((c: { categoryId: string }) => c.categoryId).join(" "));
        setSignalIds(value.conditions.signalIds.join(" "));
        setTerms(value.conditions.terms.join(" "));
        setConversionStatus("条件を生成しました。複数分野・技術要素・経験条件を編集できます。");
      } else {
        setCategoryIds(""); setSignalIds(""); setTerms("");
        setConversionStatus(value.status === "manual_required:unknown"
          ? "自動検索の対象分野を判断できませんでした。検索は実行していません。必要な条件を手動で入力できます。"
          : "分類できませんでした。手動で条件を入力してください。");
      }
      setStage(value.status === "manual_required:unknown" ? "自動検索対象外" : "条件の確認待ち");
    } catch (e) { setError(e instanceof Error ? e.message : "条件の生成に失敗しました。"); setStage("条件生成失敗"); }
    finally { setBusy(false); }
  }
  async function search(event: React.SubmitEvent) {
    event.preventDefault();
    const generation = ++searchGeneration.current;
    setError("");
    setResult(undefined);
    setBusy(true);
    setStage(query.trim() ? "一次取得・根拠確認・再順位付け中" : "一次取得中");
    try {
      if (!config) throw new Error("設定を読み込み中です。");
      const headers: Record<string, string> = {
        "Content-Type": "application/json",
      };
      if (config.mode === "Azure")
        headers.Authorization = `Bearer ${await getToken(config)}`;
      const conditions = { categories: query.trim() ? categoryIds.split(/[\s、,]+/u).filter(Boolean).map(categoryId => ({ axis: "domain", categoryId })) : category ? [{ axis: "domain", categoryId: category }] : [],
        signalIds: query.trim() ? signalIds.split(/[\s、,]+/u).filter(Boolean) : noul ? ["handover"] : [],
        terms: terms.split(/[\s、,]+/u).filter(Boolean), probabilityThreshold: 0.2, noulThreshold: 0.5, limit: 20 };
      const response = await fetch(query.trim() ? "/api/v2/search" : "/api/search", {
        method: "POST",
        headers,
        body: JSON.stringify(query.trim() ? { query, target, ...(conditionsReady ? { conditions } : {}) } : conditions),
      });
      if (generation !== searchGeneration.current) return;
      if (response.status === 422) {
        const failure = await response.json();
        setConditionsReady(false); setCategoryIds(""); setSignalIds(""); setTerms("");
        if (failure.code === "CLASSIFICATION_OUT_OF_SCOPE") {
          setConversionStatus("自動検索の対象分野を判断できませんでした。検索は実行していません。必要な条件を手動で入力できます。");
          setStage("自動検索対象外");
          return;
        }
        throw new Error("自動分類を利用できません。手動で条件を編集して検索してください。");
      }
      if (!response.ok)
        throw new Error(
          response.status === 403
            ? "このデモへの参加権限がありません。"
            : response.status === 400
              ? "分野・検索語・経験の条件を確認し、機密ログイン情報を除いてください。"
              : `検索に失敗しました（${response.status}）。時間をおいて再実行してください。`,
        );
      const value = await response.json();
      if (generation !== searchGeneration.current) return;
      if (value.conditions) {
        setCategoryIds(value.conditions.categories.map((c: { categoryId: string }) => c.categoryId).join(" "));
        setSignalIds(value.conditions.signalIds.join(" "));
        setTerms(value.conditions.terms.join(" "));
        setConditionsReady(true);
      }
      const failed = value.primary && !["succeeded", "all_unsuitable", "no_candidates"].includes(value.rerankingStatus);
      setResult(value.primary ? { ...value.primary, candidates: failed ? [] : value.primary.candidates, assessments: failed ? [] : value.assessments, runId: value.runId } : value);
      setStage("完了・表示前の権限確認済み");
    } catch (e) {
      if (generation !== searchGeneration.current) return;
      setError(e instanceof Error ? e.message : "検索に失敗しました。");
      setStage("検索失敗");
    } finally {
      if (generation === searchGeneration.current) setBusy(false);
    }
  }
  const rerankFailed = !!result?.runId && !["succeeded", "all_unsuitable", "no_candidates"].includes(result.rerankingStatus);
  return (
    <>
      <a className="skip-link" href="#main-content">本文へ移動</a>
      <header className="site-header">
        <a className="brand" href="/">
          DSE <span>Growth Loop</span>
        </a>
        <span className="mode">
          {config?.mode === "Demo"
            ? "ローカル・ダミーデータ"
            : config ? "検証環境" : "接続確認中"}
        </span>
        {config?.mode === "Azure" && !sessionEnded && <button onClick={() => { void logout(); }}>ログアウト</button>}
      </header>
      {!sessionEnded && <nav className="workspace-nav" aria-label="主な機能">
        <a href="#search">人・事例を探す</a>
        <a href="#consultations">課題・相談</a>
        <a href="#experience">自分の経験</a>
        <a className="nav-action" href="#challenge-title">課題を投稿する <span aria-hidden="true">＋</span></a>
      </nav>}
      {sessionEnded ? <main id="main-content" tabIndex={-1}>
        <h1>接続を終了しました</h1>
        <p>この画面の会話接続と認証情報を解除しました。</p>
        {logoutError && <p role="alert">Microsoftのログアウトを完了できませんでした。再ログイン時にアカウントを確認してください。</p>}
        <button onClick={() => location.reload()}>ログインし直す</button>
      </main> : <main id="main-content" tabIndex={-1}>
        <div className="intro" id="search">
          <div>
            <p className="eyebrow">経験をつなぐ。解決を次へ。</p>
            <h1>解決の糸口を、社内の経験から。</h1>
            <p>不具合調査や設計・開発の課題に、経験のある人と過去の事例を。<br />根拠を確かめて、次の相談につなげましょう。</p>
          </div>
          <p className="intro-aside">一人で抱えず、<br /><strong>部署を越えて相談する。</strong></p>
        </div>
        <div className="layout">
          <section
            className="panel search-panel"
            aria-labelledby="search-title"
          >
            <p className="eyebrow">SEARCH / 経験を探す</p>
            <h2 id="search-title">どんな課題でお困りですか？</h2>
            <form onSubmit={search}>
              <label htmlFor="query">自然文の課題</label>
              <textarea id="query" disabled={busy} value={query} maxLength={2000} onChange={e => { setQuery(e.target.value); setConditionsReady(false); setCategoryIds(""); setSignalIds(""); setTerms(""); setConversionStatus(""); setResult(undefined); }} aria-describedby="query-help" placeholder="例：ソフトウェアの引き継ぎを改善したい" />
              <small id="query-help">ダミーの課題を入力してください。パスワード・キーなどのログイン情報は入力できません。</small>
              {query.trim() && <div className="search-target">
                <label htmlFor="search-target">検索対象</label>
                <select id="search-target" value={target} disabled={busy} onChange={e => { setTarget(e.target.value); setResult(undefined); }}>
                  <option value="all">人物と事例</option>
                  <option value="person">人物</option>
                  <option value="knowledge">事例</option>
                </select>
              </div>}
              <button className="primary search-submit" disabled={busy || !config}>
                {busy ? "検索中…" : "相談候補を探す"}
              </button>
              {!query.trim() && <small>課題を入力するか、詳細条件で検索できます。初期条件：業務改善・引き継ぎ。</small>}
              <details className="search-options">
                <summary>検索条件を確認・編集</summary>
                <p className="note">自然文のまま検索できます。条件を調整したい場合はこちらを使ってください。</p>
                <button type="button" disabled={busy || !config || !query.trim()} onClick={() => { void convert(); }}>自然文から条件を生成</button>
                {query.trim() && <>
                <label htmlFor="category-ids">複数分野・技術要素の分類ID</label>
                <input id="category-ids" disabled={busy} value={categoryIds} onChange={e => { setCategoryIds(e.target.value); setConditionsReady(true); }} />
                <small>operations sales manufacturing software mechanical（スペース区切り）</small>
                <label htmlFor="signal-ids">補助経験条件ID</label>
                <input id="signal-ids" disabled={busy} value={signalIds} onChange={e => { setSignalIds(e.target.value); setConditionsReady(true); }} />
              </>}
              {!query.trim() && <>
              <label htmlFor="category">分野</label>
              <select
                id="category"
                value={category}
                onChange={(e) => setCategory(e.target.value)}
              >
                <option value="">指定しない</option>
                <option value="operations">業務改善</option>
                <option value="sales">営業</option>
                <option value="manufacturing">製造</option>
                <option value="software">ソフトウェア</option>
                <option value="mechanical">機械設計</option>
              </select>
              </>}
              <label htmlFor="terms">検索語</label>
              <input
                id="terms"
                value={terms}
                maxLength={500}
                onChange={(e) => { setTerms(e.target.value); setConditionsReady(true); }}
                placeholder="例：引き継ぎ 標準化"
              />
              <small>複数の語はスペースで区切ってください。</small>
              {!query.trim() && <label className="checkbox">
                <input
                  type="checkbox"
                  checked={noul}
                  onChange={(e) => setNoul(e.target.checked)}
                />
                引き継ぎ経験の補助判定を加える
              </label>}
              </details>
              {conversionStatus && <p role="status">{conversionStatus}</p>}
            </form>

          </section>
          <section className="search-results" aria-labelledby="results-title" aria-live="polite" aria-busy={busy}>
            {stage && <p role="status">処理段階：{stage}</p>}
            <div className="results-heading">
              <h2 id="results-title">相談候補と参考事例</h2>
              {result && <span>{result.candidates.length}件</span>}
            </div>
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            {!result && !error && !busy && (
              <div className="empty">
                <span className="empty-mark" aria-hidden="true">↗</span>
                <p className="eyebrow">経験から、次の一歩へ</p>
                <h3>相談できる人と、参考になる事例を一緒に。</h3>
                <p>課題を入力すると、関わりのある経験や事例を表示します。<br />適合理由・出典・不足情報を確認して、相談先を検討できます。</p>
                <ol className="usage-steps">
                  <li><strong>01</strong><span>課題を言葉にする</span></li>
                  <li><strong>02</strong><span>経験と根拠を確かめる</span></li>
                  <li><strong>03</strong><span>課題を投稿して相談する</span></li>
                </ol>
              </div>
            )}
            {busy && <div className="empty" role="status"><h3>関連する経験を探しています</h3><p>条件と根拠を確認しています。このままお待ちください。</p></div>}
            {result && (
              <>
                <p className="notice">
                  <strong>{result.rerankingStatus === "succeeded" ? "根拠に基づく再順位付け済み" : result.rerankingStatus === "all_unsuitable" ? "全件不適合" : rerankFailed ? "候補の評価を完了できませんでした" : "再順位付け未実施"}</strong>
                  <br />
                  {rerankFailed ? "推薦の適合性を確認できないため、候補を表示していません。検索対象を人物または事例に絞るか、時間をおいて再検索してください。" : "表示順は能力の順位ではありません。根拠と不足情報を確認してください。"}
                  {result.runId && <small>検索履歴：{result.runId} ／ {result.rerankingStatus}</small>}
                </p>
                {result.candidates.length === 0 && !rerankFailed && (
                  <div className="empty"><h3>条件に合う候補はありません。</h3><p>検索語を短くする、または詳細条件の分野を広げて再検索してください。</p></div>
                )}
                {!rerankFailed && (["person", "knowledge"] as const).filter(type => !result.runId || target === "all" || target === type).map(type => (
                  <section key={type} aria-label={type === "person" ? "相談候補" : "参考事例"}>
                    <h3>{type === "person" ? "相談候補" : "参考事例"}</h3>
                    {!result.candidates.some(c => (c.entityType === "person" ? "person" : "knowledge") === type) &&
                      <p>{type === "person" ? "相談候補" : "参考事例"}はありません。</p>}
                    {result.candidates.filter(c => (c.entityType === "person" ? "person" : "knowledge") === type).map((candidate) => (
                      <article
                        className="panel candidate"
                        key={`${candidate.entityType}:${candidate.entityId}`}
                      >
                        <div className="candidate-top">
                          <span className="kind">
                            {candidate.entityType === "person"
                              ? "相談候補"
                              : "参考事例"}
                          </span>
                          <span className="fixture">架空データ</span>
                        </div>
                        <h3>{candidate.title}</h3>
                        {result.assessments?.filter(a => a.entityId === candidate.entityId && a.entityType === candidate.entityType).map(a => <div key={a.entityId}>
                          <p>適合理由（根拠引用）：{a.reason}</p>
                          <small>不足情報：{a.missingInformation.join(" ")}</small>
                        </div>)}
                        {candidate.entityType === "person" && <small>相談する人物ID：{candidate.entityId}</small>}
                        <p>{candidate.units[0].summary}</p>
                        <small>{candidate.units.some(u => u.confirmationState === "reviewed") ? "参加者確認・公開承認済み" : candidate.units.some(u => u.confirmationState === "ai_unconfirmed") ? "AI抽出・本人未確認" : candidate.units.some(u => u.confirmationState === "self_corrected") ? "本人訂正済み" : candidate.units.some(u => u.confirmationState === "self_confirmed") ? "本人確認済み" : "固定fixture"} ／ {candidate.units.some(u => u.processingMode === "live") ? "実AI処理" : "fixture"}</small>
                        <div className="routes">
                          {candidate.routes.map((route) => (
                            <span key={route}>{routeNames[route] ?? route}</span>
                          ))}
                        </div>
                        <details>
                          <summary>根拠を見る</summary>
                          {candidate.units
                            .flatMap((u) => u.sourceRefs)
                            .map((source, i) => (
                              <blockquote key={`${source.id}:${i}`}>
                                <p>{source.text}</p>
                                <cite>
                                  {source.documentTitle} / {source.location}
                                </cite>
                              </blockquote>
                            ))}
                        </details>
                      </article>
                    ))}
                  </section>
                ))}
                <p className="note">
                  一致した職歴・事例 {result.matchedUnits}件 ／ 重複排除後{" "}
                  {result.beforeLimit}件
                </p>
              </>
            )}
          </section>
        </div>
        {config && <>
          <div className="section-heading" id="consultations"><p className="eyebrow">COLLABORATE / 課題を解決する</p><h2>経験を持ち寄り、相談を進める</h2><p>課題を投稿し、検索で見つけた人に参加を依頼。本人の承諾後にチャットで相談できます。</p></div>
          <Conversations config={config} candidates={result?.candidates ?? []} onKnowledgeChanged={invalidateKnowledge} />
          <div className="section-heading" id="experience"><p className="eyebrow">PROFILE / 経験を共有する</p><h2>あなたの経験が、誰かの手がかりに</h2><p>職歴やスキルの資料を登録し、抽出された内容を確認・訂正できます。</p></div>
          <div className="experience-grid"><Profiles config={config} /><Documents config={config} /></div>
        </>}
      </main>}
      <footer>
        DSE Growth Loop — 人の優劣ではなく、経験との接点を探す。
        <br />現在は架空データによる検証環境です。事業化提案は未実装です。
      </footer>
    </>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
