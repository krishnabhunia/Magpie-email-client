using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Magpie.Core.Models;
using MimeKit;
using MimeKit.Utils;

namespace Magpie.Core.Mail;

public enum ComposeMode { New, Reply, ReplyAll, Forward, EditDraft }

/// <summary>Everything the compose window edits.</summary>
public sealed class Draft
{
    public string AccountId { get; set; } = "";
    public ComposeMode Mode { get; set; }
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>HTML of the editor (new text + quoted original).</summary>
    public string Html { get; set; } = "";
    public List<string> AttachmentPaths { get; set; } = new();
    /// <summary>Attachments carried over from the original (forward), as raw MIME parts.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<MimeEntity> CarriedParts { get; set; } = new();
    public string InReplyTo { get; set; } = "";
    public string References { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    /// <summary>Local row of the server draft this came from (deleted after sending).</summary>
    public long? SourceDraftRow { get; set; }
    /// <summary>Id of the copy kept on this PC (design F1), once one exists.</summary>
    public long? LocalDraftId { get; set; }
    /// <summary>
    /// Message-ID used for every save and the final send of this message. Keeping it stable lets a re-save
    /// replace the previous server draft and a send remove it, instead of leaving copies behind.
    /// </summary>
    public string MessageId { get; set; } = "";
}

/// <summary>Builds replies/forwards and the final MIME message.</summary>
public static class Composer
{
    public static InternetAddressList ParseAddresses(string text)
    {
        var list = new InternetAddressList();
        if (string.IsNullOrWhiteSpace(text)) return list;
        // Accept ; and newlines as separators too.
        var normalised = Regex.Replace(text, @"[;\r\n]+", ",");
        if (InternetAddressList.TryParse(normalised, out var parsed)) return parsed;
        foreach (var piece in normalised.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MailboxAddress.TryParse(piece, out var mb)) list.Add(mb);
        return list;
    }

    /// <summary>Returns the invalid pieces of an address field (empty = all fine).</summary>
    public static List<string> InvalidAddresses(string text)
    {
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return bad;
        foreach (var piece in Regex.Split(text, @"[,;\r\n]+").Select(p => p.Trim()).Where(p => p.Length > 0))
        {
            if (!MailboxAddress.TryParse(piece, out var mb) || !Regex.IsMatch(mb.Address, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                bad.Add(piece);
        }
        return bad;
    }

    public static string FormatList(IEnumerable<InternetAddress> list) => string.Join(", ", list.Select(a => a.ToString()));

    /// <summary>Reply / reply-all recipients. <paramref name="myAddresses"/> are removed from the lists.</summary>
    public static (string to, string cc) ReplyRecipients(MessageRow original, bool replyAll, IReadOnlyCollection<string> myAddresses, bool originalIsMine)
    {
        var mine = new HashSet<string>(myAddresses.Select(a => a.ToLowerInvariant()));
        InternetAddressList to;
        if (originalIsMine)
            to = ParseAddresses(original.To); // replying to our own sent message → same recipients
        else if (!string.IsNullOrWhiteSpace(original.ReplyTo))
            to = ParseAddresses(original.ReplyTo);
        else
            to = ParseAddresses(string.IsNullOrWhiteSpace(original.FromName) ? original.FromAddress : $"\"{original.FromName}\" <{original.FromAddress}>");

        var toList = Unique(to.Mailboxes).Where(m => !mine.Contains(m.Address.ToLowerInvariant()) || originalIsMine).ToList();
        if (toList.Count == 0) toList = Unique(to.Mailboxes).ToList();
        var cc = new List<MailboxAddress>();
        if (replyAll)
        {
            var seen = new HashSet<string>(toList.Select(m => m.Address.ToLowerInvariant()));
            foreach (var m in ParseAddresses(original.To).Mailboxes.Concat(ParseAddresses(original.Cc).Mailboxes))
            {
                var a = m.Address.ToLowerInvariant();
                if (mine.Contains(a) || !seen.Add(a)) continue;
                cc.Add(m);
            }
        }
        return (FormatList(toList), FormatList(cc));
    }

    private static IEnumerable<MailboxAddress> Unique(IEnumerable<MailboxAddress> list)
    {
        var seen = new HashSet<string>();
        foreach (var m in list) if (seen.Add(m.Address.ToLowerInvariant())) yield return m;
    }

    /// <summary>Prepares a reply/forward draft from the original message.</summary>
    public static Draft Prepare(ComposeMode mode, Account account, MessageRow original, MessageBody? body, IReadOnlyCollection<string> myAddresses, MimeMessage? originalMime)
    {
        var d = new Draft { AccountId = account.Id, Mode = mode, ThreadKey = original.ThreadKey };
        var isMine = myAddresses.Contains(original.FromAddress, StringComparer.OrdinalIgnoreCase);
        var sig = SignatureHtml(account, reply: true);
        if (mode is ComposeMode.Reply or ComposeMode.ReplyAll)
        {
            (d.To, d.Cc) = ReplyRecipients(original, mode == ComposeMode.ReplyAll, myAddresses, isMine);
            d.Subject = Threading.ReplySubject(original.Subject);
            var mid = original.MessageId;
            d.InReplyTo = mid.Length > 0 ? "<" + mid + ">" : "";
            var refs = Threading.ParseReferences(original.References);
            if (mid.Length > 0 && !refs.Contains(mid)) refs.Add(mid);
            d.References = string.Join(" ", refs.TakeLast(20).Select(r => "<" + r + ">"));
            d.Html = "<p><br></p>" + sig + QuoteBlock(original, body);
        }
        else if (mode == ComposeMode.Forward)
        {
            d.Subject = Threading.ForwardSubject(original.Subject);
            d.Html = "<p><br></p>" + sig + ForwardBlock(original, body);
            if (originalMime != null)
                foreach (var part in originalMime.BodyParts)
                    if (part is MimePart { IsAttachment: true } || part is MessagePart) d.CarriedParts.Add(part);
        }
        return d;
    }

    /// <summary>The account's signature block (design B6), or "" when it has none or it is switched off for this kind of message.</summary>
    public static string SignatureHtml(Account? account, bool reply)
    {
        if (account == null || (reply ? !account.SignatureOnReplies : !account.SignatureOnNew)) return "";
        var html = account.SignatureHtml;
        if (string.IsNullOrWhiteSpace(html) && !string.IsNullOrWhiteSpace(account.Signature)) html = LegacySignatureHtml(account.Signature);
        if (string.IsNullOrWhiteSpace(MimeText.HtmlToText(html)) && !html.Contains("<img", StringComparison.OrdinalIgnoreCase)) return "";
        var clean = HtmlRenderer.SanitizeBody(html, new Dictionary<string, string>(), allowRemote: true).html;
        return "<div class=\"magpie-signature\"><br>" + clean + "</div>";
    }

    /// <summary>A 1.1.x plain-text signature as the HTML it used to produce ("-- " line, then the text).</summary>
    public static string LegacySignatureHtml(string signature) =>
        string.IsNullOrWhiteSpace(signature) ? "" :
        "-- <br>" + WebUtility.HtmlEncode(signature.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");

    /// <summary>Plain text as HTML paragraphs (blank line = new paragraph).</summary>
    public static string TextToParagraphs(string text)
    {
        var sb = new StringBuilder();
        foreach (var para in text.Replace("\r\n", "\n").Split("\n\n"))
            sb.Append("<p>").Append(WebUtility.HtmlEncode(para.Trim()).Replace("\n", "<br>")).Append("</p>");
        return sb.ToString();
    }

    /// <summary>A quick reply (design B6): the reply draft with <paramref name="text"/> as its message, above the signature and quote.</summary>
    public static Draft QuickReply(Account account, MessageRow original, MessageBody? body, IReadOnlyCollection<string> myAddresses, string text)
    {
        var d = Prepare(ComposeMode.Reply, account, original, body, myAddresses, null);
        const string empty = "<p><br></p>";
        d.Html = TextToParagraphs(text) + (d.Html.StartsWith(empty, StringComparison.Ordinal) ? d.Html[empty.Length..] : d.Html);
        return d;
    }

    private static string BodyForQuote(MessageBody? body)
    {
        if (body == null) return "";
        var html = string.IsNullOrWhiteSpace(body.Html) ? MimeText.TextToHtml(body.Text) : body.Html;
        // Quote only the sanitised body (never scripts), with remote images kept as-is for the recipient.
        return HtmlRenderer.SanitizeBody(html, new Dictionary<string, string>(), allowRemote: true).html;
    }

    public static string QuoteBlock(MessageRow o, MessageBody? body)
    {
        var who = string.IsNullOrWhiteSpace(o.FromName) ? o.FromAddress : $"{o.FromName} &lt;{WebUtility.HtmlEncode(o.FromAddress)}&gt;";
        var when = o.Date.ToLocalTime().ToString("ddd, d MMM yyyy 'at' HH:mm");
        return $"<div class=\"magpie-quote\"><p>On {WebUtility.HtmlEncode(when)}, {who} wrote:</p><blockquote style=\"margin:0 0 0 .8ex;border-left:2px solid #ccc;padding-left:1ex\">{BodyForQuote(body)}</blockquote></div>";
    }

    public static string ForwardBlock(MessageRow o, MessageBody? body)
    {
        var sb = new StringBuilder("<div class=\"magpie-quote\"><p>---------- Forwarded message ---------<br>");
        sb.Append($"From: {WebUtility.HtmlEncode(o.Sender)} &lt;{WebUtility.HtmlEncode(o.FromAddress)}&gt;<br>");
        sb.Append($"Date: {WebUtility.HtmlEncode(o.Date.ToLocalTime().ToString("ddd, d MMM yyyy 'at' HH:mm"))}<br>");
        sb.Append($"Subject: {WebUtility.HtmlEncode(o.Subject)}<br>");
        sb.Append($"To: {WebUtility.HtmlEncode(o.To)}<br>");
        if (!string.IsNullOrEmpty(o.Cc)) sb.Append($"Cc: {WebUtility.HtmlEncode(o.Cc)}<br>");
        sb.Append("</p>").Append(BodyForQuote(body)).Append("</div>");
        return sb.ToString();
    }

    /// <summary>The final RFC 5322 message.</summary>
    public static MimeMessage Build(Draft d, Account account)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(account.DisplayName, account.Email));
        msg.To.AddRange(ParseAddresses(d.To));
        msg.Cc.AddRange(ParseAddresses(d.Cc));
        msg.Bcc.AddRange(ParseAddresses(d.Bcc));
        msg.Subject = d.Subject ?? "";
        msg.Date = DateTimeOffset.Now;
        var domain = account.Email.Contains('@') ? account.Email.Split('@')[1] : "magpie.local";
        if (string.IsNullOrWhiteSpace(d.MessageId)) d.MessageId = MimeUtils.GenerateMessageId(domain);
        msg.MessageId = d.MessageId;
        if (!string.IsNullOrWhiteSpace(d.InReplyTo)) msg.InReplyTo = d.InReplyTo.Trim().Trim('<', '>');
        foreach (var r in Threading.ParseReferences(d.References)) msg.References.Add(r);
        msg.Headers.Add("X-Mailer", "Magpie 1.0");

        var builder = new BodyBuilder();
        // Pasted pictures arrive as data: URIs, which many mail apps block — send them as inline attachments.
        var inner = DataImage.Replace(d.Html, m =>
        {
            try
            {
                var bytes = Convert.FromBase64String(m.Groups[2].Value);
                var res = builder.LinkedResources.Add("image." + m.Groups[1].Value.Split('/')[1].Split('+')[0], bytes, ContentType.Parse(m.Groups[1].Value));
                res.ContentId = MimeUtils.GenerateMessageId(domain);
                return "src=\"cid:" + res.ContentId + "\"";
            }
            catch { return m.Value; }
        });
        builder.HtmlBody = WrapHtml(inner);
        builder.TextBody = MimeText.HtmlToText(d.Html);
        foreach (var path in d.AttachmentPaths.Where(File.Exists)) builder.Attachments.Add(path);
        foreach (var part in d.CarriedParts) builder.Attachments.Add(part);
        msg.Body = builder.ToMessageBody();
        return msg;
    }

    private static readonly Regex DataImage = new(@"src\s*=\s*[""'](?:data:)(image/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string WrapHtml(string inner) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Calibri,'Segoe UI',Arial,sans-serif;font-size:11pt\">" + inner + "</body></html>";

    public static byte[] ToBytes(MimeMessage msg)
    {
        using var ms = new MemoryStream();
        msg.WriteTo(FormatOptions.Default, ms);
        return ms.ToArray();
    }

    public static MimeMessage FromBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return MimeMessage.Load(ms);
    }

    private static readonly Regex CidImage = new(@"src\s*=\s*[""']cid:([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Pasted pictures are stored as inline parts referenced by cid: (see <see cref="Build"/>). When a message is
    /// opened for editing again they go back into the HTML as data: URIs, so the editor shows them and the next
    /// save or send carries them.
    /// </summary>
    private static string InlineImagesToData(string html, MimeMessage msg, HashSet<string> inlined)
    {
        if (html.IndexOf("cid:", StringComparison.OrdinalIgnoreCase) < 0) return html;
        var byCid = new Dictionary<string, MimePart>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in msg.BodyParts.OfType<MimePart>())
            if (!string.IsNullOrEmpty(part.ContentId) && part.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase))
                byCid[part.ContentId.Trim('<', '>')] = part;
        if (byCid.Count == 0) return html;
        return CidImage.Replace(html, mm =>
        {
            var key = mm.Groups[1].Value.Trim('<', '>');
            if (!byCid.TryGetValue(key, out var part) || part.Content == null) return mm.Value;
            try
            {
                using var ms = new MemoryStream();
                part.Content.DecodeTo(ms);
                inlined.Add(key);
                return "src=\"data:" + part.ContentType.MimeType + ";base64," + Convert.ToBase64String(ms.ToArray()) + "\"";
            }
            catch { return mm.Value; }
        });
    }

    /// <summary>Re-opens an outbox item or server draft for editing.</summary>
    public static Draft FromMime(MimeMessage msg, string accountId, string threadKey)
    {
        var d = new Draft
        {
            AccountId = accountId,
            Mode = ComposeMode.EditDraft,
            To = FormatList(msg.To),
            Cc = FormatList(msg.Cc),
            Bcc = FormatList(msg.Bcc),
            Subject = msg.Subject ?? "",
            InReplyTo = string.IsNullOrEmpty(msg.InReplyTo) ? "" : "<" + msg.InReplyTo + ">",
            References = string.Join(" ", msg.References.Select(r => "<" + r + ">")),
            ThreadKey = threadKey,
            MessageId = msg.MessageId ?? "",
        };
        var html = msg.HtmlBody;
        if (string.IsNullOrEmpty(html)) html = MimeText.TextToHtml(msg.TextBody ?? "");
        var m = Regex.Match(html, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var inlined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        d.Html = InlineImagesToData(m.Success ? m.Groups[1].Value : html, msg, inlined);
        foreach (var part in msg.BodyParts)
        {
            // A picture now inside the HTML is not carried again as an attachment (Outlook marks some as attachments).
            if (part is MimePart { ContentId: { } cid } && inlined.Contains(cid.Trim('<', '>'))) continue;
            if (part is MimePart { IsAttachment: true } || part is MessagePart) d.CarriedParts.Add(part);
        }
        return d;
    }
}
