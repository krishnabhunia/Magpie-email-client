using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core.Calendar;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>
/// Design B2: the new / edit event window — title, date and time or all day, calendar, people, where and a Meet link,
/// reminder, repeat, notes. An invite from someone else is answered here (Accept / Maybe / Decline) and not edited.
/// </summary>
public partial class EventWindow : Window
{
    private sealed record Choice<T>(string Label, T Value);

    private static readonly Choice<int>[] Reminders =
    {
        new("None", -1), new("At the start", 0), new("5 minutes before", 5), new("10 minutes before", 10), new("15 minutes before", 15),
        new("30 minutes before", 30), new("1 hour before", 60), new("1 day before", 1440),
    };
    private static readonly Choice<string>[] Repeats =
    {
        new("Does not repeat", ""), new("Every day", "daily"), new("Every week", "weekly"), new("Every month", "monthly"),
    };

    private readonly CalendarEvent _e;
    private readonly CalendarService _cal = AppServices.Engine.Calendar;
    private readonly bool _canEdit;

    public static void Open(Window? owner, CalendarEvent e, CalendarViewModel vm)
    {
        var w = new EventWindow(e, vm);
        if (owner != null) w.Owner = owner;
        w.ShowDialog();
    }

    private EventWindow(CalendarEvent e, CalendarViewModel vm)
    {
        InitializeComponent();
        _e = e;
        var isNew = e.Id == 0;
        var calendars = vm.WritableCalendars();
        var own = AppServices.Engine.Calendar.Calendars().FirstOrDefault(c => c.AccountId == e.AccountId && c.Id == e.CalendarId);
        if (own != null && calendars.All(c => !(c.AccountId == own.AccountId && c.Id == own.Id))) calendars.Insert(0, own);
        _canEdit = (own?.CanEdit ?? isNew) && e.IAmOrganizer;

        foreach (var box in new[] { StartTime, EndTime })
            for (var m = 0; m < 24 * 60; m += 15) box.Items.Add($"{m / 60:00}:{m % 60:00}");
        CalendarBox.ItemsSource = calendars;
        CalendarBox.SelectedItem = calendars.FirstOrDefault(c => c.AccountId == e.AccountId && c.Id == e.CalendarId) ?? calendars.FirstOrDefault();
        ReminderBox.ItemsSource = Reminders;
        ReminderBox.SelectedItem = Reminders.FirstOrDefault(r => r.Value == e.ReminderMinutes) ?? Reminders[3];
        RepeatBox.ItemsSource = Repeats;
        RepeatBox.SelectedItem = Repeats.First(r => r.Value == GoogleCalendarClient.RepeatChoice(e.Recurrence));

        Heading.Text = isNew ? "New event" : _canEdit ? "Event" : e.Title;
        Title = isNew ? "New event" : e.Title;
        TitleBox.Text = isNew ? "" : e.Title;
        AllDay.IsChecked = e.AllDay;
        StartDay.SelectedDate = e.Start.LocalDateTime.Date;
        StartTime.Text = e.Start.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        var end = e.AllDay ? e.End.LocalDateTime.AddDays(-1) : e.End.LocalDateTime;
        EndDay.SelectedDate = end.Date < e.Start.LocalDateTime.Date ? e.Start.LocalDateTime.Date : end.Date;
        EndTime.Text = e.End.LocalDateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        PeopleBox.Text = string.Join(", ", e.Attendees.Where(a => !a.Self).Select(a => a.Email));
        WhereBox.Text = e.Location;
        NotesBox.Text = e.Description;
        MeetBox.IsChecked = e.AddMeet;
        MeetBox.Visibility = e.MeetLink.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        JoinButton.Visibility = e.MeetLink.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (e.IsRepeating && !isNew)
        {
            RepeatBox.IsEnabled = false;
            RepeatPanel.ToolTip = "A change here applies to this occurrence only.";
        }

        var others = e.Attendees.Where(a => !a.Self).ToList();
        if (!isNew && others.Count > 0)
        {
            Answers.Text = string.Join(" · ", others.Select(a => $"{(a.Name.Length > 0 ? a.Name : a.Email)}: {AnswerWord(a.Answer)}"));
            Answers.Visibility = Visibility.Visible;
        }
        if (!e.IAmOrganizer && e.Attendees.Any(a => a.Self))
        {
            InviteBar.Visibility = Visibility.Visible;
            InviteText.Text = $"{(e.Organizer.Length > 0 ? e.Organizer : "Someone")} invited you · {AnswerWord(e.MyAnswer)}";
        }
        DeleteButton.Visibility = !isNew && (own?.CanEdit ?? false) ? Visibility.Visible : Visibility.Collapsed;
        if (!_canEdit)
        {
            foreach (var c in new Control[] { TitleBox, StartDay, StartTime, EndDay, EndTime, AllDay, CalendarBox, ReminderBox, PeopleBox, WhereBox, MeetBox, RepeatBox, NotesBox })
                c.IsEnabled = false;
            SaveButton.Visibility = Visibility.Collapsed;
            CloseButton.IsDefault = true;
        }
        UpdateAllDay();
        Loaded += (_, _) => { if (_canEdit) { TitleBox.Focus(); TitleBox.SelectAll(); } };
    }

    private static string AnswerWord(EventAnswer a) => a switch
    {
        EventAnswer.Accepted => "going",
        EventAnswer.Tentative => "maybe",
        EventAnswer.Declined => "not going",
        _ => "not answered",
    };

    private void OnAllDayChanged(object sender, RoutedEventArgs e) => UpdateAllDay();

    private void UpdateAllDay()
    {
        var timed = AllDay.IsChecked != true;
        StartTime.Visibility = EndTime.Visibility = timed ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>Moving the start keeps the length: the end follows.</summary>
    private void OnStartDayChanged(object? sender, SelectionChangedEventArgs e) => KeepLength();
    private void OnStartTimeChanged(object sender, SelectionChangedEventArgs e) => KeepLength();

    private DateTime? _lastStart;
    private void KeepLength()
    {
        if (!IsLoaded) { _lastStart = ReadStart(); return; }
        var start = ReadStart();
        var end = ReadEnd();
        if (start == null || end == null || _lastStart == null) { _lastStart = start; return; }
        var length = end.Value - _lastStart.Value;
        if (length < TimeSpan.Zero) length = TimeSpan.FromMinutes(30);
        var newEnd = start.Value + length;
        EndDay.SelectedDate = newEnd.Date;
        EndTime.Text = newEnd.ToString("HH:mm", CultureInfo.InvariantCulture);
        _lastStart = start;
    }

    private static TimeSpan? ParseTime(string text) =>
        TimeSpan.TryParseExact(text.Trim(), new[] { @"hh\:mm", @"h\:mm", "hhmm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;

    private DateTime? ReadStart() => StartDay.SelectedDate is { } d && ParseTime(StartTime.Text) is { } t ? d.Date + t : StartDay.SelectedDate?.Date;
    private DateTime? ReadEnd() => EndDay.SelectedDate is { } d && ParseTime(EndTime.Text) is { } t ? d.Date + t : EndDay.SelectedDate?.Date;

    private void Fail(string message)
    {
        Error.Text = message;
        Error.Visibility = Visibility.Visible;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (CalendarBox.SelectedItem is not CalendarInfo cal) { Fail("Add a Google account first: events are saved in its calendar."); return; }
        var allDay = AllDay.IsChecked == true;
        if (StartDay.SelectedDate is not { } startDay || EndDay.SelectedDate is not { } endDay) { Fail("Choose the start and end dates."); return; }
        DateTime start, end;
        if (allDay)
        {
            start = startDay.Date;
            end = endDay.Date.AddDays(1);
        }
        else
        {
            if (ParseTime(StartTime.Text) is not { } st || ParseTime(EndTime.Text) is not { } et) { Fail("Times look like 09:30."); return; }
            start = startDay.Date + st;
            end = endDay.Date + et;
        }
        if (end <= start) { Fail("The end must be after the start."); return; }
        var people = PeopleBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim().Trim('<', '>')).Where(p => p.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (_e.Id != 0 && (_e.AccountId != cal.AccountId || _e.CalendarId != cal.Id))
        {
            // Moved to another calendar: made there, removed here.
            _cal.Delete(_e.Clone());
            _e.Id = 0;
            _e.EventId = "";
            _e.MeetLink = "";
        }
        _e.AccountId = cal.AccountId;
        _e.CalendarId = cal.Id;
        _e.Title = TitleBox.Text.Trim().Length > 0 ? TitleBox.Text.Trim() : "(no title)";
        _e.AllDay = allDay;
        _e.Start = new DateTimeOffset(start);
        _e.End = new DateTimeOffset(end);
        _e.Location = WhereBox.Text.Trim();
        _e.Description = NotesBox.Text;
        _e.ReminderMinutes = (ReminderBox.SelectedItem as Choice<int>)?.Value ?? 10;
        _e.AddMeet = MeetBox.IsChecked == true;
        if (RepeatBox.IsEnabled) _e.Recurrence = GoogleCalendarClient.RepeatRule((RepeatBox.SelectedItem as Choice<string>)?.Value ?? "");
        var keep = _e.Attendees.Where(a => people.Contains(a.Email, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var p in people.Where(p => keep.All(a => !a.Email.Equals(p, StringComparison.OrdinalIgnoreCase))))
            keep.Add(new EventAttendee { Email = p, Answer = EventAnswer.NeedsAction });
        if (keep.Count > 0 && keep.All(a => !a.Self) && AppServices.Engine.AccountById(cal.AccountId) is { } me)
            keep.Insert(0, new EventAttendee { Email = me.Email, Self = true, Organizer = true, Answer = EventAnswer.Accepted });
        _e.Attendees = keep;
        _e.IAmOrganizer = true;
        _cal.Save(_e);
        Close();
    }

    private void Respond(EventAnswer answer)
    {
        _cal.Respond(_e, answer);
        Close();
    }

    private void OnAccept(object sender, RoutedEventArgs e) => Respond(EventAnswer.Accepted);
    private void OnMaybe(object sender, RoutedEventArgs e) => Respond(EventAnswer.Tentative);
    private void OnDecline(object sender, RoutedEventArgs e) => Respond(EventAnswer.Declined);

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        var what = _e.IsRepeating ? "this occurrence of" : "";
        if (!Ui.Confirm("Delete event", $"Delete {what} \"{_e.Title}\"?{(_e.Attendees.Any(a => !a.Self) ? " The people invited are told." : "")}".Replace("  ", " "))) return;
        _cal.Delete(_e);
        Close();
    }

    private void OnJoin(object sender, RoutedEventArgs e) => Ui.OpenExternal(_e.MeetLink);
}
