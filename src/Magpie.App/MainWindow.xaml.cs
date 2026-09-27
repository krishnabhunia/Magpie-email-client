using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Microsoft.Web.WebView2.Core;

namespace Magpie.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _webReady;
    private string? _pendingUrl;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.Reader.PageReady += url => Ui.Post(() => ShowPage(url));
        RestorePlacement();
        StateChanged += (_, _) => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += async (_, _) => await InitWebAsync();
        Closing += OnClosing;
    }

    // ───────────────────────── WebView2 reading pane ─────────────────────────

    private async Task InitWebAsync()
    {
        try
        {
            if (!await WebHost.InitAsync(Web, scripts: true))
            {
                Web.Visibility = Visibility.Collapsed;
                NoRuntime.Visibility = Visibility.Visible;
                return;
            }
            var core = Web.CoreWebView2;
            core.NavigationStarting += (_, e) =>
            {
                if (WebHost.IsOwnPage(e.Uri)) return;
                e.Cancel = true;
                _vm.Reader.OnLink(e.Uri);
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                _vm.Reader.OnLink(e.Uri);
            };
            core.WebMessageReceived += OnWebMessage;
            core.ContextMenuRequested += (_, e) =>
            {
                // Keep copy/select-all; drop navigation items that make no sense in a mail view.
                foreach (var item in e.MenuItems.ToList())
                    if (item.Name is "back" or "forward" or "reload" or "saveAs" or "print" or "inspectElement" or "other")
                        e.MenuItems.Remove(item);
            };
            _webReady = true;
            ShowPage(_pendingUrl ?? WebHost.Publish(HtmlRenderer.Placeholder("Welcome to Magpie", "Pick a conversation to read it here."), "view"));
        }
        catch (Exception ex)
        {
            Log.Error("WebView2 init failed", ex);
            Web.Visibility = Visibility.Collapsed;
            NoRuntime.Visibility = Visibility.Visible;
        }
    }

    private void ShowPage(string url)
    {
        if (!_webReady) { _pendingUrl = url; return; }
        try { Web.CoreWebView2.Navigate(url); } catch (Exception ex) { Log.Warn("navigate: " + ex.Message); }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            var t = root.GetProperty("t").GetString();
            long id = root.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
            switch (t)
            {
                case "link": _vm.Reader.OnLink(root.GetProperty("href").GetString() ?? ""); break;
                case "reply": _vm.Reader.Compose(ComposeMode.Reply, id); break;
                case "replyall": _vm.Reader.Compose(ComposeMode.ReplyAll, id); break;
                case "forward": _vm.Reader.Compose(ComposeMode.Forward, id); break;
                case "att": _ = _vm.Reader.OpenAttachmentAsync(id, root.GetProperty("i").GetInt32()); break;
            }
        }
        catch (Exception ex) { Log.Warn("web message: " + ex.Message); }
    }

    private void OnGetWebView2(object sender, RoutedEventArgs e) =>
        Ui.OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");

    // ───────────────────────── public API for tray / notifications ─────────────────────────

    public void OpenThread(string accountId, string threadKey)
    {
        _vm.SelectByKey(accountId, threadKey);
    }

    // ───────────────────────── toolbar menus ─────────────────────────

    private static void ShowMenu(FrameworkElement anchor, ContextMenu menu)
    {
        menu.PlacementTarget = anchor;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action onClick, string? gesture = null, bool isChecked = false, bool enabled = true)
    {
        var mi = new MenuItem { Header = header, IsChecked = isChecked, IsEnabled = enabled };
        if (gesture != null) mi.InputGestureText = gesture;
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private void OnSnoozeMenu(object sender, RoutedEventArgs e)
    {
        if (!_vm.Reader.HasThread) return;
        var menu = new ContextMenu();
        foreach (var p in TimePresets.For(DateTime.Now))
            menu.Items.Add(Item($"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}", () => _vm.Reader.Snooze(p.When)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Pick date & time…", () =>
        {
            var when = PickTimeDialog.Ask(this, "Snooze until", "The conversation leaves your inbox and comes back at this time.", DateTime.Now.AddDays(1).Date.AddHours(8));
            if (when != null) _vm.Reader.Snooze(when.Value);
        }));
        if (_vm.Reader.IsSnoozed) menu.Items.Add(Item("Unsnooze now", () => _vm.Reader.UnsnoozeCommand.Execute(null)));
        ShowMenu((FrameworkElement)sender, menu);
    }

    private void OnRemindMenu(object sender, RoutedEventArgs e)
    {
        if (!_vm.Reader.HasThread) return;
        var ifNoReply = _vm.Reader.LatestIsMine;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = ifNoReply ? "Remind me if nobody replies by…" : "Bring this back to the top on…", IsEnabled = false });
        foreach (var p in TimePresets.For(DateTime.Now))
            menu.Items.Add(Item($"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}", () => _vm.Reader.RemindMe(p.When, ifNoReply)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Pick date & time…", () =>
        {
            var when = PickTimeDialog.Ask(this, "Remind me", ifNoReply ? "If nobody has replied by then, the conversation comes back to the top of your inbox." : "The conversation comes back to the top of your inbox at this time.", DateTime.Now.AddDays(2).Date.AddHours(9));
            if (when != null) _vm.Reader.RemindMe(when.Value, ifNoReply);
        }));
        ShowMenu((FrameworkElement)sender, menu);
    }

    private void OnTagMenu(object sender, RoutedEventArgs e)
    {
        if (!_vm.Reader.HasThread) return;
        var current = _vm.Reader.CurrentTags;
        var menu = new ContextMenu();
        foreach (var t in AppServices.Engine.Config.Tags)
            menu.Items.Add(Item(t.Name, () => _vm.Reader.ToggleTag(t.Name), isChecked: current.Contains(t.Name)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Manage tags…", () => SettingsWindow.Open("General")));
        ShowMenu((FrameworkElement)sender, menu);
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (!_vm.Reader.HasThread) return;
        var r = _vm.Reader;
        var menu = new ContextMenu();
        menu.Items.Add(Item("Reply all", () => r.ReplyAllCommand.Execute(null), "A"));
        menu.Items.Add(Item("Forward", () => r.ForwardCommand.Execute(null), "F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(r.IsPinned ? "Unpin" : "Pin", () => r.TogglePinCommand.Execute(null), "P"));
        menu.Items.Add(Item("Mark as unread", () => r.MarkUnreadCommand.Execute(null), "U"));

        var move = new MenuItem { Header = "Move to" };
        foreach (var f in AppServices.Engine.Folders(r.AccountId).Where(f => f.Role is not (FolderRole.All or FolderRole.Flagged or FolderRole.Important)))
        {
            var folder = f;
            move.Items.Add(Item(new string(' ', Math.Min(f.Depth, 4) * 2) + f.Name, () => r.MoveTo(folder)));
        }
        menu.Items.Add(move);

        if (r.SenderAddress is { } sender0)
        {
            var cat = new MenuItem { Header = "Smart inbox: always put " + sender0 + " in" };
            cat.Items.Add(Item("People", () => r.SetSenderCategory(Category.People)));
            cat.Items.Add(Item("Notifications", () => r.SetSenderCategory(Category.Notifications)));
            cat.Items.Add(Item("Newsletters", () => r.SetSenderCategory(Category.Newsletters)));
            menu.Items.Add(cat);
        }
        if (r.CanUnsubscribe) menu.Items.Add(Item("Unsubscribe…", () => r.UnsubscribeCommand.Execute(null)));
        ShowMenu((FrameworkElement)sender, menu);
    }

    // List context menu
    private void OnArchive(object sender, RoutedEventArgs e) => _vm.Reader.ArchiveCommand.Execute(null);
    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected?.LocalDraftId != null) _vm.DeleteSelectedLocalDraft();
        else _vm.Reader.DeleteCommand.Execute(null);
    }
    private void OnPin(object sender, RoutedEventArgs e) => _vm.Reader.TogglePinCommand.Execute(null);
    private void OnMarkUnread(object sender, RoutedEventArgs e) => _vm.Reader.MarkUnreadCommand.Execute(null);
    private void OnReply(object sender, RoutedEventArgs e) => _vm.Reader.ReplyCommand.Execute(null);
    private void OnReplyAll(object sender, RoutedEventArgs e) => _vm.Reader.ReplyAllCommand.Execute(null);
    private void OnForward(object sender, RoutedEventArgs e) => _vm.Reader.ForwardCommand.Execute(null);

    // ───────────────────────── keyboard ─────────────────────────

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.N) { _vm.ComposeCommand.Execute(null); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (e.Key == Key.F5) { _vm.SyncAllCommand.Execute(null); e.Handled = true; return; }
        if (Keyboard.FocusedElement is TextBox) return;
        // Sidebar headings and accounts (design Q2): ← closes, → opens the focused one.
        if (e.Key is Key.Left or Key.Right && Keyboard.FocusedElement is ToggleButton section && section.Style is { } st
            && (ReferenceEquals(st, TryFindResource("Toggle.SectionHeader")) || ReferenceEquals(st, TryFindResource("Toggle.Account"))))
        {
            section.IsChecked = e.Key == Key.Right;
            e.Handled = true;
            return;
        }
        // A draft kept on this PC opens in a compose window on Enter (review #5: J/K only select it).
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && ThreadList.IsKeyboardFocusWithin && _vm.Selected?.LocalDraftId != null)
        {
            _vm.OpenSelectedLocalDraft();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None && _vm.Selected?.LocalDraftId != null)
        {
            _vm.DeleteSelectedLocalDraft();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;
        var r = _vm.Reader;
        switch (e.Key)
        {
            case Key.OemQuestion when Keyboard.Modifiers == ModifierKeys.None: SearchBox.Focus(); break;
            case Key.J: _vm.MoveSelection(1); break;
            case Key.K: _vm.MoveSelection(-1); break;
            case Key.E when r.HasThread: r.ArchiveCommand.Execute(null); break;
            case Key.Delete when r.HasThread: r.DeleteCommand.Execute(null); break;
            case Key.R when r.HasThread: (Keyboard.Modifiers == ModifierKeys.Shift ? r.ReplyAllCommand : r.ReplyCommand).Execute(null); break;
            case Key.A when r.HasThread: r.ReplyAllCommand.Execute(null); break;
            case Key.F when r.HasThread: r.ForwardCommand.Execute(null); break;
            case Key.P when r.HasThread: r.TogglePinCommand.Execute(null); break;
            case Key.U when r.HasThread: r.MarkUnreadCommand.Execute(null); break;
            case Key.S when r.HasThread: OnSnoozeMenu(ThreadList, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnThreadListClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src) return;
        var item = ItemsControl.ContainerFromElement(ThreadList, src) as ListBoxItem;
        if (item?.DataContext is ThreadItem { LocalDraftId: not null } t && ReferenceEquals(t, _vm.Selected))
            _vm.OpenSelectedLocalDraft();
    }

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm.SearchText = ""; ThreadList.Focus(); e.Handled = true; }
        else if (e.Key == Key.Down) { ThreadList.Focus(); if (_vm.Threads.Count > 0) _vm.Selected ??= _vm.Threads[0]; e.Handled = true; }
    }

    // ───────────────────────── accounts / settings ─────────────────────────

    private void OnSettings(object sender, RoutedEventArgs e) => SettingsWindow.Open(null);
    private void OnAddAccount(object sender, RoutedEventArgs e) => AddAccountWindow.ShowAdd(this);

    private void OnReauth(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AccountNode node) AddAccountWindow.ShowReauth(this, node.Account);
    }

    // ───────────────────────── window chrome & placement ─────────────────────────

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        if (AppServices.Engine.Config.CloseToTray && AppServices.Tray != null)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        e.Cancel = true;
        AppServices.Current.ExitApp();
    }

    private void OnSplitterDragged(object sender, DragCompletedEventArgs e) => SavePlacement();

    private void RestorePlacement()
    {
        var p = AppServices.Engine.Config.Window;
        if (p.Width >= MinWidth && p.Height >= MinHeight) { Width = p.Width; Height = p.Height; }
        if (!double.IsNaN(p.Left) && !double.IsNaN(p.Top)
            && p.Left > SystemParameters.VirtualScreenLeft - 50 && p.Top > SystemParameters.VirtualScreenTop - 50
            && p.Left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && p.Top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = p.Left;
            Top = p.Top;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
        if (p.ListWidth is >= 300 and <= 700) ListColumn.Width = new GridLength(p.ListWidth);
    }

    public void SavePlacement()
    {
        try
        {
            var p = AppServices.Engine.Config.Window;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (!b.IsEmpty && b.Width > 0)
            {
                p.Left = b.Left; p.Top = b.Top; p.Width = b.Width; p.Height = b.Height;
            }
            p.Maximized = WindowState == WindowState.Maximized;
            p.ListWidth = ListColumn.ActualWidth > 0 ? ListColumn.ActualWidth : p.ListWidth;
            AppServices.Engine.Settings.Save(notify: false);
        }
        catch (Exception ex) { Log.Warn("save placement: " + ex.Message); }
    }
}
