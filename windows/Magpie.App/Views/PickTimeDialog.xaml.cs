using System.Windows;

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
        if (Day.SelectedDate is not { } day || Time.SelectedItem is not string t) return;
        var parts = t.Split(':');
        var local = day.Date.AddHours(int.Parse(parts[0])).AddMinutes(int.Parse(parts[1]));
        if (local <= DateTime.Now.AddMinutes(1))
        {
            Error.Text = "Pick a time in the future.";
            Error.Visibility = Visibility.Visible;
            return;
        }
        Result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        DialogResult = true;
    }
}
