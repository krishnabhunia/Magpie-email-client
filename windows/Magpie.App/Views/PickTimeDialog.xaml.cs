using System.Windows;
using Magpie.Core;

namespace Magpie.App.Views;

public partial class PickTimeDialog : Window
{
    public DateTimeOffset? Result { get; private set; }

    public PickTimeDialog()
    {
        InitializeComponent();
        for (int h = 6; h <= 23; h++)
            foreach (var m in new[] { 0, 30 })
                Time.Items.Add($"{h:00}:{m:00}");
        Day.SelectedDateChanged += (_, _) => UpdateTimeZoneNote();
        Time.SelectionChanged += (_, _) => UpdateTimeZoneNote();
        UpdateTimeZoneNote();
    }

    public static DateTimeOffset? Ask(Window owner, string title, string explain, DateTime suggested)
    {
        var d = new PickTimeDialog { Owner = owner };
        d.Heading.Text = title;
        d.Explain.Text = explain;
        d.Day.SelectedDate = suggested.Date;
        d.Day.DisplayDateStart = DateTime.Today;
        var t = $"{suggested.Hour:00}:{(suggested.Minute >= 30 ? 30 : 0):00}";
        d.Time.SelectedItem = d.Time.Items.Contains(t) ? t : "08:00";
        return d.ShowDialog() == true ? d.Result : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (SelectedWallTime() is not { } local) return;
        if (!ScheduleTime.TryResolve(local, TimeZoneInfo.Local, out var instant, out var error))
        {
            Error.Text = error;
            Error.Visibility = Visibility.Visible;
            return;
        }
        if (instant <= DateTimeOffset.Now.AddMinutes(1))
        {
            Error.Text = "Pick a time in the future.";
            Error.Visibility = Visibility.Visible;
            return;
        }
        Result = instant;
        DialogResult = true;
    }

    private DateTime? SelectedWallTime()
    {
        if (Day.SelectedDate is not { } day || Time.SelectedItem is not string text
            || !TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var time)) return null;
        return DateTime.SpecifyKind(day.Date.Add(time), DateTimeKind.Unspecified);
    }

    private void UpdateTimeZoneNote()
    {
        Error.Visibility = Visibility.Collapsed;
        if (SelectedWallTime() is not { } local)
        {
            TimeZoneNote.Text = ScheduleTime.ZoneLabel(DateTimeOffset.Now);
            return;
        }
        TimeZoneNote.Text = ScheduleTime.TryResolve(local, TimeZoneInfo.Local, out var instant, out var error)
            ? ScheduleTime.ZoneLabel(instant) : "Local time — " + TimeZoneInfo.Local.DisplayName + "\n" + error;
    }
}
