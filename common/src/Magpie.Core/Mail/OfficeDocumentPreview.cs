using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Magpie.Core.Mail;

/// <summary>A read-only approximation, not an Office renderer. Both representations contain only package text.</summary>
public sealed record OfficePreviewDocument(string Html, string PlainText, bool Truncated)
{
    public string HtmlForTheme(bool dark) => dark
        ? Html.Replace("<html lang=\"en\">", "<html lang=\"en\" class=\"dark\">", StringComparison.Ordinal)
        : Html;
}

/// <summary>
/// Safe structured previews for modern Office files. Never executes formulas/macros or loads relationships,
/// fonts, images or other resources from outside the ZIP. All package text is HTML-encoded.
/// </summary>
public static class OfficeDocumentPreview
{
    public const int MaximumTextLength = 150_000;
    private const int MaximumStructures = 12_000;
    private const int MaximumSheets = 20;
    private const int MaximumRows = 500;
    private const int MaximumColumns = 32;
    private const int MaximumSlides = 40;
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Relationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public static OfficePreviewDocument ReadDocument(Stream package, string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not (".docx" or ".xlsx" or ".pptx"))
            throw new NotSupportedException("Structured preview supports .docx, .xlsx and .pptx only. Legacy and macro-enabled Office formats are not supported.");
        using var reader = new PackageReader(package);
        var output = new PreviewWriter();
        var detail = extension switch
        {
            ".docx" => "Word headings, paragraphs and tables. Images, charts, comments and exact page layout are not rendered.",
            ".xlsx" => "Worksheet names and cell positions. Cached values only; formulas are never evaluated. Number/date formatting and charts are not rendered.",
            _ => "Slide order, titles, text and tables. Images, charts, animations, notes and exact slide layout are not rendered.",
        };
        if (extension == ".docx") ReadWord(reader, output);
        else if (extension == ".xlsx") ReadWorkbook(reader, output);
        else ReadSlides(reader, output);
        if (output.Plain.Length == 0) output.Paragraph("No readable document text was found.");
        if (output.Truncated)
            output.Html.Append("<p class=\"notice\">Preview shortened to stay within safe text, sheet, slide, row, column or structure limits. Open the original in its application to see the complete document.</p>");
        var title = fileName.Length > 512 ? fileName[..512] : fileName;
        var html = Header + "<header><h1>" + WebUtility.HtmlEncode(title) + "</h1><p>Read-only structured preview</p><p class=\"notice\">"
            + WebUtility.HtmlEncode(detail) + "</p></header><main>" + output.Html + "</main></body></html>";
        // NavigateToString has a 2 MB ceiling; the text and structural limits above keep the UTF-8 document below it.
        if (Encoding.UTF8.GetByteCount(html) > 1_900_000)
            throw new InvalidDataException("The structured preview is too large to display safely.");
        return new OfficePreviewDocument(html, output.Plain.ToString(), output.Truncated);
    }

    private static void ReadWord(PackageReader reader, PreviewWriter output)
    {
        var document = reader.ReadRequired("word/document.xml");
        var body = document.Root?.Element(Word + "body")
            ?? throw new InvalidDataException("The Word file has no readable document body.");
        var headings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (reader.ReadOptional("word/styles.xml") is { } styles)
            foreach (var style in styles.Descendants(Word + "style"))
            {
                var id = (string?)style.Attribute(Word + "styleId");
                var level = (string?)style.Element(Word + "pPr")?.Element(Word + "outlineLvl")?.Attribute(Word + "val");
                if (id != null && int.TryParse(level, out var n) && n is >= 0 and <= 5) headings[id] = n + 1;
            }
        WordBlocks(body.Elements(), output, headings, 0);
    }

    private static void WordBlocks(IEnumerable<XElement> elements, PreviewWriter output, IReadOnlyDictionary<string, int> headings, int depth)
    {
        foreach (var element in elements)
        {
            if (output.Full) break;
            if (element.Name == Word + "p") WordParagraph(element, output, headings);
            else if (element.Name == Word + "tbl")
            {
                if (depth >= 3 || !output.Structure()) { output.Truncated = true; continue; }
                output.Html.Append("<div class=\"table-wrap\"><table><tbody>");
                foreach (var row in element.Elements(Word + "tr"))
                {
                    if (!output.Structure()) break;
                    output.Html.Append("<tr>");
                    foreach (var cell in row.Elements(Word + "tc"))
                    {
                        if (!output.Structure()) break;
                        var spanText = (string?)cell.Element(Word + "tcPr")?.Element(Word + "gridSpan")?.Attribute(Word + "val");
                        var span = int.TryParse(spanText, out var n) ? Math.Clamp(n, 1, MaximumColumns) : 1;
                        output.Html.Append("<td colspan=\"").Append(span).Append("\">");
                        WordBlocks(cell.Elements(), output, headings, depth + 1);
                        output.Html.Append("</td>"); output.Plain.Append('\t');
                    }
                    output.Html.Append("</tr>"); output.Plain.AppendLine();
                }
                output.Html.Append("</tbody></table></div>");
            }
            else if (element.Name == Word + "sdt" && element.Element(Word + "sdtContent") is { } content)
            {
                if (depth >= 3) { output.Truncated = true; continue; }
                WordBlocks(content.Elements(), output, headings, depth + 1);
            }
        }
    }

    private static void WordParagraph(XElement paragraph, PreviewWriter output, IReadOnlyDictionary<string, int> headings)
    {
        if (!output.Structure()) return;
        var style = (string?)paragraph.Element(Word + "pPr")?.Element(Word + "pStyle")?.Attribute(Word + "val") ?? "";
        var tag = "p";
        if (headings.TryGetValue(style, out var heading)) tag = "h" + heading;
        else if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var n) && n is >= 1 and <= 6) tag = "h" + n;
        else if (style.Equals("Title", StringComparison.OrdinalIgnoreCase)) tag = "h1";
        var alignment = (string?)paragraph.Element(Word + "pPr")?.Element(Word + "jc")?.Attribute(Word + "val");
        var alignClass = alignment switch { "center" => "center", "right" or "end" => "right", _ => "left" };
        output.Html.Append('<').Append(tag).Append(" class=\"").Append(alignClass).Append("\">");
        foreach (var run in paragraph.Descendants(Word + "r"))
        {
            if (!output.Structure()) break;
            var properties = run.Element(Word + "rPr");
            var bold = WordFlag(properties, "b");
            var italic = WordFlag(properties, "i");
            var underline = WordFlag(properties, "u");
            if (bold) output.Html.Append("<strong>");
            if (italic) output.Html.Append("<em>");
            if (underline) output.Html.Append("<u>");
            foreach (var piece in run.Elements())
            {
                if (piece.Name == Word + "t") output.Text(piece.Value);
                else if (piece.Name == Word + "tab") output.Text("\t");
                else if (piece.Name == Word + "br" || piece.Name == Word + "cr") { output.Html.Append("<br>"); output.Plain.AppendLine(); }
                if (output.Full) break;
            }
            if (underline) output.Html.Append("</u>");
            if (italic) output.Html.Append("</em>");
            if (bold) output.Html.Append("</strong>");
        }
        output.Html.Append("</").Append(tag).Append('>'); output.Plain.AppendLine();
    }

    private static bool WordFlag(XElement? properties, string name)
    {
        var property = properties?.Element(Word + name);
        var value = (string?)property?.Attribute(Word + "val");
        return property != null && value is not ("0" or "false" or "off" or "none");
    }

    private static void ReadWorkbook(PackageReader reader, PreviewWriter output)
    {
        var parts = OrderedParts(reader, "xl/workbook.xml", "xl/_rels/workbook.xml.rels", Sheet + "sheet", "worksheet", "xl/worksheets/sheet", MaximumSheets);
        var shared = reader.ReadOptional("xl/sharedStrings.xml")?.Descendants(Sheet + "si").Take(20_000)
            .Select(x => string.Concat(x.Descendants(Sheet + "t").Select(t => t.Value))).ToList() ?? new List<string>();
        output.Truncated |= parts.Truncated;
        foreach (var part in parts.Items)
        {
            if (output.Full) break;
            var document = reader.ReadRequired(part.Path);
            output.Heading(part.Name);
            var allRows = document.Descendants(Sheet + "sheetData").Elements(Sheet + "row").Take(MaximumRows + 1).ToList();
            if (allRows.Count > MaximumRows) { output.Truncated = true; allRows.RemoveAt(MaximumRows); }
            var rows = new List<(int Number, SortedDictionary<int, XElement> Cells)>();
            var columns = 0;
            var nextRow = 1;
            foreach (var row in allRows)
            {
                var cells = new SortedDictionary<int, XElement>();
                var nextColumn = 1;
                foreach (var cell in row.Elements(Sheet + "c"))
                {
                    var reference = (string?)cell.Attribute("r");
                    var parsedColumn = ColumnNumber(reference);
                    if (reference != null && parsedColumn == null) { output.Truncated = true; continue; }
                    var column = parsedColumn ?? nextColumn;
                    nextColumn = column + 1;
                    if (column > MaximumColumns) { output.Truncated = true; continue; }
                    if (column < 1) continue;
                    cells.TryAdd(column, cell);
                    columns = Math.Max(columns, column);
                }
                var number = int.TryParse((string?)row.Attribute("r"), out var value) && value is > 0 and <= 1_048_576 ? value : nextRow;
                rows.Add((number, cells)); nextRow = number + 1;
            }
            output.Html.Append("<div class=\"table-wrap\"><table class=\"sheet\"><thead><tr><th scope=\"col\">Row</th>");
            for (var col = 1; col <= columns; col++) output.Html.Append("<th scope=\"col\">").Append(ColumnLabel(col)).Append("</th>");
            output.Html.Append("</tr></thead><tbody>");
            foreach (var row in rows)
            {
                if (!output.Structure()) break;
                output.Html.Append("<tr><th scope=\"row\">").Append(row.Number).Append("</th>");
                output.Plain.Append(row.Number).Append(": ");
                for (var col = 1; col <= columns; col++)
                {
                    if (!output.Structure()) break;
                    row.Cells.TryGetValue(col, out var cell);
                    output.Html.Append(cell?.Element(Sheet + "f") != null ? "<td title=\"Cached formula result; not recalculated\">" : "<td>");
                    if (cell != null) output.Text(CellText(cell, shared));
                    output.Html.Append("</td>"); output.Plain.Append('\t');
                }
                output.Html.Append("</tr>"); output.Plain.AppendLine();
            }
            output.Html.Append("</tbody></table></div>");
        }
    }

    private static string CellText(XElement cell, IReadOnlyList<string> shared)
    {
        var type = (string?)cell.Attribute("t") ?? "";
        var cached = cell.Element(Sheet + "v")?.Value;
        if (type == "inlineStr") return string.Concat(cell.Element(Sheet + "is")?.Descendants(Sheet + "t").Select(t => t.Value) ?? Enumerable.Empty<string>());
        if (type == "s") return int.TryParse(cached, out var index) && index >= 0 && index < shared.Count ? shared[index] : "[Shared text unavailable]";
        if (type == "b") return cached == "1" ? "TRUE" : cached == "0" ? "FALSE" : cached ?? "";
        if (cached == null && cell.Element(Sheet + "f") != null) return "[Formula result unavailable]";
        return cached ?? "";
    }

    private static int? ColumnNumber(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > 12) return null;
        var column = 0;
        var at = 0;
        while (at < reference.Length && reference[at] is >= 'A' and <= 'Z')
        {
            column = column * 26 + reference[at++] - 'A' + 1;
            if (column > 16_384) return null;
        }
        return at > 0 && int.TryParse(reference.AsSpan(at), out var row) && row is > 0 and <= 1_048_576 ? column : null;
    }

    private static string ColumnLabel(int column)
    {
        var text = "";
        while (column > 0) { column--; text = (char)('A' + column % 26) + text; column /= 26; }
        return text;
    }

    private static void ReadSlides(PackageReader reader, PreviewWriter output)
    {
        var parts = OrderedParts(reader, "ppt/presentation.xml", "ppt/_rels/presentation.xml.rels", Presentation + "sldId", "slide", "ppt/slides/slide", MaximumSlides);
        output.Truncated |= parts.Truncated;
        foreach (var part in parts.Items)
        {
            if (output.Full) break;
            var document = reader.ReadRequired(part.Path);
            output.Html.Append("<section class=\"slide\">"); output.Heading(part.Name);
            var tree = document.Root?.Element(Presentation + "cSld")?.Element(Presentation + "spTree");
            if (tree != null) SlideShapes(tree.Elements(), output, 0);
            else foreach (var paragraph in document.Descendants(Drawing + "p")) { DrawingParagraph(paragraph, output); if (output.Full) break; }
            output.Html.Append("</section>");
        }
    }

    private static void SlideShapes(IEnumerable<XElement> shapes, PreviewWriter output, int depth)
    {
        foreach (var shape in shapes)
        {
            if (output.Full) break;
            if (shape.Name == Presentation + "grpSp")
            {
                if (depth >= 6) { output.Truncated = true; continue; }
                SlideShapes(shape.Elements(), output, depth + 1);
            }
            else if (shape.Name == Presentation + "sp")
            {
                var placeholder = (string?)shape.Descendants(Presentation + "ph").FirstOrDefault()?.Attribute("type");
                foreach (var paragraph in shape.Descendants(Drawing + "p"))
                {
                    DrawingParagraph(paragraph, output, placeholder is "title" or "ctrTitle" ? "h3" : "p");
                    if (output.Full) break;
                }
            }
            else if (shape.Name == Presentation + "graphicFrame")
                foreach (var table in shape.Descendants(Drawing + "tbl"))
                {
                    if (!output.Structure()) break;
                    output.Html.Append("<div class=\"table-wrap\"><table><tbody>");
                    foreach (var row in table.Elements(Drawing + "tr"))
                    {
                        if (!output.Structure()) break;
                        output.Html.Append("<tr>");
                        foreach (var cell in row.Elements(Drawing + "tc"))
                        {
                            if (!output.Structure()) break;
                            output.Html.Append("<td>");
                            foreach (var paragraph in cell.Descendants(Drawing + "p")) { DrawingParagraph(paragraph, output); if (output.Full) break; }
                            output.Html.Append("</td>"); output.Plain.Append('\t');
                        }
                        output.Html.Append("</tr>"); output.Plain.AppendLine();
                    }
                    output.Html.Append("</tbody></table></div>");
                }
        }
    }

    private static void DrawingParagraph(XElement paragraph, PreviewWriter output, string tag = "p")
    {
        if (!output.Structure()) return;
        output.Html.Append('<').Append(tag).Append('>');
        var bullet = (string?)paragraph.Element(Drawing + "pPr")?.Element(Drawing + "buChar")?.Attribute("char");
        if (!string.IsNullOrEmpty(bullet)) output.Text(bullet[..Math.Min(4, bullet.Length)] + " ");
        foreach (var run in paragraph.Elements())
        {
            if (!output.Structure()) break;
            if (run.Name == Drawing + "br") { output.Html.Append("<br>"); output.Plain.AppendLine(); continue; }
            var props = run.Element(Drawing + "rPr");
            var bold = (string?)props?.Attribute("b") is "1" or "true";
            var italic = (string?)props?.Attribute("i") is "1" or "true";
            if (bold) output.Html.Append("<strong>");
            if (italic) output.Html.Append("<em>");
            if (run.Name == Drawing + "t") output.Text(run.Value);
            else foreach (var text in run.Elements(Drawing + "t")) output.Text(text.Value);
            if (italic) output.Html.Append("</em>");
            if (bold) output.Html.Append("</strong>");
        }
        output.Html.Append("</").Append(tag).Append('>'); output.Plain.AppendLine();
    }

    private sealed record Part(string Path, string Name);
    private sealed record PartList(List<Part> Items, bool Truncated);

    private static PartList OrderedParts(PackageReader reader, string mainPath, string relationshipsPath, XName itemName, string kind, string fallbackPrefix, int maximum)
    {
        var main = reader.ReadOptional(mainPath);
        var relationships = reader.ReadOptional(relationshipsPath);
        var items = new List<Part>();
        if (main != null && relationships != null)
        {
            var paths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var relationship in relationships.Root?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (relationship.Name.LocalName != "Relationship" || string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                var id = (string?)relationship.Attribute("Id");
                var type = (string?)relationship.Attribute("Type") ?? "";
                var target = (string?)relationship.Attribute("Target");
                if (id == null || target == null || !type.EndsWith('/' + kind, StringComparison.Ordinal)) continue;
                var path = ResolvePartPath(mainPath, target);
                var folder = fallbackPrefix[..(fallbackPrefix.LastIndexOf('/') + 1)];
                if (path != null && path.StartsWith(folder, StringComparison.Ordinal) && reader.HasEntry(path)) paths.TryAdd(id, path);
            }
            foreach (var item in main.Descendants(itemName))
            {
                var id = (string?)item.Attribute(Relationship + "id");
                if (id == null || !paths.TryGetValue(id, out var path)) continue;
                var name = kind == "worksheet" ? (string?)item.Attribute("name") ?? "Sheet " + (items.Count + 1) : "Slide " + (items.Count + 1);
                items.Add(new Part(path, name));
                if (items.Count > maximum) break;
            }
        }
        else
        {
            // Minimal documents may omit their name/order metadata. Use a deterministic part-name fallback.
            foreach (var entry in reader.Entries.Select(e => (Entry: e, Number: PartNumber(e.FullName, fallbackPrefix))).Where(x => x.Number > 0).OrderBy(x => x.Number).Take(maximum + 1))
                items.Add(new Part(entry.Entry.FullName, (kind == "worksheet" ? "Sheet " : "Slide ") + entry.Number));
        }
        if (items.Count == 0) throw new InvalidDataException("No readable " + (kind == "worksheet" ? "worksheets" : "slides") + " were found in this file.");
        var truncated = items.Count > maximum;
        if (truncated) items.RemoveAt(maximum);
        return new PartList(items, truncated);
    }

    private static int PartNumber(string path, string prefix) => path.StartsWith(prefix, StringComparison.Ordinal)
        && path.EndsWith(".xml", StringComparison.Ordinal)
        && int.TryParse(path.AsSpan(prefix.Length, path.Length - prefix.Length - 4), out var number) ? number : 0;

    private static string? ResolvePartPath(string source, string target)
    {
        if (target.Length > 1024) return null;
        try { target = Uri.UnescapeDataString(target); } catch (UriFormatException) { return null; }
        if (target.IndexOfAny(['\\', ':', '?', '#']) >= 0 || target.Any(char.IsControl) || target.StartsWith("//", StringComparison.Ordinal)) return null;
        var path = target.StartsWith('/') ? target[1..] : source[..(source.LastIndexOf('/') + 1)] + target;
        var parts = new List<string>();
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private sealed class PreviewWriter
    {
        public StringBuilder Html { get; } = new();
        public StringBuilder Plain { get; } = new();
        private int _textLength;
        private int _structures;
        public bool Truncated { get; set; }
        public bool Full
        {
            get
            {
                var full = _textLength >= MaximumTextLength || _structures >= MaximumStructures;
                if (full) Truncated = true;
                return full;
            }
        }
        public bool Structure()
        {
            if (Full) { Truncated = true; return false; }
            _structures++; return true;
        }
        public void Text(string text)
        {
            var length = Math.Min(text.Length, MaximumTextLength - _textLength);
            if (length < text.Length) Truncated = true;
            // Do not leave a high surrogate stranded at the truncated edge.
            if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
            var value = text[..length]; _textLength += length;
            Html.Append(WebUtility.HtmlEncode(value)); Plain.Append(value);
        }
        public void Heading(string text)
        {
            if (!Structure()) return;
            Html.Append("<h2>"); Text(text); Html.Append("</h2>"); Plain.AppendLine();
        }
        public void Paragraph(string text)
        {
            if (!Structure()) return;
            Html.Append("<p>"); Text(text); Html.Append("</p>"); Plain.AppendLine();
        }
    }

    private sealed class PackageReader : IDisposable
    {
        private const long MaximumXmlBytes = 3L * 1024 * 1024;
        private long _remainingBytes = 12L * 1024 * 1024;
        private readonly ZipArchive _archive;
        private readonly Dictionary<string, XDocument> _parsed = new(StringComparer.Ordinal);
        public IEnumerable<ZipArchiveEntry> Entries => _archive.Entries;
        public PackageReader(Stream stream)
        {
            if (!stream.CanSeek || stream.Length > OfficeTextPreview.MaximumPackageBytes)
                throw new InvalidDataException("Office files above 15 MB cannot be previewed inside Magpie.");
            _archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (_archive.Entries.Count <= 2000) return;
            _archive.Dispose();
            throw new InvalidDataException("This Office document contains too many internal files for a safe preview.");
        }
        public bool HasEntry(string path) => _archive.GetEntry(path) != null;
        public XDocument ReadRequired(string path) => ReadOptional(path) ?? throw new InvalidDataException("A required document part is missing: " + path);
        public XDocument? ReadOptional(string path)
        {
            if (_parsed.TryGetValue(path, out var cached)) return cached;
            var entry = _archive.GetEntry(path);
            if (entry == null) return null;
            if (entry.Length > MaximumXmlBytes || entry.Length > _remainingBytes)
                throw new InvalidDataException("This Office document expands to too much text for a safe preview.");
            using var raw = entry.Open();
            using var bounded = new BoundedXmlStream(raw, MaximumXmlBytes, count =>
            {
                _remainingBytes -= count;
                if (_remainingBytes < 0) throw new InvalidDataException("This Office document expands to too much text for a safe preview.");
            });
            using var xml = XmlReader.Create(bounded, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes, CloseInput = false,
            });
            var parsed = XDocument.Load(xml);
            _parsed.Add(path, parsed);
            return parsed;
        }
        public void Dispose() => _archive.Dispose();
    }

    private sealed class BoundedXmlStream(Stream source, long remaining, Action<int> consumed) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining + 1)]);
            remaining -= count; consumed(count);
            if (remaining < 0) throw new InvalidDataException("This document part exceeds the safe preview size.");
            return count;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const string Header = """
        <!doctype html><html lang="en"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Magpie read-only Office preview</title><style>
        *{box-sizing:border-box}body{margin:0;padding:24px;font:15px/1.6 'Segoe UI',system-ui,sans-serif;color:#23282e;background:#fff}
        header{border-bottom:1px solid #dce2e7;margin-bottom:24px;padding-bottom:12px}header h1{font-size:22px;overflow-wrap:anywhere}
        h1,h2,h3,h4,h5,h6{line-height:1.3}h2{font-size:20px;margin-top:28px}h3{font-size:18px}p{margin:0 0 10px;white-space:pre-wrap;overflow-wrap:anywhere}
        .notice{font-size:12px;color:#596976}.table-wrap{overflow:auto;max-width:100%;margin:12px 0 20px}table{border-collapse:collapse;min-width:40%}
        td,th{border:1px solid #b8c0c8;padding:6px 10px;vertical-align:top;white-space:pre-wrap;overflow-wrap:anywhere;max-width:600px}th{background:#edf3f6;text-align:left}
        .sheet th{position:sticky;top:0}.sheet td{min-width:100px}.slide{padding:0 20px 20px;border:1px solid #c8d2d9;border-radius:8px;margin:20px 0}
        .center{text-align:center}.right{text-align:right}.left{text-align:left}.dark body{background:#181c20;color:#d9d6d0}.dark th{background:#29343e}
        .dark td,.dark th,.dark header,.dark .slide{border-color:#4a5964}.dark .notice{color:#b4c2cc}
        </style></head><body>
        """;
}
