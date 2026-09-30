using System.Globalization;

namespace Magpie.Core.Mail;

/// <summary>One line of a hover card (design HM1): its id (E1…E11, S1…S9, A1…A9) and the words shown.</summary>
public sealed record HoverOption(string Id, string Label);

/// <summary>
/// Design HM1: what the small card offers when the mouse rests on an email address, the subject or an attachment in
/// the reading pane. The ids match the approved design; the screens only draw these lists and run the chosen id.
/// </summary>
public static class HoverMenus
{
    /// <summary>
    /// Email address card. For your own address only E1–E5. E6 (Add to contacts) waits for the Contacts page;
    /// E7 only when a Google calendar can be written to; E8 only while that sender's pictures still need asking.
    /// </summary>
    public static IReadOnlyList<HoverOption> ForAddress(string name, string address, bool isMe, bool hasCalendar, bool picturesTrusted, bool hasContacts = false)
    {
        var who = FirstName(name, address);
        var list = new List<HoverOption>
        {
            new("E1", "Copy address"),
            new("E2", "Copy name and address"),
            new("E3", "Write a new email"),
            new("E4", isMe ? "Emails you sent" : $"Emails from {who}"),
            new("E5", isMe ? "All emails with you" : $"All emails with {who}"),
        };
        if (isMe) return list;
        if (hasContacts) list.Add(new("E6", "Add to contacts"));
        if (hasCalendar) list.Add(new("E7", $"New event with {who}"));
        if (!picturesTrusted) list.Add(new("E8", $"Always show pictures from {who}"));
        list.Add(new("E9", $"Make a rule for {who}'s emails…"));
        list.Add(new("E10", $"Auto-delete {who}'s emails after…"));
        list.Add(new("E11", "Block (send to Spam)"));
        return list;
    }

    /// <summary>Subject card. S7 only with a writable Google calendar; S9 only when AI summaries are switched on.</summary>
    public static IReadOnlyList<HoverOption> ForSubject(bool hasCalendar, bool aiSummary)
    {
        var list = new List<HoverOption>
        {
            new("S1", "Copy subject"),
            new("S2", "Emails with this subject"),
            new("S3", "Open in its own window"),
            new("S4", "Remind me if no reply…"),
            new("S5", "Snooze…"),
            new("S6", "Tag…"),
        };
        if (hasCalendar) list.Add(new("S7", "Make an event from this email"));
        list.Add(new("S8", "Make a rule for this subject…"));
        if (aiSummary) list.Add(new("S9", "Summarise with AI"));
        return list;
    }

    /// <summary>
    /// Attachment card. A3 only when the email has 2 or more files; A5 only for pictures and PDFs; A7 only once the
    /// file has been saved; A8 only for files other people sent.
    /// </summary>
    public static IReadOnlyList<HoverOption> ForAttachment(string fileName, string senderName, string senderAddress, bool fromMe, int attachmentCount, bool saved)
    {
        var list = new List<HoverOption> { new("A1", "Open") };
        if (CanPreview(fileName)) list.Add(new("A5", "Preview in Magpie"));
        list.Add(new("A2", "Save as…"));
        if (attachmentCount >= 2) list.Add(new("A3", $"Save all {attachmentCount} attachments…"));
        list.Add(new("A4", "Copy file"));
        list.Add(new("A6", "Forward just this file"));
        if (saved) list.Add(new("A7", "Show in folder"));
        if (!fromMe) list.Add(new("A8", $"Files from {FirstName(senderName, senderAddress)}"));
        list.Add(new("A9", "Emails with this file name"));
        return list;
    }

    /// <summary>"Anita" from "Anita Rao"; "anita" from anita@vendorco.in when there is no name.</summary>
    public static string FirstName(string? name, string? address)
    {
        var n = (name ?? "").Trim().Trim('"', '\'').Trim();
        if (n.Length > 0 && !n.Contains('@'))
        {
            // "Rao, Anita" → Anita
            if (n.Contains(',')) n = n[(n.IndexOf(',') + 1)..].Trim();
            var first = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrEmpty(first)) return first;
        }
        var a = (address ?? "").Trim();
        var at = a.IndexOf('@');
        return at > 0 ? a[..at] : a.Length > 0 ? a : "this person";
    }

    /// <summary>"Anita Rao &lt;anita@vendorco.in&gt;" (E2), or just the address when there is no name.</summary>
    public static string NameAndAddress(string? name, string address)
    {
        var n = (name ?? "").Trim().Trim('"').Trim();
        if (n.Length == 0 || n.Equals(address, StringComparison.OrdinalIgnoreCase)) return address;
        var needsQuotes = n.IndexOfAny(new[] { ',', ';', '<', '>', '@', '"', '(', ')', ':', '[', ']', '\\' }) >= 0;
        return (needsQuotes ? "\"" + n.Replace("\"", "'") + "\"" : n) + " <" + address + ">";
    }

    /// <summary>S1: the subject without "Re:" / "Fwd:".</summary>
    public static string CleanSubject(string? subject) => Threading.StripSubjectPrefixes(subject);

    /// <summary>The line under the address: "34 emails, last on 30 Sep" (dates in another year show it).</summary>
    public static string AddressLine(string address, int count, DateTimeOffset? last, DateTimeOffset now)
    {
        if (count <= 0 || last == null) return address;
        var l = last.Value.ToLocalTime();
        var when = l.Year == now.ToLocalTime().Year ? l.ToString("d MMM", CultureInfo.InvariantCulture) : l.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return $"{address} · {count:N0} email{(count == 1 ? "" : "s")}, last on {when}";
    }

    /// <summary>The line under a file name: "PDF document · 2.4 MB · on this PC" or "· downloads when opened".</summary>
    public static string AttachmentLine(string fileName, long size, bool onThisPc)
    {
        var parts = new List<string> { KindName(fileName) };
        var sz = HtmlRenderer.FormatSize(size);
        if (sz.Length > 0) parts.Add(sz);
        parts.Add(onThisPc ? "on this PC" : "downloads when opened");
        return string.Join(" · ", parts);
    }

    /// <summary>The line under the subject: "3 emails in this conversation · Inbox".</summary>
    public static string SubjectLine(int count, string folder) =>
        $"{count} email{(count == 1 ? "" : "s")} in this conversation" + (string.IsNullOrWhiteSpace(folder) ? "" : " · " + folder);

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = "PDF document", ["doc"] = "Word document", ["docx"] = "Word document", ["xls"] = "Excel workbook", ["xlsx"] = "Excel workbook",
        ["csv"] = "Spreadsheet (CSV)", ["ppt"] = "PowerPoint presentation", ["pptx"] = "PowerPoint presentation", ["txt"] = "Text file",
        ["jpg"] = "JPG picture", ["jpeg"] = "JPG picture", ["png"] = "PNG picture", ["gif"] = "GIF picture", ["webp"] = "WebP picture", ["bmp"] = "BMP picture",
        ["zip"] = "Zip folder", ["7z"] = "7-Zip archive", ["rar"] = "RAR archive", ["eml"] = "Email", ["ics"] = "Calendar invitation",
        ["mp3"] = "Sound", ["wav"] = "Sound", ["mp4"] = "Video", ["mov"] = "Video", ["html"] = "Web page", ["htm"] = "Web page",
    };

    public static string Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot >= 0 && dot < fileName.Length - 1 ? fileName[(dot + 1)..].ToLowerInvariant() : "";
    }

    /// <summary>"PDF document", "JPG picture", … or "XYZ file".</summary>
    public static string KindName(string fileName)
    {
        var ext = Extension(fileName);
        return Kinds.TryGetValue(ext, out var k) ? k : ext.Length > 0 ? ext.ToUpperInvariant() + " file" : "File";
    }

    /// <summary>The short label on the file's icon (PDF, JPG, …).</summary>
    public static string Badge(string fileName)
    {
        var ext = Extension(fileName);
        return ext.Length is > 0 and <= 4 ? ext.ToUpperInvariant() : "FILE";
    }

    /// <summary>A5: pictures and PDFs can be shown inside Magpie.</summary>
    public static bool CanPreview(string fileName) => Extension(fileName) is "pdf" or "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp";

    /// <summary>The search typed for a search option (E4, E5, S2, A8, A9).</summary>
    public static string SearchFor(string id, string value, string? second = null) => id switch
    {
        "E4" => "from:" + Quote(value),
        "E5" => "with:" + Quote(value),
        "S2" => "subject:" + Quote(value),
        "A8" => "from:" + Quote(value) + " has:attachment",
        "A9" => "file:" + Quote(value),
        _ => value,
    };

    private static string Quote(string v)
    {
        v = v.Replace("\"", "").Trim();
        return v.Any(char.IsWhiteSpace) ? "\"" + v + "\"" : v;
    }
}
