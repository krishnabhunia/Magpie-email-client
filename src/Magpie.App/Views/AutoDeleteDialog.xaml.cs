using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>
/// "Delete emails from…" (design DX1-B1): one dialog for the emails already here (PAST — those older than the kept
/// period go to Trash, with a few seconds to undo) and those arriving later (FUTURE — an auto-delete rule). Also
/// Edit / Change rule (design AD2). The file keeps its AD2 name; the dialog is the DX1 one.
/// </summary>
public partial class AutoDeleteDialog : Window
{
    private readonly MailEngine _e = AppServices.Engine;
    private readonly AutoDeleteRule _rule;
    private readonly bool _editing;
    private (int Amount, DeleteUnit Unit) _choice = (7, DeleteUnit.Days);
    private bool _otp;
    private bool _nothing;
    private readonly List<ToggleButton> _chips = new();
    private ToggleButton? _moreChip;
    private AutoDelete.PastSummary? _summary;
    private bool _ran;

    private AutoDeleteDialog(AutoDeleteRule rule, bool editing, bool past, bool future, bool keepNothing)
    {
        InitializeComponent();
        _rule = rule;
        _editing = editing;
        Heading.Text = editing ? "Change auto-delete rule" : "Delete emails from…";
        Pattern.Text = rule.Pattern;
        _otp = rule.Otp;
        _nothing = keepNothing && !rule.Otp;
        if (!rule.Otp) _choice = (rule.Amount, rule.Unit);
        PastTick.IsChecked = past;
        FutureTick.IsChecked = future;

        // KEEP THE LAST: the common chips, Nothing, OTP, then More… for the rest of the AD2 grid.
        foreach (var (amount, unit) in AutoDelete.KeepChoices)
            KeepChips.Children.Add(Chip(AutoDelete.KeepLabel(false, amount, unit), (amount, unit), () => { _choice = (amount, unit); _otp = false; _nothing = false; }));
        KeepChips.Children.Add(Chip("Nothing", "nothing", () => { _nothing = true; _otp = false; }, "Delete every email already here from them; no rule for later"));
        KeepChips.Children.Add(Chip("OTP · 24 h", "otp", () => { _otp = true; _nothing = false; }, "One-time codes and sign-in links: useful for a day, then clutter"));
        _moreChip = new ToggleButton
        {
            Content = "More…", Style = (Style)FindResource("Button.Chip"), Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2), FontSize = 12.5,
            ToolTip = "Other times: 1 day to 30 years",
        };
        _moreChip.Click += (_, _) => { MoreGrid.Visibility = _moreChip.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; };
        KeepChips.Children.Add(_moreChip);
        foreach (var (amount, unit) in AutoDelete.Choices)
        {
            var chip = Chip(amount.ToString(), (amount, unit), () => { _choice = (amount, unit); _otp = false; _nothing = false; }, AutoDelete.After(amount, unit) + " after it arrives");
            chip.MinWidth = 38;
            (unit switch { DeleteUnit.Days => DaysRow, DeleteUnit.Months => MonthsRow, _ => YearsRow }).Children.Add(chip);
        }
        if (!_otp && !_nothing && !AutoDelete.KeepChoices.Contains(_choice)) { _moreChip.IsChecked = true; MoreGrid.Visibility = Visibility.Visible; }

        var accounts = new List<Choice<string>> { new("", "All my accounts") }.Concat(_e.Accounts.Select(a => new Choice<string>(a.Id, a.Email))).ToList();
        AccountBox.ItemsSource = accounts;
        AccountBox.SelectedValue = accounts.Any(a => a.Value == rule.AccountId) ? rule.AccountId : "";
        Loaded += (_, _) => { Pattern.Focus(); Pattern.CaretIndex = Pattern.Text.Length; Refresh(); };
    }

    private ToggleButton Chip(string text, object tag, Action pick, string? tip = null)
    {
        var chip = new ToggleButton
        {
            Content = text, Style = (Style)FindResource("Button.Chip"), Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2), FontSize = 12.5, Tag = tag, ToolTip = tip,
        };
        chip.Click += (_, _) => { pick(); Refresh(); };
        _chips.Add(chip);
        return chip;
    }

    /// <summary>
    /// Opens the dialog. <paramref name="past"/> / <paramref name="future"/> set the ticks (null = the defaults: a new
    /// rule or an edit starts with FUTURE on, PAST off; the "already here" menu picks pass PAST on). Returns true when
    /// the user went ahead (the rule exists at once; the past delete runs after its undo wait).
    /// </summary>
    public static bool Show(Window? owner, AutoDeleteRule rule, bool editing, bool? past = null, bool? future = null, bool keepNothing = false)
    {
        var d = new AutoDeleteDialog(rule, editing, past ?? false, future ?? true, keepNothing);
        if (owner is { IsVisible: true }) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true && d._ran;
    }

    private void OnChanged(object sender, RoutedEventArgs e) { if (IsLoaded) Refresh(); }

    private string? PatternText => AutoDelete.NormalisePattern(Pattern.Text);
    private string AccountId => AccountBox.SelectedValue as string ?? "";
    private bool PastOn => PastTick.IsChecked == true && PastTick.IsEnabled;
    private bool FutureOn => FutureTick.IsChecked == true && FutureTick.IsEnabled;

    private AutoDeleteRule RuleNow() => new()
    {
        Id = _rule.Id, Pattern = PatternText ?? Pattern.Text.Trim(), AccountId = AccountId,
        Otp = _otp, Amount = _choice.Amount, Unit = _choice.Unit, Paused = _rule.Paused, Created = _rule.Created,
    };

    private int _refreshGen;

    private void Refresh()
    {
        foreach (var c in _chips)
            c.IsChecked = c.Tag switch
            {
                "nothing" => _nothing,
                "otp" => _otp,
                (int a, DeleteUnit u) => !_otp && !_nothing && (a, u) == _choice,
                _ => false,
            };
        var keep = AutoDelete.KeepLabel(_otp, _choice.Amount, _choice.Unit);
        // "Nothing" = delete all past, so there is nothing to keep deleting later; OTP only times new emails.
        FutureTick.IsEnabled = !_nothing;
        PastTick.IsEnabled = !_otp;
        FutureLine.Text = _nothing ? "not with Nothing: pick a time to keep deleting later." : AutoDelete.FutureLine(keep);

        var valid = PatternText != null;
        PatternError.Visibility = valid || Pattern.Text.Trim().Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        PatternError.Text = "Write an address like name@example.com, or *@example.com for everyone there.";

        var now = DateTimeOffset.Now;
        var r = RuleNow();
        if (_nothing) { PreviewBox.Visibility = Visibility.Collapsed; }
        else
        {
            PreviewBox.Visibility = Visibility.Visible;
            var at = AutoDelete.DeleteAt(r, now);
            PreviewText.Text = AutoDelete.PreviewLine(valid ? r.Pattern : "", r, now);
            PreviewTagText.Text = AutoDelete.TagText(at, now, r.Otp);
            var (bg, fg) = TagColours(AutoDelete.Urgency(at, now, r.Otp));
            PreviewTag.Background = bg;
            PreviewTagText.Foreground = fg;
        }

        // The counts (off the UI thread): all from the sender, those older than the kept period, the oldest of those.
        var gen = ++_refreshGen;
        _summary = null;
        if (!valid)
        {
            PatternHint.Text = "An address, or *@domain for everyone there.";
            PastLine.Text = _otp ? "OTP rules only time new emails; pick a time to delete the ones already here." : "delete the ones older than " + keep + " now.";
            UpdateButton();
            return;
        }
        var pattern = r.Pattern;
        var account = AccountId;
        var cut = _nothing ? (DateTimeOffset?)null : AutoDelete.KeepSince(_otp, _choice.Amount, _choice.Unit, now);
        var nothing = _nothing;
        var otp = _otp;
        PastLine.Text = "counting…";
        UpdateButton();
        Task.Run(() => _e.PastSummary(pattern, account, cut)).ContinueWith(t =>
        {
            if (t.IsFaulted) { Log.Warn("delete from: count failed: " + t.Exception?.GetBaseException().Message); return; }
            Ui.Post(() =>
            {
                if (gen != _refreshGen) return;
                _summary = t.Result;
                PatternHint.Text = "An address, or *@domain for everyone there. " + (t.Result.Total == 0 ? "No emails here from them." : $"{t.Result.Total:#,0} email{(t.Result.Total == 1 ? "" : "s")} here from them.");
                PastLine.Text = otp ? "OTP rules only time new emails; pick a time to delete the ones already here." : AutoDelete.PastLine(t.Result, nothing, keep);
                UpdateButton();
            });
        }, TaskScheduler.Default);
    }

    /// <summary>The button follows the ticks: "Delete 212 now" / "Create rule" / "Delete 212 now and keep deleting"; red when deleting.</summary>
    private void UpdateButton()
    {
        var count = _summary?.Older ?? 0;
        var text = AutoDelete.ButtonText(PastOn, FutureOn, count, _editing);
        OkButton.Content = text.Length > 0 ? text : "Create rule";
        OkButton.Style = (Style)FindResource(PastOn && count > 0 ? "Button.Danger" : "Button.Primary");
        OkButton.IsEnabled = text.Length > 0 && PatternText != null;
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
        var pattern = PatternText;
        if (pattern == null || (!PastOn && !FutureOn)) return;
        var req = new AutoDelete.DeleteFromRequest
        {
            Pattern = pattern, AccountId = AccountId, Past = PastOn, Future = FutureOn, KeepNothing = _nothing, Otp = _otp,
            Amount = _choice.Amount, Unit = _choice.Unit, RuleId = _editing ? _rule.Id : null, StartOnExisting = _e.Config.AutoDeleteIncludePast,
        };
        try
        {
            // The main window runs it: its toast carries Undo (the past delete waits 8 s) and Edit rule.
            if (AppServices.Main is { } main) main.RunDeleteFrom(req);
            else
            {
                var r = _e.ApplyDeleteFrom(req, DateTimeOffset.Now);
                if (r.Past.Count > 0) _e.TrashEmails(r.Past.Rows);
            }
            _ran = true;
            DialogResult = true;
        }
        catch (ArgumentException ex) { PatternError.Text = ex.Message; PatternError.Visibility = Visibility.Visible; }
        catch (Exception ex) { Log.Error("delete emails from", ex); Ui.Error("Delete", ex.Message, this); }
    }
}
