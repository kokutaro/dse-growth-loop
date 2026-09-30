"""One dummy person's two dated careers; no evaluated AI-quality claims."""
from pathlib import Path
from zipfile import ZipFile, ZIP_STORED
ROOT = Path(__file__).parent
careers = ['機械設計 2019-01-01〜2023-01-01', 'ソフトウェア開発 2023-01-01〜2026-01-01']
for excel in [False, True]:
    main = 'xl/workbook.xml' if excel else 'word/document.xml'
    content = 'spreadsheetml.sheet' if excel else 'wordprocessingml.document'
    parts = {
        '[Content_Types].xml': f'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/{main}" ContentType="application/vnd.openxmlformats-officedocument.{content}.main+xml"/></Types>',
        '_rels/.rels': f'<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{main}"/></Relationships>',
    }
    if excel:
        parts[main] = '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="架空職歴" sheetId="1" r:id="s1"/></sheets></workbook>'
        parts['xl/_rels/workbook.xml.rels'] = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="s1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
        parts['xl/worksheets/sheet1.xml'] = '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>' + ''.join(f'<row r="{i}"><c r="A{i}" t="inlineStr"><is><t>{text}</t></is></c></row>' for i, text in enumerate(careers, 1)) + '</sheetData></worksheet>'
    else:
        parts[main] = '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>' + ''.join(f'<w:p><w:r><w:t>{text}</w:t></w:r></w:p>' for text in careers) + '</w:body></w:document>'
    with ZipFile(ROOT / ('profile.xlsx' if excel else 'profile.docx'), 'w', ZIP_STORED) as archive:
        for name, text in parts.items():
            archive.writestr(name, text)
