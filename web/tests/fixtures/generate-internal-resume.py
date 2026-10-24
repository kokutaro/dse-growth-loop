"""Reproduce only the internal form's layout using invented content; offline, standard library only."""
from datetime import date
from pathlib import Path
from xml.sax.saxutils import escape
from zipfile import ZipFile, ZipInfo, ZIP_STORED

ROOT = Path(__file__).parent
S = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
P = 'http://schemas.openxmlformats.org/package/2006/relationships'
cells = {'J5': '架空資格', 'D6': '自己申告専用技術', 'D7': '自己申告専用業務', 'D51': '自己PR専用文',
         'C8': '番号', 'D8': '企業', 'G8': '期間', 'H8': '雇用形態', 'J8': '職種', 'L8': '事業内容',
         'C9': '例', 'D9': '雇用歴の例', 'C10': '1', 'D10': '架空組織', 'G10': '201801～202412',
         'B17': '作業期間', 'F17': '案件・業務内容', 'I17': '使用言語', 'J17': 'DB', 'K17': 'サーバOS',
         'L17': 'FW・MW・ツール等', 'M18': '要件\n定義', 'P18': '実装・単体',
         'A19': '例', 'F19': '■説明用の架空案件', 'F20': '≪担当業務≫例専用スキル'}
for n in range(1, 8):
    r = 19 + n * 4
    cells.update({f'A{r}': str(n), f'B{r}': '2020-01', f'E{r}': '2020-03',
                  f'F{r}': f'■架空の部品照合{n}', f'F{r+1}': '≪担当業務≫\n架空部品の照合画面を実装。\n≪習得スキル≫\nReact.Js 18\n≪コメント≫\n希望専用技能を今後学習したい。',
                  f'H{r}': '実装担当', f'H{r+1}': '架空チーム3名', f'I{r}': 'NodeJS 20\nAsp .Net Core 8',
                  f'J{r}': 'Mongo DB 7、Postgre SQL 16', f'K{r}': '架空CloudService',
                  f'L{r}': 'EF Core 8\n.NET Framework 4.8\n.NET 8／MAUI\n架空IDE、架空AIツール',
                  f'M{r}': '●', f'P{r}': '●', f'B{r+3}': '3'})


def column(address):
    n = 0
    for c in ''.join(c for c in address if c.isalpha()):
        n = n * 26 + ord(c) - ord('A') + 1
    return n


rows = []
for row in range(1, 60):
    content = []
    for address, value in sorted(cells.items(), key=lambda item: column(item[0])):
        if int(''.join(c for c in address if c.isdigit())) != row:
            continue
        if address[0] in ('B', 'E') and len(value) == 7:
            serial = (date.fromisoformat(value + '-01') - date(1899, 12, 30)).days
            content.append(f'<c r="{address}" s="1"><v>{serial}</v></c>')
        elif address[0] == 'B' and value.isdigit():
            content.append(f'<c r="{address}"><f>DATEDIF(B{row-3},E{row-3},"M")+1</f><v>{value}</v></c>')
        else:
            content.append(f'<c r="{address}" t="inlineStr"><is><t xml:space="preserve">{escape(value)}</t></is></c>')
    if content:
        rows.append(f'<row r="{row}">{"".join(content)}</row>')
parts = {
    '[Content_Types].xml': '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/></Types>',
    '_rels/.rels': f'<Relationships xmlns="{P}"><Relationship Id="r1" Type="{R}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/workbook.xml': f'<workbook xmlns="{S}" xmlns:r="{R}"><sheets><sheet name="説明専用" sheetId="1" r:id="s1"/><sheet name="02. スキル記入シート(例つき)" sheetId="180468472" r:id="s2"/><sheet name="説明補足" sheetId="3" r:id="s3"/></sheets></workbook>',
    'xl/_rels/workbook.xml.rels': f'<Relationships xmlns="{P}">' + ''.join(f'<Relationship Id="s{i}" Type="{R}/worksheet" Target="worksheets/sheet{i}.xml"/>' for i in range(1, 4)) + '</Relationships>',
    'xl/worksheets/sheet2.xml': f'<worksheet xmlns="{S}"><dimension ref="A1:Z59"/><sheetData>{"".join(rows)}</sheetData><mergeCells>' + ''.join(f'<mergeCell ref="F{r}:G{r}"/>' for r in [19, *range(23, 51, 4)]) + '</mergeCells></worksheet>',
    'xl/styles.xml': f'<styleSheet xmlns="{S}"><numFmts><numFmt numFmtId="165" formatCode="yyyy年m月"/></numFmts><cellXfs><xf numFmtId="0"/><xf numFmtId="165"/></cellXfs></styleSheet>',
    'xl/comments1.xml': '<comments>ノート専用サンプル技能</comments>',
}
for i in (1, 3):
    parts[f'xl/worksheets/sheet{i}.xml'] = f'<worksheet xmlns="{S}"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>説明タブ専用スキル</t></is></c></row></sheetData></worksheet>'
with ZipFile(ROOT / 'internal-resume.xlsx', 'w', ZIP_STORED) as archive:
    for name, text in parts.items():
        entry = ZipInfo(name, (2026, 1, 1, 0, 0, 0))
        archive.writestr(entry, text.encode('utf-8'))
