using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>"Choose sender and time…" / Edit / Change rule (design AD2).</summary>
public partial class AutoDeleteDialog : Window
{
    private readonly MailEngine _e = AppServices.Engine;
    private readonly AutoDeleteRule _rule;
    private readonly bool _editing;
    private (int Amount, DeleteUnit Unit) _choice = (7, DeleteUnit.Days);
    private readonly List<ToggleButton> _chips = new();

    private AutoDeleteDialog(AutoDeleteRule rule, bool editing)
    {
        InitializeComponent();
        _rule = rule;
        _editing = editing;
        Heading.Text = editing ? "Change auto-delete rule" : "Auto-delete future emails";
        OkButton.Content = editing ? "Save rule" : "Create rule";
        Pattern.Text = rule.Pattern;
        if (!rule.Otp) _choice = (rule.Amount, rule.Unit);
        foreach (var (amount, unit) in AutoDelete.Choices)
        {
            var chip = new ToggleButton
            {
                Content = amount.ToString(), Style = (Style)FindResource("Button.Chip"), MinWidth = 38, Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(8, 2, 8, 2), FontSize = 12.5, Tag = (amount, unit),
                ToolTip = AutoDelete.After(amount, unit) + " after it arrives",
            };
            chip.Click += (_, _) => { _choice = (amount, unit); AfterTime.IsChecked = true; Refresh(); };
            _chips.Add(chip);
            (unit switch { DeleteUnit.Days => DaysRow, DeleteUnit.Months => MonthsRow, _ => YearsRow }).Children.Add(chip);
        }
        AfterTime.IsChecked = !rule.Otp;
        Otp.IsChecked = rule.Otp;
        var accounts = new List<Choice<string>> { new("", "All my accounts") }.Concat(_e.Accounts.Select(a => new Choice<string>(a.Id, a.Email))).ToList();
        AccountBox.ItemsSource = accounts;
        AccountBox.SelectedValue = accounts.Any(a => a.Value == rule.AccountId) ? rule.AccountId : "";
        Loaded += (_, _) => { Pattern.Focus(); Pattern.CaretIndex = Pattern.Text.Length; Refresh(); };
    }

    /// <summary>Opens the dialog; returns the saved rule, or null when cancelled.</summary>
    public static AutoDeleteRule? Show(Window? owner, AutoDeleteRule rule, bool editing)
    {
        var d = new AutoDeleteDialog(rule, editing);
        if (owner is { IsVisible: true }) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true ? d._rule : null;
    }

    private void OnChanged(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }

    private AutoDeleteRule Current() => new()
    {
        Id = _rule.Id, Pattern = AutoDelete.NormalisePattern(Pattern.Text) ?? Pattern.Text.Trim(), AccountId = AccountBox.SelectedValue as string ?? "",
        Otp = Otp.IsChecked == true, Amount = _choice.Amount, Unit = _choice.Unit, Paused = _rule.Paused, Created = _rule.Created,
    };

    private int _refreshGen;

    private void Refresh()
    {
        foreach (var c in _chips) c.IsChecked = AfterTime.IsChecked == true && c.Tag is (int a, DeleteUnit u) && (a, u) == _choice;
        TimeGrid.Opacity = AfterTime.IsChecked == true ? 1 : 0.5;
        var r = Current();
        var valid = AutoDelete.NormalisePattern(Pattern.Text) != null;
        PatternError.Visibility = valid || Pattern.Text.Trim().Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PatternError.Text = "Write an address like name@example.com, or *@example.com for everyone there.";
        OkButton.IsEnabled = valid;

        var now = DateTimeOffset.Now;
        var at = AutoDelete.DeleteAt(r, now);
        var who = valid ? r.Pattern : "this sender";
        PreviewText.Text = $"An email from {who} arriving now ({now:d MMM, HH:mm}) will be deleted on {at.ToLocalTime():d MMM yyyy 'at' HH:mm}.";
        PreviewTagText.Text = AutoDelete.TagText(at, now, r.Otp);
        var (bg, fg) = TagColours(AutoDelete.Urgency(at, now, r.Otp));
        PreviewTag.Background = bg;
        PreviewTagText.Foreground = fg;

        // How many emails are already here (counted off the UI thread).
        var gen = ++_refreshGen;
        if (!valid) { Existing.Content = "Also start the timer on the emails already here from this sender"; Existing.IsEnabled = false; return; }
        Task.Run(() => _e.ExistingFor(r).Count).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            Ui.Post(() =>
            {
                if (gen != _refreshGen) return;
                var n = t.Result;
                Existing.IsEnabled = n > 0;
                if (n == 0) Existing.IsChecked = false;
                Existing.Content = n switch
                {
                    0 => "No emails from this sender are here now",
                    1 => "Also start the timer on the 1 email already here from this sender",
                    _ => $"Also start the timer on the {n:#,0} emails already here from this sender",
                };
            });
        }, TaskScheduler.Default);
    }

    /// <summary>Red under 48 h (and every OTP), amber under 30 days, grey later (design AD3).</summary>
    public static (Brush Bg, Brush Fg) TagColours(DeleteUrgency u) => u switch
    {
        DeleteUrgency.Soon => ((Brush)Application.Current.FindResource("Brush.Danger.Subtle"), (Brush)Application.Current.FindResource("Brush.Danger")),
        DeleteUrgency.Weeks => ((Brush)Application.Current.FindResource("Brush.Warning.Subtle"), (Brush)Application.Current.FindResource("Brush.Warning")),
        _ => ((Brush)Application.Current.FindResource("Brush.Badge.Background"), (Brush)Application.Current.FindResource("Brush.Text.Secondary")),
    };

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var r = Current();
        try
        {
            _rule.Pattern = r.Pattern;
            _rule.AccountId = r.AccountId;
            _rule.Otp = r.Otp;
            _rule.Amount = r.Amount;
            _rule.Unit = r.Unit;
            var n = _e.SaveAutoDeleteRule(_rule, Existing.IsChecked == true);
            if (n > 0) Log.Info($"auto-delete: timer started on {n} existing email(s)");
            DialogResult = true;
        }
        catch (ArgumentException ex) { PatternError.Text = ex.Message; PatternError.Visibility = Visibility.Visible; }
        catch (Exception ex) { Log.Error("auto-delete rule", ex); Ui.Error("Auto-delete", ex.Message, this); }
    }
}
