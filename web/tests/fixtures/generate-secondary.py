"""Explicit secondary work by one fictional person; no AI-quality claims."""
from pathlib import Path
from zipfile import ZipFile, ZipInfo, ZIP_STORED

TEXT = ('主業務は機械設計 2019-01-01〜2023-01-01。'
        '副業務として次の対応を共同実施した。営業で、顧客の要求を必須項目と将来項目に分けた。'
        '予算と納期に合う提案範囲を合意した。')
PARTS = {
    '[Content_Types].xml': '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>',
    '_rels/.rels': '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="r1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>',
    'word/document.xml': '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>'
                         f'<w:p><w:r><w:t>{TEXT}</w:t></w:r></w:p></w:body></w:document>',
}
with ZipFile(Path(__file__).with_name('secondary-work.docx'), 'w', ZIP_STORED) as archive:
    for name, text in PARTS.items():
        archive.writestr(ZipInfo(name, date_time=(2026, 10, 3, 0, 0, 0)), text)
