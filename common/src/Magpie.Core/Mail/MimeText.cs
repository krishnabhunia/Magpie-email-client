using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>Turns a MIME message into what we store and show: HTML, plain text, attachment list.</summary>
public static class MimeText
{
    public static MessageBody Extract(MimeMessage msg)
    {
        var body = new MessageBody();
        var html = msg.HtmlBody;
        var text = msg.TextBody;
        if (string.IsNullOrEmpty(html) && !string.IsNullOrEmpty(text))
            html = TextToHtml(text);
        if (string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(html))
            text = HtmlToText(html);
        body.Html = html ?? "";
        body.Text = text ?? "";
        body.Attachments = ListAttachments(msg);
        body.Calendar = CalendarText(msg);
        return body;
    }

    /// <summary>The first text/calendar (or .ics) part: a meeting invite, update or cancellation (design B3).</summary>
    public static string CalendarText(MimeMessage msg)
    {
        foreach (var part in msg.BodyParts.OfType<MimePart>())
        {
            var isCal = part.ContentType.IsMimeType("text", "calendar") || part.ContentType.IsMimeType("application", "ics")
                        || (part.FileName ?? "").EndsWith(".ics", StringComparison.OrdinalIgnoreCase);
            if (!isCal) continue;
            try
            {
                if (part is TextPart tp) return tp.Text ?? "";
                using var ms = new MemoryStream();
                part.Content?.DecodeTo(ms);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            catch { }
        }
        return "";
    }

    /// <summary>
    /// Every leaf part that is an attachment or an inline image with a Content-Id, numbered in
    /// <see cref="MimeMessage.BodyParts"/> order (the index is how the UI asks for a part later).
    /// </summary>
    public static List<AttachmentInfo> ListAttachments(MimeMessage msg)
    {
        var list = new List<AttachmentInfo>();
        int i = -1;
        foreach (var entity in msg.BodyParts)
        {
            i++;
            if (entity is MessagePart mp)
            {
                list.Add(new AttachmentInfo { Index = i, FileName = (mp.ContentDisposition?.FileName ?? mp.Message?.Subject ?? "message") + ".eml", ContentType = "message/rfc822", Size = 0 });
                continue;
            }
            if (entity is not MimePart part) continue;
            if (part is TextPart && !part.IsAttachment) continue;
            var cid = part.ContentId ?? "";
            bool inline = !part.IsAttachment && cid.Length > 0;
            if (!part.IsAttachment && !inline) continue;
            list.Add(new AttachmentInfo
            {
                Index = i,
                FileName = part.FileName ?? (inline ? "image" : "attachment"),
                ContentType = part.ContentType.MimeType,
                Size = EstimateSize(part),
                ContentId = cid,
                Inline = inline,
            });
        }
        return list;
    }

    public static MimeEntity? PartAt(MimeMessage msg, int index) => msg.BodyParts.ElementAtOrDefault(index);

    /// <summary>First negative index of attachments listed before their email is downloaded (design DS1).</summary>
    public const int PendingIndex = -1;

    /// <summary>
    /// Attachments and inline pictures read from the server's description of an email (BODYSTRUCTURE), before the
    /// email itself is downloaded (design DS1). They get negative indices: attachment k (in order) is -(k+1), inline
    /// pictures -1000 and below. <see cref="ResolveIndex"/> maps an attachment's index to the downloaded email.
    /// </summary>
    public static List<AttachmentInfo> PendingAttachments(IEnumerable<MailKit.BodyPartBasic> parts, MailKit.BodyPart? textBody, MailKit.BodyPart? htmlBody)
    {
        var list = new List<AttachmentInfo>();
        int k = 0, inl = 0;
        foreach (var p in parts)
        {
            if (ReferenceEquals(p, textBody) || ReferenceEquals(p, htmlBody)) continue;
            var cid = p.ContentId?.Trim('<', '>') ?? "";
            var isAttachment = p.IsAttachment || (p is MailKit.BodyPartMessage);
            var inline = !isAttachment && cid.Length > 0;
            if (p is MailKit.BodyPartText && !isAttachment) continue;
            if (!isAttachment && !inline) continue;
            list.Add(new AttachmentInfo
            {
                Index = inline ? -1000 - inl++ : PendingIndex - k++,
                FileName = p is MailKit.BodyPartMessage m ? (m.ContentDisposition?.FileName ?? m.Envelope?.Subject ?? "message") + ".eml"
                                                          : p.FileName ?? (inline ? "image" : "attachment"),
                ContentType = p.ContentType.MimeType,
                Size = p.Octets,
                ContentId = cid,
                Inline = inline,
            });
        }
        return list;
    }

    /// <summary>True when the email must be read again to show it: a picture inside it isn't kept with the text yet
    /// (design RL1). Attachments listed before download are fetched only when clicked, not on opening.</summary>
    public static bool NeedsDownload(MessageBody? body) => body != null && !body.ImagesComplete && MissingPictures(body).Any();

    /// <summary>Pictures the email shows (an image part referenced as cid: in its HTML) that aren't kept with its text.
    /// Other inline parts (a calendar, a signature file) and unused pictures don't count (Q39: they made every
    /// opening read the whole email again).</summary>
    public static IEnumerable<string> MissingPictures(MessageBody body) =>
        body.Attachments.Where(a => a.Inline && a.ContentId.Length > 0
                                    && a.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                                    && !body.Images.ContainsKey(a.ContentId)
                                    && body.Html.Contains("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase))
                        .Select(a => a.ContentId);

    /// <summary>Pictures kept with an email's text (design RL1): at most this many bytes; bigger sets come from the
    /// message file on this PC when the email is opened.</summary>
    public const long ImageCacheBytes = 4 * 1024 * 1024;

    /// <summary>The real index in <paramref name="msg"/> of an attachment listed before download (negative index); others unchanged.</summary>
    public static int ResolveIndex(MimeMessage msg, int index)
    {
        if (index >= 0 || index <= -1000) return index;
        var real = ListAttachments(msg).Where(a => !a.Inline).ElementAtOrDefault(PendingIndex - index);
        return real?.Index ?? index;
    }

    private static long EstimateSize(MimePart part)
    {
        try
        {
            if (part.Content?.Stream is { CanSeek: true } s)
            {
                var len = s.Length;
                return part.ContentTransferEncoding == ContentEncoding.Base64 ? len * 3 / 4 : len;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Content-Id → data: URI for inline images (used when rendering).</summary>
    public static Dictionary<string, string> InlineImages(MimeMessage msg, long maxBytes = 8 * 1024 * 1024)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var part in msg.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || !part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            using var ms = new MemoryStream();
            part.Content?.DecodeTo(ms);
            total += ms.Length;
            if (total > maxBytes) break;
            map[part.ContentId.Trim('<', '>')] = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
        }
        return map;
    }

    public static string TextToHtml(string text)
    {
        var enc = WebUtility.HtmlEncode(text).Replace("\r\n", "\n");
        enc = Regex.Replace(enc, @"(https?://[^\s<>&""]+)", "<a href=\"$1\">$1</a>");
        return "<div style=\"white-space:pre-wrap;font-family:inherit\">" + enc + "</div>";
    }

    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        try
        {
            var doc = new HtmlParser().ParseDocument(html);
            foreach (var bad in doc.QuerySelectorAll("script,style,head,title").ToList()) bad.Remove();
            foreach (var br in doc.QuerySelectorAll("br").ToList()) br.Replace(doc.CreateTextNode("\n"));
            foreach (var blk in doc.QuerySelectorAll("p,div,tr,li,h1,h2,h3,h4,h5,h6,blockquote,table").ToList())
                blk.Append(doc.CreateTextNode("\n"));
            var t = doc.Body?.TextContent ?? doc.DocumentElement.TextContent;
            t = Regex.Replace(t, @"[ \t ]+", " ");
            t = Regex.Replace(t, @"\n\s*\n\s*\n+", "\n\n");
            return t.Trim();
        }
        catch
        {
            return WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", " "));
        }
    }

    /// <summary>One-line preview for the message list.</summary>
    public static string Preview(string text, int max = 180)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('>')) continue;
            if (QuoteHeader.IsMatch(line)) break;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(line);
            if (sb.Length >= max) break;
        }
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return s.Length > max ? s[..max].TrimEnd() + "…" : s;
    }

    private static readonly Regex QuoteHeader = new(@"^(On .{4,200} wrote:?$|-{2,}\s*Original Message\s*-{2,}|From:\s.+|_{8,})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The new part of a message: drops "&gt;" quoted lines and everything after a reply header or signature marker.</summary>
    public static string StripQuoted(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].TrimEnd();
            var trimmed = t.Trim();
            if (trimmed.StartsWith('>')) continue;
            if (QuoteHeader.IsMatch(trimmed)) break;
            // "On Tue, 3 Sep 2026 at 10:02, Anita Rao <a@b.com>" + next line "wrote:" (wrapped header)
            if (trimmed.StartsWith("On ", StringComparison.Ordinal) && i + 1 < lines.Length && lines[i + 1].Trim().EndsWith("wrote:", StringComparison.Ordinal)) break;
            if (trimmed == "--") break;
            sb.Append(t).Append('\n');
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim();
    }
}
