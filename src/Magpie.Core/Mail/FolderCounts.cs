using System.Globalization;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>Which number a folder shows (design C2).</summary>
public enum CountKind
{
    /// <summary>"3 / 10" — folders that receive new mail.</summary>
    UnreadAndTotal,
    /// <summary>"15" — collections you fill yourself (Pinned, Drafts, Trash…).</summary>
    CountOnly,
    /// <summary>No number (Sent).</summary>
    None,
}

/// <summary>The text of a folder's number, ready for the sidebar.</summary>
public readonly record struct CountText(string Main, string Rest, bool Dim)
{
    public static readonly CountText Empty = new("", "", false);
    public bool IsEmpty => Main.Length == 0;
}

public static class FolderCounts
{
    /// <summary>The kind of number a server folder shows.</summary>
    public static CountKind KindOf(FolderRole role) => role switch
    {
        FolderRole.Sent => CountKind.None,
        FolderRole.Drafts or FolderRole.Trash or FolderRole.Flagged => CountKind.CountOnly,
        _ => CountKind.UnreadAndTotal,   // Inbox, Archive, All Mail, Important, Spam, your own folders
    };

    /// <summary>12,480 → "12.4k"; 1240 → "1,240".</summary>
    public static string Format(int n)
    {
        if (n >= 10_000)
        {
            var k = Math.Floor(n / 100.0) / 10.0;   // never round up: 12,499 is 12.4k
            return k.ToString(k >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + "k";
        }
        return n.ToString("#,0", CultureInfo.InvariantCulture);
    }

    /// <param name="unread">Unread (for <see cref="CountKind.CountOnly"/>: ignored).</param>
    /// <param name="total">How many are in there.</param>
    public static CountText Display(int unread, int total, CountKind kind, CountsMode mode)
    {
        if (mode == CountsMode.Off || kind == CountKind.None) return CountText.Empty;
        if (kind == CountKind.CountOnly)
            return total > 0 ? new CountText(Format(total), "", false) : CountText.Empty;
        unread = Math.Max(0, unread);
        total = Math.Max(total, unread);
        if (mode == CountsMode.UnreadOnly)
            return unread > 0 ? new CountText(Format(unread), "", false) : CountText.Empty;
        if (total == 0) return CountText.Empty;
        return new CountText(Format(unread), " / " + Format(total), unread == 0);
    }
}
