using System.Globalization;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Mail;

/// <summary>
/// Design PE1: the words of the "emails with one address" list — its heading, the four filters, each row's
/// "From … / To …" line and folder mark, and the lines under the list. The screens only draw these.
/// </summary>
public static class PersonView
{
    public static readonly PersonKind[] Kinds = { PersonKind.All, PersonKind.From, PersonKind.YouSent, PersonKind.CopiedIn };

    /// <summary>"Rajesh Mehta", or the address when no name is known.</summary>
    public static string DisplayName(string? name, string address)
    {
        var n = (name ?? "").Trim().Trim('"', '\'').Trim();
        return n.Length > 0 && !n.Equals(address, StringComparison.OrdinalIgnoreCase) ? n : address;
    }

    public static string Title(string? name, string address) => "Emails with " + DisplayName(name, address);

    /// <summary>The line under the heading: the address (when the heading shows a name) and where the list looks.</summary>
    public static string SubTitle(string? name, string address, int accounts)
    {
        var where = accounts > 1 ? $"every folder, {(accounts == 2 ? "both" : "all " + accounts.ToString(CultureInfo.InvariantCulture))} accounts" : "every folder";
        return DisplayName(name, address).Equals(address, StringComparison.OrdinalIgnoreCase) ? where : $"{address} · {where}";
    }

    /// <summary>A filter button: "All 51", "From Rajesh 12", "You sent 35", "Copied in 4".</summary>
    public static string FilterLabel(PersonKind kind, string? name, string address, PersonStats stats) =>
        $"{FilterWords(kind, name, address)} {stats.Of(kind).ToString("N0", CultureInfo.InvariantCulture)}";

    /// <summary>The same button before its number is known: "All", "From Rajesh", "You sent", "Copied in".</summary>
    public static string FilterWords(PersonKind kind, string? name, string address) => kind switch
    {
        PersonKind.From => "From " + HoverMenus.FirstName(name, address),
        PersonKind.YouSent => "You sent",
        PersonKind.CopiedIn => "Copied in",
        _ => "All",
    };

    /// <summary>What a filter means, for its tooltip.</summary>
    public static string FilterTip(PersonKind kind, string? name, string address)
    {
        var who = DisplayName(name, address);
        return kind switch
        {
            PersonKind.From => $"Emails {who} sent",
            PersonKind.YouSent => $"Emails you sent with {who} in To, Cc or Bcc",
            PersonKind.CopiedIn => $"Emails other people sent with {who} in To or Cc",
            _ => $"Every email {who} is part of",
        };
    }

    /// <summary>The numbers line of the address card: "51 emails · 12 received · 35 you sent · 4 copied in".</summary>
    public static string StatsLine(PersonStats stats)
    {
        if (stats.All == 0) return "No emails on this PC yet";
        string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
        var parts = new List<string> { $"{N(stats.All)} email{(stats.All == 1 ? "" : "s")}" };
        if (stats.From > 0) parts.Add($"{N(stats.From)} received");
        if (stats.YouSent > 0) parts.Add($"{N(stats.YouSent)} you sent");
        if (stats.CopiedIn > 0) parts.Add($"{N(stats.CopiedIn)} copied in");
        return string.Join(" · ", parts);
    }

    /// <summary>"Last email on 13 Nov" (the year too when it is another year); empty when there is none.</summary>
    public static string LastLine(PersonStats stats, DateTimeOffset now)
    {
        if (stats.Last is not { } last) return "";
        var l = last.ToLocalTime();
        return "Last email on " + (l.Year == now.ToLocalTime().Year ? l.ToString("d MMM", CultureInfo.InvariantCulture) : l.ToString("d MMM yyyy", CultureInfo.InvariantCulture));
    }

    private static string Label(MailboxLike m) => m.Name.Length > 0 ? m.Name : m.Address;

    private readonly record struct MailboxLike(string Name, string Address);

    private static List<MailboxLike> Boxes(string list) =>
        Composer.ParseAddresses(list).Mailboxes.Select(m => new MailboxLike((m.Name ?? "").Trim(), (m.Address ?? "").Trim())).Where(m => m.Address.Length > 0).ToList();

    /// <summary>
    /// The first line of a row: "From Rajesh Mehta", "To Rajesh Mehta +12", "To Rajesh Mehta, Anita Rao",
    /// "To Anita Rao · bcc Rajesh Mehta", "From Anita Rao · cc Rajesh Mehta".
    /// </summary>
    public static string RowLine(PersonEmail email, string? name, string address)
    {
        var row = email.Row;
        var who = DisplayName(name, address);
        bool Is(MailboxLike m) => m.Address.Equals(address, StringComparison.OrdinalIgnoreCase);
        var to = Boxes(row.To);
        var cc = Boxes(row.Cc);
        var bcc = Boxes(row.Bcc);
        if (email.Kind == PersonKind.From) return "From " + (row.FromName.Trim().Length > 0 ? row.FromName.Trim() : who);
        var sender = row.Sender;
        var inTo = to.Any(Is);
        var part = inTo ? "to" : cc.Any(Is) ? "cc" : bcc.Any(Is) ? "bcc" : "to";
        if (email.Kind == PersonKind.CopiedIn) return $"From {sender} · {part} {who}";
        // You sent it.
        if (inTo)
        {
            var others = to.Where(m => !Is(m)).Concat(cc).Concat(bcc).ToList();
            return others.Count switch { 0 => "To " + who, 1 => $"To {who}, {Label(others[0])}", _ => $"To {who} +{others.Count.ToString(CultureInfo.InvariantCulture)}" };
        }
        var first = to.FirstOrDefault();
        return first.Address is { Length: > 0 } ? $"To {Label(first)} · {part} {who}" : $"{char.ToUpperInvariant(part[0])}{part[1..]} {who}";
    }

    /// <summary>The person is only in this email's Bcc line (the row gets a "Bcc" mark).</summary>
    public static bool OnlyBcc(PersonEmail email, string address)
    {
        bool Has(string list) => Boxes(list).Any(m => m.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        return email.Kind == PersonKind.YouSent && !Has(email.Row.To) && !Has(email.Row.Cc) && Has(email.Row.Bcc);
    }

    /// <summary>The folder mark of a row: "Inbox", "Sent", "Trash", … or the folder's own path ("Flat/Handover").</summary>
    public static string FolderLabel(FolderRole role, string path) => role switch
    {
        FolderRole.Inbox => "Inbox",
        FolderRole.Sent => "Sent",
        FolderRole.Drafts => "Drafts",
        FolderRole.Trash => "Trash",
        FolderRole.Junk => "Spam",
        FolderRole.Archive => "Archive",
        FolderRole.All => "All Mail",
        FolderRole.Flagged => "Starred",
        FolderRole.Important => "Important",
        _ => path.StartsWith("[Gmail]/", StringComparison.OrdinalIgnoreCase) ? path[8..] : path,
    };

    /// <summary>Under the list: how many are shown and whether Trash and Spam are in.</summary>
    public static string FootLine(int shown, bool more, bool includeBin, bool conversations)
    {
        var what = conversations ? (shown == 1 ? "conversation" : "conversations") : (shown == 1 ? "email" : "emails");
        var count = $"{shown.ToString("N0", CultureInfo.InvariantCulture)}{(more ? "+" : "")} {what} on this PC.";
        return count + (includeBin ? " Trash and Spam are included." : " Trash and Spam are left out.");
    }

    /// <summary>When the list is empty.</summary>
    public static string EmptyText(PersonKind kind, string? name, string address)
    {
        var who = DisplayName(name, address);
        return kind switch
        {
            PersonKind.From => $"No emails from {who} on this PC.",
            PersonKind.YouSent => $"No emails you sent to {who} on this PC.",
            PersonKind.CopiedIn => $"No emails from other people with {who} copied in on this PC.",
            _ => $"No emails with {who} on this PC.",
        };
    }

    /// <summary>What "Search the server too" found.</summary>
    public static string ServerResult(int added, string problem)
    {
        var found = added == 0 ? "Nothing more on the server." : $"{added.ToString("N0", CultureInfo.InvariantCulture)} more email{(added == 1 ? "" : "s")} found on the server.";
        return problem.Length == 0 ? found : (added == 0 ? "" : found + " ") + "Could not ask " + problem;
    }
}
