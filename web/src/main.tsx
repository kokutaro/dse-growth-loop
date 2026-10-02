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
      setConditionsReady(true);
      if (value.conditions) {
        setCategoryIds(value.conditions.categories.map((c: { categoryId: string }) => c.categoryId).join(" "));
        setSignalIds(value.conditions.signalIds.join(" "));
        setTerms(value.conditions.terms.join(" "));
        setConversionStatus("条件を生成しました。複数分野・技術要素・経験条件を編集できます。");
      } else setConversionStatus("分類できませんでした。手動で条件を入力してください（" + value.status + "）。");
      setStage("条件の確認待ち");
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
        body: JSON.stringify(query.trim() ? { query, ...(conditionsReady ? { conditions } : {}) } : conditions),
      });
      if (generation !== searchGeneration.current) return;
      if (response.status === 422) {
        setConditionsReady(true);
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
      setResult(value.primary ? { ...value.primary, assessments: value.assessments, runId: value.runId } : value);
      setStage("完了・表示前の権限確認済み");
    } catch (e) {
      if (generation !== searchGeneration.current) return;
      setError(e instanceof Error ? e.message : "検索に失敗しました。");
      setStage("検索失敗");
    } finally {
      if (generation === searchGeneration.current) setBusy(false);
    }
  }
  return (
    <>
      <header>
        <a className="brand" href="/">
          DSE <span>Growth Loop</span>
        </a>
        <span className="mode">
          {config?.mode === "Demo"
            ? "ローカル・ダミーデータ"
            : "ダミーデータ検証"}
        </span>
        {config?.mode === "Azure" && !sessionEnded && <button onClick={() => { void logout(); }}>ログアウト</button>}
      </header>
      {sessionEnded ? <main>
        <h1>接続を終了しました</h1>
        <p>この画面の会話接続と認証情報を解除しました。</p>
        {logoutError && <p role="alert">Microsoftのログアウトを完了できませんでした。再ログイン時にアカウントを確認してください。</p>}
        <button onClick={() => location.reload()}>ログインし直す</button>
      </main> : <main>
        <div className="intro">
          <p className="eyebrow">経験をつなぐ。解決を次へ。</p>
          <h1>
            その困りごとに、
            <br />
            別の部署の経験を。
          </h1>
          <p>経験のある人や過去の事例を見つけ、相談のきっかけにします。</p>
        </div>
        <div className="layout">
          <section
            className="panel search-panel"
            aria-labelledby="search-title"
          >
            <p className="eyebrow">01 / 相談先を探す</p>
            <h2 id="search-title">探したい経験は？</h2>
            <form onSubmit={search}>
              <label htmlFor="query">自然文の課題</label>
              <textarea id="query" disabled={busy} value={query} maxLength={2000} onChange={e => { setQuery(e.target.value); setConditionsReady(false); setConversionStatus(""); setResult(undefined); }} placeholder="例：ソフトウェアの引き継ぎを改善したい" />
              <small>ダミーの課題を入力してください。パスワード・キーなどのログイン情報は入力できません。</small>
              <button type="button" disabled={busy || !config || !query.trim()} onClick={() => { void convert(); }}>自然文から条件を生成</button>
              {conversionStatus && <p role="status">{conversionStatus}</p>}
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
              <button disabled={busy || !config}>
                {busy ? "検索中…" : "相談候補を探す"}
              </button>
            </form>
            <p className="note">
              自然文は条件を確認・編集して検索できます。人物・経験・分類値はすべて架空の検証用データです。
            </p>
          </section>
          <section aria-labelledby="results-title" aria-live="polite">
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
            {!result && !error && (
              <div className="empty">
                <span className="loop">↗</span>
                <h3>部署を越えたヒントを見つける</h3>
                <p>
                  「引き継ぎ」で、営業と製造の経験を
                  <br />
                  つなぐ検索を試してみてください。
                </p>
              </div>
            )}
            {result && (
              <>
                <p className="notice">
                  <strong>{result.rerankingStatus === "succeeded" ? "根拠に基づく再順位付け済み" : result.rerankingStatus === "all_unsuitable" ? "全件不適合" : "再順位付け未実施"}</strong>
                  <br />
                  表示順は能力の順位ではありません。根拠と不足情報を確認してください。
                  {result.runId && <small>検索履歴：{result.runId} ／ {result.rerankingStatus}</small>}
                </p>
                {result.candidates.length === 0 && (
                  <p>条件に合う候補はありません。</p>
                )}
                {result.candidates.map((candidate) => (
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
                <p className="note">
                  一致した職歴・事例 {result.matchedUnits}件 ／ 重複排除後{" "}
                  {result.beforeLimit}件
                </p>
              </>
            )}
          </section>
        </div>
        {config && <Conversations config={config} candidates={result?.candidates ?? []} onKnowledgeChanged={invalidateKnowledge} />}
        {config && <Documents config={config} />}
        {config && <Profiles config={config} />}
      </main>}
      <footer>
        人の優劣ではなく、経験との接点を探す。DSE Growth Loop / 開発初期版
      </footer>
    </>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
