using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Magpie.Mac.Services;

namespace Magpie.Mac.ViewModels;

/// <summary>
/// The main window: the sidebar (All inboxes, then each account with its folders and unread counts), the message
/// list with search, the reading pane and the "Sending… Undo" notes. Everything comes from Magpie.Core's engine, as
/// on Windows; this only arranges it.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static MailEngine E => AppServices.Engine;
    public const int ListLimit = 400;

    private readonly Debouncer _reload = new(TimeSpan.FromMilliseconds(250));
    private readonly Debouncer _search = new(TimeSpan.FromMilliseconds(300));
    private readonly Debouncer _counts = new(TimeSpan.FromMilliseconds(600));
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reloading, _keepSelection;

    public ObservableCollection<SidebarItem> Sidebar { get; } = new();
    public ObservableCollection<ThreadItem> Threads { get; } = new();
    public ObservableCollection<ToastItem> Toasts { get; } = new();
    public ReaderViewModel Reader { get; } = new();
    public MacUpdateService? Updates => AppServices.Updates;
    public void UpdatesAttached() => OnPropertyChanged(nameof(Updates));

    [ObservableProperty] private SidebarItem? _current;
    [ObservableProperty] private ThreadItem? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _listTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _syncText = "";

    /// <summary>Design V1: the version in the main window ("8.0.0").</summary>
    public string VersionText => AppServices.Current.ToString();
    public string VersionTip => $"Magpie {AppServices.Current}" + (AppServices.ReleaseDate.Length > 0 ? $" · released {AppServices.ReleaseDate}" : "");
    public bool HasAccounts => E.Accounts.Count > 0;
    public bool ShowEmpty => Threads.Count == 0;

    public MainViewModel()
    {
        E.Changed += cs => AppServices.Post(() =>
        {
            _reload.Run(ReloadList);
            if (cs.FoldersChanged) BuildSidebar(); else _counts.Run(RefreshCounts);
        });
        E.StatusChanged += (id, st) => AppServices.Post(() => UpdateStatus(id, st));
        E.OutboxChanged += () => AppServices.Post(() => _counts.Run(RefreshCounts));
        E.Settings.Changed += () => AppServices.Post(() => { OnPropertyChanged(nameof(HasAccounts)); BuildSidebar(); });
        E.Sent += item => AppServices.Post(() => RemoveToast(item.Id));
        E.SendFailed += (item, err) => AppServices.Post(() =>
        {
            RemoveToast(item.Id);
            Toasts.Add(new ToastItem { OutboxId = item.Id, CanUndo = false, Text = $"Couldn't send “{item.Subject}”: {err} Magpie will retry.", Until = DateTimeOffset.Now.AddSeconds(12) });
            _toastTimer.Start();
        });
        Reader.ThreadRemoved += SelectNeighbour;
        _toastTimer.Tick += (_, _) => TickToasts();
        BuildSidebar();
    }

    // ───────────────────────── sidebar ─────────────────────────

    public void BuildSidebar()
    {
        var keep = Current?.Key;
        var old = Sidebar.ToDictionary(s => s.Key, s => s.Unread);
        Sidebar.Clear();
        Sidebar.Add(new SidebarItem { Kind = SidebarKind.AllInboxes, Label = "All inboxes" });
        var folders = E.Folders();
        foreach (var a in E.Accounts)
        {
            Sidebar.Add(new SidebarItem { Kind = SidebarKind.Account, Label = a.Email, AccountId = a.Id, AccountColor = a.Color });
            foreach (var (f, depth, _) in FolderTree.Order(folders.Where(f => f.AccountId == a.Id)))
            {
                if (f.Role is FolderRole.All or FolderRole.Important or FolderRole.Flagged) continue;   // Gmail's All Mail / Important / Starred copies
                Sidebar.Add(new SidebarItem
                {
                    Kind = SidebarKind.Folder, Label = f.Name, AccountId = a.Id, FolderId = f.Id, Role = f.Role,
                    Depth = Math.Min(depth, 5), AccountColor = a.Color,
                });
            }
        }
        foreach (var s in Sidebar) if (old.TryGetValue(s.Key, out var n)) s.Unread = n;
        var restore = Sidebar.FirstOrDefault(s => s.Key == keep && s.IsSelectable) ?? Sidebar[0];
        // Same view as before (e.g. after Settings saved): swap in the new item without closing the open conversation.
        _keepSelection = restore.Key == keep;
        try { Current = restore; }
        finally { _keepSelection = false; }
        OnPropertyChanged(nameof(HasAccounts));
        RefreshCounts();
    }

    partial void OnCurrentChanged(SidebarItem? oldValue, SidebarItem? newValue)
    {
        // Account headings aren't views: keep the previous choice.
        if (newValue is { IsSelectable: false })
        {
            Dispatcher.UIThread.Post(() => Current = oldValue?.IsSelectable == true ? oldValue : Sidebar.FirstOrDefault());
            return;
        }
        UpdateTitle();
        if (_keepSelection) { ReloadList(); return; }
        Selected = null;
        Reader.Clear();
        if (newValue?.Kind == SidebarKind.Folder) E.Prioritise(new[] { newValue.FolderId });
        ReloadList();
    }

    private void UpdateTitle()
    {
        ListTitle = Current?.Kind switch
        {
            SidebarKind.Folder => $"{Current.Label} · {E.AccountById(Current.AccountId)?.Email}",
            SidebarKind.AllInboxes => "All inboxes",
            _ => "",
        };
    }

    public void RefreshCounts()
    {
        _ = Task.Run(() => E.Store.CountThreadsByFolder(DateTimeOffset.Now)).ContinueWith(t =>
        {
            if (t.IsFaulted) { Log.Warn("counts: " + t.Exception?.GetBaseException().Message); return; }
            AppServices.Post(() =>
            {
                var by = t.Result;
                var inboxes = E.FolderIds(FolderRole.Inbox).ToHashSet();
                foreach (var s in Sidebar)
                {
                    s.Unread = s.Kind switch
                    {
                        SidebarKind.AllInboxes => by.Where(kv => inboxes.Contains(kv.Key)).Sum(kv => kv.Value.Unread),
                        SidebarKind.Folder when s.Role is not (FolderRole.Sent or FolderRole.Drafts or FolderRole.Trash)
                            => by.TryGetValue(s.FolderId, out var c) ? c.Unread : 0,
                        _ => 0,
                    };
                }
            });
        }, TaskScheduler.Default);
    }

    private readonly Dictionary<string, SyncStatus> _status = new();

    /// <summary>The line under the list: a problem with an account first ("needs you to sign in again"), else "Checking for mail…".</summary>
    private void UpdateStatus(string accountId, SyncStatus st)
    {
        _status[accountId] = st;
        var problem = _status.FirstOrDefault(kv => kv.Value.State is SyncState.Error or SyncState.Offline or SyncState.NeedsSignIn && E.AccountById(kv.Key) != null);
        var busy = _status.Values.Any(v => v.State is SyncState.Connecting or SyncState.Syncing);
        SyncText = problem.Value != null ? $"{E.AccountById(problem.Key)?.Email}: {problem.Value.Message}" : busy ? "Checking for mail…" : "";
    }

    // ───────────────────────── list ─────────────────────────

    private List<long> FolderIdsFor(SidebarItem nav, bool searching) => nav.Kind switch
    {
        SidebarKind.AllInboxes => searching ? E.AllMailFolderIds() : E.FolderIds(FolderRole.Inbox),
        SidebarKind.Folder => new List<long> { nav.FolderId },
        _ => new List<long>(),
    };

    /// <summary>The folders Archive / Delete take the conversation out of, for the current view.</summary>
    public List<long> ActionFolders(string accountId)
    {
        if (Current == null) return new();
        var own = E.Folders(accountId).Select(f => f.Id).ToHashSet();
        return FolderIdsFor(Current, searching: false).Where(own.Contains).ToList();
    }

    partial void OnSearchTextChanged(string value) => _search.Run(ReloadList);

    public void ReloadList()
    {
        var nav = Current;
        if (nav == null || !nav.IsSelectable) return;
        var now = DateTimeOffset.Now;
        var keep = Selected?.Key;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchQuery.Parse(SearchText);
        List<ThreadRow> rows;
        try
        {
            rows = E.Store.ListThreads(new ListQuery { FolderIds = FolderIdsFor(nav, search != null), Search = search, Limit = ListLimit }, now);
        }
        catch (Exception ex)
        {
            Log.Warn("list: " + ex.Message);
            rows = new();
        }
        var multi = E.Accounts.Count > 1;
        var items = rows.Select(r =>
        {
            var acc = E.AccountById(r.AccountId);
            return new ThreadItem(r, acc?.Email ?? "", acc?.Color ?? "#14606E", multi, now);
        }).ToList();
        _reloading = true;
        try
        {
            Threads.Clear();
            foreach (var i in items) Threads.Add(i);
            Selected = keep == null ? null : Threads.FirstOrDefault(t => t.Key == keep);
        }
        finally { _reloading = false; }
        OnPropertyChanged(nameof(ShowEmpty));
        EmptyText = Threads.Count > 0 ? "" : !HasAccounts ? "Add an account to get started (Magpie → Settings… → Accounts)." :
            search != null ? "No messages match your search." :
            nav.Kind == SidebarKind.AllInboxes ? "Inbox zero. Nice." :
            E.StillListing(FolderIdsFor(nav, false)) ? "Getting this folder's emails from the server…" : "No conversations here.";
        if (Selected != null) Reader.RefreshIfShowing(Selected.Row);
    }

    partial void OnSelectedChanged(ThreadItem? value)
    {
        if (_reloading) return;
        if (value == null) { if (Reader.HasThread) Reader.Clear(); return; }
        Reader.Show(value.Row, ActionFolders);
    }

    /// <summary>After Archive / Delete: the next conversation in the list opens.</summary>
    public void SelectNeighbour()
    {
        var i = Selected == null ? -1 : Threads.IndexOf(Selected);
        ReloadList();
        if (Threads.Count == 0) { Selected = null; Reader.Clear(); return; }
        if (Selected == null || i < 0) Selected = Threads[Math.Clamp(i < 0 ? 0 : i, 0, Threads.Count - 1)];
    }

    // ───────────────────────── commands ─────────────────────────

    [RelayCommand] private void Compose() => Views.ComposeWindow.Open(ComposeMode.New, null);
    [RelayCommand] private void Sync() => E.SyncNow();
    [RelayCommand] private void ClearSearch() => SearchText = "";
    [RelayCommand] private void OpenSettings() => Views.SettingsWindow.Open();
    [RelayCommand] private void AddAccount() => Views.AddAccountWindow.Open(null);

    // ───────────────────────── Sending… Undo ─────────────────────────

    /// <summary>A message was queued with an undo wait (Settings: Undo send seconds).</summary>
    public void ShowUndo(long outboxId, int seconds, string subject)
    {
        if (seconds <= 0) return;
        var t = new ToastItem { OutboxId = outboxId, Subject = Shorten(subject, 40), Until = DateTimeOffset.Now.AddSeconds(seconds) };
        t.Refresh(DateTimeOffset.Now);
        Toasts.Add(t);
        _toastTimer.Start();
    }

    /// <summary>Send later: a short note that it's scheduled.</summary>
    public void ShowScheduled(long outboxId, DateTimeOffset when, string subject)
    {
        Toasts.Add(new ToastItem
        {
            OutboxId = outboxId, CanUndo = false, Until = DateTimeOffset.Now.AddSeconds(6),
            Text = $"“{Shorten(subject, 40)}” will be sent {TimePresets.Describe(when, DateTime.Now)}.",
        });
        _toastTimer.Start();
    }

    [RelayCommand]
    private async Task Undo(ToastItem? toast)
    {
        toast ??= Toasts.LastOrDefault(t => t.CanUndo);
        if (toast == null) return;
        Toasts.Remove(toast);
        var d = E.Recall(toast.OutboxId);
        if (d == null) { await Dialogs.Error("Undo", "Too late — the message has already been sent."); return; }
        Views.ComposeWindow.OpenDraft(d);
    }

    [RelayCommand]
    private void SendNow(ToastItem? toast)
    {
        if (toast == null) return;
        E.SendNow(toast.OutboxId);
        Toasts.Remove(toast);
    }

    [RelayCommand] private void DismissToast(ToastItem? toast) { if (toast != null) Toasts.Remove(toast); }

    private void RemoveToast(long outboxId)
    {
        foreach (var t in Toasts.Where(t => t.OutboxId == outboxId && t.CanUndo).ToList()) Toasts.Remove(t);
    }

    private void TickToasts()
    {
        var now = DateTimeOffset.Now;
        foreach (var t in Toasts.ToList())
        {
            if (t.Until <= now) Toasts.Remove(t);
            else t.Refresh(now);
        }
        if (Toasts.Count == 0) _toastTimer.Stop();
    }

    private static string Shorten(string s, int n) => string.IsNullOrEmpty(s) ? "(no subject)" : s.Length > n ? s[..n] + "…" : s;
}
