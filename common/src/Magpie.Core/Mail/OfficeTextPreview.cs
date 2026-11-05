using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Magpie.Core.Mail;

/// <summary>Bounded, read-only Office Open XML text extraction. No macros, links, media or external content.</summary>
public static class OfficeTextPreview
{
    public const long MaximumPackageBytes = 15L * 1024 * 1024;
    private const long MaximumDocumentXmlBytes = 3L * 1024 * 1024;
    private const int MaximumTextLength = 150_000;

    public static string ReadWordDocument(Stream document)
    {
        if (!document.CanSeek || document.Length > MaximumPackageBytes)
            throw new InvalidDataException("Word documents above 15 MB cannot be previewed inside Magpie.");
        using var archive = new ZipArchive(document, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > 2000)
            throw new InvalidDataException("This Word document has too many internal files to preview safely.");
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The Word document does not contain a readable main document.");
        if (entry.Length > MaximumDocumentXmlBytes)
            throw new InvalidDataException("The document text is too large for a safe in-app preview.");

        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumDocumentXmlBytes,
            CloseInput = false,
        });
        var parsed = XDocument.Load(xml);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var text = new System.Text.StringBuilder();
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
}
