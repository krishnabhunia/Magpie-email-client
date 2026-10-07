using System.Globalization;
using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>
/// Design DD1: the words for "when does this email get deleted?" — pointing at a row (H1–H3), on the row (L1–L4) and in
/// the reading pane (R1–R3). The screens only place these texts; which option shows is <see cref="DeleteDateLook"/>.
/// </summary>
public static class DeleteDates
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>"in 5 h", "in 3 days", "in 2 months" (L1 pill, R1 banner, R3).</summary>
    public static string Left(DateTimeOffset at, DateTimeOffset now)
    {
        var left = at - now;
        if (left <= TimeSpan.Zero) return "any moment now";
        if (left < TimeSpan.FromHours(1)) return $"in {Math.Max(1, (int)left.TotalMinutes)} min";
        if (left < TimeSpan.FromHours(48)) return $"in {(int)left.TotalHours} h";
        if (left < TimeSpan.FromDays(60)) return $"in {(int)Math.Round(left.TotalDays)} days";
        if (left < TimeSpan.FromDays(730)) return $"in {(int)Math.Round(left.TotalDays / 30.4)} months";
        return $"in {(int)Math.Round(left.TotalDays / 365.25)} years";
    }

    /// <summary>The short form under the date or next to the ring (L3, L4): "40 m", "5 h", "3 d", "2 mo", "1 y".</summary>
    public static string Short(DateTimeOffset at, DateTimeOffset now)
    {
        var left = at - now;
        if (left <= TimeSpan.Zero) return "now";
        if (left < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)left.TotalMinutes)} m";
        if (left < TimeSpan.FromHours(48)) return $"{(int)left.TotalHours} h";
        if (left < TimeSpan.FromDays(60)) return $"{(int)Math.Round(left.TotalDays)} d";
        if (left < TimeSpan.FromDays(730)) return $"{(int)Math.Round(left.TotalDays / 30.4)} mo";
        return $"{(int)Math.Round(left.TotalDays / 365.25)} y";
    }

    /// <summary>When the timer started: the delete date minus the rule's time (the arrival, or the day the rule was made).</summary>
    public static DateTimeOffset Start(AutoDeleteRule? rule, DateTimeOffset at, DateTimeOffset fallback)
    {
        if (rule == null) return fallback;
        if (rule.Otp) return at.AddHours(-24);
        return rule.Unit switch
        {
            DeleteUnit.Months => at.AddMonths(-rule.Amount),
            DeleteUnit.Years => at.AddYears(-rule.Amount),
            _ => at.AddDays(-rule.Amount),
        };
    }

    /// <summary>Share of the time still left, 0…1 (L4 ring, R1 bar): 3 of 7 days left → 0.43.</summary>
    public static double ShareLeft(DateTimeOffset start, DateTimeOffset at, DateTimeOffset now)
    {
        var total = (at - start).TotalSeconds;
        if (total <= 0) return 0;
        return Math.Clamp((at - now).TotalSeconds / total, 0, 1);
    }

    /// <summary>"4 Oct" (or "4 Oct 2027" in another year).</summary>
    public static string Day(DateTimeOffset at, DateTimeOffset now)
    {
        var l = at.ToLocalTime();
        return l.Year == now.ToLocalTime().Year ? l.ToString("d MMM", C) : l.ToString("d MMM yyyy", C);
    }

    /// <summary>"Rule: alert@magicbricks.com after 7 days" / "Rule: OTP from x@y.com, 24 hours" / "Its rule was removed".</summary>
    public static string RuleLine(AutoDeleteRule? rule)
    {
        if (rule == null) return "Its rule was removed (this email keeps its date)";
        var who = rule.Pattern.StartsWith("*@", StringComparison.Ordinal) ? "anyone at " + rule.Pattern[2..] : rule.Pattern;
        var what = rule.Otp ? $"Rule: OTP from {who}, 24 hours" : $"Rule: {who} after {AutoDelete.After(rule.Amount, rule.Unit)}";
        return what + (rule.Paused ? " (paused)" : "");
    }

    /// <summary>H1: the date tooltip — received, deletes on, rule.</summary>
    public static string Tooltip(DateTimeOffset received, DateTimeOffset at, DateTimeOffset now, AutoDeleteRule? rule) =>
        $"Received {received.ToLocalTime().ToString("d MMM yyyy, HH:mm", C)}\n" +
        $"Deletes on {at.ToLocalTime().ToString("d MMM yyyy", C)} ({Left(at, now)})\n" +
        RuleLine(rule);

    /// <summary>H2 card and R2 chip card, first line: "Deletes on 4 Oct 2026 · in 3 days".</summary>
    public static string CardTitle(DateTimeOffset at, DateTimeOffset now) =>
        $"Deletes on {at.ToLocalTime().ToString("d MMM yyyy", C)} · {Left(at, now)}";

    /// <summary>H3: the tag grown to the full date and rule — "Deletes Sat 4 Oct, 11:06 · rule: after 7 days".</summary>
    public static string LongTag(DateTimeOffset at, DateTimeOffset now, AutoDeleteRule? rule)
    {
        var l = at.ToLocalTime();
        var day = l.Year == now.ToLocalTime().Year ? l.ToString("ddd d MMM, HH:mm", C) : l.ToString("ddd d MMM yyyy, HH:mm", C);
        var r = rule == null ? "rule removed" : rule.Otp ? "rule: OTP, 24 hours" : "rule: after " + AutoDelete.After(rule.Amount, rule.Unit);
        return $"Deletes {day} · {r}";
    }

    /// <summary>R1 banner, bold part: "Deletes on Saturday 4 Oct 2026, 11:06".</summary>
    public static string BannerDate(DateTimeOffset at) => "Deletes on " + at.ToLocalTime().ToString("dddd d MMM yyyy, HH:mm", C);

    /// <summary>R2 chip: "Deletes today", "Deletes tomorrow", "Deletes 4 Oct".</summary>
    public static string Chip(DateTimeOffset at, DateTimeOffset now)
    {
        var d = at.ToLocalTime().Date;
        var today = now.ToLocalTime().Date;
        if (d <= today) return "Deletes today " + at.ToLocalTime().ToString("HH:mm", C);
        if (d == today.AddDays(1)) return "Deletes tomorrow";
        return "Deletes " + Day(at, now);
    }

    /// <summary>R3: each email of a conversation, "deletes in 3 days".</summary>
    public static string MessageNote(DateTimeOffset at, DateTimeOffset now) => "deletes " + Left(at, now);
}
