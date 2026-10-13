using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Calendar;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

/// <summary>A calendar in the left rail: its colour and tick (design B2).</summary>
public partial class CalendarItem : ObservableObject
{
    private readonly Action<CalendarItem, bool> _toggle;
    public CalendarInfo Info { get; }
    public string Name => Info.Name;
    public Brush Colour { get; }
    [ObservableProperty] private bool _selected;
    public CalendarItem(CalendarInfo info, Action<CalendarItem, bool> toggle)
    {
        Info = info;
        _toggle = toggle;
        _selected = info.Selected;
        Colour = CalendarColours.Solid(info.Color);
    }
    partial void OnSelectedChanged(bool value) => _toggle(this, value);
}

/// <summary>The calendars of one Google account (a heading in the left rail).</summary>
public sealed class CalendarGroup
{
    public string Account { get; init; } = "";
    public List<CalendarItem> Calendars { get; init; } = new();
}

/// <summary>An event drawn in a view: its words, colours and (for timed ones in Day / Week) where it sits.</summary>
public sealed class EventItem
{
    public CalendarEvent Event { get; init; } = new();
    public string Title { get; init; } = "";
    public string TimeText { get; init; } = "";
    public string Sub { get; init; } = "";
    public Brush Fill { get; init; } = Brushes.Transparent;
    public Brush Bar { get; init; } = Brushes.Transparent;
    /// <summary>An invite not answered yet: dashed border (design B2).</summary>
    public DoubleCollection? Dashes { get; init; }
    public double StrokeWidth => Dashes == null ? 0 : 1.2;
    public double Top { get; init; }
    public double Height { get; init; }
    public double LeftFraction { get; init; }
    public double WidthFraction { get; init; } = 1;
    public bool Short => Height < 34;
    public string Tip => $"{Title}\n{TimeText}{(Sub.Length > 0 ? "\n" + Sub : "")}";
}

public sealed class DayColumn
{
    public DateTime Date { get; init; }
    public string Weekday { get; init; } = "";
    public string DayNumber { get; init; } = "";
    public bool IsToday { get; init; }
    public List<EventItem> AllDay { get; init; } = new();
    public List<EventItem> Timed { get; init; } = new();
    /// <summary>Where "now" is in today's column (-1 on other days).</summary>
    public double NowTop { get; init; } = -1;
    public bool ShowNow => NowTop >= 0;
}

public sealed class MonthCell
{
    public DateTime Date { get; init; }
    public string DayNumber { get; init; } = "";
    public bool InMonth { get; init; }
    public bool IsToday { get; init; }
    public List<EventItem> Items { get; init; } = new();
    public string More { get; init; } = "";
}

public sealed class AgendaDay
{
    public string Header { get; init; } = "";
    public bool IsToday { get; init; }
    public List<EventItem> Items { get; init; } = new();
}

public sealed class MiniDay
{
    public DateTime Date { get; init; }
    public string Text { get; init; } = "";
    public bool InMonth { get; init; }
    public bool IsToday { get; init; }
    public bool InRange { get; init; }
}

/// <summary>Colours of a calendar: the solid one for the bar and a soft one for the block (both themes).</summary>
public static class CalendarColours
{
    public static Color Parse(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return Color.FromRgb(0x14, 0x60, 0x6E); }
    }

    public static Brush Solid(string hex) { var b = new SolidColorBrush(Parse(hex)); b.Freeze(); return b; }

    public static Brush Soft(string hex)
    {
        var c = Parse(hex);
        var b = new SolidColorBrush(Color.FromArgb(ThemeManager.IsDark ? (byte)0x55 : (byte)0x33, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }
}

/// <summary>Design B2: the calendar page — left rail (new event, mini month, calendars) and Day / Week / Month / Agenda.</summary>
public partial class CalendarViewModel : ObservableObject
{
    public const double HourHeight = 48;
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-GB");
    private readonly MailEngine _e = AppServices.Engine;
    private readonly CalendarService _cal;
    private Dictionary<string, string> _colours = new();
    private readonly Ui.Debouncer _changed = new(TimeSpan.FromMilliseconds(250));

    public ObservableCollection<CalendarGroup> Groups { get; } = new();
    public ObservableCollection<DayColumn> Days { get; } = new();
    public ObservableCollection<MonthCell> MonthCells { get; } = new();
    public ObservableCollection<AgendaDay> Agenda { get; } = new();
    public ObservableCollection<MiniDay> MiniDays { get; } = new();
    public IReadOnlyList<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => $"{h:00}:00").ToList();
    public IReadOnlyList<string> WeekdayLetters { get; } = new[] { "M", "T", "W", "T", "F", "S", "S" };

    [ObservableProperty] private CalendarMode _mode = CalendarMode.Week;
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime _miniMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _miniTitle = "";
    [ObservableProperty] private string _problem = "";
    [ObservableProperty] private string _problemAccountId = "";
    [ObservableProperty] private bool _problemNeedsSignIn;
    [ObservableProperty] private bool _noGoogleAccount;
    [ObservableProperty] private bool _hasAllDay;
    [ObservableProperty] private string _agendaEmpty = "";

    public bool IsDayOrWeek => Mode is CalendarMode.Day or CalendarMode.Week;
    public bool IsMonth => Mode == CalendarMode.Month;
    public bool IsAgenda => Mode == CalendarMode.Agenda;
    public int DayCount => Mode == CalendarMode.Day ? 1 : 7;
    public bool HasProblem => Problem.Length > 0;
    public double GridHeight => 24 * HourHeight;

    /// <summary>Asks the page to scroll the day grid to working hours (after the view changes).</summary>
    public event Action? ScrollToMorning;
    /// <summary>Asks the page to open an event (new or existing) in the event window.</summary>
    public event Action<CalendarEvent>? OpenEvent;

    public CalendarViewModel()
    {
        _cal = _e.Calendar;
        _cal.Changed += () => Ui.Post(() => _changed.Run(Refresh));
        ThemeManager.Changed += () => Ui.Post(Refresh);
        Refresh();
    }

    partial void OnModeChanged(CalendarMode value)
    {
        OnPropertyChanged(nameof(IsDayOrWeek));
        OnPropertyChanged(nameof(IsMonth));
        OnPropertyChanged(nameof(IsAgenda));
        OnPropertyChanged(nameof(DayCount));
        Refresh();
        if (IsDayOrWeek) ScrollToMorning?.Invoke();
    }

    partial void OnAnchorChanged(DateTime value)
    {
        MiniMonth = new DateTime(value.Year, value.Month, 1);
        Refresh();
    }

    partial void OnMiniMonthChanged(DateTime value) => BuildMini();
    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    [RelayCommand] private void Today() { Anchor = DateTime.Today; if (IsDayOrWeek) ScrollToMorning?.Invoke(); }
    [RelayCommand] private void Previous() => Anchor = CalendarLayout.Step(Mode, Anchor, -1);
    [RelayCommand] private void Next() => Anchor = CalendarLayout.Step(Mode, Anchor, 1);
    [RelayCommand] private void SetMode(string? name) { if (Enum.TryParse<CalendarMode>(name, out var m)) Mode = m; }
    [RelayCommand] private void MiniPrevious() => MiniMonth = MiniMonth.AddMonths(-1);
    [RelayCommand] private void MiniNext() => MiniMonth = MiniMonth.AddMonths(1);
    [RelayCommand] private void PickDay(MiniDay? d) { if (d != null) Anchor = d.Date; }
    [RelayCommand] private void OpenDay(MonthCell? c) { if (c == null) return; Anchor = c.Date; Mode = CalendarMode.Day; }
    [RelayCommand] private void Open(EventItem? item) { if (item != null) OpenEvent?.Invoke(item.Event.Clone()); }
    [RelayCommand] private void CheckNow() => _cal.Poke();

    /// <summary>A new event: the next half hour on the day being looked at, or the time clicked in Day / Week.</summary>
    [RelayCommand]
    private void NewEvent() => OpenEvent?.Invoke(NewAt(Anchor.Date == DateTime.Today ? NextHalfHour() : Anchor.Date.AddHours(9)));

    public void NewEventAt(DateTime day, double minuteOfDay)
    {
        var start = day.Date.AddMinutes(Math.Floor(minuteOfDay / 30) * 30);
        OpenEvent?.Invoke(NewAt(start));
    }

    /// <summary>Design HM1 (E7, S7): the new-event window with these people invited, from the next half hour.
    /// False when there is no Google calendar Magpie may write to.</summary>
    public bool NewEventWith(string title, IEnumerable<(string Name, string Email)> people, string notes = "")
    {
        if (WritableCalendars().Count == 0) return false;
        var e = NewAt(NextHalfHour());
        e.Title = title;
        e.Description = notes;
        foreach (var (name, email) in people.DistinctBy(p => p.Email.ToLowerInvariant()))
            e.Attendees.Add(new EventAttendee { Email = email, Name = name });
        OpenEvent?.Invoke(e);
        return true;
    }

    private static DateTime NextHalfHour()
    {
        var n = DateTime.Now;
        var t = n.Date.AddMinutes(Math.Ceiling(n.TimeOfDay.TotalMinutes / 30) * 30);
        return t <= n ? t.AddMinutes(30) : t;
    }

    private CalendarEvent NewAt(DateTime start)
    {
        var cal = WritableCalendars().FirstOrDefault(c => c.Primary) ?? WritableCalendars().FirstOrDefault();
        return new CalendarEvent
        {
            AccountId = cal?.AccountId ?? "", CalendarId = cal?.Id ?? "",
            Start = new DateTimeOffset(start), End = new DateTimeOffset(start.AddMinutes(30)), ReminderMinutes = 10, IAmOrganizer = true,
        };
    }

    public List<CalendarInfo> WritableCalendars() => _cal.Calendars().Where(c => c.CanEdit).ToList();

    public void GoTo(DateTime day) { Anchor = day.Date; }

    // ───────────────────────── building the views ─────────────────────────

    public void Refresh()
    {
        Title = CalendarLayout.Title(Mode, Anchor, En);
        BuildRail();
        BuildProblem();
        var (from, to) = CalendarLayout.Range(Mode, Anchor);
        var events = _cal.Between(new DateTimeOffset(from), new DateTimeOffset(to));
        Days.Clear();
        MonthCells.Clear();
        Agenda.Clear();
        switch (Mode)
        {
            case CalendarMode.Day:
            case CalendarMode.Week: BuildDays(from, to, events); break;
            case CalendarMode.Month: BuildMonth(from, events); break;
            default: BuildAgenda(from, to, events); break;
        }
        BuildMini();
    }

    private void BuildRail()
    {
        var cals = _cal.Calendars();
        _colours = cals.GroupBy(c => c.AccountId + "\n" + c.Id).ToDictionary(g => g.Key, g => g.First().Color);
        Groups.Clear();
        foreach (var g in cals.GroupBy(c => c.AccountId))
            Groups.Add(new CalendarGroup
            {
                Account = _e.AccountById(g.Key)?.Email ?? "",
                Calendars = g.Select(c => new CalendarItem(c, (item, on) => _cal.SetSelected(item.Info, on))).ToList(),
            });
        NoGoogleAccount = _cal.GoogleAccounts.Count == 0;
    }

    private void BuildProblem()
    {
        var p = _cal.Problems.FirstOrDefault();
        if (p.Value == null) { Problem = ""; ProblemAccountId = ""; ProblemNeedsSignIn = false; return; }
        var who = _e.AccountById(p.Key)?.Email ?? "";
        Problem = (who.Length > 0 ? who + ": " : "") + p.Value.Message;
        ProblemAccountId = p.Key;
        ProblemNeedsSignIn = p.Value.NeedsSignIn;
    }

    private string ColourOf(CalendarEvent e) => _colours.TryGetValue(e.AccountId + "\n" + e.CalendarId, out var c) ? c : "#14606E";

    private EventItem Item(CalendarEvent e, double top = 0, double height = 0, double left = 0, double width = 1)
    {
        var hex = ColourOf(e);
        return new EventItem
        {
            Event = e,
            Title = e.Title,
            TimeText = TimeText(e),
            Sub = string.Join(" · ", new[] { e.Location, e.IsUnansweredInvite ? "Invite · not answered" : e.MyAnswer == EventAnswer.Tentative ? "Maybe" : "" }.Where(s => s.Length > 0)),
            Fill = CalendarColours.Soft(hex),
            Bar = CalendarColours.Solid(hex),
            Dashes = e.IsUnansweredInvite ? new DoubleCollection { 4, 2 } : null,
            Top = top, Height = height, LeftFraction = left, WidthFraction = width,
        };
    }

    public static string TimeText(CalendarEvent e)
    {
        if (e.AllDay)
        {
            var days = (int)Math.Round((e.End - e.Start).TotalDays);
            return days > 1 ? $"All day · {e.Start.LocalDateTime:d MMM} – {e.End.LocalDateTime.AddDays(-1):d MMM}" : "All day";
        }
        return $"{e.Start.LocalDateTime:HH:mm} – {e.End.LocalDateTime:HH:mm}";
    }

    private void BuildDays(DateTime from, DateTime to, List<CalendarEvent> events)
    {
        var anyAllDay = false;
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var allDay = events.Where(e => e.AllDay && CalendarLayout.OnDay(e, d)).Select(e => Item(e)).ToList();
            anyAllDay |= allDay.Count > 0;
            var timed = CalendarLayout.PlaceTimed(events, d).Select(p => Item(p.Event,
                top: p.StartMinute / 60 * HourHeight, height: Math.Max(18, (p.EndMinute - p.StartMinute) / 60 * HourHeight - 2),
                left: (double)p.Column / p.Columns, width: 1.0 / p.Columns)).ToList();
            var today = d == DateTime.Today;
            Days.Add(new DayColumn
            {
                Date = d, Weekday = d.ToString("ddd", En), DayNumber = d.Day.ToString(En), IsToday = today, AllDay = allDay, Timed = timed,
                NowTop = today ? DateTime.Now.TimeOfDay.TotalMinutes / 60 * HourHeight : -1,
            });
        }
        HasAllDay = anyAllDay;
    }

    private void BuildMonth(DateTime from, List<CalendarEvent> events)
    {
        for (var i = 0; i < 42; i++)
        {
            var d = from.AddDays(i);
            var list = events.Where(e => CalendarLayout.OnDay(e, d)).OrderByDescending(e => e.AllDay).ThenBy(e => e.Start).ToList();
            MonthCells.Add(new MonthCell
            {
                Date = d, DayNumber = d.Day == 1 ? d.ToString("d MMM", En) : d.Day.ToString(En), InMonth = d.Month == Anchor.Month, IsToday = d == DateTime.Today,
                Items = list.Take(3).Select(e => Item(e)).ToList(),
                More = list.Count > 3 ? $"+{list.Count - 3} more" : "",
            });
        }
    }

    private void BuildAgenda(DateTime from, DateTime to, List<CalendarEvent> events)
    {
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var list = events.Where(e => CalendarLayout.OnDay(e, d)).OrderByDescending(e => e.AllDay).ThenBy(e => e.Start).ToList();
            if (list.Count == 0) continue;
            Agenda.Add(new AgendaDay
            {
                Header = d == DateTime.Today ? "Today · " + d.ToString("dddd d MMMM", En) : d.ToString("dddd d MMMM", En),
                IsToday = d == DateTime.Today,
                Items = list.Select(e => Item(e)).ToList(),
            });
        }
        AgendaEmpty = Agenda.Count == 0 ? $"Nothing in the next {CalendarLayout.AgendaDays} days." : "";
    }

    private void BuildMini()
    {
        MiniTitle = MiniMonth.ToString("MMMM yyyy", En);
        var start = CalendarLayout.WeekStart(MiniMonth);
        var (rangeFrom, rangeTo) = Mode == CalendarMode.Agenda ? (Anchor, Anchor.AddDays(1)) : CalendarLayout.Range(Mode, Anchor);
        if (Mode == CalendarMode.Month) (rangeFrom, rangeTo) = (Anchor, Anchor.AddDays(1));
        MiniDays.Clear();
        for (var i = 0; i < 42; i++)
        {
            var d = start.AddDays(i);
            MiniDays.Add(new MiniDay
            {
                Date = d, Text = d.Day.ToString(En), InMonth = d.Month == MiniMonth.Month, IsToday = d == DateTime.Today,
                InRange = d >= rangeFrom && d < rangeTo,
            });
        }
    }
}
