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
        _vm.Reader.Loading += ShowLoading;
        _vm.StatusBar.SignInRequested += id =>
        {
            if (AppServices.Engine.AccountById(id) is { } a) AddAccountWindow.ShowReauth(this, a);
        };
        RestorePlacement();
        StateChanged += (_, _) => MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";
        PreviewKeyDown += OnPreviewKeyDown;
        // Status bar: no once-a-second refresh while Magpie sits in the tray or minimised.
        IsVisibleChanged += (_, _) => _vm.StatusBar.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        StateChanged += (_, _) => _vm.StatusBar.SetWindowVisible(IsVisible && WindowState != WindowState.Minimized);
        Web.DefaultBackgroundColor = ThemeManager.WebBackground;
        ThemeManager.Changed += () => Web.DefaultBackgroundColor = ThemeManager.WebBackground;
        Loaded += async (_, _) => await InitWebAsync();
        Closing += OnClosing;
        Deactivated += (_, _) => { _hoverTimer?.Stop(); _vm.HideHoverCard(); };
    }

    // ───────────────────────── sidebar width (design H1) ─────────────────────────

    private double _dragGrab;

    // The width follows the mouse itself (not the thumb's own deltas, which shift as the column moves), so the drag
    // is 1:1 and the snap to the rail can't bounce.
    private void OnSidebarDragStarted(object sender, DragStartedEventArgs e)
    {
        var x = Mouse.GetPosition(BodyGrid).X;
        _dragGrab = x - (_vm.SidebarRail ? Core.Settings.WindowPlacement.SidebarMin - 20 : _vm.SidebarWidth);
    }
    private void OnSidebarDragDelta(object sender, DragDeltaEventArgs e) => _vm.DragSidebar(Mouse.GetPosition(BodyGrid).X - _dragGrab);
    private void OnSidebarDragCompleted(object sender, DragCompletedEventArgs e) => _vm.SaveSidebarWidth();
    private void OnSidebarHandleDoubleClick(object sender, MouseButtonEventArgs e) { _vm.ResetSidebar(); e.Handled = true; }

    // ───────────────────────── folder details on hover (design H2) ─────────────────────────

    private System.Windows.Threading.DispatcherTimer? _hoverTimer;
    private FrameworkElement? _hoverTarget;

    private void OnNavEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NavItem item } el) return;
        if (!_vm.HoverCardsOn) return;
        _hoverTarget = el;
        _hoverTimer ??= new System.Windows.Threading.DispatcherTimer();
        _hoverTimer.Stop();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(_vm.SidebarRail ? 300 : AppServices.Engine.Config.Appearance.FolderHover.DelayMs);
        _hoverTimer.Tick -= OnHoverTick;
        _hoverTimer.Tick += OnHoverTick;
        _hoverTimer.Start();
    }

    private void OnHoverTick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        if (_hoverTarget is not { IsMouseOver: true, DataContext: NavItem item } target) return;
        _vm.ShowHoverCard(item, card =>
        {
            if (!ReferenceEquals(_hoverTarget, target) || !target.IsMouseOver) return;
            HoverPopup.PlacementTarget = target;
            card.IsOpen = true;
        });
    }

    private void OnNavLeave(object sender, MouseEventArgs e)
    {
        _hoverTimer?.Stop();
        _hoverTarget = null;
        _vm.HideHoverCard();
    }

    // ───────────────────────── row actions + multi-select (design H3) ─────────────────────────

    private static ThreadItem? RowOf(DependencyObject? src)
    {
        for (var d = src; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: ThreadItem t }) return t;
        return null;
    }

    private void OnAvatarClick(object sender, MouseButtonEventArgs e)
    {
        if (RowOf(sender as DependencyObject) is { } item) { _vm.ToggleCheck(item); e.Handled = true; }
    }

    /// <summary>A hover button on a row acts on that row only (even when another conversation is open).</summary>
    private async void OnRowAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ToolbarButtonVm b } anchor || RowOf(anchor) is not { } item) return;
        e.Handled = true;
        if (item.LocalDraftId != null)   // a draft kept on this PC has no server copy: only its own actions apply
        {
            if (b.Id == "delete") { _vm.Selected = item; _vm.DeleteSelectedLocalDraft(); }
            return;
        }
        _vm.HideHoverCard();
        await RunOnItemsAsync(new[] { item }, b.Id, anchor);
    }

    private void OnBulkAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } anchor) return;
        var items = _vm.CheckedItems;
        if (items.Count == 0) return;
        _ = RunOnItemsAsync(items, id, anchor);
    }

    private void OnBulkClear(object sender, RoutedEventArgs e) => _vm.ClearSelection();
    private void OnBulkUndo(object sender, RoutedEventArgs e) => _vm.UndoBulk();

    /// <summary>Actions that need a choice (snooze / remind / tag / move) open their menu next to the button first.</summary>
    private async Task RunOnItemsAsync(IReadOnlyList<ThreadItem> items, string id, FrameworkElement anchor)
    {
        // A menu opened from a row's hover buttons keeps those buttons on screen until it closes.
        void ShowMenu(FrameworkElement a, ContextMenu m)
        {
            var row = items.Count == 1 ? RowOf(a) : null;
            if (row != null) { row.MenuOpen = true; m.Closed += (_, _) => row.MenuOpen = false; }
            MainWindow.ShowMenu(a, m);
        }
        switch (id)
        {
            case "snooze":
            {
                var menu = new ContextMenu();
                foreach (var p in TimePresets.For(DateTime.Now))
                    menu.Items.Add(Item($"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}", () => _ = _vm.RunOnAsync(items, "snooze", (DateTimeOffset)p.When)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Pick date & time…", () =>
                {
                    var when = PickTimeDialog.Ask(this, "Snooze until", "The conversations leave your inbox and come back at this time.", DateTime.Now.AddDays(1).Date.AddHours(8));
                    if (when != null) _ = _vm.RunOnAsync(items, "snooze", (DateTimeOffset)when.Value);
                }));
                ShowMenu(anchor, menu);
                return;
            }
            case "remind":
            {
                var menu = new ContextMenu();
                menu.Items.Add(new MenuItem { Header = "Bring back to the top on…", IsEnabled = false });
                foreach (var p in TimePresets.For(DateTime.Now))
                    menu.Items.Add(Item($"{p.Label}  ·  {TimePresets.Describe(p.When, DateTime.Now)}", () => _ = _vm.RunOnAsync(items, "remind", ((DateTimeOffset)p.When, true))));
                ShowMenu(anchor, menu);
                return;
            }
            case "tag":
            {
                var menu = new ContextMenu();
                foreach (var t in AppServices.Engine.Config.Tags)
                {
                    var tag = t.Name;
                    menu.Items.Add(Item(tag, () => _ = _vm.RunOnAsync(items, "tag", tag), isChecked: items.All(i => i.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))));
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Manage tags…", () => SettingsWindow.Open("General")));
                ShowMenu(anchor, menu);
                return;
            }
            case "move":
            {
                var accountId = items[0].Row.AccountId;
                if (items.Any(i => i.Row.AccountId != accountId)) { Ui.Error("Move", "Pick conversations from one account at a time to move them."); return; }
                var menu = new ContextMenu();
                foreach (var f in AppServices.Engine.Folders(accountId).Where(f => f.Role is not (FolderRole.All or FolderRole.Flagged or FolderRole.Important)))
                {
                    var folder = f;
                    var mi = Item(new string(' ', Math.Min(f.Depth, 4) * 2) + f.Name, () => _ = _vm.RunOnAsync(items, "move", folder));
                    mi.Icon = new IconChip { Icon = Icons.ForRole(f.Role), Size = 18 };
                    menu.Items.Add(mi);
                }
                ShowMenu(anchor, menu);
                return;
            }
            default:
                await _vm.RunOnAsync(items, id);
                return;
        }
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
            ShowPage(_pendingUrl ?? WebHost.Publish(HtmlRenderer.Placeholder("Welcome to Magpie", "Pick a conversation to read it here.", ThemeManager.IsDark), "view"));
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

    /// <summary>
    /// Another conversation was picked: replace what is on screen with "Loading…" straight away, inside the
    /// current page (no navigation, so it is instant), and stop any page that is still on its way in.
    /// </summary>
    private void ShowLoading(string bodyHtml)
    {
        if (!_webReady) return;
        try
        {
            Web.CoreWebView2.Stop();
            _ = Web.CoreWebView2.ExecuteScriptAsync("document.body.innerHTML=" + JsonSerializer.Serialize(bodyHtml) + ";window.scrollTo(0,0);");
        }
        catch (Exception ex) { Log.Warn("loading page: " + ex.Message); }
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
                case "retry": _vm.Reader.RetryLoad(); break;
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

    /// <summary>A reading-pane toolbar button (designs C1, C3).</summary>
    private void OnToolbarButton(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ToolbarButtonVm b } anchor) RunAction(b.Id, anchor);
    }

    /// <summary>Runs a toolbar / menu action by its id (the ids of <see cref="Core.Settings.Appearance.ToolbarIds"/>).</summary>
    private void RunAction(string id, FrameworkElement anchor)
    {
        var r = _vm.Reader;
        if (id == "delete" && _vm.Selected?.LocalDraftId != null) { _vm.DeleteSelectedLocalDraft(); return; }
        if (!r.HasThread) return;
        switch (id)
        {
            case "archive": r.ArchiveCommand.Execute(null); break;
            case "delete": r.DeleteCommand.Execute(null); break;
            case "snooze": OnSnoozeMenu(anchor, new RoutedEventArgs()); break;
            case "remind": OnRemindMenu(anchor, new RoutedEventArgs()); break;
            case "tag": OnTagMenu(anchor, new RoutedEventArgs()); break;
            case "pin": r.TogglePinCommand.Execute(null); break;
            case "setaside": r.ToggleSetAsideCommand.Execute(null); break;
            case "move": ShowMenu(anchor, MoveMenu()); break;
            case "unread": r.MarkUnreadCommand.Execute(null); break;
            case "replyall": r.ReplyAllCommand.Execute(null); break;
            case "forward": r.ForwardCommand.Execute(null); break;
        }
    }

    private static MenuItem IconItem(string header, string icon, Action onClick, string? gesture = null)
    {
        var mi = Item(header, onClick, gesture);
        mi.Icon = new IconChip { Icon = icon, Size = 18 };
        return mi;
    }

    private ContextMenu MoveMenu()
    {
        var r = _vm.Reader;
        var menu = new ContextMenu();
        foreach (var f in AppServices.Engine.Folders(r.AccountId).Where(f => f.Role is not (FolderRole.All or FolderRole.Flagged or FolderRole.Important)))
        {
            var folder = f;
            var mi = Item(new string(' ', Math.Min(f.Depth, 4) * 2) + f.Name, () => r.MoveTo(folder));
            mi.Icon = new IconChip { Icon = Icons.ForRole(f.Role), Size = 18 };
            menu.Items.Add(mi);
        }
        return menu;
    }

    private MenuItem ActionItem(ToolbarButtonVm b, FrameworkElement anchor)
    {
        var r = _vm.Reader;
        var name = b.Id == "pin" && r.IsPinned ? "Unpin" : b.Id == "setaside" && r.IsSetAside ? "Back to Inbox" : b.Id == "unread" ? "Mark as unread" : b.Name;
        if (b.Id == "move")
        {
            var move = new MenuItem { Header = "Move to", Icon = new IconChip { Icon = "move", Size = 18 } };
            foreach (var item in MoveMenu().Items.OfType<MenuItem>().ToList())
            {
                ((ContextMenu)item.Parent).Items.Remove(item);
                move.Items.Add(item);
            }
            return move;
        }
        return IconItem(name + (b.Id is "snooze" or "remind" or "tag" ? "…" : ""), b.IconKey, () => RunAction(b.Id, anchor), b.Shortcut.Length > 0 ? b.Shortcut : null);
    }

    /// <summary>··· More: the buttons hidden from the toolbar, then the less common actions.</summary>
    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        if (!_vm.Reader.HasThread) return;
        var r = _vm.Reader;
        var anchor = (FrameworkElement)sender;
        var menu = new ContextMenu();
        foreach (var b in _vm.HiddenButtons) menu.Items.Add(ActionItem(b, anchor));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        if (r.SenderAddress is { } sender0)
        {
            var cat = new MenuItem { Header = "Smart inbox: always put " + sender0 + " in" };
            cat.Items.Add(Item("People", () => r.SetSenderCategory(Category.People)));
            cat.Items.Add(Item("Notifications", () => r.SetSenderCategory(Category.Notifications)));
            cat.Items.Add(Item("Newsletters", () => r.SetSenderCategory(Category.Newsletters)));
            menu.Items.Add(cat);
        }
        if (r.CanUnsubscribe) menu.Items.Add(IconItem("Unsubscribe…", "unsubscribe", () => r.UnsubscribeCommand.Execute(null)));
        menu.Items.Add(IconItem("Customise toolbar…", "toolbar", () => SettingsWindow.Open("Toolbar")));
        ShowMenu(anchor, menu);
    }

    /// <summary>Right-click on the list: same actions, in the toolbar's order when that option is on (design C3).</summary>
    private void OnListMenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Items.Clear();
        var r = _vm.Reader;
        if (_vm.Selected?.LocalDraftId != null)
        {
            menu.Items.Add(IconItem("Open draft", "drafts", _vm.OpenSelectedLocalDraft, "Enter"));
            menu.Items.Add(IconItem("Delete draft", "delete", _vm.DeleteSelectedLocalDraft, "Del"));
            return;
        }
        if (!r.HasThread) { menu.IsOpen = false; return; }
        var a = AppServices.Engine.Config.Appearance;
        var ids = a.MenuFollowsToolbar ? a.Toolbar.Where(b => b.Visible).Select(b => b.Id).ToList() : Core.Settings.Appearance.ToolbarIds.Take(Core.Settings.Appearance.DefaultVisible).ToList();
        foreach (var id in ids.Where(id => id is not ("replyall" or "forward")))
            menu.Items.Add(ActionItem(ToolbarButtonVm.For(id, a.ButtonStyle), ThreadList));
        menu.Items.Add(new Separator());
        menu.Items.Add(IconItem("Reply", "reply", () => r.ReplyCommand.Execute(null), "R"));
        menu.Items.Add(IconItem("Reply all", "replyall", () => r.ReplyAllCommand.Execute(null), "A"));
        menu.Items.Add(IconItem("Forward", "forward", () => r.ForwardCommand.Execute(null), "F"));
    }

    public void AttachUpdates(UpdateService updates) => UpdatePill.DataContext = updates;

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
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        // Sidebar (design H1): Ctrl+Shift+← / → narrower / wider, Ctrl+Shift+B show / hide.
        if (ctrl && shift && e.Key == Key.Left) { _vm.NudgeSidebar(-1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Right) { _vm.NudgeSidebar(1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.B) { _vm.ToggleSidebarHidden(); e.Handled = true; return; }
        // Send now / Undo on the newest waiting message (design SN1).
        if (ctrl && shift && e.Key == Key.Enter && _vm.Toasts.Any(t => t.CanSendNow)) { _vm.SendNowCommand.Execute(null); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.Z && Keyboard.FocusedElement is not TextBox && _vm.Toasts.Any(t => !t.Done)) { _vm.UndoSendCommand.Execute(null); e.Handled = true; return; }
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox && _vm.SelectedCount > 0) { _vm.ClearSelection(); e.Handled = true; return; }
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
            case Key.L when r.HasThread: r.ToggleSetAsideCommand.Execute(null); break;
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
        // A bulk action still waiting for its Undo goes ahead — and finishes before the engine is disposed.
        try { _vm.CommitPendingBulk().GetAwaiter().GetResult(); } catch (Exception ex) { Log.Warn("bulk on close: " + ex.Message); }
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
