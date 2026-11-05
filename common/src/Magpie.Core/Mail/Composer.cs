using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using MimeKit;
using MimeKit.Utils;

namespace Magpie.Core.Mail;

public enum ComposeMode { New, Reply, ReplyAll, Forward, EditDraft }

/// <summary>Design CW1 (D1): the sender's priority mark. High / Low go out as X-Priority, Importance and X-MSMail-Priority headers.</summary>
public enum MailPriority { Normal = 0, High = 1, Low = 2 }

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
    /// <summary>New-text font captured by the composer. Existing inline formatting in Html takes precedence.</summary>
    public TextStyle? ComposeStyle { get; set; }
    public List<string> AttachmentPaths { get; set; } = new();
    /// <summary>Attachments carried over from the original (forward), as raw MIME parts.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<MimeEntity> CarriedParts { get; set; } = new();
    public string InReplyTo { get; set; } = "";
    public string References { get; set; } = "";
    public string ThreadKey { get; set; } = "";
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
    /// <summary>Design CW1 (A5): the Reply-To header ("" = none).</summary>
    public string ReplyTo { get; set; } = "";
    /// <summary>Design CW1 (D1).</summary>
    public MailPriority Priority { get; set; }
    /// <summary>Design CW1 (D2): ask the recipient's mail app for a read receipt (Disposition-Notification-To).</summary>
    public bool ReadReceipt { get; set; }
    /// <summary>Design CW1 (B16): send as text/plain only — no formatting, pictures or tables.</summary>
    public bool PlainText { get; set; }
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

    /// <summary>The pieces of an address field (", " / ";" / new lines split it; separators inside quoted names are kept).</summary>
    public static List<string> SplitAddresses(string text) => AddressPieces(text ?? "");

    /// <summary>"Name &lt;address&gt;" → (Name, address); a bare address → ("", address); anything else → ("", piece).</summary>
    public static (string Name, string Address) NameAndAddress(string piece)
    {
        if (MailboxAddress.TryParse(piece, out var mb)) return (mb.Name ?? "", mb.Address ?? "");
        return ("", (piece ?? "").Trim());
    }

    /// <summary>True when the piece is a usable email address (what Send would accept).</summary>
    public static bool IsValidAddress(string piece) => InvalidAddresses(piece).Count == 0 && !string.IsNullOrWhiteSpace(piece);

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
        if (!string.IsNullOrWhiteSpace(d.ReplyTo)) msg.ReplyTo.AddRange(ParseAddresses(d.ReplyTo));
        foreach (var (name, value) in PriorityHeaders(d.Priority)) msg.Headers.Add(name, value);
        if (d.ReadReceipt) msg.Headers.Add("Disposition-Notification-To", new MailboxAddress(account.DisplayName, account.Email).ToString());

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
        if (!string.IsNullOrEmpty(d.CalendarReply))
        {
            // An invite answer: plain text + the iCalendar reply, as alternatives (what calendar servers expect).
            var cal = new TextPart("calendar") { Text = d.CalendarReply };
            cal.ContentType.Parameters.Add("method", "REPLY");
            cal.ContentType.Charset = "utf-8";
            cal.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
            msg.Body = new Multipart("alternative") { new TextPart("plain") { Text = MimeText.HtmlToText(d.Html) }, cal };
            return msg;
        }
        if (d.PlainText) builder.TextBody = MimeText.HtmlToText(d.Html);
        else
        {
            builder.HtmlBody = WrapHtml(inner, d.ComposeStyle);
            builder.TextBody = MimeText.HtmlToText(d.Html);
        }
        foreach (var path in d.AttachmentPaths.Where(File.Exists)) builder.Attachments.Add(path);
        foreach (var part in d.CarriedParts) builder.Attachments.Add(part);
        msg.Body = builder.ToMessageBody();
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
            InReplyTo = string.IsNullOrEmpty(msg.InReplyTo) ? "" : "<" + msg.InReplyTo + ">",
            References = string.Join(" ", msg.References.Select(r => "<" + r + ">")),
            ThreadKey = threadKey,
            MessageId = msg.MessageId ?? "",
            ReplyTo = FormatList(msg.ReplyTo),
            Priority = PriorityOf(msg.Headers["X-Priority"] ?? "", msg.Headers["Importance"] ?? "", msg.Headers["X-MSMail-Priority"] ?? ""),
            ReadReceipt = !string.IsNullOrWhiteSpace(msg.Headers["Disposition-Notification-To"]),
            PlainText = string.IsNullOrEmpty(msg.HtmlBody) && !string.IsNullOrEmpty(msg.TextBody),
        };
        var html = msg.HtmlBody;
        if (string.IsNullOrEmpty(html)) html = MimeText.TextToHtml(msg.TextBody ?? "");
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

    // ───────────────────────── design CW1: priority, receipts, limits, checks ─────────────────────────

    /// <summary>The headers a priority goes out with (none for Normal).</summary>
    public static IEnumerable<(string Name, string Value)> PriorityHeaders(MailPriority p)
    {
        switch (p)
        {
            case MailPriority.High:
                yield return ("X-Priority", "1 (Highest)");
                yield return ("Importance", "High");
                yield return ("X-MSMail-Priority", "High");
                break;
            case MailPriority.Low:
                yield return ("X-Priority", "5 (Lowest)");
                yield return ("Importance", "Low");
                yield return ("X-MSMail-Priority", "Low");
                break;
        }
    }

    /// <summary>Reads a received email's priority from its headers (any of the three; X-Priority 1–2 = High, 4–5 = Low).</summary>
    public static MailPriority PriorityOf(string xPriority, string importance, string msMailPriority)
    {
        var imp = (importance ?? "").Trim();
        if (imp.Length == 0) imp = (msMailPriority ?? "").Trim();
        if (imp.StartsWith("high", StringComparison.OrdinalIgnoreCase) || imp.StartsWith("urgent", StringComparison.OrdinalIgnoreCase)) return MailPriority.High;
        if (imp.StartsWith("low", StringComparison.OrdinalIgnoreCase) || imp.StartsWith("non-urgent", StringComparison.OrdinalIgnoreCase)) return MailPriority.Low;
        var m = Regex.Match(xPriority ?? "", @"^\s*([1-5])");
        if (!m.Success) return MailPriority.Normal;
        return m.Groups[1].Value switch { "1" or "2" => MailPriority.High, "4" or "5" => MailPriority.Low, _ => MailPriority.Normal };
    }

    public static bool IsHighPriority(string xPriority, string importance, string msMailPriority) =>
        PriorityOf(xPriority, importance, msMailPriority) == MailPriority.High;

    /// <summary>The attachment limit the compose window shows for an account, in bytes (C2): the account's own number, or
    /// Gmail 25 MB, Outlook / Microsoft 365 20 MB, IMAP 25 MB.</summary>
    public static long AttachmentLimit(Account? account)
    {
        if (account == null) return 25L * 1024 * 1024;
        if (account.AttachmentLimitMb > 0) return account.AttachmentLimitMb * 1024L * 1024;
        return account.Kind == AccountKind.Microsoft ? 20L * 1024 * 1024 : 25L * 1024 * 1024;
    }

    /// <summary>Bytes of the attached files plus the pictures inside the message (data: URIs), for the size bar (C2).</summary>
    public static long AttachedBytes(IEnumerable<string> paths, IEnumerable<long> carriedSizes, string html)
    {
        long total = 0;
        foreach (var p in paths) { try { if (File.Exists(p)) total += new FileInfo(p).Length; } catch { } }
        total += carriedSizes.Sum();
        foreach (Match m in DataImage.Matches(html ?? "")) total += m.Groups[2].Value.Length * 3L / 4;
        return total;
    }

    private static readonly Regex MentionsFile = new(@"\b(attach(?:ed|ment|ments|ing)?|enclos(?:ed|ure)|pfa|see the file|find the file|the file (?:is|here))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>C3: the new text talks about an attachment ("attached", "enclosed", "PFA", …).</summary>
    public static bool MentionsAttachment(string newText) => !string.IsNullOrWhiteSpace(newText) && MentionsFile.IsMatch(newText);

    /// <summary>D8: words in the new text (runs of letters / digits).</summary>
    public static int WordCount(string text) => string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"[\p{L}\p{N}]+(?:['’\-][\p{L}\p{N}]+)*").Count;

    /// <summary>A6: the same address twice in one field is merged (first one kept, case ignored); separators become ", ".</summary>
    public static string MergeDuplicates(string field)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (var piece in AddressPieces(field ?? ""))
        {
            var key = MailboxAddress.TryParse(piece, out var mb) ? mb.Address : piece;
            if (seen.Add(key)) kept.Add(piece);
        }
        return string.Join(", ", kept);
    }

    /// <summary>A6: how many different people the message is visible to (To + Cc), for the "more than 20" note.</summary>
    public static int VisibleRecipients(string to, string cc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var piece in AddressPieces(to ?? "").Concat(AddressPieces(cc ?? "")))
            seen.Add(MailboxAddress.TryParse(piece, out var mb) ? mb.Address : piece);
        return seen.Count;
    }
    public const int ManyRecipients = 20;

    /// <summary>B9: a picture file as an inline &lt;img&gt; (data: URI; sent as cid, see <see cref="Build"/>).</summary>
    public static string PictureHtml(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var type = ext switch { "jpg" or "jpeg" => "image/jpeg", "png" => "image/png", "gif" => "image/gif", "webp" => "image/webp", "bmp" => "image/bmp", _ => "" };
        if (type.Length == 0) throw new InvalidOperationException("Only png, jpg, gif, webp and bmp pictures can go inside the message.");
        var data = Convert.ToBase64String(File.ReadAllBytes(path));
        return "<img src=\"data:" + type + ";base64," + data + "\" alt=\"" + WebUtility.HtmlEncode(Path.GetFileName(path)) + "\" style=\"max-width:100%\">";
    }

    public static readonly string[] PictureExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };
    public static bool IsPicture(string path) => PictureExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>D9: the message as a .eml file (what Send would build, Bcc included so the file is complete).</summary>
    public static byte[] ToEml(Draft d, Account account) => ToBytes(Build(d, account));

    /// <summary>D4: a template from a message — the new text as plain paragraphs (no signature, no quote).</summary>
    public static string TemplateBody(string newText) => (newText ?? "").Replace("\r\n", "\n").Trim();

    /// <summary>D3: swaps the signature block in the editor HTML for <paramref name="signatureHtml"/> ("" removes it). Returns the new HTML.</summary>
    public static string ReplaceSignature(string html, string signatureHtml)
    {
        var block = string.IsNullOrWhiteSpace(signatureHtml) ? "" : "<div class=\"magpie-signature\"><br>" + signatureHtml + "</div>";
        var m = SignatureBlock.Match(html ?? "");
        if (m.Success) return html![..m.Index] + block + html[(m.Index + m.Length)..];
        var q = QuoteStart.Match(html ?? "");
        return q.Success ? html![..q.Index] + block + html[q.Index..] : (html ?? "") + block;
    }

    private static readonly Regex SignatureBlock = new(@"<div\b[^>]*class=[""'][^""']*\bmagpie-signature\b[^""']*[""'][^>]*>.*?</div>(?=\s*(?:<div\b[^>]*class=[""'][^""']*\bmagpie-quote\b|$))", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex QuoteStart = new(@"<div\b[^>]*class=[""'][^""']*\bmagpie-quote\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Signature HTML of the signature with this id, the account's default when the id is unknown, "" for none.</summary>
    public static string SignatureHtmlFor(Account? account, string? signatureId, bool reply)
    {
        if (account == null || (reply ? !account.SignatureOnReplies : !account.SignatureOnNew)) return "";
        var def = (account.Signatures ?? new()).FirstOrDefault(s => s.Id == signatureId) ?? account.DefaultSignature;
        var html = def?.Html ?? account.SignatureHtml;
        if (string.IsNullOrWhiteSpace(MimeText.HtmlToText(html)) && !(html ?? "").Contains("<img", StringComparison.OrdinalIgnoreCase)) return "";
        return HtmlRenderer.SanitizeBody(html!, new Dictionary<string, string>(), allowRemote: true).html;
    }
}
