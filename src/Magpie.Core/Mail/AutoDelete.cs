namespace Magpie.Core.Mail;

// ───────────────────────── Auto-delete and OTP delete (designs AD1–AD4) ─────────────────────────
// Decisions: delete = move to Trash · pinned mail is never auto-deleted · rules apply to future mail; existing
// mail only when asked · runs while Magpie runs, overdue deletes happen at the next start · OTP is per sender.

public enum DeleteUnit { Days = 0, Months = 1, Years = 2 }

public sealed class AutoDeleteRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>An address ("codes@bank.com") or everyone at a domain ("*@xyz.com").</summary>
    public string Pattern { get; set; } = "";
    /// <summary>"" = all accounts, else the account id.</summary>
    public string AccountId { get; set; } = "";
    /// <summary>OTP delete: 24 hours after each email arrives (Amount / Unit are ignored).</summary>
    public bool Otp { get; set; }
    public int Amount { get; set; } = 7;
    public DeleteUnit Unit { get; set; } = DeleteUnit.Days;
    /// <summary>Paused: no new timers, and nothing is deleted until it is resumed.</summary>
    public bool Paused { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
}

/// <summary>How close a delete date is (design AD3): red under 48 h (and every OTP), amber under 30 days, grey later.</summary>
public enum DeleteUrgency { Soon = 0, Weeks = 1, Later = 2 }

public static class AutoDelete
{
    /// <summary>The day / month / year choices of the AD1 submenu and the AD2 dialog.</summary>
    public static readonly (int Amount, DeleteUnit Unit)[] Choices =
    {
        (1, DeleteUnit.Days), (3, DeleteUnit.Days), (7, DeleteUnit.Days), (14, DeleteUnit.Days),
        (1, DeleteUnit.Months), (3, DeleteUnit.Months), (6, DeleteUnit.Months), (12, DeleteUnit.Months),
        (1, DeleteUnit.Years), (2, DeleteUnit.Years), (3, DeleteUnit.Years), (4, DeleteUnit.Years), (5, DeleteUnit.Years),
        (10, DeleteUnit.Years), (15, DeleteUnit.Years), (20, DeleteUnit.Years), (30, DeleteUnit.Years),
    };

    /// <summary>Design DP1 (D4): "Delete this sender's emails older than…" — 1 week to 2 years.</summary>
    public static readonly (string Label, Func<DateTimeOffset, DateTimeOffset> Cutoff)[] OlderThan =
    {
        ("1 week", n => n.AddDays(-7)), ("1 month", n => n.AddMonths(-1)), ("3 months", n => n.AddMonths(-3)),
        ("6 months", n => n.AddMonths(-6)), ("1 year", n => n.AddYears(-1)), ("2 years", n => n.AddYears(-2)),
    };

    /// <summary>Emails already here that a delete would take (design DP1): copies of one email in two folders count once.</summary>
    public sealed record PastEmails(IReadOnlyList<Models.MessageRow> Rows, int Count, DateTimeOffset? Oldest, DateTimeOffset? Newest)
    {
        public static PastEmails Of(IReadOnlyList<Models.MessageRow> rows)
        {
            var distinct = rows.GroupBy(m => m.MessageId.Length > 0 ? m.AccountId + "|" + m.MessageId : "#" + m.Id).Select(g => g.First()).ToList();
            return new PastEmails(rows, distinct.Count, distinct.Count == 0 ? null : distinct.Min(m => m.Date), distinct.Count == 0 ? null : distinct.Max(m => m.Date));
        }
    }

    /// <summary>The confirmation text of D2–D4: "Oldest: 3 March 2024 · Newest: 30 September 2026".</summary>
    public static string DatesLine(PastEmails p) =>
        p.Oldest is { } o && p.Newest is { } n
            ? $"Oldest: {o.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} · Newest: {n.ToLocalTime().ToString("d MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}"
            : "";

    /// <summary>"a@b.com" / "*@b.com" in lower case, or null when it isn't one of those.</summary>
    public static string? NormalisePattern(string? pattern)
    {
        var p = (pattern ?? "").Trim().ToLowerInvariant();
        if (p.StartsWith("@", StringComparison.Ordinal)) p = "*" + p;
        var at = p.IndexOf('@');
        if (at <= 0 || at != p.LastIndexOf('@') || at == p.Length - 1) return null;
        var local = p[..at];
        var domain = p[(at + 1)..];
        if (domain.Contains('*') || !domain.Contains('.') || domain.StartsWith('.') || domain.EndsWith('.') || p.Any(char.IsWhiteSpace)) return null;
        if (local.Contains('*') && local != "*") return null;
        return p;
    }

    public static string DomainPattern(string address)
    {
        var at = address.LastIndexOf('@');
        return at < 0 ? "" : "*@" + address[(at + 1)..].Trim().ToLowerInvariant();
    }

    public static bool Matches(string pattern, string address)
    {
        var a = (address ?? "").Trim().ToLowerInvariant();
        var p = (pattern ?? "").Trim().ToLowerInvariant();
        if (a.Length == 0 || p.Length == 0) return false;
        return p.StartsWith("*@", StringComparison.Ordinal) ? a.EndsWith(p[1..], StringComparison.Ordinal) : a == p;
    }

    public static bool AppliesToAccount(AutoDeleteRule r, string accountId) => r.AccountId.Length == 0 || r.AccountId == accountId;

    /// <summary>When an email that arrived at <paramref name="arrived"/> moves to Trash under this rule.</summary>
    public static DateTimeOffset DeleteAt(AutoDeleteRule r, DateTimeOffset arrived) => r.Otp ? arrived.AddHours(24) : r.Unit switch
    {
        DeleteUnit.Months => arrived.AddMonths(r.Amount),
        DeleteUnit.Years => arrived.AddYears(r.Amount),
        _ => arrived.AddDays(r.Amount),
    };

    public static string After(int amount, DeleteUnit unit) => unit switch
    {
        DeleteUnit.Months => amount == 1 ? "1 month" : $"{amount} months",
        DeleteUnit.Years => amount == 1 ? "1 year" : $"{amount} years",
        _ => amount == 1 ? "1 day" : $"{amount} days",
    };

    /// <summary>"7 days after arrival" / "OTP · 24 hours after arrival".</summary>
    public static string Describe(AutoDeleteRule r) => r.Otp ? "OTP · 24 hours after arrival" : After(r.Amount, r.Unit) + " after arrival";

    /// <summary>"emails from x@y.com" / "emails from anyone at y.com".</summary>
    public static string Who(string pattern) => pattern.StartsWith("*@", StringComparison.Ordinal) ? "emails from anyone at " + pattern[2..] : "emails from " + pattern;

    /// <summary>The tag on a row (design AD3): "OTP · deletes in 23 h 54 m", "Deletes tomorrow", "Deletes 4 Oct 2026".</summary>
    public static string TagText(DateTimeOffset deleteAt, DateTimeOffset now, bool otp)
    {
        var left = deleteAt - now;
        if (otp)
        {
            if (left <= TimeSpan.Zero) return "OTP · deleting now";
            var h = (int)left.TotalHours;
            return h > 0 ? $"OTP · deletes in {h} h {left.Minutes} m" : $"OTP · deletes in {Math.Max(1, left.Minutes)} m";
        }
        var d = deleteAt.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (d <= today) return "Deletes today";
        if (d == today.AddDays(1)) return "Deletes tomorrow";
        return "Deletes " + d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static DeleteUrgency Urgency(DateTimeOffset deleteAt, DateTimeOffset now, bool otp)
    {
        var left = deleteAt - now;
        if (otp || left < TimeSpan.FromHours(48)) return DeleteUrgency.Soon;
        return left < TimeSpan.FromDays(30) ? DeleteUrgency.Weeks : DeleteUrgency.Later;
    }

    /// <summary>The reader bar (design AD3): "Moves to Trash on 4 Oct 2026 at 11:20 (in 6 days)."</summary>
    public static string BarText(DateTimeOffset deleteAt, DateTimeOffset now)
    {
        var l = deleteAt.ToLocalTime();
        var left = deleteAt - now;
        var inText = left <= TimeSpan.Zero ? "any moment now"
            : left < TimeSpan.FromHours(1) ? $"in {Math.Max(1, (int)left.TotalMinutes)} min"
            : left < TimeSpan.FromHours(48) ? $"in {(int)left.TotalHours} h"
            : left < TimeSpan.FromDays(60) ? $"in {(int)Math.Round(left.TotalDays)} days"
            : left < TimeSpan.FromDays(730) ? $"in {(int)Math.Round(left.TotalDays / 30.4)} months"
            : $"in {(int)Math.Round(left.TotalDays / 365.25)} years";
        return $"Moves to Trash on {l:d MMM yyyy} at {l:HH:mm} ({inText}).";
    }

    /// <summary>"Next email arriving today would be deleted on …" (the AD1 submenu).</summary>
    public static string NextArrivalLine(int amount, DeleteUnit unit, DateTimeOffset now) =>
        "Next email arriving today would be deleted on " + DeleteAt(new AutoDeleteRule { Amount = amount, Unit = unit }, now).ToLocalTime().ToString("d MMM yyyy");

    // ───────────────────────── "Delete emails from…" (design DX1-B1) ─────────────────────────

    /// <summary>The KEEP THE LAST chips shown first; "More…" reveals the rest of <see cref="Choices"/>.</summary>
    public static readonly (int Amount, DeleteUnit Unit)[] KeepChoices =
    {
        (1, DeleteUnit.Days), (3, DeleteUnit.Days), (7, DeleteUnit.Days), (14, DeleteUnit.Days),
        (1, DeleteUnit.Months), (3, DeleteUnit.Months), (6, DeleteUnit.Months), (12, DeleteUnit.Months),
    };

    /// <summary>"1 week" / "2 weeks" / "1 year" / "3 days" / "24 hours" (OTP): the period as a chip reads it.</summary>
    public static string KeepLabel(bool otp, int amount, DeleteUnit unit) => otp ? "24 hours" : (amount, unit) switch
    {
        (7, DeleteUnit.Days) => "1 week",
        (14, DeleteUnit.Days) => "2 weeks",
        (12, DeleteUnit.Months) => "1 year",
        _ => After(amount, unit),
    };

    /// <summary>Emails that arrived before this are "older than the kept period" (the mirror of <see cref="DeleteAt"/>).</summary>
    public static DateTimeOffset KeepSince(bool otp, int amount, DeleteUnit unit, DateTimeOffset now) => otp ? now.AddHours(-24) : unit switch
    {
        DeleteUnit.Months => now.AddMonths(-amount),
        DeleteUnit.Years => now.AddYears(-amount),
        _ => now.AddDays(-amount),
    };

    /// <summary>The counts under "Emails already here": all from the sender, those older than the kept period, the oldest of those.</summary>
    public sealed record PastSummary(int Total, int Older, DateTimeOffset? Oldest)
    {
        public int Kept => Math.Max(0, Total - Older);
    }

    /// <summary>What the dialog asks for in one run.</summary>
    public sealed class DeleteFromRequest
    {
        public string Pattern { get; set; } = "";
        /// <summary>"" = all accounts.</summary>
        public string AccountId { get; set; } = "";
        /// <summary>"Emails already here": those older than the kept period go to Trash (after the undo wait).</summary>
        public bool Past { get; set; }
        /// <summary>"Emails that arrive later": an auto-delete rule.</summary>
        public bool Future { get; set; }
        /// <summary>KEEP THE LAST = Nothing: every past email goes; no rule.</summary>
        public bool KeepNothing { get; set; }
        public bool Otp { get; set; }
        public int Amount { get; set; } = 7;
        public DeleteUnit Unit { get; set; } = DeleteUnit.Days;
        /// <summary>The rule being changed, when editing one.</summary>
        public string? RuleId { get; set; }
        /// <summary>With FUTURE and PAST off: whether the emails already here (not yet past the time) get the timer — AppSettings.AutoDeleteIncludePast.</summary>
        public bool StartOnExisting { get; set; } = true;
    }

    /// <summary>What one run did: the past emails to trash (after the undo wait), the rule (new or updated) and the timers set.</summary>
    public sealed record DeleteFromResult(PastEmails Past, AutoDeleteRule? Rule, bool RuleIsNew, int TimersSet);

    /// <summary>The red button: "Delete 212 now" / "Create rule" / "Delete 212 now and keep deleting"; "" = nothing to do.</summary>
    public static string ButtonText(bool past, bool future, int pastCount, bool editing = false) => (past, future) switch
    {
        (true, true) => $"Delete {pastCount:#,0} now and keep deleting",
        (true, false) => $"Delete {pastCount:#,0} now",
        (false, true) => editing ? "Save rule" : "Create rule",
        _ => "",
    };

    /// <summary>The line after "Emails already here —".</summary>
    public static string PastLine(PastSummary s, bool keepNothing, string keepLabel)
    {
        var oldest = s.Oldest is { } o ? $" (oldest {o.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)})" : "";
        if (keepNothing)
            return s.Total == 0 ? "none here now. Pinned ones, Sent and Drafts are never deleted."
                : $"delete all {s.Total:#,0} now{oldest}. Keeps pinned ones, Sent and Drafts.";
        var keeps = $"Keeps {s.Kept:#,0} from the last {keepLabel}, pinned ones, Sent and Drafts.";
        return s.Older == 0 ? $"none older than {keepLabel} here now. {keeps}"
            : $"delete the {s.Older:#,0} older than {keepLabel} now{oldest}. {keeps}";
    }

    /// <summary>The line after "Emails that arrive later —".</summary>
    public static string FutureLine(string keepLabel) =>
        $"delete each one {keepLabel} after it arrives (an auto-delete rule you can pause or remove in Settings → Rules).";

    /// <summary>"Next email from X arriving today 14:30 would be deleted on 14 Oct 2026, 14:30" (the tag is shown next to it).</summary>
    public static string PreviewLine(string pattern, AutoDeleteRule rule, DateTimeOffset now)
    {
        var at = DeleteAt(rule, now).ToLocalTime();
        var who = pattern.Length > 0 ? pattern : "this sender";
        return $"Next email from {who} arriving today {now.ToLocalTime():HH:mm} would be deleted on {at.ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture)} and carry the tag";
    }

    /// <summary>The toast after a run: "Deleting 212 emails from *@xyz.com" / "Emails from x will be deleted 1 week after they arrive".</summary>
    public static string ToastText(string pattern, DeleteFromResult r)
    {
        var later = r.Rule == null ? "" : $"new ones go {KeepLabel(r.Rule.Otp, r.Rule.Amount, r.Rule.Unit)} after they arrive";
        if (r.Past.Count > 0)
            return $"Deleting {r.Past.Count:#,0} email{(r.Past.Count == 1 ? "" : "s")} from {pattern}" + (later.Length > 0 ? " · " + later : "");
        if (r.Rule == null) return $"No emails from {pattern} to delete";
        var w = Who(pattern);
        return char.ToUpperInvariant(w[0]) + w[1..] + $" will be deleted {KeepLabel(r.Rule.Otp, r.Rule.Amount, r.Rule.Unit)} after they arrive"
               + (r.TimersSet > 0 ? $" · {r.TimersSet:#,0} already here timed" : "");
    }
}
