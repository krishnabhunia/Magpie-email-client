using System.Globalization;
using Magpie.Core.Models;

namespace Magpie.Core.Calendar;

public enum CalendarMode { Day, Week, Month, Agenda }

/// <summary>One timed event placed in a day column: minutes from midnight, and which of the side-by-side columns it takes.</summary>
public sealed record PlacedEvent(CalendarEvent Event, double StartMinute, double EndMinute, int Column, int Columns);

/// <summary>Design B2: the dates a view shows, its title, and where timed events sit in a day (overlapping ones side by side).</summary>
public static class CalendarLayout
{
    /// <summary>Weeks start on Monday (as in India).</summary>
    public const DayOfWeek FirstDay = DayOfWeek.Monday;
    public const int AgendaDays = 30;

    public static DateTime WeekStart(DateTime d)
    {
        d = d.Date;
        var back = ((int)d.DayOfWeek - (int)FirstDay + 7) % 7;
        return d.AddDays(-back);
    }

    /// <summary>The days a view covers: [from, to).</summary>
    public static (DateTime From, DateTime To) Range(CalendarMode mode, DateTime anchor)
    {
        anchor = anchor.Date;
        switch (mode)
        {
            case CalendarMode.Day: return (anchor, anchor.AddDays(1));
            case CalendarMode.Week: { var s = WeekStart(anchor); return (s, s.AddDays(7)); }
            case CalendarMode.Month:
            {
                var first = new DateTime(anchor.Year, anchor.Month, 1);
                var s = WeekStart(first);
                return (s, s.AddDays(42));   // always six weeks, so the grid never jumps
            }
            default: return (anchor, anchor.AddDays(AgendaDays));
        }
    }

    /// <summary>Where ‹ and › go.</summary>
    public static DateTime Step(CalendarMode mode, DateTime anchor, int direction) => mode switch
    {
        CalendarMode.Day => anchor.AddDays(direction),
        CalendarMode.Week => anchor.AddDays(7 * direction),
        CalendarMode.Month => anchor.AddMonths(direction),
        _ => anchor.AddDays(AgendaDays * direction),
    };

    /// <summary>"5 – 11 October 2026", "Monday 5 October 2026", "October 2026", "28 September – 4 October 2026".</summary>
    public static string Title(CalendarMode mode, DateTime anchor, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.GetCultureInfo("en-GB");
        anchor = anchor.Date;
        if (mode == CalendarMode.Day) return anchor.ToString("dddd d MMMM yyyy", c);
        if (mode == CalendarMode.Month) return anchor.ToString("MMMM yyyy", c);
        var (from, to) = Range(mode, anchor);
        var last = to.AddDays(-1);
        if (from.Year != last.Year) return $"{from.ToString("d MMMM yyyy", c)} – {last.ToString("d MMMM yyyy", c)}";
        if (from.Month != last.Month) return $"{from.ToString("d MMMM", c)} – {last.ToString("d MMMM yyyy", c)}";
        return $"{from.Day} – {last.ToString("d MMMM yyyy", c)}";
    }

    /// <summary>Does the event touch this day (all-day events: every day they cover)?</summary>
    public static bool OnDay(CalendarEvent e, DateTime day)
    {
        var start = day.Date;
        var end = start.AddDays(1);
        return e.Start.LocalDateTime < end && e.End.LocalDateTime > start
               || (e.Start.LocalDateTime == start && e.End <= e.Start);   // zero-length at midnight
    }

    /// <summary>
    /// The timed events of one day, cut to that day, with overlapping ones side by side: each group of events that
    /// overlap (directly or through others) shares its width equally among as many columns as it needs.
    /// </summary>
    public static List<PlacedEvent> PlaceTimed(IEnumerable<CalendarEvent> events, DateTime day)
    {
        var start = day.Date;
        var items = events.Where(e => !e.AllDay && OnDay(e, start))
            .Select(e =>
            {
                var s = Math.Max(0, (e.Start.LocalDateTime - start).TotalMinutes);
                var en = Math.Min(24 * 60, (e.End.LocalDateTime - start).TotalMinutes);
                return (e, s, en: Math.Max(en, s + 15));   // at least 15 minutes tall
            })
            .OrderBy(x => x.s).ThenByDescending(x => x.en - x.s).ThenBy(x => x.e.Title, StringComparer.Ordinal)
            .ToList();
        var placed = new List<PlacedEvent>();
        var group = new List<(CalendarEvent e, double s, double en, int col)>();
        var colEnds = new List<double>();
        double groupEnd = -1;
        void Flush()
        {
            foreach (var g in group) placed.Add(new PlacedEvent(g.e, g.s, g.en, g.col, colEnds.Count));
            group.Clear();
            colEnds.Clear();
        }
        foreach (var (e, s, en) in items)
        {
            if (group.Count > 0 && s >= groupEnd) Flush();
            var col = colEnds.FindIndex(end => end <= s);
            if (col < 0) { col = colEnds.Count; colEnds.Add(en); }
            else colEnds[col] = en;
            group.Add((e, s, en, col));
            groupEnd = group.Count == 1 ? en : Math.Max(groupEnd, en);
        }
        Flush();
        return placed;
    }
}
