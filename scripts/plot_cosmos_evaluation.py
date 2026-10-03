"""Render saved Cosmos measurements; makes no network or AI calls."""
import argparse
import json
import os
from pathlib import Path

os.environ.setdefault("MPLCONFIGDIR", str(Path(".artifacts/matplotlib-cache").resolve()))
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib import font_manager


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", default="docs/validation-results/20261003-issue-14-cosmos")
    parser.add_argument("--out")
    args = parser.parse_args()
    data = Path(args.data)
    out = Path(args.out) if args.out else data
    out.mkdir(parents=True, exist_ok=True)
    rows = json.loads((data / "queries.json").read_text())
    summary = json.loads((data / "summary.json").read_text())
    ids = list(dict.fromkeys(row["questionId"] for row in rows))
    if len(ids) != 24 or len(rows) != 48 or not all(row["matched"] for row in rows):
        raise ValueError("Expected 24 questions x 2, all candidate hashes matched")
    fonts = {font.name for font in font_manager.fontManager.ttflist}
    japanese = next((f for f in ["Hiragino Sans", "Noto Sans CJK JP", "IPAexGothic"] if f in fonts), None)
    if not japanese:
        raise ValueError("Install a Japanese font")
    plt.rcParams.update({"font.family": japanese, "font.size": 10, "svg.fonttype": "path", "pdf.fonttype": 42})
    fig, axes = plt.subplots(1, 2, figsize=(13, 5.2), gridspec_kw={"width_ratios": [1.7, 1]})
    for repeat, offset, color in [(1, -.18, "#2563a6"), (2, .18, "#45aeb0")]:
        values = [next(r["requestCharge"] for r in rows if r["questionId"] == q and r["repeat"] == repeat) for q in ids]
        axes[0].bar([i + offset for i in range(24)], values, width=.36, color=color, label=f"{repeat}回目")
    axes[0].set_xticks(range(24), [str(i + 1) for i in range(24)])
    axes[0].set_xlabel("確認集合の質問番号（正解なし4問を含む）")
    axes[0].set_ylabel("検索1回のRequestCharge（RU）")
    axes[0].set_ylim(0, 10)
    axes[0].axhline(summary["meanQueryRu"], color="#8b4528", linestyle="--", label=f"平均 {summary['meanQueryRu']:.2f} RU")
    axes[0].legend(frameon=False, fontsize=9)
    axes[0].set_title("24問 × 2回の取得RU")
    values = [summary["createRu"], summary["queryRuTotal"], 1]
    labels = ["初回投入\n60件", "検索\n48回", "接続確認\n1回"]
    bars = axes[1].bar(labels, values, color=["#66758a", "#2563a6", "#b4becb"])
    for bar, value in zip(bars, values):
        axes[1].text(bar.get_x() + bar.get_width()/2, value + 25, f"{value:,.2f}", ha="center")
    axes[1].set_ylim(0, 1500)
    axes[1].set_ylabel("合計RequestCharge（RU）")
    axes[1].set_title("一度限りの投入と検索を分離")
    for ax in axes:
        ax.spines[["top", "right"]].set_visible(False)
        ax.set_axisbelow(True)
        ax.grid(axis="y", alpha=.2)
    fig.suptitle("実Cosmosの検索RU：保存済み分類を再利用、追加AI呼び出し0回", fontsize=14, y=.98)
    fig.text(.04, .085, f"平均 {summary['meanQueryRu']:.2f} RU/検索  |  取得p95 {summary['p95QueryMs']:.1f} ms  |  候補hash一致 48/48", fontsize=11)
    fig.text(.04, .035, "対象：60件のダミー、Japan East Serverless、単一評価パーティション。認可読取・監査・保存量・検索全体時間は含まない。", fontsize=9)
    fig.tight_layout(rect=(0, .14, 1, .92))
    for extension in ["png", "svg", "pdf"]:
        fig.savefig(out / f"cosmos-ru.{extension}", dpi=180, metadata={"Creator": "plot_cosmos_evaluation.py"})
    svg = out / "cosmos-ru.svg"
    svg.write_text("\n".join(line.rstrip() for line in svg.read_text().splitlines()) + "\n")
    plt.close(fig)


if __name__ == "__main__":
    main()
