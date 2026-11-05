using System.Text.RegularExpressions;

namespace Magpie.Core.Mail;

/// <summary>Pure checks for the compose screen. No network or mailbox mutations.</summary>
public static partial class ComposeSafety
{
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
