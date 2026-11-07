using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using MimeKit;
using MimeKit.Utils;

namespace Magpie.Core.Mail;

public enum ComposeMode { New, Reply, ReplyAll, Forward, EditDraft }

/// <summary>One attachment's position. Paths and forwarded MIME parts are stored separately.</summary>
public sealed class DraftAttachmentPosition
{
    public string? Path { get; set; }
    public int? CarriedIndex { get; set; }
}

/// <summary>Everything the compose window edits.</summary>
public sealed class Draft
{
    public string AccountId { get; set; } = "";
    public ComposeMode Mode { get; set; }
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    /// <summary>Optional address for replies to this message (not the sender account).</summary>
    public string ReplyTo { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Normal, High or Low. Kept as text for compatible draft JSON.</summary>
    public string Priority { get; set; } = "Normal";
    public bool RequestReadReceipt { get; set; }
    /// <summary>HTML of the editor (new text + quoted original).</summary>
    public string Html { get; set; } = "";
    /// <summary>Send as text/plain only. Formatting is removed explicitly by the composer UI.</summary>
    public bool IsPlainText { get; set; }
    /// <summary>New-text font captured by the composer. Existing inline formatting in Html takes precedence.</summary>
    public TextStyle? ComposeStyle { get; set; }
    public List<string> AttachmentPaths { get; set; } = new();
    /// <summary>Names sent to recipients without changing the files on disk.</summary>
    public Dictionary<string, string> AttachmentNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Order of both new and forwarded attachments, including interleaving.</summary>
    public List<DraftAttachmentPosition> AttachmentOrder { get; set; } = new();
    /// <summary>Attachments carried over from the original (forward), as raw MIME parts.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<MimeEntity> CarriedParts { get; set; } = new();
    public string InReplyTo { get; set; } = "";
    public string References { get; set; } = "";
    public string ThreadKey { get; set; } = "";
    /// <summary>Trusted local origin of a reply; independent of the selected sending account and never a MIME header.</summary>
    public ReplyArchiveSource? ReplyArchiveSource { get; set; }
    /// <summary>AW1 (Q85): the message being forwarded (its row on this PC), so the AI note reads that email.</summary>
    public long? ForwardOfRow { get; set; }
    /// <summary>Local row of the server draft this came from (deleted after sending).</summary>
    public long? SourceDraftRow { get; set; }
    /// <summary>Id of the copy kept on this PC (design F1), once one exists.</summary>
    public long? LocalDraftId { get; set; }
    /// <summary>
    /// Message-ID used for every save and the final send of this message. Keeping it stable lets a re-save
    /// replace the previous server draft and a send remove it, instead of leaving copies behind.
    /// </summary>
    public string MessageId { get; set; } = "";
    /// <summary>An iCalendar reply sent with the message as text/calendar; method=REPLY (invite answers, design B3).</summary>
    public string CalendarReply { get; set; } = "";
}

/// <summary>Builds replies/forwards and the final MIME message.</summary>
public static class Composer
{
    public static InternetAddressList ParseAddresses(string text)
    {
        var list = new InternetAddressList();
        if (string.IsNullOrWhiteSpace(text)) return list;
        // Accept ; and newlines as separators too.
        var pieces = AddressPieces(text);
        var normalised = string.Join(",", pieces);
        if (InternetAddressList.TryParse(normalised, out var parsed)) return parsed;
        foreach (var piece in pieces)
            if (MailboxAddress.TryParse(piece, out var mb)) list.Add(mb);
        return list;
    }

    /// <summary>Returns the invalid pieces of an address field (empty = all fine).</summary>
    public static List<string> InvalidAddresses(string text)
    {
        var bad = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return bad;
        foreach (var piece in AddressPieces(text))
            if (!MailboxAddress.TryParse(piece, out var mb) || !Regex.IsMatch(mb.Address, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                bad.Add(piece);
        return bad;
    }

    // Keep separators inside quoted display names intact in both parsing and validation.
    private static List<string> AddressPieces(string text)
    {
        var pieces = new List<string>();
        var part = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var ch in text)
        {
            if (ch == '"' && !escaped) quoted = !quoted;
            if (!quoted && ch is ',' or ';' or '\r' or '\n')
            {
                if (part.ToString().Trim() is { Length: > 0 } value) pieces.Add(value);
                part.Clear();
            }
            else part.Append(ch);
            escaped = ch == '\\' && !escaped;
        }
        if (part.ToString().Trim() is { Length: > 0 } last) pieces.Add(last);
        return pieces;
    }

    public static string FormatList(IEnumerable<InternetAddress> list) => string.Join(", ", list.Select(a => a.ToString()));

    /// <summary>Reply / reply-all recipients. <paramref name="myAddresses"/> are removed from the lists.</summary>
    public static (string to, string cc) ReplyRecipients(MessageRow original, bool replyAll, IReadOnlyCollection<string> myAddresses, bool originalIsMine)
    {
        var mine = new HashSet<string>(myAddresses.Select(a => a.Trim()), StringComparer.OrdinalIgnoreCase);
        InternetAddressList to;
        if (originalIsMine)
            to = ParseAddresses(original.To); // replying to our own sent message → same recipients
        else if (!string.IsNullOrWhiteSpace(original.ReplyTo))
            to = ParseAddresses(original.ReplyTo);
        else
            to = ParseAddresses(string.IsNullOrWhiteSpace(original.FromName) ? original.FromAddress : $"\"{original.FromName}\" <{original.FromAddress}>");

        var toList = Unique(to.Mailboxes).Where(m => !mine.Contains(m.Address)).ToList();
        // A Reply-To pointing only to ourselves must not silently put our own addresses back into Reply All.
        // Fall back to the actual sender if it is not one of our addresses; otherwise leave recipients for the user.
        if (toList.Count == 0 && !originalIsMine && !mine.Contains(original.FromAddress)
            && MailboxAddress.TryParse(original.FromAddress, out var sender)) toList.Add(sender);
        var cc = new List<MailboxAddress>();
        if (replyAll)
        {
            var seen = new HashSet<string>(toList.Select(m => m.Address.ToLowerInvariant()));
            foreach (var m in ParseAddresses(original.To).Mailboxes.Concat(ParseAddresses(original.Cc).Mailboxes))
            {
                var a = m.Address.ToLowerInvariant();
                if (mine.Contains(m.Address) || !seen.Add(a)) continue;
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
            d.ReplyArchiveSource = new ReplyArchiveSource(original.AccountId, original.ThreadKey);
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
            d.ForwardOfRow = original.Id;
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

    /// <summary>
    /// A 1.1.x plain-text signature as HTML ("-- " line, then the text). Only &amp; &lt; &gt; are escaped — what the
    /// editor's innerHTML gives back — so opening it in the editor doesn't count as a change.
    /// </summary>
    /// <summary>Design SG1: the signature every account starts with — "Thanks and Regards", your name, job title and
    /// company, contact number (empty lines left out). Editable like any signature afterwards.</summary>
    public static string DefaultSignatureHtml(Account a)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s.Trim());
        var lines = new List<string> { "Thanks and Regards" };
        if (!string.IsNullOrWhiteSpace(a.DisplayName)) lines.Add(E(a.DisplayName));
        var role = string.Join(" · ", new[] { a.JobTitle, a.Company }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(E));
        if (role.Length > 0) lines.Add(role);
        if (!string.IsNullOrWhiteSpace(a.Phone)) lines.Add(E(a.Phone));
        return "<p>" + string.Join("<br>", lines) + "</p>";
    }

    public static string LegacySignatureHtml(string signature) =>
        string.IsNullOrWhiteSpace(signature) ? "" :
        "-- <br>" + signature.Trim().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r\n", "\n").Replace("\n", "<br>");

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
        // A recipient entered in two fields must get only one copy. The first (To, then Cc,
        // then Bcc) wins, and Bcc-only addresses are never promoted into a visible field.
        var seenRecipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in ParseAddresses(d.To).Mailboxes)
            if (seenRecipients.Add(address.Address)) msg.To.Add(address);
        foreach (var address in ParseAddresses(d.Cc).Mailboxes)
            if (seenRecipients.Add(address.Address)) msg.Cc.Add(address);
        foreach (var address in ParseAddresses(d.Bcc).Mailboxes)
            if (seenRecipients.Add(address.Address)) msg.Bcc.Add(address);
        if (!string.IsNullOrWhiteSpace(d.ReplyTo))
            msg.ReplyTo.AddRange(ParseAddresses(d.ReplyTo));
        msg.Subject = d.Subject ?? "";
        msg.Date = DateTimeOffset.Now;
        var domain = account.Email.Contains('@') ? account.Email.Split('@')[1] : "magpie.local";
        if (string.IsNullOrWhiteSpace(d.MessageId)) d.MessageId = MimeUtils.GenerateMessageId(domain);
        msg.MessageId = d.MessageId;
        if (!string.IsNullOrWhiteSpace(d.InReplyTo)) msg.InReplyTo = d.InReplyTo.Trim().Trim('<', '>');
        foreach (var r in Threading.ParseReferences(d.References)) msg.References.Add(r);
        msg.Headers.Add("X-Mailer", "Magpie 1.0");
        if (string.Equals(d.Priority, "High", StringComparison.OrdinalIgnoreCase))
        {
            msg.Headers.Add("X-Priority", "1 (Highest)");
            msg.Headers.Add("Importance", "high");
            msg.Headers.Add("X-MSMail-Priority", "High");
        }
        else if (string.Equals(d.Priority, "Low", StringComparison.OrdinalIgnoreCase))
        {
            msg.Headers.Add("X-Priority", "5 (Lowest)");
            msg.Headers.Add("Importance", "low");
            msg.Headers.Add("X-MSMail-Priority", "Low");
        }
        if (d.RequestReadReceipt)
            msg.Headers.Add("Disposition-Notification-To", account.Email);

        var builder = new BodyBuilder();
        // Pasted pictures arrive as data: URIs, which many mail apps block — send them as inline attachments.
        var inner = d.IsPlainText ? d.Html : DataImage.Replace(d.Html, m =>
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
        // Plain-editor whitespace is message content, not rich-HTML preview formatting.
        var plainBody = d.IsPlainText ? MimeText.EditorHtmlToPlainText(d.Html) : MimeText.HtmlToText(d.Html);
        if (!string.IsNullOrEmpty(d.CalendarReply))
        {
            // An invite answer: plain text + the iCalendar reply, as alternatives (what calendar servers expect).
            var cal = new TextPart("calendar") { Text = d.CalendarReply };
            cal.ContentType.Parameters.Add("method", "REPLY");
            cal.ContentType.Charset = "utf-8";
            cal.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
            var text = new TextPart("plain") { Text = plainBody };
            if (d.IsPlainText) text.ContentTransferEncoding = ContentEncoding.Base64;
            msg.Body = new Multipart("alternative") { text, cal };
            return msg;
        }
        if (!d.IsPlainText) builder.HtmlBody = WrapHtml(inner, d.ComposeStyle);
        builder.TextBody = plainBody;
        // Preserve the visible order even when local files and forwarded MIME parts
        // are interleaved. Older drafts without an order retain paths-then-parts.
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedParts = new HashSet<int>();
        void AddPath(string path)
        {
            if (!addedPaths.Add(path)) return;
            if (!File.Exists(path)) throw new FileNotFoundException("An attached file is missing. Choose it again before sending.", path);
            var attachment = builder.Attachments.Add(path);
            if (attachment is MimePart mime && d.AttachmentNames.TryGetValue(path, out var alias)
                && ComposeSafety.ValidAttachmentName(alias)) mime.FileName = alias;
        }
        void AddPart(int index)
        {
            if (index < 0 || index >= d.CarriedParts.Count || !addedParts.Add(index)) return;
            builder.Attachments.Add(d.CarriedParts[index]);
        }
        foreach (var entry in d.AttachmentOrder)
        {
            if (entry.Path is { } path && d.AttachmentPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) AddPath(path);
            else if (entry.CarriedIndex is { } index) AddPart(index);
        }
        // Never discard an attachment just because an old or partially restored
        // draft has no corresponding AttachmentOrder entry.
        foreach (var path in d.AttachmentPaths) AddPath(path);
        for (var i = 0; i < d.CarriedParts.Count; i++) AddPart(i);
        msg.Body = builder.ToMessageBody();
        if (d.IsPlainText)
        {
            // MIME transport terminates unencoded text with a newline. Encoding the authored body keeps that
            // framing out of the decoded message, including an empty body or one without a final newline.
            // With no HTML/linked resources, BodyBuilder puts this part first, before any carried attachments.
            var text = msg.Body is TextPart single ? single : (TextPart)((Multipart)msg.Body)[0];
            text.ContentTransferEncoding = ContentEncoding.Base64;
        }
        return msg;
    }

    private static readonly Regex DataImage = new(@"src\s*=\s*[""'](?:data:)(image/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string WrapHtml(string inner, TextStyle? selected)
    {
        var style = selected?.IsValid == true ? selected : new TextStyle { Family = "Arial", Size = 18 };
        var family = WebUtility.HtmlEncode(style.Family.Replace("'", "\\'", StringComparison.Ordinal));
        var size = style.Size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        return "<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:'"
            + family + "',Arial,sans-serif;font-size:" + size + "px;font-weight:normal\">" + inner + "</body></html>";
    }

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

    /// <summary>A styled draft's body element is removed when it enters the editor. Preserve its safe text styling
    /// on an inner wrapper so the new-message default cannot reformat a draft the user already wrote.</summary>
    private static string RetainBodyTypography(string html, string inner)
    {
        var body = Regex.Match(html, @"<body\b([^>]*)>", RegexOptions.IgnoreCase);
        if (!body.Success) return inner;
        var attr = Regex.Match(body.Groups[1].Value, """\bstyle\s*=\s*(["'])(.*?)\1""", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!attr.Success) return inner;
        var css = WebUtility.HtmlDecode(attr.Groups[2].Value);
        var retained = new List<string>();
        foreach (Match declaration in Regex.Matches(css, @"(?:^|;)\s*(font-family|font-size|font-weight|font-style)\s*:\s*([^;]+)", RegexOptions.IgnoreCase))
        {
            var property = declaration.Groups[1].Value.ToLowerInvariant();
            var value = declaration.Groups[2].Value.Trim();
            switch (property)
            {
                case "font-family":
                    var family = value.Split(',')[0].Trim().Trim('"', '\'');
                    if (TypographyCatalog.IsValidFamily(family)) retained.Add("font-family:" + TypographyCss.Family(family));
                    break;
                case "font-size":
                    if (Regex.IsMatch(value, @"^(?:\d{1,3}(?:\.\d{1,4})?)(?:px|pt)$", RegexOptions.IgnoreCase))
                        retained.Add("font-size:" + value);
                    break;
                case "font-weight":
                    if (Regex.IsMatch(value, @"^(?:normal|bold|[1-9]00)$", RegexOptions.IgnoreCase)) retained.Add("font-weight:" + value);
                    break;
                case "font-style":
                    if (Regex.IsMatch(value, @"^(?:normal|italic|oblique)$", RegexOptions.IgnoreCase)) retained.Add("font-style:" + value);
                    break;
            }
        }
        return retained.Count == 0 ? inner : "<div style=\"" + WebUtility.HtmlEncode(string.Join(";", retained)) + "\">" + inner + "</div>";
    }

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
            ReplyTo = FormatList(msg.ReplyTo),
            Priority = MailPriorityHeaders.Read(msg.Headers),
            RequestReadReceipt = !string.IsNullOrWhiteSpace(msg.Headers["Disposition-Notification-To"]),
            InReplyTo = string.IsNullOrEmpty(msg.InReplyTo) ? "" : "<" + msg.InReplyTo + ">",
            References = string.Join(" ", msg.References.Select(r => "<" + r + ">")),
            ThreadKey = threadKey,
            MessageId = msg.MessageId ?? "",
            IsPlainText = msg.HtmlBody == null,
        };
        var html = msg.HtmlBody;
        if (string.IsNullOrEmpty(html))
            html = d.IsPlainText ? MimeText.PlainTextToEditorHtml(msg.TextBody) : MimeText.TextToHtml(msg.TextBody ?? "");
        var m = Regex.Match(html, @"<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var inlined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inner = m.Success ? RetainBodyTypography(html, m.Groups[1].Value) : html;
        d.Html = InlineImagesToData(inner, msg, inlined);
        foreach (var part in msg.BodyParts)
        {
            // A picture now inside the HTML is not carried again as an attachment (Outlook marks some as attachments).
            if (part is MimePart { ContentId: { } cid } && inlined.Contains(cid.Trim('<', '>'))) continue;
            if (part is MimePart { IsAttachment: true } || part is MessagePart) d.CarriedParts.Add(part);
        }
        return d;
    }
}
