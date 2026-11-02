using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Settings;

namespace Magpie.App.Views;

/// <summary>
/// Design SY1·1 (Settings → Accounts): "Bring my accounts to other PCs". Off by default. Turning it on picks the home
/// account, the sync password (10+ characters, typed twice, strength line; Magpie can't recover it) and what comes along.
/// On: the status line ("✓ Copy updated 10:42 · 17 accounts · 46 KB · used by HP-OMEN, NEW-PC"), Update the copy now,
/// Change sync password, Stop and delete the copy, and accounts from other PCs waiting to be added here.
/// Its actions take effect at once (like the backup card), not with Apply.
/// </summary>
public sealed class AccountsCopyCard : Border
{
    private readonly StackPanel _root = new();
    private static Style S(string key) => (Style)Application.Current.FindResource(key);
    private static MailEngine E => AppServices.Engine;

    public AccountsCopyCard()
    {
        Style = S("Card");
        Child = _root;
        Loaded += (_, _) => { E.AccountsCopy.StatusChanged += OnStatus; E.AccountsCopy.AccountsAvailable += OnAvailable; Build(); };
        Unloaded += (_, _) => { E.AccountsCopy.StatusChanged -= OnStatus; E.AccountsCopy.AccountsAvailable -= OnAvailable; };
    }

    private void OnStatus() => Ui.Post(Build);
    private void OnAvailable(IReadOnlyList<AccountsCopy.CopiedAccount> _) => Ui.Post(Build);

    private TextBlock Caption(string text, Thickness? margin = null) =>
        new() { Text = text, Style = S("Text.Caption"), TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 0, 0, 8) };

    private void Build()
    {
        _root.Children.Clear();
        _root.Children.Add(new TextBlock { Text = "Bring my accounts to other PCs", FontWeight = FontWeights.Medium, FontSize = 13.5 });
        _root.Children.Add(Caption("Magpie keeps a locked copy of your accounts in one mailbox (the home account), in a hidden \"Magpie Sync\" folder. "
            + "On another PC you sign in to that account once, type the sync password, and every account comes along. Emails are never in the copy.",
            new Thickness(0, 4, 0, 12)));
        if (E.Accounts.Count == 0) { _root.Children.Add(Caption("Add an account first.")); return; }
        if (E.Config.AccountsCopy.Enabled) BuildOn(); else BuildOff();
    }

    // ───────────────────────── off: turn it on ─────────────────────────

    private void BuildOff()
    {
        var o = E.Config.AccountsCopy;
        _root.Children.Add(Caption("Home account (the copy is kept in its mailbox)", new Thickness(0, 0, 0, 4)));
        var home = new ComboBox { ItemsSource = E.Accounts.Select(a => a.Email).ToList(), SelectedIndex = 0, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        _root.Children.Add(home);

        _root.Children.Add(Caption("Sync password (at least 10 characters; Magpie can't recover it, so keep it safe)", new Thickness(0, 0, 0, 4)));
        var p1 = new PasswordBox { Padding = new Thickness(8, 6, 8, 6), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch };
        var p2 = new PasswordBox { Padding = new Thickness(8, 6, 8, 6), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 6, 0, 0) };
        var strength = Caption("", new Thickness(0, 6, 0, 12));
        _root.Children.Add(p1);
        _root.Children.Add(Caption("Type it again", new Thickness(0, 8, 0, 0)));
        _root.Children.Add(p2);
        _root.Children.Add(strength);

        _root.Children.Add(Caption("What comes along", new Thickness(0, 0, 0, 4)));
        _root.Children.Add(new CheckBox { Content = "Accounts and their sign-ins (always)", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 2, 0, 2) });
        var settings = new CheckBox { Content = "Settings: rules, signatures, quick replies, templates, tags, Gatekeeper lists, look", IsChecked = o.IncludeSettings, Margin = new Thickness(0, 2, 0, 2) };
        var ai = new CheckBox { Content = "AI connections and their keys", IsChecked = o.IncludeAiKeys, Margin = new Thickness(0, 2, 0, 2) };
        var auto = new CheckBox { Content = "Add accounts from my other PCs here by themselves", IsChecked = o.AutoAdd, Margin = new Thickness(0, 2, 0, 12) };
        _root.Children.Add(settings);
        _root.Children.Add(ai);
        _root.Children.Add(auto);

        var turnOn = new Button { Content = "Turn on", Style = S("Button.Primary"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        _root.Children.Add(turnOn);
        void Check()
        {
            var (score, text) = AccountsCopy.Strength(p1.Password);
            strength.Text = p1.Password.Length == 0 ? "" : p2.Password.Length > 0 && p2.Password != p1.Password ? "The two don't match yet." : text;
            strength.Foreground = score <= 1 && p1.Password.Length > 0 ? (Brush)Application.Current.FindResource("Brush.Warning") : (Brush)Application.Current.FindResource("Brush.Text.Secondary");
            turnOn.IsEnabled = score > 0 && p1.Password == p2.Password;
        }
        p1.PasswordChanged += (_, _) => Check();
        p2.PasswordChanged += (_, _) => Check();
        turnOn.Click += async (_, _) =>
        {
            if (home.SelectedItem is not string homeEmail) return;
            turnOn.IsEnabled = false;
            turnOn.Content = "Looking in the mailbox…";
            try
            {
                // When the mailbox already has a copy (made on another PC), this PC joins it with that copy's password.
                await E.AccountsCopy.EnableAsync(homeEmail, p1.Password, settings.IsChecked == true, ai.IsChecked == true, auto.IsChecked == true, CancellationToken.None);
                Log.Info("accounts copy turned on");
                Build();
            }
            catch (Exception ex)
            {
                Ui.Error("Bring my accounts to other PCs", ex is InvalidOperationException or ArgumentException ? ex.Message : Core.Mail.Connector.Friendly(ex), Window.GetWindow(this));
                turnOn.Content = "Turn on";
                Check();
            }
        };
    }

    // ───────────────────────── on: status and actions ─────────────────────────

    private void BuildOn()
    {
        var o = E.Config.AccountsCopy;
        var svc = E.AccountsCopy;
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), FontSize = 13 };
        if (svc.Problem.Length > 0)
        {
            status.Text = "⚠ " + svc.Problem;
            status.Foreground = (Brush)Application.Current.FindResource("Brush.Warning");
        }
        else if (o.CopyUpdated is { } t)
        {
            var parts = new List<string> { $"✓ Copy updated {t.ToLocalTime():HH:mm}{(t.ToLocalTime().Date != DateTime.Today ? t.ToLocalTime().ToString(" d MMM") : "")}", $"{o.CopyAccounts} account(s)", $"{Math.Max(1, o.CopySize / 1024)} KB" };
            if (o.UsedBy.Count > 0) parts.Add("used by " + string.Join(", ", o.UsedBy));
            status.Text = string.Join(" · ", parts);
        }
        else status.Text = "Making the copy… (it's written within a minute)";
        _root.Children.Add(status);
        if (svc.HeldBack) _root.Children.Add(Caption("Changes from your other PCs come in when this Settings window is closed."));
        if (o.TwoCopies.Length > 0)
        {
            var warn = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            var ok = new Button { Content = "OK", Style = S("Button.Link"), Padding = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            ok.Click += (_, _) => { o.TwoCopies = ""; E.Settings.Save(notify: false); Build(); };
            DockPanel.SetDock(ok, Dock.Right);
            warn.Children.Add(ok);
            warn.Children.Add(new TextBlock { Text = "⚠ " + o.TwoCopies, TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = (Brush)Application.Current.FindResource("Brush.Warning") });
            _root.Children.Add(warn);
        }
        _root.Children.Add(Caption($"Home account: {o.HomeEmail} · this PC is called {svc.PcName} in the copy."));

        var settings = new CheckBox { Content = "Settings come along (rules, signatures, quick replies, templates, tags, Gatekeeper lists, look)", IsChecked = o.IncludeSettings, Margin = new Thickness(0, 2, 0, 2) };
        var ai = new CheckBox { Content = "AI connections and their keys come along", IsChecked = o.IncludeAiKeys, Margin = new Thickness(0, 2, 0, 2) };
        var auto = new CheckBox { Content = "Add accounts from my other PCs here by themselves", IsChecked = o.AutoAdd, Margin = new Thickness(0, 2, 0, 12) };
        void Choices() => E.AccountsCopy.SetChoices(settings.IsChecked == true, ai.IsChecked == true, auto.IsChecked == true);
        foreach (var box in new[] { settings, ai, auto }) { box.Checked += (_, _) => Choices(); box.Unchecked += (_, _) => Choices(); }
        _root.Children.Add(settings);
        _root.Children.Add(ai);
        _root.Children.Add(auto);

        if (svc.NeedsPassword)
        {
            var type = new Button { Content = "Type the sync password…", Style = S("Button.Primary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
            type.Click += (_, _) => AccountsCopyUi.AskPassword(Window.GetWindow(this));
            _root.Children.Add(type);
        }

        var waiting = svc.Available.Where(c => E.Accounts.All(a => !a.Email.Equals(c.Account.Email, StringComparison.OrdinalIgnoreCase))).ToList();
        if (waiting.Count > 0)
        {
            _root.Children.Add(new TextBlock { Text = "From your other PCs, not on this PC", FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 4) });
            foreach (var c in waiting)
            {
                var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
                var add = new Button { Content = "Add", Style = S("Button.Link"), Padding = new Thickness(6, 0, 0, 0) };
                var item = c;
                add.Click += (_, _) => { AddingAccountsWindow.ShowFor(E.AccountsCopy.AddWaiting(new[] { item })); Build(); };
                DockPanel.SetDock(add, Dock.Right);
                row.Children.Add(add);
                row.Children.Add(new TextBlock { Text = c.Account.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                _root.Children.Add(row);
            }
            if (waiting.Count > 1)
            {
                var all = new Button { Content = $"Add all {waiting.Count}", Style = S("Button.Secondary"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 12) };
                all.Click += (_, _) => { AddingAccountsWindow.ShowFor(E.AccountsCopy.AddWaiting(waiting)); Build(); };
                _root.Children.Add(all);
            }
        }

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        var now = new Button { Content = svc.Missing ? "Make a new copy from this PC" : "Update the copy now", Style = S(svc.Missing ? "Button.Primary" : "Button.Secondary"), Margin = new Thickness(0, 0, 8, 6) };
        now.Click += async (_, _) =>
        {
            now.IsEnabled = false;
            try { await E.AccountsCopy.RunOnceAsync(CancellationToken.None, rebuild: svc.Missing); }
            catch (Exception ex) { Ui.Error("Update the copy now", Core.Mail.Connector.Friendly(ex), Window.GetWindow(this)); }
            finally { Build(); }
        };
        var change = new Button { Content = "Change sync password…", Style = S("Button.Secondary"), Margin = new Thickness(0, 0, 8, 6) };
        change.Click += (_, _) =>
        {
            var p = PasswordDialog.Ask(Window.GetWindow(this), "Change the sync password",
                "The copy is locked with the new password from its next update. Magpie on your other PCs asks for it once.",
                confirm: true, AccountsCopy.MinPasswordLength, "Change");
            if (p != null) { E.AccountsCopy.ChangePassword(p); Log.Info("accounts copy: new sync password chosen"); }
        };
        var stop = new Button { Content = "Stop and delete the copy", Style = S("Button.Secondary"), Margin = new Thickness(0, 0, 8, 6) };
        stop.Click += async (_, _) =>
        {
            if (!Ui.Confirm("Stop and delete the copy",
                "Your accounts and sign-ins are taken out of the home mailbox (a short \"stopped\" note stays, with nothing else in it). Magpie on your other PCs stops too and keeps its accounts. Your accounts on this PC stay as they are.",
                Window.GetWindow(this))) return;
            stop.IsEnabled = false;
            try { await E.AccountsCopy.DisableAsync(deleteCopy: true, CancellationToken.None); }
            catch (Exception ex)
            {
                // Offline, or another PC changed the sync password: the copy stays as it is. Stop here only, if wanted.
                if (Ui.Confirm("Stop and delete the copy", Core.Mail.Connector.Friendly(ex) + "\n\nThe copy couldn't be deleted now. Stop it on this PC only? It stays in the mailbox for your other PCs.", Window.GetWindow(this)))
                    await E.AccountsCopy.DisableAsync(deleteCopy: false, CancellationToken.None);
            }
            Build();
        };
        buttons.Children.Add(now);
        buttons.Children.Add(change);
        buttons.Children.Add(stop);
        _root.Children.Add(buttons);
    }
}
