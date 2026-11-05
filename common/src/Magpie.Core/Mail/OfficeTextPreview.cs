using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Magpie.Core.Mail;

/// <summary>Bounded, read-only Office Open XML text extraction. No macros, links, media or external content.</summary>
public static class OfficeTextPreview
{
    public const long MaximumPackageBytes = 15L * 1024 * 1024;
    private const long MaximumDocumentXmlBytes = 3L * 1024 * 1024;
    private const int MaximumTextLength = 150_000;

    /// <summary>Text-only preview for three non-macro Office Open XML formats.</summary>
    public static string ReadDocument(Stream package, string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".docx" => ReadWordDocument(package),
            ".xlsx" => ReadSpreadsheet(package),
            ".pptx" => ReadPresentation(package),
            _ => throw new NotSupportedException("Only .docx, .xlsx and .pptx documents can be previewed."),
        };

    public static string ReadWordDocument(Stream document)
    {
        using var archive = OpenPackage(document);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The Word document does not contain a readable main document.");
        var parsed = ReadXml(entry);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text = new StringBuilder();
        foreach (var paragraph in parsed.Descendants(word + "p"))
        {
            var line = string.Concat(paragraph.Descendants(word + "t").Select(t => t.Value));
            if (line.Length == 0) continue;
            if (text.Length + line.Length + Environment.NewLine.Length > MaximumTextLength)
            {
                var available = Math.Max(0, MaximumTextLength - text.Length);
                if (available > 0) text.Append(line.AsSpan(0, Math.Min(available, line.Length)));
                text.Append("\n\n[Preview stops after 150,000 characters.]");
                break;
            }
            text.AppendLine(line);
        }
        return text.Length == 0 ? "[No readable document text was found.]" : text.ToString();
    }

    public static string ReadPresentation(Stream document)
    {
        using var archive = OpenPackage(document);
        var slides = NumberedEntries(archive, "ppt/slides/slide", maximum: 40);
        if (slides.Count == 0) throw new InvalidDataException("No readable slides were found in this presentation.");
        XNamespace draw = "http://schemas.openxmlformats.org/drawingml/2006/main";
        var text = new StringBuilder();
        long budget = 12L * 1024 * 1024;
        foreach (var (number, entry) in slides)
        {
            SpendBudget(entry, ref budget);
            var xml = ReadXml(entry);
            if (!AppendLine(text, $"[Slide {number}]")) break;
            foreach (var paragraph in xml.Descendants(draw + "p"))
            {
                var value = string.Concat(paragraph.Descendants(draw + "t").Select(t => t.Value));
                if (value.Length > 0 && !AppendLine(text, value)) break;
            }
            if (text.Length >= MaximumTextLength) break;
        }
        return text.ToString();
    }

    public static string ReadSpreadsheet(Stream document)
    {
        using var archive = OpenPackage(document);
        var sheets = NumberedEntries(archive, "xl/worksheets/sheet", maximum: 20);
        if (sheets.Count == 0) throw new InvalidDataException("No readable worksheets were found in this spreadsheet.");
        XNamespace sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        long budget = 12L * 1024 * 1024;
        var shared = new List<string>();
        if (archive.GetEntry("xl/sharedStrings.xml") is { } sharedEntry)
        {
            SpendBudget(sharedEntry, ref budget);
            var xml = ReadXml(sharedEntry);
            foreach (var item in xml.Descendants(sheet + "si").Take(20_000))
                shared.Add(string.Concat(item.Descendants(sheet + "t").Select(t => t.Value)));
        }
        var text = new StringBuilder();
        foreach (var (number, entry) in sheets)
        {
            SpendBudget(entry, ref budget);
            var xml = ReadXml(entry);
            if (!AppendLine(text, $"[Sheet {number}]")) break;
            foreach (var row in xml.Descendants(sheet + "row").Take(2000))
            {
                foreach (var cell in row.Elements(sheet + "c"))
                {
                    var reference = (string?)cell.Attribute("r") ?? "Cell";
                    var type = (string?)cell.Attribute("t") ?? "";
                    var value = cell.Element(sheet + "v")?.Value ?? "";
                    if (type == "s")
                        value = int.TryParse(value, out var index) && index >= 0 && index < shared.Count ? shared[index] : "";
                    else if (type == "inlineStr")
                        value = string.Concat((cell.Element(sheet + "is")?.Descendants(sheet + "t") ??
                            Enumerable.Empty<XElement>()).Select(x => x.Value));
                    if (value.Length > 0 && !AppendLine(text, reference + ": " + value)) break;
                }
                if (text.Length >= MaximumTextLength) break;
            }
            if (text.Length >= MaximumTextLength) break;
        }
        return text.ToString();
    }

    private static ZipArchive OpenPackage(Stream document)
    {
        if (!document.CanSeek || document.Length > MaximumPackageBytes)
            throw new InvalidDataException("Office files above 15 MB cannot be previewed inside Magpie.");
        var archive = new ZipArchive(document, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count <= 2000) return archive;
        archive.Dispose();
        throw new InvalidDataException("This Office document has too many internal files to preview safely.");
    }

    private static XDocument ReadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaximumDocumentXmlBytes)
            throw new InvalidDataException("This document's text is too large for an in-app preview.");
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentXmlBytes,
            CloseInput = false,
        });
        return XDocument.Load(xml);
    }

    private static int EntryNumber(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".xml", StringComparison.Ordinal)) return 0;
        return int.TryParse(path.AsSpan(prefix.Length, path.Length - prefix.Length - 4), out var number) ? number : 0;
    }

    private static List<(int Number, ZipArchiveEntry Entry)> NumberedEntries(ZipArchive archive, string prefix, int maximum) =>
        archive.Entries.Select(e => (Number: EntryNumber(e.FullName, prefix), Entry: e))
            .Where(e => e.Number > 0)
            .OrderBy(e => e.Number).Take(maximum).ToList();

    private static void SpendBudget(ZipArchiveEntry entry, ref long bytes)
    {
        if (entry.Length > bytes) throw new InvalidDataException("This Office document expands to too much text for a safe preview.");
        bytes -= entry.Length;
    }

    private static bool AppendLine(StringBuilder text, string line)
    {
        if (text.Length + line.Length + Environment.NewLine.Length <= MaximumTextLength)
        {
            text.AppendLine(line);
            return true;
        }
        var available = Math.Max(0, MaximumTextLength - text.Length);
        if (available > 0) text.Append(line.AsSpan(0, Math.Min(line.Length, available)));
        text.Append("\n\n[Preview stops after 150,000 characters.]");
        return false;
    }
}
