using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.ViewModels;

public enum NavKind { Inbox, Pinned, Snoozed, FollowUp, Scheduled, Role, Folder, Tag }

public partial class NavItem : ObservableObject
{
    public NavKind Kind { get; init; }
    public string Label { get; init; } = "";
    public string Glyph { get; init; } = "";
    public FolderRole Role { get; init; }
    public long FolderId { get; init; }
    public string? AccountId { get; init; }
    public string? TagName { get; init; }
    public string TagColor { get; init; } = "#14606E";
    public int Depth { get; init; }
    public System.Windows.Thickness Indent => new(Depth * 14, 0, 0, 0);

    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;

    public string Key => $"{Kind}|{Role}|{FolderId}|{AccountId}|{TagName}";
}

public partial class AccountNode : ObservableObject
{
    public Account Account { get; init; } = new();
    public ObservableCollection<NavItem> Folders { get; } = new();
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _hasProblem;
    [ObservableProperty] private bool _needsSignIn;
    [ObservableProperty] private bool _isExpanded;
    public string Email => Account.Email;
    public string Color => Account.Color;
}

/// <summary>One conversation row in the message list.</summary>
public sealed class ThreadItem
{
    public ThreadRow Row { get; }
    public string AccountColor { get; }
    public bool ShowAccountDot { get; }
    public ThreadItem(ThreadRow row, string accountColor, bool showAccountDot, DateTimeOffset now, string myEmail)
    {
        Row = row;
        AccountColor = accountColor;
        ShowAccountDot = showAccountDot;
        var m = row.Latest;
        var mine = m.FromAddress.Equals(myEmail, StringComparison.OrdinalIgnoreCase);
        Sender = row.Count > 1 && !string.IsNullOrEmpty(row.Participants) ? row.Participants : (mine ? "To: " + FirstRecipient(m.To) : m.Sender);
        DateText = HtmlRenderer.FriendlyDate(m.Date, now);
        if (row.SnoozeUntil is { } s && s > now) SnoozeText = "Snoozed until " + TimePresets.Describe(s, now.LocalDateTime);
        Tags = m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string FirstRecipient(string to)
    {
        var first = Composer.ParseAddresses(to).Mailboxes.FirstOrDefault();
        return first == null ? to : (string.IsNullOrEmpty(first.Name) ? first.Address : first.Name);
    }

    public string Sender { get; }
    public string Subject => string.IsNullOrWhiteSpace(Row.Latest.Subject) ? "(no subject)" : Row.Latest.Subject;
    public string Preview => Row.Latest.Preview;
    public string DateText { get; }
    public bool IsUnread => Row.UnreadCount > 0;
    public bool IsPinned => Row.Flagged;
    public bool HasAttachments => Row.HasAttachments;
    public string CountText => Row.Count > 1 ? Row.Count.ToString() : "";
    public string? SnoozeText { get; }
    public List<string> Tags { get; }
    public string Key => Row.AccountId + "|" + Row.ThreadKey;
}

public sealed class ScheduledItem
{
    public OutboxItem Item { get; }
    public ScheduledItem(OutboxItem item, string accountEmail) { Item = item; AccountEmail = accountEmail; }
    public string AccountEmail { get; }
    public string Subject => string.IsNullOrWhiteSpace(Item.Subject) ? "(no subject)" : Item.Subject;
    public string To => "To: " + Item.ToText;
    public string When => Item.Status == OutboxStatus.Failed
        ? $"Not sent yet — {Item.LastError}"
        : "Sends " + TimePresets.Describe(Item.SendAt, DateTime.Now);
    public bool Failed => Item.Status == OutboxStatus.Failed;
}

public partial class MainViewModel : ObservableObject
{
    private readonly MailEngine _e = AppServices.Engine;
    private readonly Ui.Debouncer _reload = new(TimeSpan.FromMilliseconds(250));
    private readonly Ui.Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(300));
    private readonly Ui.Debouncer _navRefresh = new(TimeSpan.FromMilliseconds(600));

    public ObservableCollection<NavItem> Smart { get; } = new();
    public ObservableCollection<AccountNode> AccountNodes { get; } = new();
    public ObservableCollection<NavItem> TagItems { get; } = new();
    public ObservableCollection<ThreadItem> Threads { get; } = new();
    public ObservableCollection<ScheduledItem> Scheduled { get; } = new();

    public ThreadViewModel Reader { get; } = new();

    [ObservableProperty] private NavItem? _current;
    [ObservableProperty] private ThreadItem? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private Category? _category = Core.Models.Category.People;
    [ObservableProperty] private string _listTitle = "Inbox";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _unreadOnly;
    [ObservableProperty] private int _peopleCount, _notificationsCount, _newslettersCount;

    // Undo send toast
    [ObservableProperty] private bool _undoVisible;
    [ObservableProperty] private string _undoText = "";
    private long _undoId;
    private System.Windows.Threading.DispatcherTimer? _undoTimer;

    public bool IsInbox => Current?.Kind == NavKind.Inbox;
    public bool ShowCategories => IsInbox && _e.Config.SmartInbox && string.IsNullOrWhiteSpace(SearchText);
    public bool IsScheduledView => Current?.Kind == NavKind.Scheduled;
    public bool HasAccounts => _e.Accounts.Count > 0;

    public MainViewModel()
    {
        if (!_e.Config.SmartInbox) _category = null;
        _e.Changed += cs => Ui.Post(() => { _reload.Run(ReloadList); if (cs.FoldersChanged) BuildNav(); else _navRefresh.Run(RefreshCounts); });
        _e.StatusChanged += (id, st) => Ui.Post(() => UpdateStatus(id, st));
        _e.OutboxChanged += () => Ui.Post(() => { _navRefresh.Run(RefreshCounts); if (IsScheduledView) _reload.Run(ReloadList); });
        _e.Settings.Changed += () => Ui.Post(() => { OnPropertyChanged(nameof(ShowCategories)); Reader.RefreshAiVisibility(); BuildNav(); });
        Reader.ThreadRemoved += () => SelectNeighbour();
        BuildNav();
        Current = Smart.FirstOrDefault();
    }

    // ───────────────────────── navigation ─────────────────────────

    public void BuildNav()
    {
        var keep = Current?.Key;
        Smart.Clear();
        Smart.Add(new NavItem { Kind = NavKind.Inbox, Label = "Inbox", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Pinned, Label = "Pinned", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Snoozed, Label = "Snoozed", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.FollowUp, Label = "Follow up", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Scheduled, Label = "Scheduled", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Sent, Label = "Sent", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Drafts, Label = "Drafts", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Archive, Label = "Archive", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Junk, Label = "Spam", Glyph = "" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Trash, Label = "Trash", Glyph = "" });

        var expanded = AccountNodes.Where(a => a.IsExpanded).Select(a => a.Account.Id).ToHashSet();
        AccountNodes.Clear();
        var folders = _e.Folders();
        foreach (var a in _e.Accounts)
        {
            var node = new AccountNode { Account = a, IsExpanded = expanded.Contains(a.Id) };
            foreach (var f in folders.Where(f => f.AccountId == a.Id).OrderBy(f => f.Role == FolderRole.Inbox ? 0 : f.Role == FolderRole.Other ? 2 : 1).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                node.Folders.Add(new NavItem
                {
                    Kind = NavKind.Folder, FolderId = f.Id, AccountId = a.Id, Label = f.Name, Role = f.Role,
                    Glyph = f.Role switch
                    {
                        FolderRole.Inbox => "", FolderRole.Sent => "", FolderRole.Drafts => "", FolderRole.Trash => "",
                        FolderRole.Junk => "", FolderRole.Archive => "", FolderRole.All => "", FolderRole.Flagged => "",
                        _ => "",
                    },
                    Depth = f.Role == FolderRole.Other ? Math.Min(f.Depth, 4) : 0,
                });
            }
            if (_e.StatusOf(a.Id) is { } st) ApplyStatus(node, st);
            AccountNodes.Add(node);
        }

        TagItems.Clear();
        foreach (var t in _e.Config.Tags)
            TagItems.Add(new NavItem { Kind = NavKind.Tag, TagName = t.Name, TagColor = t.Color, Label = t.Name, Glyph = "" });

        var all = Smart.Concat(AccountNodes.SelectMany(n => n.Folders)).Concat(TagItems).ToList();
        var restore = all.FirstOrDefault(n => n.Key == keep) ?? Smart[0];
        if (keep != null && restore.Key == keep)
        {
            // Same view as before (e.g. after saving Settings): swap in the new nav item without resetting the
            // list selection, so the open conversation stays open.
            _current = restore;
            restore.IsSelected = true;
            OnPropertyChanged(nameof(Current));
            ReloadList();
        }
        else
        {
            _current = null;
            Current = restore;
        }
        OnPropertyChanged(nameof(HasAccounts));
        RefreshCounts();
    }

    partial void OnCurrentChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue != null) newValue.IsSelected = true;
        ListTitle = newValue?.Kind switch
        {
            NavKind.Folder => $"{newValue.Label} · {_e.AccountById(newValue.AccountId ?? "")?.Email}",
            NavKind.Tag => "Tag: " + newValue.Label,
            null => "",
            _ => newValue.Label,
        };
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(ShowCategories));
        OnPropertyChanged(nameof(IsScheduledView));
        Selected = null;
        ReloadList();
    }

    [RelayCommand]
    private void Navigate(NavItem? item)
    {
        if (item == null) return;
        if (item == Current) { ReloadList(); return; }
        Current = item;
    }

    partial void OnCategoryChanged(Category? value) => ReloadList();
    partial void OnUnreadOnlyChanged(bool value) => ReloadList();
    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(ShowCategories));
        _searchDebounce.Run(ReloadList);
    }

    [RelayCommand] private void SetCategory(string? name) => Category = Enum.TryParse<Category>(name, out var c) ? c : null;

    private List<long> FolderIdsFor(NavItem nav) => nav.Kind switch
    {
        NavKind.Inbox => _e.FolderIds(FolderRole.Inbox),
        NavKind.Snoozed => _e.FolderIds(FolderRole.Inbox),
        NavKind.Pinned or NavKind.Tag => _e.AllMailFolderIds(),
        NavKind.Role => _e.FolderIds(nav.Role),
        NavKind.Folder => new List<long> { nav.FolderId },
        _ => new List<long>(),
    };

    /// <summary>Folders an action like Archive/Delete applies to, for the current view.</summary>
    public List<long> ActionFolders(string accountId)
    {
        if (Current == null) return new();
        var ids = Current.Kind switch
        {
            NavKind.Pinned or NavKind.Tag or NavKind.FollowUp => _e.Folders(accountId).Where(f => f.Synced && f.Role is not (FolderRole.Sent or FolderRole.Drafts)).Select(f => f.Id).ToList(),
            _ => FolderIdsFor(Current),
        };
        var own = _e.Folders(accountId).Select(f => f.Id).ToHashSet();
        return ids.Where(own.Contains).ToList();
    }

    public void ReloadList()
    {
        var nav = Current;
        if (nav == null) return;
        var now = DateTimeOffset.Now;
        var keepKey = Selected?.Key;

        if (nav.Kind == NavKind.Scheduled)
        {
            Threads.Clear();
            Scheduled.Clear();
            foreach (var o in _e.Outbox().Where(o => o.Status is OutboxStatus.Queued or OutboxStatus.Failed))
                Scheduled.Add(new ScheduledItem(o, _e.AccountById(o.AccountId)?.Email ?? ""));
            EmptyText = Scheduled.Count == 0 ? "Nothing scheduled. Use \"Send later\" in a new message to schedule one." : "";
            return;
        }

        List<ThreadRow> rows;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchQuery.Parse(SearchText);
        if (nav.Kind == NavKind.FollowUp)
        {
            rows = new List<ThreadRow>();
            foreach (var r in _e.DueReminders().Concat(_e.WaitingReminders()))
            {
                var t = _e.Store.GetThread(r.AccountId, r.ThreadKey);
                if (t.Count == 0) continue;
                var latest = t.Last();
                rows.Add(new ThreadRow { AccountId = r.AccountId, ThreadKey = r.ThreadKey, Latest = latest, Count = t.Count, UnreadCount = t.Count(m => !m.IsSeen), Flagged = t.Any(m => m.IsFlagged) });
            }
            rows = rows.DistinctBy(r => r.AccountId + r.ThreadKey).ToList();
        }
        else
        {
            var q = new ListQuery
            {
                FolderIds = search != null && nav.Kind == NavKind.Inbox ? _e.AllMailFolderIds() : FolderIdsFor(nav),
                Category = ShowCategories ? Category : null,
                UnreadOnly = UnreadOnly,
                FlaggedOnly = nav.Kind == NavKind.Pinned,
                Snoozed = nav.Kind == NavKind.Snoozed,
                Tag = nav.Kind == NavKind.Tag ? nav.TagName : null,
                Search = search,
                Limit = 400,
            };
            rows = _e.Store.ListThreads(q, now);
        }

        var multi = _e.Accounts.Count > 1;
        var items = rows.Select(r =>
        {
            var acc = _e.AccountById(r.AccountId);
            return new ThreadItem(r, acc?.Color ?? "#14606E", multi, now, acc?.Email ?? "");
        }).ToList();

        // Rebuild the list. While it is cleared the ListBox pushes Selected = null through the two-way
        // binding; _reloading stops that from closing the open conversation (it would blank the reader
        // every time opening a thread marks it read and the list reloads).
        _reloading = true;
        try
        {
            Scheduled.Clear();
            Threads.Clear();
            foreach (var i in items) Threads.Add(i);
            Selected = keepKey == null ? null : Threads.FirstOrDefault(t => t.Key == keepKey);
        }
        finally { _reloading = false; }

        EmptyText = Threads.Count > 0 ? "" : !HasAccounts ? "Add an account to get started." :
            search != null ? "No messages match your search." :
            nav.Kind switch
            {
                NavKind.Inbox => ShowCategories && Category != null ? $"Nothing new in {Category}." : "Inbox zero. Nice.",
                NavKind.Pinned => "Pin conversations you want to keep handy.",
                NavKind.Snoozed => "Snoozed conversations wait here until their time.",
                NavKind.FollowUp => "Use \"Remind me\" on a conversation to follow it up.",
                _ => "No conversations here.",
            };
        if (Selected != null) Reader.RefreshIfShowing(Selected.Row);
    }

    public void RefreshCounts()
    {
        var now = DateTimeOffset.Now;
        var inbox = _e.FolderIds(FolderRole.Inbox);
        foreach (var n in Smart)
        {
            n.Count = n.Kind switch
            {
                NavKind.Inbox => _e.Store.CountUnreadThreads(inbox, now),
                NavKind.Snoozed => _e.Store.CountSnoozedThreads(now),
                NavKind.FollowUp => _e.DueReminders().Count,
                NavKind.Scheduled => _e.Outbox().Count(o => o.Status is OutboxStatus.Queued or OutboxStatus.Failed),
                NavKind.Role when n.Role is FolderRole.Drafts => _e.Folders().Where(f => f.Role == FolderRole.Drafts).Sum(f => f.Total),
                _ => 0,
            };
        }
        var folderMap = _e.Folders().ToDictionary(f => f.Id);
        foreach (var node in AccountNodes)
            foreach (var f in node.Folders)
                f.Count = folderMap.TryGetValue(f.FolderId, out var mf) && f.Role is not (FolderRole.Sent or FolderRole.Trash or FolderRole.Junk or FolderRole.All) ? mf.Unread : 0;
        PeopleCount = _e.Store.CountUnreadThreads(inbox, now, Core.Models.Category.People);
        NotificationsCount = _e.Store.CountUnreadThreads(inbox, now, Core.Models.Category.Notifications);
        NewslettersCount = _e.Store.CountUnreadThreads(inbox, now, Core.Models.Category.Newsletters);
        var unread = Smart[0].Count;
        AppServices.Tray?.SetTooltip(unread > 0 ? $"Magpie — {unread} unread" : "Magpie");
    }

    private void UpdateStatus(string accountId, SyncStatus st)
    {
        var node = AccountNodes.FirstOrDefault(n => n.Account.Id == accountId);
        if (node != null) ApplyStatus(node, st);
        var problems = AccountNodes.Where(n => n.HasProblem).ToList();
        var busy = AccountNodes.Any(n => n.Status.StartsWith("Connecting") || n.Status.StartsWith("Checking"));
        StatusText = problems.Count > 0 ? $"{problems[0].Email}: {problems[0].Status}" : busy ? "Checking for mail…" : "";
    }

    private static void ApplyStatus(AccountNode node, SyncStatus st)
    {
        node.Status = st.Message;
        node.HasProblem = st.State is SyncState.Error or SyncState.Offline or SyncState.NeedsSignIn;
        node.NeedsSignIn = st.State == SyncState.NeedsSignIn;
    }

    // ───────────────────────── selection & actions ─────────────────────────

    private bool _reloading;

    partial void OnSelectedChanged(ThreadItem? value)
    {
        if (_reloading) return;
        if (value == null) { Reader.Clear(); return; }
        Reader.Show(value.Row, this);
    }

    /// <summary>After archive/delete/snooze: drop the row and open the next conversation (like eM Client).</summary>
    public void SelectNeighbour()
    {
        if (Selected == null) { Reader.Clear(); return; }
        var i = Threads.IndexOf(Selected);
        Threads.Remove(Selected);
        Selected = Threads.Count == 0 ? null : Threads[Math.Clamp(i, 0, Threads.Count - 1)];
    }

    public void SelectByKey(string accountId, string threadKey)
    {
        var item = Threads.FirstOrDefault(t => t.Row.AccountId == accountId && t.Row.ThreadKey == threadKey);
        if (item != null) Selected = item;
        else
        {
            var rows = _e.Store.GetThread(accountId, threadKey);
            if (rows.Count == 0) return;
            var tr = new ThreadRow { AccountId = accountId, ThreadKey = threadKey, Latest = rows.Last(), Count = rows.Count };
            Selected = null;
            Reader.Show(tr, this);
        }
    }

    public void MoveSelection(int delta)
    {
        if (Threads.Count == 0) return;
        var i = Selected == null ? -1 : Threads.IndexOf(Selected);
        i = Math.Clamp(i + delta, 0, Threads.Count - 1);
        Selected = Threads[i];
    }

    [RelayCommand] private void SyncAll() => _e.SyncNow();

    [RelayCommand] private void Compose() => Views.ComposeWindow.Open(ComposeMode.New, null);

    [RelayCommand]
    private void CancelScheduled(ScheduledItem? s)
    {
        if (s == null) return;
        var d = _e.Recall(s.Item.Id);
        if (d != null) Views.ComposeWindow.OpenDraft(d);
        ReloadList();
    }

    [RelayCommand]
    private void SendScheduledNow(ScheduledItem? s)
    {
        if (s == null) return;
        _e.Reschedule(s.Item.Id, DateTimeOffset.Now);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    // ───────────────────────── undo send ─────────────────────────

    public void ShowUndo(long outboxId, int seconds, string subject)
    {
        if (seconds <= 0) return;
        _undoId = outboxId;
        UndoText = $"Sending \"{(subject.Length > 40 ? subject[..40] + "…" : subject)}\"";
        UndoVisible = true;
        _undoTimer?.Stop();
        _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _undoTimer.Tick += (_, _) => { _undoTimer?.Stop(); UndoVisible = false; };
        _undoTimer.Start();
    }

    public void ShowScheduled(long outboxId, DateTimeOffset when, string subject)
    {
        _undoId = outboxId;
        UndoText = $"\"{(subject.Length > 30 ? subject[..30] + "…" : subject)}\" scheduled for {TimePresets.Describe(when, DateTime.Now)}";
        UndoVisible = true;
        _undoTimer?.Stop();
        _undoTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _undoTimer.Tick += (_, _) => { _undoTimer?.Stop(); UndoVisible = false; };
        _undoTimer.Start();
    }

    [RelayCommand]
    private void UndoSend()
    {
        _undoTimer?.Stop();
        UndoVisible = false;
        var d = _e.Recall(_undoId);
        if (d == null) { Ui.Error("Undo", "Too late — the message has already been sent."); return; }
        Views.ComposeWindow.OpenDraft(d);
    }
}
