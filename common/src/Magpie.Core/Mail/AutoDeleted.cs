using System.Globalization;
using Magpie.Core.Storage;

namespace Magpie.Core.Mail;

/// <summary>
/// Design AL1 (option C): every word Trash says about an email that Magpie's auto delete moved there — the label on
/// the row, the banner above the open email, the note on each email, and the filter over the list. The screens only
/// place these texts.
/// </summary>
public static class AutoDeleted
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public const string Title = "Deleted by Magpie auto delete";

    /// <summary>What is kept of a rule at the moment it deletes an email: its id and how it read ("Rule: x@y.com after 30 days").</summary>
    public static (string RuleId, string RuleLine) RuleSnapshot(AutoDeleteRule? rule) =>
        rule == null ? ("", "") : (rule.Id, DeleteDates.RuleLine(rule).Replace(" (paused)", "", StringComparison.Ordinal));

    /// <summary>"21 Nov, 18:24", with the year when it is not this year.</summary>
    public static string ShortWhen(DateTimeOffset at, DateTimeOffset now)
    {
        var l = at.ToLocalTime();
        return l.Year == now.ToLocalTime().Year ? l.ToString("d MMM, HH:mm", C) : l.ToString("d MMM yyyy, HH:mm", C);
    }

    /// <summary>The banner's second line: "Saturday, 21 November 2026, 18:24".</summary>
    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("dddd, d MMMM yyyy, HH:mm", C);

    /// <summary>The label on a row of Trash: "Auto-deleted · 21 Nov, 18:24".</summary>
    public static string RowLabel(DateTimeOffset at, DateTimeOffset now) => "Auto-deleted · " + ShortWhen(at, now);

    /// <summary>The note next to one email's date in the open conversation: "auto-deleted 21 Nov, 18:24".</summary>
    public static string MessageNote(DateTimeOffset at, DateTimeOffset now) => "auto-deleted " + ShortWhen(at, now);

    /// <summary>The rule as it read when it deleted the email; a rule that was already gone then is said so.</summary>
    public static string RuleText(TrashNote note) => note.RuleLine.Length > 0 ? note.RuleLine : "Its rule was no longer there";

    /// <summary>
    /// The banner's third line: "Rule: offers@shop.com after 30 days · Arrived 22 Oct 2026 · Was in Inbox". A part
    /// that is not known is left out.
    /// </summary>
    public static string Details(TrashNote note, DateTimeOffset? arrived, string? folderName)
    {
        var parts = new List<string> { RuleText(note) };
        if (arrived is { } a) parts.Add("Arrived " + a.ToLocalTime().ToString("d MMM yyyy", C));
        if (!string.IsNullOrWhiteSpace(folderName)) parts.Add("Was in " + folderName.Trim());
        return string.Join(" · ", parts);
    }

    /// <summary>Under the banner when auto delete removed more than one email of the conversation.</summary>
    public static string ManyLine(int count) =>
        count > 1 ? $"{count} emails of this conversation were auto-deleted; the date above is the latest one." : "";

    /// <summary>What pointing at the label, or at one email's note, says: title, full date and time, the rule.</summary>
    public static string Tip(TrashNote note, int count = 1)
    {
        var lines = new List<string> { Title, When(note.At), RuleText(note) };
        if (count > 1) lines.Add(ManyLine(count));
        return string.Join("\n", lines);
    }

    /// <summary>The Restore button of the banner: where the email goes, and that its rule leaves it alone from then on.</summary>
    public static string RestoreTip(string? folderName) =>
        $"Puts it back in {(string.IsNullOrWhiteSpace(folderName) ? "the Inbox" : folderName.Trim())} and keeps it from auto delete";

    // ── the filter over the Trash list (option C) ──

    public static string FilterLabel(TrashReason reason, int all, int auto) => reason switch
    {
        TrashReason.AutoDeleted => $"Auto-deleted · {auto:N0}",
        TrashReason.NotAutoDeleted => $"Deleted by you or elsewhere · {Math.Max(0, all - auto):N0}",
        _ => $"All · {all:N0}",
    };

    public static string FilterTip(TrashReason reason) => reason switch
    {
        TrashReason.AutoDeleted => "Only the conversations that Magpie's auto delete moved to Trash",
        TrashReason.NotAutoDeleted => "Only the conversations you deleted yourself, here or in another app",
        _ => "Everything in Trash",
    };

    /// <summary>What an empty list says under a filter; "" for the whole of Trash (its own empty text stays).</summary>
    public static string EmptyText(TrashReason reason) => reason switch
    {
        TrashReason.AutoDeleted => "Auto delete has not moved anything here.",
        TrashReason.NotAutoDeleted => "Everything here was moved by auto delete.",
        _ => "",
    };

    /// <summary>The filter is offered only when it can tell two kinds apart: something auto-deleted is in Trash.</summary>
    public static bool ShowFilter(int auto) => auto > 0;
}
