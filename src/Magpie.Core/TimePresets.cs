namespace Magpie.Core;

public sealed record TimePreset(string Label, DateTimeOffset When);

/// <summary>Snooze / send-later / remind-me choices, computed from the local clock.</summary>
public static class TimePresets
{
    public static List<TimePreset> For(DateTime nowLocal, bool sendLater = false)
    {
        var list = new List<TimePreset>();
        var today = nowLocal.Date;
        DateTimeOffset At(DateTime d) => new(d, TimeZoneInfo.Local.GetUtcOffset(d));

        // Later today: +3 h, but only if that is still today and before 21:00.
        var later = nowLocal.AddHours(3);
        later = new DateTime(later.Year, later.Month, later.Day, later.Hour, later.Minute >= 30 ? 30 : 0, 0);
        if (later.Date == today && later.Hour < 21) list.Add(new("Later today", At(later)));
        if (nowLocal.Hour < 17) list.Add(new("This evening", At(today.AddHours(18))));
        list.Add(new(sendLater ? "Tomorrow morning" : "Tomorrow", At(today.AddDays(1).AddHours(8))));
        if (sendLater) list.Add(new("Tomorrow afternoon", At(today.AddDays(1).AddHours(13))));

        var dow = nowLocal.DayOfWeek;
        if (dow is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !sendLater)
        {
            var sat = today.AddDays(((int)DayOfWeek.Saturday - (int)dow + 7) % 7);
            list.Add(new("This weekend", At(sat.AddHours(9))));
        }
        var mon = today.AddDays(((int)DayOfWeek.Monday - (int)dow + 7) % 7);
        if (mon <= today.AddDays(1)) mon = mon.AddDays(7);
        list.Add(new("Next week", At(mon.AddHours(8))));
        return list;
    }

    public static string Describe(DateTimeOffset when, DateTime nowLocal)
    {
        var l = when.ToLocalTime().DateTime;
        if (l.Date == nowLocal.Date) return "today " + l.ToString("HH:mm");
        if (l.Date == nowLocal.Date.AddDays(1)) return "tomorrow " + l.ToString("HH:mm");
        if ((l.Date - nowLocal.Date).TotalDays < 7) return l.ToString("ddd HH:mm");
        return l.ToString("d MMM, HH:mm");
    }
}
