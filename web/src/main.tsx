import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { initializeAuth, getToken, type AppConfig } from "./auth";
import "./style.css";
import { Conversations } from "./conversations";
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
  units: { summary: string; sourceRefs: SourceRef[] }[];
};
type SearchResult = {
  candidates: Candidate[];
  matchedUnits: number;
  beforeLimit: number;
  rerankingStatus: string;
};
const routeNames: Record<string, string> = {
  probability: "分類一致",
  noul: "経験の補助判定",
  terms: "語一致",
};

function App() {
  const [config, setConfig] = useState<AppConfig>();
  const [category, setCategory] = useState("operations");
  const [terms, setTerms] = useState("引き継ぎ");
  const [noul, setNoul] = useState(true);
  const [result, setResult] = useState<SearchResult>();
  const [error, setError] = useState("");
  const [busy, setBusy] = useState(false);
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
  async function search(event: React.SubmitEvent) {
    event.preventDefault();
    setError("");
    setResult(undefined);
    setBusy(true);
    try {
      if (!config) throw new Error("設定を読み込み中です。");
      const headers: Record<string, string> = {
        "Content-Type": "application/json",
      };
      if (config.mode === "Azure")
        headers.Authorization = `Bearer ${await getToken(config)}`;
      const response = await fetch("/api/search", {
        method: "POST",
        headers,
        body: JSON.stringify({
          categories: category
            ? [{ axis: "domain", categoryId: category }]
            : [],
          signalIds: noul ? ["handover"] : [],
          terms: terms.split(/[\s、,]+/u).filter(Boolean),
          probabilityThreshold: 0.2,
          noulThreshold: 0.5,
          limit: 20,
        }),
      });
      if (!response.ok)
        throw new Error(
          response.status === 403
            ? "このデモへの参加権限がありません。"
            : response.status === 400
              ? "分野・検索語・経験のいずれかを指定してください。"
              : `検索に失敗しました（${response.status}）。時間をおいて再実行してください。`,
        );
      setResult(await response.json());
    } catch (e) {
      setError(e instanceof Error ? e.message : "検索に失敗しました。");
    } finally {
      setBusy(false);
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
      </header>
      <main>
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
              <label htmlFor="terms">検索語</label>
              <input
                id="terms"
                value={terms}
                maxLength={500}
                onChange={(e) => setTerms(e.target.value)}
                placeholder="例：引き継ぎ 標準化"
              />
              <small>複数の語はスペースで区切ってください。</small>
              <label className="checkbox">
                <input
                  type="checkbox"
                  checked={noul}
                  onChange={(e) => setNoul(e.target.checked)}
                />
                引き継ぎ経験の補助判定を加える
              </label>
              <button disabled={busy || !config}>
                {busy ? "検索中…" : "相談候補を探す"}
              </button>
            </form>
            <p className="note">
              現段階は手動の条件指定による一次検索です。人物・経験・分類値はすべて架空の検証用データです。
            </p>
          </section>
          <section aria-labelledby="results-title" aria-live="polite">
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
                  <strong>再順位付け未実施</strong>
                  <br />
                  AIによる適合性の評価前です。表示順は能力の順位ではありません。
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
                    {candidate.entityType === "person" && <small>相談する人物ID：{candidate.entityId}</small>}
                    <p>{candidate.units[0].summary}</p>
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
        {config && <Conversations config={config} candidates={result?.candidates ?? []} />}
        {config && <Documents config={config} />}
      </main>
      <footer>
        人の優劣ではなく、経験との接点を探す。DSE Growth Loop / 開発初期版
      </footer>
    </>
  );
}
createRoot(document.getElementById("root")!).render(<App />);
