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
            return _open._accounts.All(Settled) ? "" : $"Adding accounts · {ready} of {_open._accounts.Count} ready";
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
        var now = e.AccountById(a.Id) ?? a;
        if (!now.Enabled) return ("Off (switched off in Settings)", "");
        if (e.IsReady(a.Id)) return ("✓ Ready", "");
        if (e.IsWaiting(a.Id)) return ("Waiting", "");
        var st = e.StatusOf(a.Id);
        return st?.State switch
        {
            SyncState.NeedsSignIn => (now.Auth == AuthMethod.OAuth2 ? "Google or Microsoft refused the copied sign-in" : "The saved password no longer works",
                                      now.Auth == AuthMethod.OAuth2 ? "Sign in again" : "Type password"),
            SyncState.Offline => ("Offline — tries again by itself", ""),
            SyncState.Error => (st.Message, ""),
            _ when st is { Total: > 0 } => ($"Getting {(st.Folder.Length > 0 ? st.Folder : "Inbox")} · {st.Done:#,0} of {st.Total:#,0}", ""),
            _ => ("Getting Inbox…", ""),
        };
    }

    /// <summary>Done = ready, switched off, or waiting for the user (sign in again): nothing more happens by itself.</summary>
    private static bool Settled(Account a)
    {
        var e = AppServices.Engine;
        var now = e.AccountById(a.Id);
        return now == null || !now.Enabled || e.IsReady(a.Id) || e.StatusOf(a.Id)?.State == SyncState.NeedsSignIn;
    }

    private sealed class Row
    {
        public DockPanel Panel = new() { Margin = new Thickness(0, 3, 0, 3) };
        public TextBlock Status = new() { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        public Button Action = new() { Padding = new Thickness(6, 0, 0, 0), Visibility = Visibility.Collapsed };
    }

    private readonly Dictionary<string, Row> _rows = new();

    // Rows are made once and only their words change, so a click on "Sign in again" is never lost to a redraw.
    private void Refresh()
    {
        var e = AppServices.Engine;
        _accounts.RemoveAll(a => e.AccountById(a.Id) == null);
        foreach (var gone in _rows.Keys.Where(id => _accounts.All(a => a.Id != id)).ToList()) { _list.Children.Remove(_rows[gone].Panel); _rows.Remove(gone); }
        foreach (var a in _accounts)
        {
            if (!_rows.TryGetValue(a.Id, out var row))
            {
                row = new Row();
                row.Status.Style = (Style)Application.Current.FindResource("Text.Caption");
                row.Action.Style = (Style)Application.Current.FindResource("Button.Link");
                var id = a.Id;
                row.Action.Click += (_, _) => { if (e.AccountById(id) is { } acc) AddAccountWindow.ShowReauth(this, acc); };
                DockPanel.SetDock(row.Action, Dock.Right);
                DockPanel.SetDock(row.Status, Dock.Right);
                row.Panel.Children.Add(row.Action);
                row.Panel.Children.Add(row.Status);
                row.Panel.Children.Add(new TextBlock { Text = a.Email, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                _rows[a.Id] = row;
                _list.Children.Add(row.Panel);
            }
            var (text, action) = Line(a);
            if (!Equals(row.Status.Text, text)) row.Status.Text = text;
            row.Action.Content = action;
            row.Action.Visibility = action.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        var ready = _accounts.Count(a => e.IsReady(a.Id));
        _title.Text = _accounts.All(Settled) ? $"{_accounts.Count} account(s) added" : $"Adding accounts · {ready} of {_accounts.Count} ready";
    }
}
