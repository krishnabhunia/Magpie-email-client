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
        return "Deletes " + d.ToString("d MMM yyyy");
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
}
