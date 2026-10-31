using System.Globalization;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Dse.Domain;
using Microsoft.Extensions.Configuration;

namespace Dse.Infrastructure;

public sealed class IngestionOptions
{
    public int OriginalBytes { get; init; } = 10 * 1024 * 1024;
    public int ExpandedBytes { get; init; } = 50 * 1024 * 1024;
    public int Entries { get; init; } = 10000;
    public int ProcessingSeconds { get; init; } = 120;
    public int Attempts { get; init; } = 3;
    public const string ProcessorVersion = "openxml-3";
    public static IngestionOptions Read(IConfiguration config)
    {
        var o = config.GetSection("Ingestion").Get<IngestionOptions>() ?? new();
        if (o.OriginalBytes is < 1 or > 10485760 || o.ExpandedBytes is < 1 or > 52428800 ||
            o.Entries is < 1 or > 10000 || o.ProcessingSeconds is < 1 or > 120 || o.Attempts is < 1 or > 3)
            throw new InvalidOperationException("Ingestion limits must be positive and within the documented safety ceilings.");
        return o;
    }
}
public sealed record CellSource(string Sheet, string Address, string? MergeRange, string SheetState,
    bool HiddenRow, bool HiddenColumn, string ValueType, string RawValue, string Text, string? NumberFormat, bool Formula, string ValueState);
public sealed record WorkbookCapture(string Version, string[] Sheets, CellSource[] Cells, string[] Warnings);
public sealed record ExtractedSpan(string Type, string Label, string Text)
{
    public CellSource? Cell { get; init; }
}
public sealed record Extraction(ExtractedSpan[] Spans, string[] Warnings, ResumeImport? ResumeImport = null)
{
    public WorkbookCapture? Workbook { get; init; }
}

// Reads Office Open XML parts only. Never invokes Office, formulas, relationships or embedded code.
public sealed class OfficeExtractor(IngestionOptions options)
{
    public const string WordMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string ExcelMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    { ".docx" => WordMime, ".xlsx" => ExcelMime, _ => throw new DomainException(400, "UNSUPPORTED_DOCUMENT") };

    public Dictionary<string, byte[]> Validate(byte[] bytes, string filename, string mime, CancellationToken ct)
    {
        if (bytes.Length == 0) throw new DomainException(400, "INVALID_INPUT");
        if (bytes.Length > options.OriginalBytes) throw new DomainException(413, "PAYLOAD_TOO_LARGE");
        var expected = Mime(filename);
        if (mime != expected) throw new DomainException(400, "INVALID_PACKAGE");
        try
        {
            var checksums = CheckZipHeaders(bytes);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (checksums.Length != zip.Entries.Count) throw new DomainException(400, "INVALID_PACKAGE");
            int entryIndex = 0;
            if (zip.Entries.Count > options.Entries) throw new DomainException(413, "PAYLOAD_TOO_LARGE");
            Dictionary<string, byte[]> parts = new(StringComparer.Ordinal);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var expectedCrc = checksums[entryIndex++];
                var name = entry.FullName;
                if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(x => x is "." or "..") || parts.ContainsKey(name))
                    throw new DomainException(400, "INVALID_PACKAGE");
                if (entry.Length > options.ExpandedBytes || entry.Length > Math.Max(1, entry.CompressedLength) * 100 ||
                    (total += entry.Length) > options.ExpandedBytes) throw new DomainException(413, "PAYLOAD_TOO_LARGE");
                if (name.EndsWith('/')) continue;
                if (name.Contains("vba", StringComparison.OrdinalIgnoreCase) || name.Contains("embeddings/", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("activeX/", StringComparison.OrdinalIgnoreCase)) throw new DomainException(400, "UNSUPPORTED_DOCUMENT");
                using var input = entry.Open(); using var output = new MemoryStream();
                var buffer = new byte[8192]; int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (output.Length + read > entry.Length) throw new DomainException(413, "PAYLOAD_TOO_LARGE");
                    output.Write(buffer, 0, read);
                }
                if (output.Length != entry.Length) throw new DomainException(400, "INVALID_PACKAGE");
                var contentBytes = output.ToArray();
                if (Crc32(contentBytes) != expectedCrc) throw new DomainException(400, "INVALID_PACKAGE");
                parts.Add(name, contentBytes);
            }
            var types = Xml(parts, "[Content_Types].xml");
            if (types.Root?.Name != XName.Get("Types", "http://schemas.openxmlformats.org/package/2006/content-types")) throw new DomainException(400, "INVALID_PACKAGE");
            if (types.Descendants().Attributes("ContentType").Any(x => new[] { "macro", "vba", "oleObject", "activeX" }.Any(term => x.Value.Contains(term, StringComparison.OrdinalIgnoreCase))))
                throw new DomainException(400, "UNSUPPORTED_DOCUMENT");
            var main = expected == WordMime ? "word/document.xml" : "xl/workbook.xml";
            var mainType = expected == WordMime ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";
            if (!types.Descendants().Any(x => (string?)x.Attribute("PartName") == "/" + main && (string?)x.Attribute("ContentType") == mainType))
                throw new DomainException(400, "INVALID_PACKAGE");
            foreach (var name in parts.Keys.Where(x => x.EndsWith(".rels", StringComparison.Ordinal)))
            {
                var relationships = Xml(parts, name);
                if (relationships.Root?.Name != XName.Get("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships")) throw new DomainException(400, "INVALID_PACKAGE");
                foreach (var rel in relationships.Root.Elements())
                {
                    var target = (string?)rel.Attribute("Target") ?? "";
                    if ((string?)rel.Attribute("TargetMode") == "External") continue; // ignored, never fetched
                    Resolve(name == "_rels/.rels" ? "" : name[..name.LastIndexOf("/_rels/", StringComparison.Ordinal)] + "/", target);
                }
            }
            var rootRels = Xml(parts, "_rels/.rels");
            if (!rootRels.Root!.Elements().Any(x => ((string?)x.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true &&
                (string?)x.Attribute("TargetMode") != "External" && Resolve("", (string?)x.Attribute("Target") ?? "") == main))
                throw new DomainException(400, "INVALID_PACKAGE");
            _ = Xml(parts, main);
            return parts;
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or NotSupportedException or ArgumentException or OverflowException)
        { throw new DomainException(400, "INVALID_PACKAGE"); }
    }
    public Extraction Extract(byte[] bytes, string filename, string mime, CancellationToken ct)
    {
        var parts = Validate(bytes, filename, mime, ct);
        List<ExtractedSpan> spans = []; HashSet<string> warnings = []; List<CellSource> capturedCells = []; List<string> capturedSheets = [];
        if (parts.Keys.Any(x => x.Contains("/media/"))) warnings.Add("IMAGES_NOT_OCR");
        if (parts.Keys.Where(x => x.EndsWith(".rels")).Any(x => Xml(parts, x).Descendants().Any(e => (string?)e.Attribute("TargetMode") == "External"))) warnings.Add("EXTERNAL_LINKS_IGNORED");
        void Add(string type, string label, string text, CellSource? cell = null)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (label.Length > 260) throw new DomainException(400, "INVALID_PACKAGE");
            // Fixed Unicode-safe chunks retain exact source position and offset.
            var buffer = new StringBuilder(); int count = 0, offset = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (count == 8000)
                {
                    if (!string.IsNullOrWhiteSpace(buffer.ToString())) spans.Add(new(type, $"{label} / offset {offset}", buffer.ToString()) { Cell = cell });
                    buffer.Clear(); offset += count; count = 0; ct.ThrowIfCancellationRequested();
                }
                buffer.Append(rune); count++;
            }
            if (!string.IsNullOrWhiteSpace(buffer.ToString())) spans.Add(new(type, offset == 0 ? label : $"{label} / offset {offset}", buffer.ToString()) { Cell = cell });
        }
        if (mime == WordMime)
        {
            var document = Xml(parts, "word/document.xml");
            if (document.Root?.Name != W + "document" || document.Root.Element(W + "body") is null) throw new DomainException(400, "INVALID_PACKAGE");
            int p = 0;
            foreach (var paragraph in document.Descendants(W + "p"))
            {
                var table = paragraph.Ancestors(W + "tbl").FirstOrDefault();
                var label = $"word/document.xml / paragraph {++p}";
                if (table is not null)
                {
                    var cell = paragraph.Ancestors(W + "tc").First(); var row = cell.Parent!;
                    label += $" / table {document.Descendants(W + "tbl").TakeWhile(x => x != table).Count() + 1} row {row.ElementsBeforeSelf(W + "tr").Count() + 1} cell {cell.ElementsBeforeSelf(W + "tc").Count() + 1}";
                }
                Add("paragraph", label, string.Concat(paragraph.Descendants().Where(x => (x.Name == W + "t" || x.Name == W + "tab" || x.Name == W + "br") && x.Ancestors(W + "p").FirstOrDefault() == paragraph).Select(x => x.Name == W + "t" ? x.Value : x.Name == W + "tab" ? "\t" : "\n")));
            }
            if (parts.Keys.Any(x => x.StartsWith("word/header") || x.StartsWith("word/footer") || x is "word/footnotes.xml" or "word/endnotes.xml")) warnings.Add("AUXILIARY_PARTS_NOT_EXTRACTED");
        }
        else
        {
            var workbook = Xml(parts, "xl/workbook.xml");
            if (workbook.Root?.Name != S + "workbook") throw new DomainException(400, "INVALID_PACKAGE");
            var rels = Xml(parts, "xl/_rels/workbook.xml.rels").Root!.Elements().Where(x => (string?)x.Attribute("TargetMode") != "External")
                .ToDictionary(x => (string)x.Attribute("Id")!, x => Resolve("xl/", (string)x.Attribute("Target")!));
            var strings = parts.ContainsKey("xl/sharedStrings.xml") ? Xml(parts, "xl/sharedStrings.xml").Descendants(S + "si").Select(x => string.Concat(x.Descendants(S + "t").Select(t => t.Value))).ToArray() : [];
            var styles = parts.ContainsKey("xl/styles.xml") ? Xml(parts, "xl/styles.xml") : null;
            var formats = styles?.Descendants(S + "numFmt").ToDictionary(x => (int)x.Attribute("numFmtId")!, x => (string)x.Attribute("formatCode")!) ?? [];
            var xfs = styles?.Root?.Element(S + "cellXfs")?.Elements().Select(x => (int?)x.Attribute("numFmtId") ?? 0).ToArray() ?? [];
            var date1904 = (string?)workbook.Root.Element(S + "workbookPr")?.Attribute("date1904") is "1" or "true";
            foreach (var sheet in workbook.Descendants(S + "sheet"))
            {
                if (!rels.TryGetValue((string?)sheet.Attribute(R + "id") ?? "", out var part)) throw new DomainException(400, "INVALID_PACKAGE");
                var xml = Xml(parts, part);
                if (xml.Root?.Name != S + "worksheet" || ((string?)sheet.Attribute("name")) is not { Length: > 0 and <= 31 }) throw new DomainException(400, "INVALID_PACKAGE");
                capturedSheets.Add((string)sheet.Attribute("name")!);
                var seen = new HashSet<string>();
                var merged = xml.Descendants(S + "mergeCell").Select(x => (string?)x.Attribute("ref") ?? "").ToArray();
                foreach (var cell in xml.Descendants(S + "c"))
                {
                    ct.ThrowIfCancellationRequested();
                    var address = (string?)cell.Attribute("r") ?? throw new DomainException(400, "INVALID_PACKAGE");
                    if (!ValidAddress(address) || !seen.Add(address)) throw new DomainException(400, "INVALID_PACKAGE");
                    var value = (string?)cell.Element(S + "v") ?? "";
                    var type = (string?)cell.Attribute("t");
                    var rawValue = value;
                    var hasFormula = cell.Element(S + "f") is not null;
                    var valueState = hasFormula && cell.Element(S + "v") is null ? "formula_cache_missing" : "available";
                    var styleIndex = (int?)cell.Attribute("s") ?? 0;
                    var numberFormat = styleIndex >= 0 && styleIndex < xfs.Length ? xfs[styleIndex] : 0;
                    var formatCode = formats.GetValueOrDefault(numberFormat);
                    if (numberFormat != 0 && !(numberFormat is >= 14 and <= 22 or >= 45 and <= 47 || formatCode is not null && IsDate(formatCode)))
                        warnings.Add("NUMBER_DISPLAY_FORMAT_NOT_RENDERED");
                    if (cell.Element(S + "f") is not null) { warnings.Add("FORMULA_CACHED_VALUES_ONLY"); if (value.Length == 0) warnings.Add("FORMULA_CACHE_MISSING"); }
                    if (type == "s")
                    {
                        if (!int.TryParse(value, out var index) || index < 0 || index >= strings.Length) throw new DomainException(400, "INVALID_PACKAGE");
                        value = strings[index];
                    }
                    else if (type == "inlineStr") value = string.Concat(cell.Descendants(S + "t").Select(x => x.Value));
                    else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                    else if (type is null or "n" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
                    {
                        var style = (int?)cell.Attribute("s") ?? 0;
                        var format = style >= 0 && style < xfs.Length ? xfs[style] : 0;
                        if (format is >= 14 and <= 22 or >= 45 and <= 47 || formats.TryGetValue(format, out var code) && IsDate(code))
                        {
                            if (!date1904 && serial >= 60 && serial < 61) { value = "1900-02-29 (Excel serial " + value + ")"; warnings.Add("EXCEL_1900_LEAP_DAY"); }
                            else
                            {
                                try { value = (date1904 ? new DateTime(1904, 1, 1).AddDays(serial) : DateTime.FromOADate(serial < 60 ? serial + 1 : serial)).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture); }
                                catch (ArgumentException) { warnings.Add("INVALID_DATE_SERIAL"); }
                            }
                        }
                    }
                    var range = merged.FirstOrDefault(x => InRange(address, x));
                    var column = ColumnNumber(address);
                    var cellSource = new CellSource((string)sheet.Attribute("name")!, address, range,
                        (string?)sheet.Attribute("state") ?? "visible", (string?)cell.Parent?.Attribute("hidden") is "1" or "true",
                        xml.Descendants(S + "col").Any(c => (string?)c.Attribute("hidden") is "1" or "true" &&
                            (int?)c.Attribute("min") <= column && (int?)c.Attribute("max") >= column),
                        type ?? "n", rawValue, value, formatCode ?? numberFormat.ToString(CultureInfo.InvariantCulture), hasFormula, valueState);
                    capturedCells.Add(cellSource);
                    if (capturedCells.Count > 50000) throw new DomainException(422, "WORKBOOK_CAPTURE_CELL_LIMIT");
                    Add("cells", $"{part} / {(string?)sheet.Attribute("name")}!{(range?.Split(':')[0] == address ? range : address)}", value, cellSource);
                }
                foreach (var text in xml.Descendants().Where(e => e.Name.LocalName is "oddHeader" or "evenHeader" or "firstHeader" or "oddFooter" or "evenFooter" or "firstFooter"))
                    Add("sheetText", $"{part} / {(string?)sheet.Attribute("name")} / {text.Name.LocalName}", text.Value);
            }
            // Notes/drawing text is retained as auxiliary context, not interpreted as executable content.
            foreach (var part in parts.Keys.Where(p => p.StartsWith("xl/comments", StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.Ordinal) || p.StartsWith("xl/drawings/", StringComparison.Ordinal) && p.EndsWith(".xml", StringComparison.Ordinal)))
            {
                var xml = Xml(parts, part); int n = 0;
                foreach (var text in xml.Descendants().Where(e => e.Name.LocalName is "t" or "text" && !e.Elements().Any()))
                    Add("sheetText", $"{part} / text {++n}", text.Value);
            }
        }
        if (spans.Count == 0) warnings.Add("EMPTY_DOCUMENT");
        var extraction = new Extraction(spans.ToArray(), warnings.Order().ToArray()) {
            Workbook = mime == ExcelMime ? new("workbook-cells-v1", capturedSheets.ToArray(), capturedCells.ToArray(), warnings.Order().ToArray()) : null
        };
        return extraction; // All sheets/cells are authoritative; no template adapter discards them.
    }
    // ZipArchive does not reject every encryption flag or validate every stored-entry CRC.
    // Inspect central AND local headers, then check the actual decompressed bytes ourselves.
    private static uint[] CheckZipHeaders(byte[] bytes)
    {
        uint U32(int i) => i < 0 || i > bytes.Length - 4 ? throw new DomainException(400, "INVALID_PACKAGE") : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i, 4));
        ushort U16(int i) => i < 0 || i > bytes.Length - 2 ? throw new DomainException(400, "INVALID_PACKAGE") : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i, 2));
        int end = -1;
        for (var i = bytes.Length - 22; i >= Math.Max(0, bytes.Length - 65557); i--)
            if (U32(i) == 0x06054b50 && i + 22 + U16(i + 20) == bytes.Length) { end = i; break; }
        if (end < 0 || U16(end + 4) != 0 || U16(end + 6) != 0 || U16(end + 8) != U16(end + 10)) throw new DomainException(400, "INVALID_PACKAGE");
        var count = U16(end + 10); var offset = U32(end + 16); var size = U32(end + 12);
        if (count == ushort.MaxValue || offset > bytes.Length || size > bytes.Length || (long)offset + size != end) throw new DomainException(400, "INVALID_PACKAGE");
        uint[] crc = new uint[count]; var cursor = (int)offset;
        for (var i = 0; i < count; i++)
        {
            if (U32(cursor) != 0x02014b50) throw new DomainException(400, "INVALID_PACKAGE");
            var flags = U16(cursor + 8); var method = U16(cursor + 10);
            if ((flags & 0x2041) != 0 || method is not (0 or 8)) throw new DomainException(400, "UNSUPPORTED_DOCUMENT");
            var localOffset = U32(cursor + 42);
            if (localOffset >= offset) throw new DomainException(400, "INVALID_PACKAGE");
            var local = (int)localOffset;
            if (U32(local) != 0x04034b50 || U16(local + 6) != flags || U16(local + 8) != method) throw new DomainException(400, "INVALID_PACKAGE");
            if ((long)local + 30 + U16(local + 26) + U16(local + 28) + U32(cursor + 20) > offset) throw new DomainException(400, "INVALID_PACKAGE");
            crc[i] = U32(cursor + 16);
            cursor = checked(cursor + 46 + U16(cursor + 28) + U16(cursor + 30) + U16(cursor + 32));
        }
        if (cursor != end) throw new DomainException(400, "INVALID_PACKAGE");
        return crc;
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
    {
        uint value = (uint)i; for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320 ^ (value >> 1) : value >> 1;
        return value;
    }).ToArray();
    private static uint Crc32(byte[] bytes)
    {
        uint value = uint.MaxValue; foreach (var b in bytes) value = CrcTable[(value ^ b) & 255] ^ (value >> 8);
        return ~value;
    }
    private static bool ValidAddress(string address)
    {
        var match = System.Text.RegularExpressions.Regex.Match(address, "^([A-Z]{1,3})([1-9][0-9]{0,6})$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (!match.Success) return false;
        int column = 0; foreach (var c in match.Groups[1].Value) column = column * 26 + c - 'A' + 1;
        return column <= 16384 && int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) <= 1048576;
    }
    private static int ColumnNumber(string address) => address.TakeWhile(char.IsAsciiLetter).Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
    private static bool InRange(string address, string range)
    {
        var bounds = range.Split(':');
        if (bounds.Length is < 1 or > 2 || !bounds.All(ValidAddress)) throw new DomainException(400, "INVALID_PACKAGE");
        if (bounds.Length == 1) return address == bounds[0];
        int Row(string a) => int.Parse(new string(a.SkipWhile(char.IsAsciiLetter).ToArray()), CultureInfo.InvariantCulture);
        return ColumnNumber(address) >= ColumnNumber(bounds[0]) && ColumnNumber(address) <= ColumnNumber(bounds[1]) && Row(address) >= Row(bounds[0]) && Row(address) <= Row(bounds[1]);
    }
    private static bool IsDate(string format)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(format, "\"[^\"]*\"|\\\\.|\\[[^]]*\\]", "", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        return stripped.IndexOfAny(['y', 'd', 'h', 's', 'Y', 'D', 'H', 'S']) >= 0;
    }
    private static XDocument Xml(Dictionary<string, byte[]> parts, string name)
    {
        if (!parts.TryGetValue(name, out var bytes)) throw new DomainException(400, "INVALID_PACKAGE");
        using var reader = XmlReader.Create(new MemoryStream(bytes), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 50 * 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static string Resolve(string folder, string target)
    {
        if (target.Contains('\\') || target.Contains(':') || target.Contains('%') || target.Contains('#')) throw new DomainException(400, "INVALID_PACKAGE");
        List<string> segments = [];
        foreach (var segment in (target.StartsWith('/') ? target[1..] : folder + target).Split('/'))
        {
            if (segment is "" or ".") continue;
            if (segment == "..") { if (segments.Count == 0) throw new DomainException(400, "INVALID_PACKAGE"); segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return string.Join('/', segments);
    }
}
