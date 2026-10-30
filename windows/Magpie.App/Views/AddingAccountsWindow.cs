using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Magpie.App.Services;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>
/// Design SY1·4: "Adding accounts · 9 of 16 ready". One line per account: ✓ Ready / Getting Inbox · n of N / Waiting /
/// Type password / Sign in again. First downloads run 4 at a time; one account's problem never stops the others.
/// Closing the window doesn't stop anything.
/// </summary>
public sealed class AddingAccountsWindow : Window
{
    private static AddingAccountsWindow? _open;
    private readonly List<Account> _accounts = new();
    private readonly StackPanel _list = new();
    private readonly TextBlock _title = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Shows the window (or adds the accounts to the one already open).</summary>
    public static void ShowFor(IReadOnlyList<Account> accounts)
    {
        if (accounts.Count == 0) return;
        if (_open == null)
        {
            _open = new AddingAccountsWindow();
            var owner = Application.Current.MainWindow;
            if (owner is { IsVisible: true }) { _open.Owner = owner; _open.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            else _open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            _open.Closed += (_, _) => _open = null;
            _open.Show();
        }
        foreach (var a in accounts.Where(a => _open._accounts.All(x => x.Id != a.Id))) _open._accounts.Add(a);
        _open.Refresh();
    }

    /// <summary>Status bar (design SY1·4): "Adding accounts · 9 of 16 ready" while the window's accounts are coming in.</summary>
    public static string StatusText
    {
        get
        {
            if (_open == null) return "";
            var e = AppServices.Engine;
            var ready = _open._accounts.Count(a => e.IsReady(a.Id));
            return ready >= _open._accounts.Count ? "" : $"Adding accounts · {ready} of {_open._accounts.Count} ready";
        }
    }

    private AddingAccountsWindow()
    {
        Title = "Adding accounts — Magpie";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        Style = (Style)Application.Current.FindResource("Window.Dialog");
        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        _title.Style = (Style)Application.Current.FindResource("Text.Serif");
        _title.FontSize = 17;
        root.Children.Add(_title);
        root.Children.Add(new TextBlock
        {
            Text = "Each account starts with its Inbox, 4 at a time. You can read mail from the first one that's ready; closing this window doesn't stop anything.",
            Style = (Style)Application.Current.FindResource("Text.Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        });
        root.Children.Add(new ScrollViewer { Content = _list, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Style = (Style)Application.Current.FindResource("Button.Secondary") };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>What one account is doing, in the window's words.</summary>
    internal static (string Text, string Action) Line(Account a)
    {
        var e = AppServices.Engine;
        if (e.IsReady(a.Id)) return ("✓ Ready", "");
        if (e.IsWaiting(a.Id)) return ("Waiting", "");
        var st = e.StatusOf(a.Id);
        return st?.State switch
        {
            SyncState.NeedsSignIn => (a.Auth == AuthMethod.OAuth2 ? "Google or Microsoft refused the copied sign-in" : "The saved password no longer works",
                                      a.Auth == AuthMethod.OAuth2 ? "Sign in again" : "Type password"),
            SyncState.Offline => ("Offline — tries again by itself", ""),
            SyncState.Error => (st.Message, ""),
            _ when st is { Total: > 0 } => ($"Getting {(st.Folder.Length > 0 ? st.Folder : "Inbox")} · {st.Done:#,0} of {st.Total:#,0}", ""),
            _ => ("Getting Inbox…", ""),
        };
    }

    private void Refresh()
    {
        var e = AppServices.Engine;
        _accounts.RemoveAll(a => e.AccountById(a.Id) == null);
        var ready = _accounts.Count(a => e.IsReady(a.Id));
        _title.Text = ready >= _accounts.Count ? $"{_accounts.Count} account(s) added" : $"Adding accounts · {ready} of {_accounts.Count} ready";
        _list.Children.Clear();
        foreach (var a in _accounts)
        {
            var (text, action) = Line(a);
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            if (action.Length > 0)
            {
                var b = new Button { Content = action, Style = (Style)Application.Current.FindResource("Button.Link"), Padding = new Thickness(6, 0, 0, 0) };
                var acc = e.AccountById(a.Id) ?? a;
                b.Click += (_, _) => AddAccountWindow.ShowReauth(this, acc);
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
            }
            var status = new TextBlock { Text = text, Style = (Style)Application.Current.FindResource("Text.Caption"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(status, Dock.Right);
            row.Children.Add(status);
            row.Children.Add(new TextBlock { Text = a.Email, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            _list.Children.Add(row);
        }
    }
}
