using System.Text.RegularExpressions;
using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>Pure checks for the compose screen. No network or mailbox mutations.</summary>
public static partial class ComposeSafety
{
    /// <summary>Supported quick-template placeholders. Unavailable values remain visible for the author to fill.</summary>
    public const string QuickTemplateVariableHelp =
        "{{first_name}}, {{last_name}}, {{full_name}}, {{recipient_email}}, {{sender_name}}, {{sender_email}}, {{subject}}, {{date}}, {{time}}";

    [GeneratedRegex(@"\{\{(first_name|last_name|full_name|recipient_email|sender_name|sender_email|subject|date|time)\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuickTemplateVariables();

    /// <summary>Expands only known placeholders as plain text; unrecognised and missing fields are left untouched.</summary>
    public static string ExpandQuickTemplate(string text, Account? sender, string to, string subject, DateTimeOffset now)
    {
        var recipient = Composer.ParseAddresses(to).Mailboxes.FirstOrDefault();
        var recipientName = recipient?.Name?.Trim() ?? "";
        var parts = recipientName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["first_name"] = parts.FirstOrDefault() ?? "",
            ["last_name"] = parts.Length > 1 ? parts[^1] : "",
            ["full_name"] = recipientName,
            ["recipient_email"] = recipient?.Address ?? "",
            ["sender_name"] = sender?.DisplayName ?? "",
            ["sender_email"] = sender?.Email ?? "",
            ["subject"] = subject,
            ["date"] = now.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture),
            ["time"] = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        };
        return QuickTemplateVariables().Replace(text, match =>
            values.TryGetValue(match.Groups[1].Value, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : match.Value);
    }

    /// <summary>Newest attachments first, no duplicates, with a short local-history limit.</summary>
    public static IReadOnlyList<string> RecentAttachmentPaths(IEnumerable<string> newest, IEnumerable<string> previous, int maximum = 12) =>
        newest.Concat(previous).Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(Math.Clamp(maximum, 0, 40)).ToArray();

    /// <summary>UI guidance only; actual server limits depend on the provider and mailbox policy.</summary>
    public static long SuggestedOutgoingLimitBytes(Account? account) => account?.Kind switch
    {
        AccountKind.Gmail => 25_000_000L,
        AccountKind.Microsoft => 20_000_000L,
        _ => Math.Clamp(account?.MaxOutgoingMessageMb is int configured && configured > 0 ? configured : 25, 1, 100) * 1_000_000L,
    };

    /// <summary>Only formats the composer can inspect safely without launching another application.</summary>
    public static string AttachmentPreviewKind(string? filename) => Path.GetExtension(filename ?? "").ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" => "image",
        ".pdf" => "pdf",
        ".txt" or ".csv" or ".md" or ".log" or ".json" or ".xml" => "text",
        ".docx" or ".xlsx" or ".pptx" => "office",
        _ => "none",
    };

    /// <summary>Only preview forwarded MIME parts when both their name and declared content type are supported.</summary>
    public static string ForwardedAttachmentPreviewKind(string? filename, string? mimeType)
    {
        var extension = Path.GetExtension(filename ?? "").ToLowerInvariant();
        var type = (mimeType ?? "").ToLowerInvariant();
        return (extension, type) switch
        {
            (".png", "image/png") or (".jpg" or ".jpeg", "image/jpeg")
                or (".gif", "image/gif") or (".bmp", "image/bmp") => "image",
            (".pdf", "application/pdf") => "pdf",
            (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document") => "office",
            (".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet") => "office",
            (".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation") => "office",
            (".txt" or ".log", "text/plain") or (".csv", "text/csv" or "text/plain")
                or (".md", "text/markdown" or "text/plain")
                or (".json", "application/json" or "text/plain")
                or (".xml", "application/xml" or "text/xml" or "text/plain") => "text",
            _ => "none",
        };
    }

    /// <summary>Animated GIFs are excluded because re-encoding a frame would discard animation.</summary>
    public static bool CanCompressImage(string? filename) => Path.GetExtension(filename ?? "").ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp";

    public static bool ValidComposeLink(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" or "mailto";

    /// <summary>Prevent ambiguous paths/control characters in the outgoing attachment filename.</summary>
    public static bool ValidAttachmentName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 160 && name is not ("." or "..")
        && !name.Contains('/') && !name.Contains('\\') && !name.EndsWith('.')
        && !name.EndsWith(' ') && !name.Any(char.IsControl)
        && !name.Any(ch => "<>:\"|?*".Contains(ch))
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    [GeneratedRegex(@"\b(?:attach|attached|attachment|attachments|enclosed|included file|see the file)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttachmentWords();

    public static IReadOnlyList<string> MissingFiles(IEnumerable<string> paths) =>
        paths.Where(path => !File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>All case-insensitive addresses repeated within or across To, Cc and Bcc.</summary>
    public static IReadOnlyList<string> DuplicateRecipients(string to, string cc, string bcc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repeated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in Composer.ParseAddresses(to).Mailboxes
                     .Concat(Composer.ParseAddresses(cc).Mailboxes)
                     .Concat(Composer.ParseAddresses(bcc).Mailboxes))
            if (!seen.Add(address.Address)) repeated.Add(address.Address);
        return repeated.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Append an @mentioned contact to To once, preserving existing recipients and never duplicating Cc/Bcc.</summary>
    public static (string To, bool Added) AddMentionRecipient(string to, string cc, string bcc, string name, string address)
    {
        address = address.Trim();
        var parsed = Composer.ParseAddresses(address).Mailboxes.ToArray();
        if (address.Length == 0 || Composer.InvalidAddresses(address).Count > 0
            || parsed.Length != 1 || !string.Equals(parsed[0].Address, address, StringComparison.OrdinalIgnoreCase))
            return (to, false);
        if (Composer.ParseAddresses(to).Mailboxes.Concat(Composer.ParseAddresses(cc).Mailboxes)
                .Concat(Composer.ParseAddresses(bcc).Mailboxes)
                .Any(item => item.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            return (to, false);

        var entry = new MimeKit.MailboxAddress(name, address).ToString();
        return (string.IsNullOrWhiteSpace(to) ? entry : to.TrimEnd().TrimEnd(',', ';') + ", " + entry, true);
    }

    /// <summary>Addresses visible to other recipients, excluding duplicates and private Bcc recipients.</summary>
    public static int VisibleRecipientCount(string to, string cc) =>
        Composer.ParseAddresses(to).Mailboxes.Concat(Composer.ParseAddresses(cc).Mailboxes)
            .Select(address => address.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>
    /// Keep the first visible address in To, and move the others to Bcc.
    /// Call only after validating all three input fields; invalid addresses must never be silently discarded.
    /// </summary>
    public static (string To, string Cc, string Bcc) MoveVisibleRecipientsToBcc(string to, string cc, string bcc)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visible = Composer.ParseAddresses(to).Mailboxes.Concat(Composer.ParseAddresses(cc).Mailboxes)
            .Where(address => seen.Add(address.Address)).ToList();
        var privateSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (visible.Count > 0) privateSeen.Add(visible[0].Address);
        var privateRecipients = visible.Skip(1).Concat(Composer.ParseAddresses(bcc).Mailboxes)
            .Where(address => privateSeen.Add(address.Address)).ToArray();
        return (visible.FirstOrDefault()?.ToString() ?? "", "", string.Join(", ", privateRecipients.Select(a => a.ToString())));
    }

    /// <summary>Move one address between recipient rows without losing display names or invalid input.</summary>
    public static (string To, string Cc, string Bcc, bool Moved) MoveRecipientBetweenFields(
        string to, string cc, string bcc, string address, string source, string destination)
    {
        if (source == destination || source is not ("to" or "cc" or "bcc")
            || destination is not ("to" or "cc" or "bcc") || string.IsNullOrWhiteSpace(address)
            || Composer.InvalidAddresses(to).Count > 0 || Composer.InvalidAddresses(cc).Count > 0
            || Composer.InvalidAddresses(bcc).Count > 0)
            return (to, cc, bcc, false);

        var fields = new Dictionary<string, List<MimeKit.MailboxAddress>>(StringComparer.Ordinal)
        {
            ["to"] = Composer.ParseAddresses(to).Mailboxes.ToList(),
            ["cc"] = Composer.ParseAddresses(cc).Mailboxes.ToList(),
            ["bcc"] = Composer.ParseAddresses(bcc).Mailboxes.ToList(),
        };
        var current = fields[source].FirstOrDefault(item => item.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (current == null) return (to, cc, bcc, false);
        fields[source].RemoveAll(item => item.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (!fields[destination].Any(item => item.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
            fields[destination].Add(current);
        return (Composer.FormatList(fields["to"]), Composer.FormatList(fields["cc"]), Composer.FormatList(fields["bcc"]), true);
    }

    /// <summary>Recipients outside the From account's domain; used only when the sender opts into a warning.</summary>
    public static IReadOnlyList<string> ExternalRecipients(string senderAddress, string to, string cc, string bcc)
    {
        var separator = senderAddress?.LastIndexOf('@') ?? -1;
        if (separator < 0) return Array.Empty<string>();
        var senderDomain = senderAddress![(separator + 1)..];
        return Composer.ParseAddresses(to).Mailboxes
            .Concat(Composer.ParseAddresses(cc).Mailboxes)
            .Concat(Composer.ParseAddresses(bcc).Mailboxes)
            .Select(recipient => recipient.Address)
            .Where(address => !address.EndsWith("@" + senderDomain, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static int WordCount(string text) => Regex.Matches(text ?? "", @"\b[\p{L}\p{N}][\p{L}\p{M}\p{N}]*(?:['’\-][\p{L}\p{N}][\p{L}\p{M}\p{N}]*)*\b").Count;

    public static int EstimatedReadingMinutes(int words) => Math.Max(1, (Math.Max(0, words) + 199) / 200);

    /// <summary>Does the newly written text mention an attachment? Quotes/signatures should be removed by the editor.</summary>
    public static bool MentionsAttachment(string newText) => AttachmentWords().IsMatch(newText ?? "");

    /// <summary>MIME base64 expansion plus a conservative envelope allowance. This is an estimate, not the provider's quota.</summary>
    public static long EstimatedEncodedBytes(IEnumerable<long> fileSizes, long bodyBytes = 0)
    {
        checked
        {
            var encodedFiles = fileSizes.Where(size => size > 0).Sum(size => ((size + 2) / 3) * 4 + ((size + 56) / 57) * 2);
            return encodedFiles + Math.Max(0, bodyBytes) + 4096;
        }
    }

    public static long EncodedSizeOfExistingFiles(IEnumerable<string> paths, string html)
    {
        var sizes = paths.Where(File.Exists).Select(path => new FileInfo(path).Length);
        return EstimatedEncodedBytes(sizes, System.Text.Encoding.UTF8.GetByteCount(html ?? ""));
    }
}
