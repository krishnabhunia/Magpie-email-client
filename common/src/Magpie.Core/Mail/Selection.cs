using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>Design SL1: the choices of Select ▾ above the list (F1–F11).</summary>
public enum SelectFilter { All, None, Invert, Unread, Read, Pinned, WithAttachments, SameSender, OlderThan, Category, Tag }

public static class Selection
{
    /// <summary>F9 "Older than…": by the date of the newest email in the conversation.</summary>
    public static readonly (string Label, Func<DateTimeOffset, DateTimeOffset> Cutoff)[] OlderThan =
    {
        ("1 week", n => n.AddDays(-7)), ("1 month", n => n.AddMonths(-1)), ("3 months", n => n.AddMonths(-3)),
        ("6 months", n => n.AddMonths(-6)), ("1 year", n => n.AddYears(-1)),
    };

    /// <summary>
    /// Whether a conversation is ticked after choosing <paramref name="filter"/>. <paramref name="arg"/> is the sender's
    /// address (SameSender), the cutoff date (OlderThan), the category or the tag name; <paramref name="wasTicked"/> is
    /// used by Invert.
    /// </summary>
    public static bool Ticks(ThreadRow t, SelectFilter filter, object? arg, bool wasTicked) => filter switch
    {
        SelectFilter.All => true,
        SelectFilter.None => false,
        SelectFilter.Invert => !wasTicked,
        SelectFilter.Unread => t.UnreadCount > 0,
        SelectFilter.Read => t.UnreadCount == 0,
        SelectFilter.Pinned => t.Flagged,
        SelectFilter.WithAttachments => t.HasAttachments,
        SelectFilter.SameSender => arg is string a && a.Length > 0 && t.Latest.FromAddress.Equals(a, StringComparison.OrdinalIgnoreCase),
        SelectFilter.OlderThan => arg is DateTimeOffset cut && t.Latest.Date < cut,
        SelectFilter.Category => arg is Category c && t.Latest.Category == c,
        SelectFilter.Tag => arg is string tag && t.Latest.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .Contains(tag, StringComparer.OrdinalIgnoreCase),
        _ => wasTicked,
    };

    /// <summary>The tick box above the list: none ticked → unticked, all → ticked, some → a dash (null).</summary>
    public static bool? HeaderState(int ticked, int total) => ticked == 0 ? false : ticked >= total ? true : null;

    /// <summary>F12: the line under the bar once everything on screen is ticked but the view holds more.</summary>
    public static string AllOnScreenLine(int onScreen, string view) =>
        $"All {onScreen:N0} on screen are selected.";
}
