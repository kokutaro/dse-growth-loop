#!/usr/bin/env python3
"""Render source-backed Luna6 evaluation figures; no network or inference calls."""
import argparse
from decimal import Decimal
import hashlib
import json
import os
from pathlib import Path
import statistics

ROOT = Path(__file__).resolve().parents[1]
os.environ.setdefault('MPLCONFIGDIR', str(ROOT / '.artifacts/matplotlib-cache'))
import matplotlib
matplotlib.use('Agg')
from matplotlib import font_manager, pyplot as plt
from matplotlib.colors import LinearSegmentedColormap
from matplotlib.patches import Patch
import numpy as np

BLUE = '#276DA8'
ORANGE = '#C77728'
GRAY = '#84909C'
INK = '#243345'
LIGHT = '#E6EAF0'
CONDITIONS = [('primary-order-no-ai', 'original', 1, '一次候補の順序'),
              ('gpt-6-luna-2026-09-22', 'original', 1, 'Luna6 元順序'),
              ('gpt-6-luna-2026-09-22', 'reversed', 1, 'Luna6 逆順序'),
              ('gpt-6-luna-2026-09-22', 'original', 2, 'Luna6 再実行')]


def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()


def source_data(directory):
    report = json.loads((directory / 'scored-report.json').read_text())
    summary = json.loads((directory / 'final-summary.json').read_text())
    fixture = json.loads((ROOT / 'evaluation/expansion-v2/dataset.json').read_text())
    questions = {q['id']: q for q in fixture['questions'] if q['split'] == 'final'}
    groups = []
    for model, order, attempt, label in CONDITIONS:
        rows = [r for r in report['comparisons'] if
                (r['modelVersion'], r['order'], r['attempt'], r['k']) == (model, order, attempt, 5)]
        assert len(rows) == len(questions) == 24 and len({r['questionId'] for r in rows}) == 24
        positive = [r for r in rows if r['scores']['relevantCount'] > 0]
        negative = [r for r in rows if r['scores']['relevantCount'] == 0]
        good = bad = 0
        for row in rows:
            judgments = {j['entityId']: j for j in questions[row['questionId']]['judgments']}
            for candidate in row['ranking'][:5]:
                j = judgments[candidate]
                if j['grade'] >= 2 and not j['insufficient']: good += 1
                else: bad += 1
        groups.append({'label': label, 'rows': rows,
                       'positiveQuestions': len(positive), 'negativeQuestions': len(negative),
                       'hits': sum(r['scores']['finalHitRate'] for r in positive),
                       'ndcg': statistics.mean(r['scores']['ndcg'] for r in positive),
                       'falseRecommendations': sum(r['scores']['falseRecommendation'] for r in negative),
                       'displayedRelevant': good, 'displayedIrrelevant': bad})
    # Independent count check rather than averaging type-level or repeated-run percentages.
    assert groups[0]['displayedRelevant'] == 30 and groups[0]['displayedIrrelevant'] == 85
    assert groups[1]['displayedRelevant'] == 37 and groups[1]['displayedIrrelevant'] == 0
    assert Decimal(summary['measuredUsageCostUsd']) + Decimal(summary['unknownAndInterruptedReservationUsd']) == Decimal(summary['conservativeFullCostUsd'])
    return groups, summary, questions


def configure(font_path):
    if font_path:
        font_manager.fontManager.addfont(str(font_path))
        family = font_manager.FontProperties(fname=font_path).get_name()
    else:
        installed = {f.name for f in font_manager.fontManager.ttflist}
        family = next((n for n in ['Hiragino Sans', 'Noto Sans CJK JP', 'IPAexGothic'] if n in installed), None)
        if not family: raise ValueError('Japanese font required; pass --font /path/to/font.ttf')
    plt.rcParams.update({'font.family': family, 'font.size': 12, 'axes.titlesize': 15,
        'axes.titleweight': 'normal', 'text.color': INK, 'axes.labelcolor': INK,
        'xtick.color': INK, 'ytick.color': INK, 'axes.edgecolor': LIGHT,
        'figure.facecolor': 'white', 'axes.facecolor': 'white', 'axes.spines.top': False,
        'axes.spines.right': False, 'svg.fonttype': 'path', 'svg.hashsalt': 'luna6-evaluation-v1',
        'pdf.fonttype': 42, 'axes.unicode_minus': False})
    return family


def header(fig, title, subtitle):
    fig.text(.06, .963, title, fontsize=22, va='top', color=INK)
    fig.text(.06, .963 - .48 / fig.get_size_inches()[1], subtitle, fontsize=12, va='top', color=GRAY)


def grid(ax, axis='y'):
    ax.set_axisbelow(True)
    ax.grid(axis=axis, color=LIGHT, linewidth=.8)


def save(fig, output, stem):
    for extension in ('png', 'svg', 'pdf'):
        metadata = {'Date': None} if extension == 'svg' else {'CreationDate': None} if extension == 'pdf' else None
        fig.savefig(output / f'{stem}.{extension}', dpi=180, facecolor='white', metadata=metadata)
    plt.close(fig)


def overview(groups, output):
    base, luna = groups[:2]
    fig, axs = plt.subplots(2, 2, figsize=(15.5, 10))
    fig.subplots_adjust(left=.075, right=.95, top=.80, bottom=.15, wspace=.30, hspace=.60)
    header(fig, 'Luna6は正解を含む質問数を維持し、不適合候補を絞り込む',
           '確認集合：正解あり20問＋正解なし4問｜同じEの一次候補・K=5｜Luna6は元順序・初回')
    labels = ['一次候補の順序', 'Luna6 元順序']
    for ax, title, values, maximum, texts in [
        (axs[0, 0], '正解を1件以上表示した質問数', [base['hits'], luna['hits']], 20,
         [f"{int(g['hits'])}/20" for g in (base, luna)]),
        (axs[0, 1], '順位品質：平均nDCG@5', [base['ndcg'], luna['ndcg']], 1,
         [f"{g['ndcg']:.3f}" for g in (base, luna)]),
        (axs[1, 0], '正解なし質問への誤推薦（少ないほど良い）',
         [base['falseRecommendations'], luna['falseRecommendations']], 4,
         [f"{int(g['falseRecommendations'])}/4" for g in (base, luna)])]:
        bars = ax.bar(labels, values, color=[GRAY, BLUE], width=.48)
        bars[0].set_hatch('//')
        ax.set_title(title, loc='left', pad=19)
        ax.set_ylim(0, maximum * 1.15)
        ax.set_yticks([0, maximum/2, maximum])
        grid(ax)
        for bar, text in zip(bars, texts):
            ax.text(bar.get_x()+bar.get_width()/2, bar.get_height()+maximum*.035, text,
                    ha='center', va='bottom', fontsize=17, color=INK)
    ax = axs[0, 0]
    ax.axhline(18, color=INK, ls='--', lw=1)
    ax.text(.50, .79, '承認目標 18/20', transform=ax.transAxes, ha='center', fontsize=11,
            bbox={'facecolor':'white','edgecolor':'none','pad':3})
    ax = axs[1, 1]
    ax.set_title('表示候補の内訳（上位5件・全24問）', loc='left', pad=19)
    y = np.arange(2)
    good = [base['displayedRelevant'], luna['displayedRelevant']]
    bad = [base['displayedIrrelevant'], luna['displayedIrrelevant']]
    ax.barh(y, good, color=BLUE, height=.48, label='正解候補（関連度2以上）')
    ax.barh(y, bad, left=good, color=ORANGE, hatch='///', height=.48, label='不適合候補')
    ax.set_yticks(y, labels); ax.invert_yaxis(); ax.set_xlim(0, 135)
    ax.set_xticks([0, 50, 100]); ax.set_xlabel('表示件数'); grid(ax, 'x')
    for n, (g, b) in enumerate(zip(good, bad)):
        ax.text(g/2, n, str(g), ha='center', va='center', color='white', fontsize=14)
        if b: ax.text(g+b/2, n, str(b), ha='center', va='center', color='white', fontsize=14)
        ax.text(g+b+3, n, f'計{g+b}件', va='center', fontsize=12)
    ax.legend(loc='upper left', bbox_to_anchor=(0, -.27), frameon=False, fontsize=10)
    fig.text(.06, .060, '表示数を5件に固定せず、不適合候補は省略。分母が変わる適合率だけで改善を示さない。', fontsize=11)
    fig.text(.06, .028, '出典：scored-report.json・承認済みdataset.json｜合成ダミー集合の実測。実務全体への一般化は未検証。', fontsize=10, color=GRAY)
    save(fig, output, '01-quality-overview')


def positive_ids(questions):
    return sorted((q for q, data in questions.items() if any(j['grade'] >= 2 and not j['insufficient'] for j in data['judgments'])), key=lambda q:int(q.split('-')[2]))


def question_matrix(groups, summary, questions, output):
    ids = positive_ids(questions)
    indexed = [{r['questionId']: r for r in g['rows']} for g in groups]
    matrix = np.array([[rows[q]['scores']['ndcg'] for rows in indexed] for q in ids])
    domains = {'software':'ソフト', 'mechanical':'機械', 'sales':'営業', 'manufacturing':'製造', 'operations':'運用'}
    labels = []
    for q in ids:
        _, domain, number, _ = q.split('-')
        labels.append(f"{number}  {domains[domain]}・{'人物' if questions[q]['entityType']=='person' else '事例'}")
    fig = plt.figure(figsize=(15.5, 11))
    header(fig, '提示順を変えたときの順位品質と安定性', '左：正解あり20問のnDCG@5｜右：正解なしも含む24問の候補ID・順序の一致')
    ax = fig.add_axes([.17, .17, .42, .64])
    image = ax.imshow(matrix, vmin=0, vmax=1, aspect='auto', cmap=LinearSegmentedColormap.from_list('quality', ['#F0F4F8', BLUE]))
    ax.set_yticks(range(len(ids)), labels)
    ax.set_xticks(range(4), ['一次順序', '元順序', '逆順序', '再実行'])
    ax.tick_params(axis='both', length=0, pad=10)
    ax.set_xticks(np.arange(-.5, 4, 1), minor=True); ax.set_yticks(np.arange(-.5, 20, 1), minor=True)
    ax.grid(which='minor', color='white', linewidth=2); ax.tick_params(which='minor', length=0)
    for i in range(20):
        for j in range(4):
            text = f'{matrix[i,j]:.2f}' + (' *' if 'software-04-' in ids[i] and j==2 else '')
            ax.text(j, i, text, ha='center', va='center', fontsize=11, color='white' if matrix[i,j]>.65 else INK)
    color_ax = fig.add_axes([.17, .11, .42, .018])
    cbar = fig.colorbar(image, cax=color_ax, orientation='horizontal', ticks=[0,.5,1])
    cbar.set_label('nDCG@5（正解あり全20問。高いほど良い）', fontsize=10)
    stability = fig.add_axes([.69, .53, .25, .23])
    values=[summary['sameOrderRepeatAgreementQuestions'], summary['reversedOrderAgreementQuestions']]
    stability.barh([1,0], values, color=[BLUE,GRAY], hatch=['','//'], height=.45)
    stability.set_yticks([1,0], ['同順序の再実行', '逆順序で提示']); stability.set_xlim(0,27)
    stability.set_xticks([0,12,24]); stability.set_xlabel('一致した質問数（全24問）', fontsize=10); grid(stability, 'x')
    for y,value in zip([1,0],values): stability.text(value+.4,y,f'{value}/24',va='center',fontsize=14)
    fig.text(.65,.44,'逆順序で異なったのは1問',fontsize=16,va='top')
    fig.text(.65,.35,'04 ソフト・事例（ * のセル）\n一次検索で正解2件を取得できず、\n元順序・再実行では表示なし。\n逆順序だけ案内文1件を誤推薦。',fontsize=12,linespacing=1.8,va='top')
    fig.text(.65,.20,'誤推薦も関連度0なのでnDCGは0。\nスコアが同じでも出力内容は異なる。\n根拠が実在しても、適切な推薦とは限らない。',fontsize=11,linespacing=1.6,va='top')
    fig.text(.06,.027,'出典：scored-report.json・comparisons.json｜再実行を新しい独立質問として分母へ加算していない。',fontsize=10,color=GRAY)
    save(fig, output, '02-question-quality-and-stability')


def cost_figure(summary, output):
    observed = float(summary['measuredUsageCostUsd'])
    reserve = float(summary['unknownAndInterruptedReservationUsd'])
    total = float(summary['conservativeFullCostUsd'])
    budget = float(summary['approvedBudgetUsd'])
    fig = plt.figure(figsize=(15.5, 7))
    header(fig, '本評価の費用推計と承認上限', '通常69リクエスト完了｜USD・本評価のみ。事前の計測とJEV費用は別。')
    ax = fig.add_axes([.15, .43, .78, .33])
    ax.barh(1, observed, color=BLUE, height=.40)
    ax.barh(1, reserve, left=observed, color=ORANGE, hatch='///', height=.40)
    ax.barh(0, budget, facecolor='white', edgecolor=INK, height=.40, linewidth=1.3)
    ax.set_yticks([1,0], ['保守的な合計', '承認済み上限'])
    ax.set_xlim(0,.85); ax.set_xticks([0,.25,.5,.75], ['$0.00','$0.25','$0.50','$0.75']); grid(ax, 'x')
    ax.set_xlabel('費用（USD）')
    ax.text(total+.015,1,f'${total:.5f}',va='center',fontsize=17)
    ax.text(budget+.015,0,f'${budget:.2f}',va='center',fontsize=17)
    fig.legend(handles=[Patch(facecolor=BLUE,label=f'正常69応答のusage × 公表単価：${observed:.5f}'),
                        Patch(facecolor=ORANGE,hatch='///',label=f'失敗・中断の保守予約：${reserve:.5f}')],
               loc='lower left',bbox_to_anchor=(.14,.26),frameon=False,fontsize=12)
    fig.text(.15,.18,'正常：入力65,763 / 出力8,836トークン（入力のうちキャッシュ7,138、推論0）',fontsize=12)
    fig.text(.15,.115,'予約：HTTP 429の130試行＋中断pending 1件。実際にその額が請求されたことを意味しない。',fontsize=11)
    fig.text(.15,.068,'実請求は未照合。保守予約は信頼区間ではなく、usage未取得分の費用上限として計上。',fontsize=11)
    fig.text(.06,.025,'出典：final-summary.json・全capture記録｜単価：Azure GPT-6 Luna / Global Standard（2026-10-03確認）。',fontsize=10,color=GRAY)
    save(fig, output, '03-cost-and-budget')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data',type=Path,default=ROOT/'docs/validation-results/20261003-issue-14-luna6')
    parser.add_argument('--out',type=Path); parser.add_argument('--font',type=Path)
    args=parser.parse_args(); output=args.out or args.data/'charts'; output.mkdir(parents=True,exist_ok=True)
    family=configure(args.font);groups,summary,questions=source_data(args.data)
    overview(groups,output);question_matrix(groups,summary,questions,output);cost_figure(summary,output)
    data={'scope':'Approved dummy final dataset; K=5; no new AI calls',
          'conditions':[{k:v for k,v in g.items() if k!='rows'} for g in groups],
          'questionNdcg':[{ 'questionId':q,'values': [next(r['scores']['ndcg'] for r in g['rows'] if r['questionId']==q) for g in groups] } for q in positive_ids(questions)],
          'cost':{k:summary[k] for k in ('measuredUsageCostUsd','unknownAndInterruptedReservationUsd','conservativeFullCostUsd','approvedBudgetUsd')}}
    (output/'chart-data.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n')
    sources=[args.data/'scored-report.json',args.data/'final-summary.json',ROOT/'evaluation/expansion-v2/dataset.json']
    (output/'manifest.json').write_text(json.dumps({'renderer':'Matplotlib','matplotlibVersion':matplotlib.__version__,
        'fontFamily':family,'scriptSha256':sha(Path(__file__)),
        'sourceSha256':{str(p.relative_to(ROOT)):sha(p) for p in sources},
        'files':{p.name:sha(p) for p in output.iterdir() if p.is_file() and p.name!='manifest.json'}},ensure_ascii=False,indent=2)+'\n')
    print(f'Created 3 figures × PNG/SVG/PDF in {output}')

if __name__=='__main__': main()
