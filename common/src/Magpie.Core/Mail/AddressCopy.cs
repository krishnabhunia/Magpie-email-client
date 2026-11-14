using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>
/// Design CP1 (option A): what each copy button of the Details card puts on the clipboard, and what it says after.
/// One address = that address; one group = its addresses, one per line; all = the groups under the headings
/// To / CC / BCC with an empty line between them. Lines are joined with "\n" for the page;
/// <see cref="ForClipboard"/> turns them into Windows line ends so Notepad shows one address per line.
/// </summary>
public static class AddressCopy
{
    /// <summary>The headings of "Copy all", in the order they are pasted.</summary>
    public const string To = "To", Cc = "CC", Bcc = "BCC";

    /// <summary>The addresses of one group as they are listed: no empty ones, the same address only once.</summary>
    public static List<string> Addresses(IEnumerable<MailboxAddress> people) =>
        people.Select(p => (p.Address ?? "").Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>One group: one address per line, no heading.</summary>
    public static string Group(IReadOnlyCollection<string> addresses) => string.Join("\n", addresses);

    /// <summary>Every group under its heading; a group without addresses is left out.</summary>
    public static string All(IReadOnlyCollection<string> to, IReadOnlyCollection<string> cc, IReadOnlyCollection<string> bcc)
    {
        var blocks = new List<string>();
        foreach (var (heading, list) in new[] { (To, to), (Cc, cc), (Bcc, bcc) })
            if (list.Count > 0) blocks.Add(heading + "\n" + Group(list));
        return string.Join("\n\n", blocks);
    }

    /// <summary>The text with Windows line ends, whatever it came with.</summary>
    public static string ForClipboard(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    public static string SaidOne(string address) => "Copied " + address;

    /// <summary><paramref name="word"/> is the card's own label: To, Cc or Bcc.</summary>
    public static string SaidGroup(string word, int count) => $"Copied {count} {word} address{(count == 1 ? "" : "es")}";

    public static string SaidAll(int count) => $"Copied {count} address{(count == 1 ? "" : "es")}";

    /// <summary>What the page and the Windows app say when the clipboard refused.</summary>
    public const string Failed = "Could not copy. Try again.";
}
