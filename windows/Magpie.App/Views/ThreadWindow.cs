using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Magpie.App.Views;

/// <summary>
/// Design OW1 (option A): an email (its conversation) in its own window — a double-click or Enter in the list,
/// right-click → Open in new window, the New window button, or the subject card's line (design HM1, S3). It shows the
/// same head as the reading pane (<see cref="ReaderHeadView"/>: every toolbar button and banner), steps to the
/// previous / next email of the list it was opened from, exists once per conversation, and remembers its size and place.
/// </summary>
public sealed class ThreadWindow : Window, IReaderHost
{
    /// <summary>One window per conversation (<see cref="EmailWindow.Key"/>): opening it again brings this one forward.</summary>
    private static readonly Dictionary<string, ThreadWindow> OpenWindows = new(StringComparer.Ordinal);

    private readonly MainViewModel _main;
    private ThreadRow _row;
    private readonly MessageRow? _focus;
    private readonly List<ThreadRow> _list;
    private readonly List<string> _keys;
    private readonly string? _listName;
    /// <summary>Per account, the folders of the list the window was opened from: what Archive, Delete and Move act on.</summary>
    private readonly Dictionary<string, List<long>> _listFolders = new(StringComparer.Ordinal);
    private readonly ThreadViewModel _reader = new();
    private readonly WebView2 _web = new();
    private readonly ReaderHeadView _head = new();
    private readonly ReaderHover _hover;
    private readonly ReaderPresenter _presenter = new();
    private readonly Ui.Debouncer _refresh = new(TimeSpan.FromMilliseconds(300));
    private readonly Border _navBar;
    private bool _ready, _shown, _closing;
    /// <summary>
    /// The window shows an email of Trash or Spam on purpose: it was opened from one of those two, or the email had no
    /// copy anywhere else when it was opened. Only then do its actions touch the copies there (Delete = for good, as
    /// in Trash itself). Any other window never does, and closes when its email ends up there.
    /// </summary>
    private bool _binScope;
    private string _typography = "", _deleteLook = "";

    /// <summary>"2 of 42 in Inbox" — where the email is in the list the window was opened from.</summary>
    public TextBlock NavText { get; } = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    public Button PreviousButton { get; }
    public Button NextButton { get; }
    public ReaderHeadView Head => _head;
    public ThreadViewModel ReaderModel => _reader;
    /// <summary>The conversation the window shows now (it changes with Previous / Next).</summary>
    public string ConversationKey => EmailWindow.Key(_row.AccountId, _row.ThreadKey);
    public static int OpenCount => OpenWindows.Count;

    /// <summary>
    /// Opens the conversation in its own window, or brings its window forward when it is open already. The list on
    /// screen in the main window is taken along for Previous / Next when the conversation is in it.
    /// </summary>
    /// <param name="focus">Design PE1: open the conversation at this email (a row of the "emails with one address" list).</param>
    /// <param name="activate">False keeps the keyboard where it is.</param>
    /// <param name="browser">False leaves the page's browser unstarted: the smoke test checks the window without one.</param>
    public static ThreadWindow Open(MainViewModel main, ThreadRow row, MessageRow? focus = null, bool activate = true, bool browser = true)
    {
        var key = EmailWindow.Key(row.AccountId, row.ThreadKey);
        if (OpenWindows.TryGetValue(key, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            // Another email of the conversation was opened (two rows of the "emails with one address" list): its
            // window goes to that email instead of staying at the one it was opened at.
            if (focus != null) open._reader.Show(open._row, main, focus);
            if (activate) open.Activate();
            return open;
        }
        var list = main.Threads.Where(t => t.LocalDraftId == null).Select(t => t.Row)
            .DistinctBy(r => EmailWindow.Key(r.AccountId, r.ThreadKey)).ToList();
        if (!list.Any(r => EmailWindow.Key(r.AccountId, r.ThreadKey) == key)) list.Clear();
        var w = new ThreadWindow(main, row, list, main.IsPersonView ? null : main.ListTitle, focus, browser) { ShowActivated = activate };
        w.Show();
        if (activate) w.Activate();
        return w;
    }

    private ThreadWindow(MainViewModel main, ThreadRow row, List<ThreadRow> list, string? listName, MessageRow? focus, bool browser)
    {
        _main = main;
        _row = row;
        _focus = focus;
        _list = list;
        _keys = list.Select(r => EmailWindow.Key(r.AccountId, r.ThreadKey)).ToList();
        _listName = listName;
        foreach (var account in list.Select(r => r.AccountId).Append(row.AccountId).Distinct()) _listFolders[account] = main.ActionFolders(account);
        _binScope = BinScope(row);
        _typography = JsonSerializer.Serialize(AppServices.Engine.Config.Typography);
        _deleteLook = AppServices.Engine.Config.Appearance.DeleteDates.ToString();
        _reader.ActionFolders = FoldersFor;
        Style = (Style)Application.Current.FindResource("Window.Dialog");
        ShowInTaskbar = true;   // a window of its own: it is found on the taskbar like any other
        Icon = Application.Current.MainWindow?.Icon;
        ResizeMode = ResizeMode.CanResize;
        MinWidth = EmailWindowPlacement.MinWidth;
        MinHeight = EmailWindowPlacement.MinHeight;
        DataContext = _reader;
        Title = EmailWindow.Title(row.Latest?.Subject);
        RestorePlacement();

        var root = new DockPanel();

        // Previous / Next in the list the email was opened from, and where it is in it.
        PreviousButton = NavButton("", EmailWindow.PreviousTip, -1);
        NextButton = NavButton("", EmailWindow.NextTip, 1);
        NavText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        var nav = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        nav.Children.Add(NavText);
        nav.Children.Add(PreviousButton);
        nav.Children.Add(NextButton);
        _navBar = new Border { Child = nav, Padding = new Thickness(18, 6, 14, 0) };
        _navBar.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        DockPanel.SetDock(_navBar, Dock.Top);
        root.Children.Add(_navBar);

        // The same head as the reading pane: toolbar, subject, AI summary, meeting invite, banners.
        var headBorder = new Border { Child = _head };
        headBorder.SetResourceReference(Border.BackgroundProperty, "Brush.Card.Background");
        DockPanel.SetDock(headBorder, Dock.Top);
        root.Children.Add(headBorder);
        _web.DefaultBackgroundColor = ThemeManager.WebBackground;
        root.Children.Add(_web);
        Content = root;

        _hover = new ReaderHover(this, main, _reader, js => _ready ? _web.CoreWebView2.ExecuteScriptAsync(js) : Task.CompletedTask);
        _head.Attach(this);
        PreviewMouseDown += (_, e) => _hover.PressedIn(e.OriginalSource, _web);   // design CO1
        SubjectCard? card = null;
        card = new SubjectCard(_head.SubjectText, () => _hover.SubjectContent(ownWindow: true), (id, anchor) => _hover.RunSubject(id, anchor, card!));

        _reader.PageReady += page => Ui.Post(() => _presenter.Show(page));
        _reader.Loading += _presenter.Loading;
        _reader.ThreadRemoved += () => Ui.Post(CloseOnce);   // archived, deleted, snoozed, moved from here
        _reader.PropertyChanged += OnReaderChanged;
        Action redraw = () => Ui.Post(() => { _web.DefaultBackgroundColor = ThemeManager.WebBackground; _reader.Redraw(); });
        ThemeManager.Changed += redraw;
        // What changes elsewhere (a new reply, read in the list, deleted on the phone) shows here too.
        Action<ChangeSet> changed = cs => { if (cs.AccountId.Length == 0 || cs.AccountId == _row.AccountId) Ui.Post(() => _refresh.Run(RefreshFromStore)); };
        AppServices.Engine.Changed += changed;
        // Settings → AI features, Reading and Appearance apply to this window's reader as they do to the reading pane.
        Action settings = () => Ui.Post(OnSettingsChanged);
        AppServices.Engine.Settings.Changed += settings;
        Action autoDelete = () => Ui.Post(() => { if (!_closing) _reader.RefreshDeleteBar(); });
        AppServices.Engine.AutoDeleteChanged += autoDelete;
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => _head.CloseCards();
        LocationChanged += (_, _) => _head.CloseCards();
        Closing += (_, _) => { _closing = true; SavePlacement(); };
        Closed += (_, _) =>
        {
            ThemeManager.Changed -= redraw;
            AppServices.Engine.Changed -= changed;
            AppServices.Engine.Settings.Changed -= settings;
            AppServices.Engine.AutoDeleteChanged -= autoDelete;
            if (OpenWindows.TryGetValue(ConversationKey, out var mine) && ReferenceEquals(mine, this)) OpenWindows.Remove(ConversationKey);
            _reader.Clear();
        };
        if (browser) Loaded += async (_, _) => await InitAsync();

        OpenWindows[ConversationKey] = this;
        UpdateNav();
        // The conversation's subject, toolbar and banners are there at once; the page follows when the browser is ready.
        _reader.Show(_row, _main, _focus);
        _shown = true;
    }

    // ───────────────────────── the reader's host ─────────────────────────

    Window IReaderHost.Window => this;
    MainViewModel IReaderHost.Main => _main;
    ThreadViewModel IReaderHost.Reader => _reader;
    bool IReaderHost.OwnWindow => true;
    void IReaderHost.RunAction(string id, FrameworkElement anchor) => ReaderActions.Run(this, id, anchor);

    void IReaderHost.DeleteForever()
    {
        if (_reader.CurrentThreadRow is { } row)
            _ = _main.DeleteForeverAsync(new[] { new ThreadItem(row, "#14606E", false, DateTimeOffset.Now, "") }, account => FoldersFor(account, row.ThreadKey));
    }

    /// <summary>
    /// The folders an action on this conversation works on: those of the list the window was opened from, whatever the
    /// main window shows by now. When the conversation has no copy in them (opened from Contacts, or moved since), every
    /// folder but Sent, Drafts and Gmail's All Mail / Starred / Important — as the Pinned view does. Trash and Spam
    /// are among them only for a window that shows an email of Trash or Spam on purpose (<see cref="_binScope"/>):
    /// Delete on a copy there removes it for good, and an email that another app moved to Trash while its window was
    /// open must not be lost to a plain Delete.
    /// </summary>
    private List<long> FoldersFor(string accountId, string threadKey)
    {
        var others = AppServices.Engine.Folders(accountId)
            .Where(f => f.Synced && f.Role is not (FolderRole.Sent or FolderRole.Drafts or FolderRole.All or FolderRole.Flagged or FolderRole.Important))
            .Select(f => f.Id).ToList();
        return EmailWindow.ActionFolders(Listed(accountId), Copies(accountId, threadKey), others, Bins(accountId), _binScope);
    }

    /// <summary>The folders of the list the window was opened from, for this account.</summary>
    private List<long> Listed(string accountId) => _listFolders.TryGetValue(accountId, out var listed) ? listed : new();

    /// <summary>The folders that hold a copy of the conversation on this PC.</summary>
    private static List<long> Copies(string accountId, string threadKey) =>
        AppServices.Engine.Store.GetThreadCopies(accountId, threadKey).Select(m => m.FolderId).ToList();

    /// <summary>The account's Trash and Spam folders.</summary>
    private static HashSet<long> Bins(string accountId) =>
        AppServices.Engine.Folders(accountId).Where(f => f.Role is FolderRole.Trash or FolderRole.Junk).Select(f => f.Id).ToHashSet();

    /// <summary>See <see cref="_binScope"/>: opened from Trash or Spam, or an email that is nowhere else.</summary>
    private bool BinScope(ThreadRow row) =>
        EmailWindow.BinScope(Listed(row.AccountId), Copies(row.AccountId, row.ThreadKey), Bins(row.AccountId));

    /// <summary>Design AL1: the conversation goes back to the folder it was in; the window closes, as it does for Archive and Delete.</summary>
    void IReaderHost.RestoreFromTrash()
    {
        if (!_reader.HasThread) return;
        try { AppServices.Engine.Restore(_reader.AccountId, _reader.ThreadKey); }
        catch (Exception ex) { Log.Error("restore from the email window", ex); Ui.Error("Magpie", ex.Message, this); return; }
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (!_closing) Close();
    }

    // ───────────────────────── previous / next ─────────────────────────

    private Button NavButton(string glyph, string tip, int delta)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Margin = new Thickness(0, 0, 4, 0) };
        b.SetResourceReference(StyleProperty, "Button.Icon");
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => Step(delta);
        return b;
    }

    /// <summary>
    /// The conversation one step up (-1) or down (+1) in the list. Conversations that are gone since are stepped over,
    /// and so are those that went to Trash or Spam since (unless the list is Trash or Spam itself): they left the list.
    /// </summary>
    private ThreadRow? Neighbour(int delta)
    {
        var key = ConversationKey;
        while (EmailWindow.Step(_keys, key, delta) is { } next)
        {
            key = next;
            var row = _list[_keys.IndexOf(next)];
            if (!EmailWindow.StepsOver(Listed(row.AccountId), Copies(row.AccountId, row.ThreadKey), Bins(row.AccountId))) return row;
        }
        return null;
    }

    /// <summary>Shows the previous / next email of the list in this window. False at the end of the list.</summary>
    public bool Step(int delta)
    {
        if (Neighbour(delta) is not { } to) return false;
        var toKey = EmailWindow.Key(to.AccountId, to.ThreadKey);
        // That email has a window of its own already: this one makes way for it instead of showing it twice.
        if (OpenWindows.TryGetValue(toKey, out var other) && !ReferenceEquals(other, this))
        {
            if (other.WindowState == WindowState.Minimized) other.WindowState = WindowState.Normal;
            other.Activate();
            return true;
        }
        OpenWindows.Remove(ConversationKey);
        _row = to;
        _binScope = BinScope(to);
        OpenWindows[ConversationKey] = this;
        _head.CloseCards();
        _reader.Show(_row, _main);
        UpdateNav();
        return true;
    }

    private void UpdateNav()
    {
        var position = EmailWindow.Position(_keys, ConversationKey, _listName);
        NavText.Text = position;
        _navBar.Visibility = position.Length > 0 && _keys.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PreviousButton.IsEnabled = EmailWindow.Step(_keys, ConversationKey, -1) != null;
        NextButton.IsEnabled = EmailWindow.Step(_keys, ConversationKey, 1) != null;
    }

    // ───────────────────────── staying current ─────────────────────────

    private void OnReaderChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThreadViewModel.Subject)) Title = EmailWindow.Title(_reader.Subject);
        // The conversation is gone (deleted for good, restored, removed in another app): nothing is left to show.
        else if (e.PropertyName == nameof(ThreadViewModel.HasThread) && _shown && !_closing && !_reader.HasThread) Ui.Post(CloseOnce);
    }

    private void RefreshFromStore()
    {
        if (_closing || !_reader.HasThread) return;
        // Deleted or marked as spam somewhere else (the list, the phone): the window closes, as it does for its own Delete.
        if (EmailWindow.ClosesWhenBinned(_binScope, Copies(_row.AccountId, _row.ThreadKey), Bins(_row.AccountId))) { CloseOnce(); return; }
        _reader.RefreshIfShowing(_row);
    }

    private void OnSettingsChanged()
    {
        if (_closing) return;
        _reader.RefreshAiVisibility();   // AI switched off: the Summarise button and a running summary go
        var look = AppServices.Engine.Config.Appearance.DeleteDates.ToString();
        if (look != _deleteLook) { _deleteLook = look; _reader.RefreshDeleteBar(); }   // design DD1: another look for the deletion dates
        var typography = JsonSerializer.Serialize(AppServices.Engine.Config.Typography);
        if (typography == _typography) return;
        _typography = typography;
        _reader.Redraw();
    }

    // ───────────────────────── keys ─────────────────────────

    /// <summary>Esc closes the window; J / K step through the list; the letters of the main window act on this email.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && !AnyMenuOrCardOpen()) { e.Handled = true; CloseOnce(); return; }
        if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CloseOnce(); return; }
        if (Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox) return;   // typing a note to the organiser
        if (Keyboard.Modifiers != ModifierKeys.None && Keyboard.Modifiers != ModifierKeys.Shift) return;
        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        var r = _reader;
        if (!r.HasThread) return;
        switch (e.Key)
        {
            case Key.J: Step(1); break;
            case Key.K: Step(-1); break;
            case Key.E: r.ArchiveCommand.Execute(null); break;
            case Key.Delete: if (shift) ((IReaderHost)this).DeleteForever(); else r.DeleteCommand.Execute(null); break;
            case Key.R: (shift ? r.ReplyAllCommand : r.ReplyCommand).Execute(null); break;
            case Key.A: r.ReplyAllCommand.Execute(null); break;
            case Key.F: r.ForwardCommand.Execute(null); break;
            case Key.P: r.TogglePinCommand.Execute(null); break;
            case Key.U: r.MarkUnreadCommand.Execute(null); break;
            case Key.S: ReaderActions.ShowMenu(_head, ReaderMenus.Snooze(r, this)); break;
            case Key.L: r.ToggleSetAsideCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Esc first closes what is open on top (the Details card closes itself in the page).</summary>
    private static bool AnyMenuOrCardOpen() => Keyboard.FocusedElement is MenuItem or System.Windows.Controls.ContextMenu;

    // ───────────────────────── size and place ─────────────────────────

    private void RestorePlacement()
    {
        EmailWindow.Area? main = Application.Current.MainWindow is { IsVisible: true, WindowState: WindowState.Normal } m && !ReferenceEquals(m, this)
            ? new EmailWindow.Area(m.Left, m.Top, m.Width, m.Height) : null;
        var saved = AppServices.Engine.Config.EmailWindow;
        var at = EmailWindow.Place(saved, Screens.WorkAreas(), main, OpenWindows.Count);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = at.Left;
        Top = at.Top;
        Width = at.Width;
        Height = at.Height;
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>The next email window opens with this one's size and place (the last one closed decides).</summary>
    private void SavePlacement()
    {
        if (AppServices.Engine.IsStoppedForVersionChange) return;
        try
        {
            var p = AppServices.Engine.Config.EmailWindow;
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (b.IsEmpty || b.Width < MinWidth || b.Height < MinHeight) return;
            p.Left = b.Left;
            p.Top = b.Top;
            p.Width = b.Width;
            p.Height = b.Height;
            p.Maximized = WindowState == WindowState.Maximized;
            p.HasSavedPlacement = true;
            AppServices.Engine.Settings.Save(notify: false);
        }
        catch (Exception ex) { Log.Warn("save email window placement: " + ex.Message); }
    }

    // ───────────────────────── the page ─────────────────────────

    private async Task InitAsync()
    {
        try
        {
            if (!await WebHost.InitAsync(_web, scripts: true))
            {
                Ui.Error("Magpie", "The reading pane needs Microsoft Edge WebView2.", this);
                CloseOnce();
                return;
            }
            if (_closing) return;
            var core = _web.CoreWebView2;
            core.NavigationStarting += (_, e) =>
            {
                if (WebHost.IsOwnPage(e.Uri)) return;
                e.Cancel = true;
                _reader.OnLink(e.Uri);
            };
            core.NewWindowRequested += (_, e) => { e.Handled = true; _reader.OnLink(e.Uri); };
            core.WebMessageReceived += OnWebMessage;
            core.ContextMenuRequested += (_, e) =>
            {
                foreach (var item in e.MenuItems.ToList())
                    if (item.Name is "back" or "forward" or "reload" or "saveAs" or "print" or "inspectElement" or "other")
                        e.MenuItems.Remove(item);
            };
            _ready = true;
            _presenter.Attach(core);   // shows the page the reader has prepared meanwhile
        }
        catch (Exception ex)
        {
            Log.Error("conversation window", ex);
            CloseOnce();
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            _hover.OnMessage(doc.RootElement);
        }
        catch (Exception ex) { Log.Warn("web message: " + ex.Message); }
    }
}
