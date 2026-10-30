using System.Windows;
using System.Windows.Controls;
using Magpie.Core.Settings;

namespace Magpie.App.Views;

/// <summary>
/// Design SY1·3 (new PC): "Your other accounts are in this mailbox". Asks for the sync password (the copy says which PC
/// left it and when, without the password), then lists every account, all ticked; accounts already on this PC are
/// greyed. Returns the unlocked copy, the password and the ticked addresses, or null for "Not now — only this account".
/// </summary>
public static class AccountsFoundDialog
{
    public sealed record Choice(AccountsCopy.Contents Copy, string Password, List<string> Emails);

    public static Choice? Ask(Window? owner, byte[] file, string homeEmail, IReadOnlyCollection<string> alreadyHere)
    {
        var summary = AccountsCopy.Peek(file);
        var w = new Window { Title = "Your other accounts — Magpie", Width = 500, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize };
        w.Style = (Style)Application.Current.FindResource("Window.Dialog");
        if (owner is { IsVisible: true }) { w.Owner = owner; w.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Style S(string key) => (Style)Application.Current.FindResource(key);

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        root.Children.Add(new TextBlock { Text = "Your other accounts are in this mailbox", Style = S("Text.Serif"), FontSize = 17 });
        var when = summary is { Updated: var u } && u != default ? Describe(u.ToLocalTime()) : "";
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 14),
            Text = $"Magpie{(summary?.Pc is { Length: > 0 } pc ? " on " + pc : "")} left a locked copy in {homeEmail}"
                 + (when.Length > 0 ? $" (updated {when})" : "") + (summary is { Accounts: > 0 } ? $": {summary.Accounts} account(s)." : ".")
                 + " Type the sync password to add them here too.",
        });

        // Step 1: the password.
        var step1 = new StackPanel();
        step1.Children.Add(new TextBlock { Text = "Sync password", Style = S("Text.Caption"), Margin = new Thickness(0, 0, 0, 5) });
        var pwd = new PasswordBox { Padding = new Thickness(8, 6, 8, 6) };
        step1.Children.Add(pwd);
        var hint = new TextBlock { Style = S("Text.Caption"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        step1.Children.Add(hint);
        root.Children.Add(step1);

        // Step 2: the accounts, all ticked.
        var step2 = new StackPanel { Visibility = Visibility.Collapsed };
        var list = new StackPanel();
        step2.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        root.Children.Add(step2);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var notNow = new Button { Content = "Not now — only this account", IsCancel = true, Margin = new Thickness(0, 0, 8, 0), Style = S("Button.Secondary") };
        var ok = new Button { Content = "Open", IsDefault = true, Style = S("Button.Primary"), IsEnabled = false };
        buttons.Children.Add(notNow);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        AccountsCopy.Contents? copy = null;
        string password = "";
        var boxes = new List<(CheckBox Box, string Email)>();
        void Count()
        {
            var n = boxes.Count(b => b.Box.IsEnabled && b.Box.IsChecked == true);
            ok.Content = n == 0 ? "Add" : n == 1 ? "Add 1 account" : $"Add {n} accounts";
            ok.IsEnabled = n > 0;
        }
        pwd.PasswordChanged += (_, _) => { ok.IsEnabled = pwd.Password.Length > 0; hint.Text = ""; };
        Choice? result = null;
        ok.Click += (_, _) =>
        {
            if (copy == null)
            {
                try { copy = AccountsCopy.Unlock(file, pwd.Password); password = pwd.Password; }
                catch (AccountsCopy.WrongPasswordException) { hint.Text = "That password doesn't open this copy. Try again."; pwd.SelectAll(); pwd.Focus(); return; }
                catch (Exception ex) { hint.Text = ex.Message; return; }
                foreach (var a in copy.Accounts)
                {
                    var here = alreadyHere.Contains(a.Email, StringComparer.OrdinalIgnoreCase) || a.Email == AccountsCopy.Key(homeEmail);
                    var box = new CheckBox
                    {
                        IsChecked = true, IsEnabled = !here, Margin = new Thickness(0, 3, 0, 3),
                        Content = new TextBlock { Text = a.Account.Label + (here ? "  (already here)" : ""), TextTrimming = TextTrimming.CharacterEllipsis },
                    };
                    box.Checked += (_, _) => Count();
                    box.Unchecked += (_, _) => Count();
                    boxes.Add((box, a.Email));
                    list.Children.Add(box);
                }
                step1.Visibility = Visibility.Collapsed;
                step2.Visibility = Visibility.Visible;
                Count();
                if (!ok.IsEnabled) { result = new Choice(copy, password, new()); w.DialogResult = true; }   // nothing else to add
                return;
            }
            result = new Choice(copy, password, boxes.Where(b => b.Box.IsEnabled && b.Box.IsChecked == true).Select(b => b.Email).ToList());
            w.DialogResult = true;
        };
        w.Content = root;
        w.Loaded += (_, _) => pwd.Focus();
        w.ShowDialog();
        return result;
    }

    private static string Describe(DateTimeOffset t)
    {
        var d = t.Date;
        var today = DateTime.Today;
        return (d == today ? "today" : d == today.AddDays(-1) ? "yesterday" : t.ToString("d MMM")) + " " + t.ToString("HH:mm");
    }
}
