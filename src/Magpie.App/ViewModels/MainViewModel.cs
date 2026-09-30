using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Settings;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.ViewModels;

public enum NavKind { Inbox, Pinned, Snoozed, SetAside, DeletingSoon, FollowUp, Scheduled, Role, Folder, Tag }

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
    /// <summary>Folder tree (design Q2): server path, parent path ("" = top level), and whether it has subfolders.</summary>
    public string Path { get; init; } = "";
    public string ParentPath { get; init; } = "";
    public bool HasChildren { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(BadgeText), nameof(HasBadge))] private int _count;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Badge on the icon rail (design H1): the unread count, or the count for count-only rows.</summary>
    public string BadgeText => Count > 0 ? FolderCounts.Format(Count) : "";
    public bool HasBadge => Count > 0;
    /// <summary>Icon key (design C1) and the number shown (design C2): "3" + " / 10", or "15", or nothing.</summary>
    public string IconKey { get; init; } = "folder";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCount))] private string _countMain = "";
    [ObservableProperty] private string _countRest = "";
    [ObservableProperty] private System.Windows.Media.Brush _countBrush = System.Windows.Media.Brushes.Gray;
    [ObservableProperty] private bool _countBold;
    public bool HasCount => CountMain.Length > 0;

    public void SetCounts(int unread, int total, CountKind kind, CountsMode mode)
    {
        Count = kind == CountKind.CountOnly ? total : unread;
        var t = FolderCounts.Display(unread, total, kind, mode);
        CountMain = t.Main;
        CountRest = t.Rest;
        CountBold = kind == CountKind.UnreadAndTotal && !t.Dim;
        CountBrush = kind != CountKind.UnreadAndTotal ? Icons.Neutral : t.Dim ? Icons.Dim : Icons.Accent(IconKey);
    }
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;

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
    /// <summary>Unread conversations across this account's receiving folders (design C2).</summary>
    [ObservableProperty] private string _unreadText = "";
    public string Email => Account.Email;
    public string Color => Account.Color;
    /// <summary>Tooltip: the address too, for the icon rail where only the dot shows.</summary>
    public string Tip => string.IsNullOrEmpty(Status) ? Email : Email + " — " + Status;
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(Tip));
}

/// <summary>One conversation row in the message list.</summary>
public sealed partial class ThreadItem : ObservableObject
{
    /// <summary>Ticked for a bulk action (design H3).</summary>
    [ObservableProperty] private bool _isChecked;
    /// <summary>A menu opened from this row's buttons is showing: the buttons stay while it is.</summary>
    [ObservableProperty] private bool _menuOpen;
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
        if (row.IsSnoozed(now)) SnoozeText = "Snoozed until " + TimePresets.Describe(row.SnoozeUntil!.Value, now.LocalDateTime);
        Tags = m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        TagChips = Tags.Select(TagChip.For).ToList();
        // Auto-delete tag (design AD3): red under 48 h (and every OTP), amber under 30 days, grey later; pinned = kept.
        if (row.DeleteAt is { } at && !row.Flagged)
        {
            var rule = DeleteRules.GetValueOrDefault(row.DeleteRule);
            var otp = rule?.Otp == true;
            DeleteTag = AutoDelete.TagText(at, now, otp);
            (DeleteTagBg, DeleteTagFg) = Views.AutoDeleteDialog.TagColours(AutoDelete.Urgency(at, now, otp));
            DeleteTagTip = (rule == null ? "Its rule was removed" : $"Rule: {AutoDelete.Who(rule.Pattern)} · {AutoDelete.Describe(rule)}")
                           + $"\nMoves to Trash {at.ToLocalTime():d MMM yyyy, HH:mm}";
        }
        // Coloured initials (design C1): the other person's, stable per address.
        var who = mine ? FirstRecipient(m.To) : m.Sender;
        var addr = mine ? (Composer.ParseAddresses(m.To).Mailboxes.FirstOrDefault()?.Address ?? who) : m.FromAddress;
        Initials = MakeInitials(who);
        AvatarBrush = Icons.Brush(AvatarPalette[(int)(StableHash(addr.ToLowerInvariant()) % (uint)AvatarPalette.Length)]);
    }

    private static readonly string[] AvatarPalette = { "#2563EB", "#B91C1C", "#EA580C", "#6D28D9", "#0F766E", "#A21CAF", "#334155", "#0369A1", "#15803D", "#B45309", "#BE185D", "#4338CA" };

    private static uint StableHash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }

    internal static string MakeInitials(string name)
    {
        var clean = new string((name ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '@').ToArray()).Trim();
        if (clean.Contains('@')) clean = clean.Split('@')[0].Replace('.', ' ');
        var parts = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0][0]).ToString();
        return parts.Length > 1 ? first + char.ToUpperInvariant(parts[^1][0]) : parts[0].Length > 1 ? first + char.ToLowerInvariant(parts[0][1]) : first;
    }

    public string Initials { get; }
    public System.Windows.Media.Brush AvatarBrush { get; }
    public List<TagChip> TagChips { get; }

    /// <summary>Auto-delete rules by id, for the row tags' colours and hover text (refreshed when the list reloads).</summary>
    public static Dictionary<string, AutoDeleteRule> DeleteRules { get; set; } = new();
    public string DeleteTag { get; } = "";
    public System.Windows.Media.Brush? DeleteTagBg { get; }
    public System.Windows.Media.Brush? DeleteTagFg { get; }
    public string DeleteTagTip { get; } = "";
    public bool HasDeleteTag => DeleteTag.Length > 0;
    public bool HasTagLine => TagChips.Count > 0 || HasDeleteTag;

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
    /// <summary>Set for a draft kept on this PC (design F1); such rows open in Compose instead of the reader.</summary>
    public long? LocalDraftId { get; init; }
    public string LocalBadge => LocalDraftId != null ? (LocalPending ? "On this PC · uploads when online" : "On this PC") : "";
    public bool LocalPending { get; init; }
    public string? SnoozeText { get; }
    public List<string> Tags { get; }
    public string Key => Row.AccountId + "|" + Row.ThreadKey;
}

/// <summary>A tag on a list row, in the tag's own colour.</summary>
public sealed class TagChip
{
    public string Name { get; init; } = "";
    public System.Windows.Media.Brush Foreground { get; init; } = System.Windows.Media.Brushes.Teal;
    public System.Windows.Media.Brush Background { get; init; } = System.Windows.Media.Brushes.Transparent;

    /// <summary>Tag name → colour, from Settings (refreshed when the list reloads).</summary>
    public static Dictionary<string, string> Colors { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static TagChip For(string name)
    {
        var hex = Icons.ForTheme(Colors.TryGetValue(name, out var c) ? c : "#14606E");
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var bg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(Icons.Dark ? (byte)0x2E : (byte)0x24, color.R, color.G, color.B));
        bg.Freeze();
        return new TagChip { Name = name, Foreground = Icons.Brush(hex), Background = bg };
    }
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

/// <summary>One "Sending … Undo" row (design F2).</summary>
public partial class UndoToast : ObservableObject
{
    public long OutboxId { get; }
    private readonly bool _showCountdown;
    [ObservableProperty] private string _countdown = "";

    /// <summary>After "Send now": the row says "Sent ✓" for a moment, without buttons (design SN1).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasButtons), nameof(CanSendNow), nameof(HasEdit))] private bool _done;
    /// <summary>A toast for something other than a send (e.g. a new auto-delete rule): its own Undo, and optionally Edit.</summary>
    public Action? UndoAction { get; init; }
    public Action? EditAction { get; init; }
    public bool HasEdit => EditAction != null && !Done;
    public bool HasButtons => !Done;
    /// <summary>"Send now" only for a message in its undo window — not for one scheduled for later (that has its own button in Scheduled).</summary>
    public bool CanSendNow => !Done && _showCountdown;

    public UndoToast(long outboxId, string text, DateTimeOffset until, bool showCountdown)
    {
        OutboxId = outboxId;
        _text = text;
        Until = until;
        _showCountdown = showCountdown;
        Refresh(DateTimeOffset.Now);
    }

    public DateTimeOffset Until { get; private set; }
    [ObservableProperty] private string _text;

    public void MarkSent(string subject)
    {
        Done = true;
        Text = $"Sent \"{subject}\" ✓";
        Countdown = "";
        Until = DateTimeOffset.Now.AddSeconds(3);
    }

    public void Refresh(DateTimeOffset now)
    {
        if (Done) return;
        var left = (int)Math.Ceiling((Until - now).TotalSeconds);
        Countdown = _showCountdown && left > 0 ? left + " s" : "";
    }
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
    public StatusBarViewModel StatusBar { get; }

    /// <summary>Reading-pane toolbar (designs C1, C3): the buttons the user chose, in their order.</summary>
    public ObservableCollection<ToolbarButtonVm> ToolbarButtons { get; } = new();
    /// <summary>Buttons the user hid: they live in ··· More.</summary>
    public List<ToolbarButtonVm> HiddenButtons { get; private set; } = new();

    [ObservableProperty] private NavItem? _current;
    [ObservableProperty] private ThreadItem? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private Category? _category = Core.Models.Category.People;
    [ObservableProperty] private string _listTitle = "Inbox";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _unreadOnly;
    [ObservableProperty] private string _peopleCount = "", _notificationsCount = "", _newslettersCount = "";

    // Sidebar sections (design Q2) — open/closed is remembered in settings.
    [ObservableProperty] private bool _foldersOpen = true;
    [ObservableProperty] private bool _accountsOpen = true;
    [ObservableProperty] private bool _tagsOpen = true;
    /// <summary>Shown on a closed FOLDERS heading: unread count in the unified inbox.</summary>
    public string FoldersBadge => !FoldersOpen && Smart.Count > 0 && Smart[0].Count > 0 ? Smart[0].Count.ToString() : "";
    /// <summary>Shown on a closed ACCOUNTS heading when an account needs attention.</summary>
    public string AccountsWarning => AccountsOpen ? "" :
        AccountNodes.Any(n => n.NeedsSignIn) ? $"{AccountNodes.Count(n => n.NeedsSignIn)} needs sign-in" :
        AccountNodes.Any(n => n.HasProblem) ? "check connection" : "";


    public bool IsInbox => Current?.Kind == NavKind.Inbox;
    public bool ShowCategories => IsInbox && _e.Config.SmartInbox && string.IsNullOrWhiteSpace(SearchText);
    public bool IsScheduledView => Current?.Kind == NavKind.Scheduled;
    public bool HasAccounts => _e.Accounts.Count > 0;

    public MainViewModel()
    {
        if (!_e.Config.SmartInbox) _category = null;
        var sb = _e.Config.Sidebar;
        _foldersOpen = sb.FoldersOpen;
        _accountsOpen = sb.AccountsOpen;
        _tagsOpen = sb.TagsOpen;
        _e.Changed += cs => Ui.Post(() => { _reload.Run(ReloadList); if (cs.FoldersChanged) BuildNav(); else _navRefresh.Run(RefreshCounts); });
        _e.StatusChanged += (id, st) => Ui.Post(() => UpdateStatus(id, st));
        _e.OutboxChanged += () => Ui.Post(() => { _navRefresh.Run(RefreshCounts); if (IsScheduledView) _reload.Run(ReloadList); });
        _e.Settings.Changed += () => Ui.Post(() =>
        {
            Icons.Colourful = _e.Config.Appearance.Colourful;   // before the counts are coloured again
            OnPropertyChanged(nameof(ShowCategories));
            Reader.RefreshAiVisibility();
            BuildNav();
        });
        ThemeManager.Changed += () => { BuildNav(); _reload.Run(ReloadList); Reader.Redraw(); };
        _e.GateChanged += () => Ui.Post(RefreshGate);
        _e.AutoDeleteChanged += () => Ui.Post(() =>
        {
            var want = _e.AutoDeleteRules().Count > 0 || _e.Store.CountDeletingSoon(DateTimeOffset.Now.AddDays(7)) > 0;
            if (want != Smart.Any(n => n.Kind == NavKind.DeletingSoon)) BuildNav(); else _navRefresh.Run(RefreshCounts);
            Reader.RefreshDeleteBar();
        });   // counts are coloured per theme; the reader is HTML
        Reader.ThreadRemoved += () => SelectNeighbour();
        _e.Settings.Changed += () => Ui.Post(BuildToolbar);
        BuildToolbar();
        var w = _e.Config.Window;
        _sidebarWidth = w.SidebarWidth is >= WindowPlacement.SidebarMin and <= WindowPlacement.SidebarMax ? w.SidebarWidth : WindowPlacement.SidebarDefault;
        _sidebarRail = w.SidebarRail;
        _sidebarHidden = w.SidebarHidden;
        StatusBar = new StatusBarViewModel(this);
        BuildNav();
        Current = Smart.FirstOrDefault();
        RefreshGate();
    }

    // ───────────────────────── version in the title bar (design V1) ─────────────────────────

    public string VersionText => UpdateService.Current.ToString();
    public string VersionTip => $"Magpie {UpdateService.Current} · released {SettingsViewModel.ReleaseDate} · click for About";

    // ───────────────────────── Gatekeeper banner + Set aside pile (design B7) ─────────────────────────

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowGateBanner), nameof(GateBannerText))] private int _gateCount;
    public bool ShowGateBanner => GateCount > 0 && Current?.Kind == NavKind.Inbox;
    public string GateBannerText => GateCount == 1 ? "1 new sender wants to reach you" : $"{GateCount} new senders want to reach you";
    public bool IsSetAsideView => Current?.Kind == NavKind.SetAside;
    public bool IsDeletingSoonView => Current?.Kind == NavKind.DeletingSoon;

    /// <summary>"Keep all" in Deleting soon (design AD4): every timer due in the next 7 days comes off.</summary>
    [RelayCommand]
    private void KeepAllDeleting()
    {
        var n = Threads.Count(t => t.LocalDraftId == null);
        if (n == 0) return;
        if (!Ui.Confirm("Keep all", n == 1 ? "Keep this conversation (take its auto-delete timer off)?" : $"Keep all {n} conversations (take their auto-delete timers off)?")) return;
        _e.KeepAllDeletingSoon();
        ReloadList();
    }

    public void RefreshGate()
    {
        _ = Task.Run(() => _e.GateSenders().Count).ContinueWith(t =>
        {
            if (!t.IsFaulted) Ui.Post(() => GateCount = t.Result);
        }, TaskScheduler.Default);
    }

    [RelayCommand] private void ReviewGate() => Views.GatekeeperWindow.Open();

    /// <summary>"Clear all" in the Set aside pile: every conversation goes back to the Inbox.</summary>
    [RelayCommand]
    private void ClearSetAside()
    {
        var items = Threads.Where(t => t.LocalDraftId == null).ToList();
        if (items.Count == 0) return;
        if (!Ui.Confirm("Clear all", items.Count == 1 ? "Put this conversation back in the Inbox?" : $"Put all {items.Count} conversations back in the Inbox?")) return;
        foreach (var t in items) _e.SetAside(t.Row.AccountId, t.Row.ThreadKey, false);
        Reader.Clear();
        ReloadList();
    }

    // ───────────────────────── navigation ─────────────────────────

    public void BuildNav()
    {
        var keep = Current?.Key;
        // Keep the numbers on screen while the new ones are counted (no blank flicker after Settings → Save).
        var oldCounts = Smart.Concat(AccountNodes.SelectMany(n => n.Folders)).Concat(TagItems)
            .GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        var oldAccountUnread = AccountNodes.ToDictionary(n => n.Account.Id, n => n.UnreadText);
        Smart.Clear();
        Smart.Add(new NavItem { Kind = NavKind.Inbox, Label = "Inbox", Glyph = "", IconKey = "inbox" });
        Smart.Add(new NavItem { Kind = NavKind.Pinned, Label = "Pinned", Glyph = "", IconKey = "pin" });
        Smart.Add(new NavItem { Kind = NavKind.Snoozed, Label = "Snoozed", Glyph = "", IconKey = "snooze" });
        Smart.Add(new NavItem { Kind = NavKind.SetAside, Label = "Set aside", Glyph = "", IconKey = "setaside" });
        if (_e.AutoDeleteRules().Count > 0 || _e.Store.CountDeletingSoon(DateTimeOffset.Now.AddDays(7)) > 0)
            Smart.Add(new NavItem { Kind = NavKind.DeletingSoon, Label = "Deleting soon", Glyph = "", IconKey = "clock" });
        Smart.Add(new NavItem { Kind = NavKind.FollowUp, Label = "Follow up", Glyph = "", IconKey = "followup" });
        Smart.Add(new NavItem { Kind = NavKind.Scheduled, Label = "Scheduled", Glyph = "", IconKey = "scheduled" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Sent, Label = "Sent", Glyph = "", IconKey = "sent" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Drafts, Label = "Drafts", Glyph = "", IconKey = "drafts" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Archive, Label = "Archive", Glyph = "", IconKey = "archive" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Junk, Label = "Spam", Glyph = "", IconKey = "spam" });
        Smart.Add(new NavItem { Kind = NavKind.Role, Role = FolderRole.Trash, Label = "Trash", Glyph = "", IconKey = "delete" });

        foreach (var old in AccountNodes) old.PropertyChanged -= OnAccountNodeChanged;
        AccountNodes.Clear();
        var sbs = _e.Config.Sidebar;
        var openAccounts = sbs.OpenAccounts.ToHashSet();
        var openFolders = sbs.OpenFolders.ToHashSet();
        var folders = _e.Folders();
        foreach (var a in _e.Accounts)
        {
            var node = new AccountNode { Account = a, IsExpanded = openAccounts.Contains(a.Id) };
            foreach (var (f, depth, parent) in FolderTree.Order(folders.Where(f => f.AccountId == a.Id)))
            {
                var item = new NavItem
                {
                    Kind = NavKind.Folder, FolderId = f.Id, AccountId = a.Id, Label = f.Name, Role = f.Role,
                    Path = f.Path, ParentPath = parent, IconKey = Icons.ForRole(f.Role),
                    Glyph = f.Role switch
                    {
                        FolderRole.Inbox => "", FolderRole.Sent => "", FolderRole.Drafts => "", FolderRole.Trash => "",
                        FolderRole.Junk => "", FolderRole.Archive => "", FolderRole.All => "", FolderRole.Flagged => "",
                        _ => "",
                    },
                    Depth = Math.Min(depth, 5),
                };
                item.IsExpanded = openFolders.Contains(a.Id + "|" + f.Path);
                node.Folders.Add(item);
            }
            var parents = node.Folders.Select(i => i.ParentPath).Where(p => p.Length > 0).ToHashSet();
            foreach (var i in node.Folders) i.HasChildren = parents.Contains(i.Path);
            ApplyFolderVisibility(node);
            if (_e.StatusOf(a.Id) is { } st) ApplyStatus(node, st);
            node.PropertyChanged += OnAccountNodeChanged;
            AccountNodes.Add(node);
        }

        TagItems.Clear();
        TagChip.Colors = _e.Config.Tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Color, StringComparer.OrdinalIgnoreCase);
        foreach (var t in _e.Config.Tags)
            TagItems.Add(new NavItem { Kind = NavKind.Tag, TagName = t.Name, TagColor = t.Color, Label = t.Name, Glyph = "", IconKey = "tag" });

        var all = Smart.Concat(AccountNodes.SelectMany(n => n.Folders)).Concat(TagItems).ToList();
        foreach (var n in all)
            if (oldCounts.TryGetValue(n.Key, out var o))
            {
                n.Count = o.Count;
                n.CountMain = o.CountMain;
                n.CountRest = o.CountRest;
                n.CountBrush = o.CountBrush;
                n.CountBold = o.CountBold;
            }
        foreach (var node in AccountNodes)
            if (oldAccountUnread.TryGetValue(node.Account.Id, out var u)) node.UnreadText = u;
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
        OnPropertyChanged(nameof(ShowGateBanner));
        OnPropertyChanged(nameof(IsSetAsideView));
        OnPropertyChanged(nameof(IsDeletingSoonView));
        OnPropertyChanged(nameof(ShowCategories));
        OnPropertyChanged(nameof(IsScheduledView));
        Selected = null;
        ClearSelection();
        if (newValue?.Kind is NavKind.Folder or NavKind.Role) _e.Prioritise(FolderIdsFor(newValue));
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
        NavKind.Snoozed or NavKind.SetAside or NavKind.DeletingSoon => _e.FolderIds(FolderRole.Inbox),
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
            // Not Gmail's All Mail / Starred / Important: "moving" a copy out of those would unstar or un-archive the email.
            NavKind.Pinned or NavKind.Tag or NavKind.FollowUp => _e.Folders(accountId)
                .Where(f => f.Synced && f.Role is not (FolderRole.Sent or FolderRole.Drafts or FolderRole.All or FolderRole.Flagged or FolderRole.Important)).Select(f => f.Id).ToList(),
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
                SetAside = nav.Kind == NavKind.SetAside,
                DeletingBefore = nav.Kind == NavKind.DeletingSoon ? now.AddDays(7) : null,
                Tag = nav.Kind == NavKind.Tag ? nav.TagName : null,
                Search = search,
                Limit = 400,
            };
            rows = _e.Store.ListThreads(q, now);
        }

        // Conversations a bulk action is about to remove (design H3: Undo still possible) are not shown.
        if (_pendingBulk != null) rows = rows.Where(r => !_pendingBulk.Keys.Contains(r.AccountId + "|" + r.ThreadKey)).ToList();
        var multi = _e.Accounts.Count > 1;
        ThreadItem.DeleteRules = _e.AutoDeleteRules().ToDictionary(r => r.Id);
        var checkedKeys = Threads.Where(t => t.IsChecked).Select(t => t.Key).ToHashSet();
        var items = rows.Select(r =>
        {
            var acc = _e.AccountById(r.AccountId);
            return new ThreadItem(r, acc?.Color ?? "#14606E", multi, now, acc?.Email ?? "") { IsChecked = checkedKeys.Contains(r.AccountId + "|" + r.ThreadKey) };
        }).ToList();

        // Drafts kept on this PC (design F1) are listed first in Drafts views.
        var draftsAccount = nav.Kind == NavKind.Role && nav.Role == FolderRole.Drafts ? "*"
            : nav.Kind == NavKind.Folder && nav.Role == FolderRole.Drafts ? nav.AccountId : null;
        if (draftsAccount != null && search == null)
        {
            var locals = _e.LocalDrafts().Where(l => draftsAccount == "*" || l.AccountId == draftsAccount).Select(l =>
            {
                var acc = _e.AccountById(l.AccountId);
                var row = new ThreadRow
                {
                    AccountId = l.AccountId, ThreadKey = "local:" + l.Id, Count = 1,
                    Latest = new MessageRow
                    {
                        AccountId = l.AccountId, FromAddress = acc?.Email ?? "", To = l.ToText, Subject = l.Subject,
                        Preview = l.Preview, Date = l.Updated.ToLocalTime(), SortDate = l.Updated, Flags = MessageFlags.Seen,
                    },
                };
                return new ThreadItem(row, acc?.Color ?? "#14606E", multi, now, acc?.Email ?? "") { LocalDraftId = l.Id, LocalPending = l.PendingUpload };
            });
            items.InsertRange(0, locals);
        }

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
        RefreshSelectionCount();

        EmptyText = Threads.Count > 0 ? "" : !HasAccounts ? "Add an account to get started." :
            search != null ? "No messages match your search." :
            nav.Kind switch
            {
                NavKind.Inbox => ShowCategories && Category != null ? $"Nothing new in {Category}." : "Inbox zero. Nice.",
                NavKind.Pinned => "Pin conversations you want to keep handy.",
                NavKind.Snoozed => "Snoozed conversations wait here until their time.",
                NavKind.DeletingSoon => "Nothing is due to go to Trash in the next 7 days.",
                NavKind.SetAside => "Nothing set aside. Press L on a conversation to put it here, out of the Inbox, until you want it.",
                NavKind.FollowUp => "Use \"Remind me\" on a conversation to follow it up.",
                _ => _e.StillListing(FolderIdsFor(nav)) ? "Getting this folder's emails from the server…" : "No conversations here.",
            };
        if (Selected != null) Reader.RefreshIfShowing(Selected.Row);
    }

    private int _countsGen;

    private sealed class CountsSnapshot
    {
        public Dictionary<long, (int Unread, int Total)> ByFolder = new();
        public Dictionary<FolderRole, List<long>> RoleIds = new();
        public int Pinned, Snoozed, SetAside, DeletingSoon, FollowUp, Scheduled, LocalDrafts;
        public Dictionary<string, (int Unread, int Total)> Tags = new();
        public Dictionary<Category, (int Unread, int Total)> Categories = new();
    }

    /// <summary>
    /// Folder numbers (design C2), counted in conversations: unread / total where new mail arrives, a single
    /// count where you file mail yourself, nothing for Sent (Settings can switch to unread only, or off).
    /// The queries run in the background; the newest result is applied on the UI thread.
    /// </summary>
    public void RefreshCounts()
    {
        var gen = ++_countsGen;
        var mode = _e.Config.Appearance.Counts;
        _ = Task.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var snap = new CountsSnapshot { ByFolder = _e.Store.CountThreadsByFolder(now) };
            foreach (var role in new[] { FolderRole.Inbox, FolderRole.Drafts, FolderRole.Archive, FolderRole.Junk, FolderRole.Trash, FolderRole.Sent })
                snap.RoleIds[role] = _e.FolderIds(role).ToList();
            var all = _e.AllMailFolderIds();
            snap.Pinned = _e.Store.CountThreads(all, now, flaggedOnly: true).Total;
            snap.Snoozed = _e.Store.CountSnoozedThreads(now);
            snap.SetAside = _e.Store.CountSetAsideThreads();
            snap.DeletingSoon = _e.Store.CountDeletingSoon(now.AddDays(7));
            snap.FollowUp = _e.DueReminders().Count;
            snap.Scheduled = _e.OutboxSummary().Count(o => o.Status is OutboxStatus.Queued or OutboxStatus.Failed);
            snap.LocalDrafts = _e.Store.CountLocalDrafts();
            snap.Tags = _e.Store.CountThreadsByTag(all, now);
            snap.Categories = _e.Store.CountThreadsByCategory(snap.RoleIds[FolderRole.Inbox], now);
            return snap;
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) { Log.Warn("counts: " + t.Exception?.GetBaseException().Message); return; }
            Ui.Post(() => { if (gen == _countsGen) ApplyCounts(t.Result, mode); });
        }, TaskScheduler.Default);
    }

    private void ApplyCounts(CountsSnapshot snap, CountsMode mode)
    {
        (int, int) Sum(IEnumerable<long> ids)
        {
            int u = 0, t = 0;
            foreach (var id in ids) if (snap.ByFolder.TryGetValue(id, out var c)) { u += c.Unread; t += c.Total; }
            return (u, t);
        }
        List<long> Ids(FolderRole r) => snap.RoleIds.TryGetValue(r, out var l) ? l : new List<long>();
        foreach (var n in Smart)
        {
            switch (n.Kind)
            {
                case NavKind.Inbox:
                {
                    var (u, t) = Sum(Ids(FolderRole.Inbox));
                    n.SetCounts(u, t, CountKind.UnreadAndTotal, mode);
                    break;
                }
                case NavKind.Pinned: n.SetCounts(0, snap.Pinned, CountKind.CountOnly, mode); break;
                case NavKind.Snoozed: n.SetCounts(0, snap.Snoozed, CountKind.CountOnly, mode); break;
                case NavKind.SetAside: n.SetCounts(0, snap.SetAside, CountKind.CountOnly, mode); break;
                case NavKind.DeletingSoon: n.SetCounts(0, snap.DeletingSoon, CountKind.CountOnly, mode); break;
                case NavKind.FollowUp: n.SetCounts(0, snap.FollowUp, CountKind.CountOnly, mode); break;
                case NavKind.Scheduled: n.SetCounts(0, snap.Scheduled, CountKind.CountOnly, mode); break;
                case NavKind.Role when n.Role is FolderRole.Drafts:
                    n.SetCounts(0, Sum(Ids(FolderRole.Drafts)).Item2 + snap.LocalDrafts, CountKind.CountOnly, mode);
                    break;
                case NavKind.Role:
                {
                    var (u, t) = Sum(Ids(n.Role));
                    n.SetCounts(u, t, FolderCounts.KindOf(n.Role), mode);
                    break;
                }
            }
        }
        foreach (var node in AccountNodes)
        {
            var unreadInAccount = 0;
            foreach (var f in node.Folders)
            {
                var (u, t) = snap.ByFolder.TryGetValue(f.FolderId, out var c) ? c : (0, 0);
                var kind = FolderCounts.KindOf(f.Role);
                f.SetCounts(u, t, kind, mode);
                if (kind == CountKind.UnreadAndTotal && f.Role is not (FolderRole.Junk or FolderRole.All or FolderRole.Important or FolderRole.Archive))
                    unreadInAccount += u;
            }
            node.UnreadText = mode != CountsMode.Off && unreadInAccount > 0 ? FolderCounts.Format(unreadInAccount) : "";
        }
        foreach (var tag in TagItems)
        {
            var (u, t) = tag.TagName != null && snap.Tags.TryGetValue(tag.TagName, out var c) ? c : (0, 0);
            tag.SetCounts(u, t, CountKind.UnreadAndTotal, mode);
        }
        string Cat(Category c)
        {
            var (u, t) = snap.Categories.TryGetValue(c, out var x) ? x : (0, 0);
            var d = FolderCounts.Display(u, t, CountKind.UnreadAndTotal, mode);
            return d.Main + d.Rest;
        }
        PeopleCount = Cat(Core.Models.Category.People);
        NotificationsCount = Cat(Core.Models.Category.Notifications);
        NewslettersCount = Cat(Core.Models.Category.Newsletters);
        var unread = Smart.Count > 0 ? Smart[0].Count : 0;
        OnPropertyChanged(nameof(FoldersBadge));
        AppServices.Tray?.SetTooltip(unread > 0 ? $"Magpie — {unread} unread" : "Magpie");
    }

    private void UpdateStatus(string accountId, SyncStatus st)
    {
        var node = AccountNodes.FirstOrDefault(n => n.Account.Id == accountId);
        if (node != null) ApplyStatus(node, st);
        OnPropertyChanged(nameof(AccountsWarning));
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

    // ───────────────────────── sidebar sections (design Q2) ─────────────────────────

    partial void OnFoldersOpenChanged(bool value) { OnPropertyChanged(nameof(FoldersBadge)); SaveSidebar(); }
    partial void OnAccountsOpenChanged(bool value) { OnPropertyChanged(nameof(AccountsWarning)); SaveSidebar(); }
    partial void OnTagsOpenChanged(bool value) => SaveSidebar();

    private void OnAccountNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountNode.IsExpanded)) SaveSidebar();
    }

    /// <summary>Opens/closes a folder's subfolders.</summary>
    [RelayCommand]
    private void ToggleFolder(NavItem? item)
    {
        if (item == null || !item.HasChildren) return;
        item.IsExpanded = !item.IsExpanded;
        var node = AccountNodes.FirstOrDefault(n => n.Account.Id == item.AccountId);
        if (node != null) ApplyFolderVisibility(node);
        SaveSidebar();
    }

    /// <summary>A folder shows only when every ancestor is open.</summary>
    private static void ApplyFolderVisibility(AccountNode node)
    {
        var byPath = new Dictionary<string, NavItem>(StringComparer.Ordinal);
        foreach (var f in node.Folders) byPath.TryAdd(f.Path, f);
        foreach (var f in node.Folders)
        {
            var visible = true;
            var parent = f.ParentPath;
            var guard = 0;
            while (parent.Length > 0 && byPath.TryGetValue(parent, out var p) && guard++ < 32)
            {
                if (!p.IsExpanded) { visible = false; break; }
                parent = p.ParentPath;
            }
            f.IsVisible = visible;
        }
    }

    private readonly Ui.Debouncer _saveSidebar = new(TimeSpan.FromMilliseconds(500));

    private void SaveSidebar()
    {
        var sb = _e.Config.Sidebar;
        sb.FoldersOpen = FoldersOpen;
        sb.AccountsOpen = AccountsOpen;
        sb.TagsOpen = TagsOpen;
        // Only replace entries for accounts shown now; keep the rest (e.g. an account that failed to load).
        var known = AccountNodes.Select(n => n.Account.Id).ToHashSet();
        sb.OpenAccounts = sb.OpenAccounts.Where(id => !known.Contains(id))
            .Concat(AccountNodes.Where(n => n.IsExpanded).Select(n => n.Account.Id)).Distinct().ToList();
        sb.OpenFolders = sb.OpenFolders.Where(k => !known.Contains(k.Split('|')[0]))
            .Concat(AccountNodes.SelectMany(n => n.Folders).Where(f => f.HasChildren && f.IsExpanded).Select(f => f.AccountId + "|" + f.Path))
            .Distinct().ToList();
        _saveSidebar.Run(() =>
        {
            try { _e.Settings.Save(notify: false); } catch (Exception ex) { Log.Warn("save sidebar: " + ex.Message); }
        });
    }

    // ───────────────────────── selection & actions ─────────────────────────

    private bool _reloading;

    partial void OnSelectedChanged(ThreadItem? value)
    {
        if (_reloading) return;
        if (value == null) { Reader.Clear(); return; }
        if (value.LocalDraftId != null)
        {
            // Drafts kept on this PC open in Compose on click or Enter (not on arrow keys / J / K).
            Reader.Clear("Draft saved on this PC", "Click it or press Enter to open it in a new window.");
            return;
        }
        // Q38: the next three conversations and the one above get ready while this one is read.
        var i = Threads.IndexOf(value);
        Reader.Neighbours = new[] { i + 1, i + 2, i + 3, i - 1 }
            .Where(j => j >= 0 && j < Threads.Count && Threads[j].LocalDraftId == null)
            .Select(j => Threads[j].Row).ToList();
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

    /// <summary>Opens the selected local draft in Compose (or brings its open window forward).</summary>
    public void OpenSelectedLocalDraft()
    {
        if (Selected?.LocalDraftId is not { } id) return;
        if (Views.ComposeWindow.ActivateLocal(id)) return;
        if (_e.OpenLocalDraft(id) is { } d) Views.ComposeWindow.OpenDraft(d);
        else ReloadList();   // it was sent / uploaded meanwhile
    }

    /// <summary>Delete key / context menu on a draft kept on this PC (it has no server copy to move to Trash).</summary>
    public void DeleteSelectedLocalDraft()
    {
        if (Selected?.LocalDraftId is not { } id) return;
        if (_e.IsLocalDraftOpen(id)) { Views.ComposeWindow.ActivateLocal(id); return; }   // its window decides
        if (!Ui.Confirm("Delete draft", "Delete this draft saved on this PC? It has not reached the server, so it can't be restored.")) return;
        _e.DeleteLocalDraft(id);
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

    // ───────────────────────── undo send (design F2: one row per message) ─────────────────────────

    public ObservableCollection<UndoToast> Toasts { get; } = new();
    public ObservableCollection<UndoToast> VisibleToasts { get; } = new();
    [ObservableProperty] private string _toastOverflow = "";
    private const int MaxToasts = 3;
    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    public void ShowUndo(long outboxId, int seconds, string subject)
    {
        if (seconds <= 0) return;
        AddToast(new UndoToast(outboxId, $"Sending \"{Shorten(subject, 44)}\"", DateTimeOffset.Now.AddSeconds(seconds), showCountdown: true));
    }

    public void ShowScheduled(long outboxId, DateTimeOffset when, string subject)
    {
        AddToast(new UndoToast(outboxId, $"\"{Shorten(subject, 30)}\" scheduled for {TimePresets.Describe(when, DateTime.Now)}",
            DateTimeOffset.Now.AddSeconds(8), showCountdown: false));
    }

    private static string Shorten(string s, int n) => string.IsNullOrEmpty(s) ? "(no subject)" : s.Length > n ? s[..n] + "…" : s;

    private void AddToast(UndoToast t)
    {
        Toasts.Add(t);
        RefreshToasts();
        if (_toastTimer == null)
        {
            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _toastTimer.Tick += (_, _) => TickToasts();
        }
        _toastTimer.Start();
    }

    private void TickToasts()
    {
        var now = DateTimeOffset.Now;
        foreach (var t in Toasts.Where(t => t.Until <= now).ToList()) Toasts.Remove(t);
        foreach (var t in Toasts) t.Refresh(now);
        RefreshToasts();
        if (Toasts.Count == 0) _toastTimer?.Stop();
    }

    /// <summary>Newest three are shown (oldest first, newest at the bottom); the rest fold into "+N more".</summary>
    private void RefreshToasts()
    {
        var shown = Toasts.Skip(Math.Max(0, Toasts.Count - MaxToasts)).ToList();
        if (!shown.SequenceEqual(VisibleToasts))
        {
            VisibleToasts.Clear();
            foreach (var t in shown) VisibleToasts.Add(t);
        }
        var hidden = Toasts.Count - shown.Count;
        ToastOverflow = hidden > 0 ? $"+{hidden} more — see Scheduled" : "";
    }

    /// <summary>
    /// A toast with its own Undo (and Edit). With <paramref name="outboxId"/> it is a send (e.g. an invite answer):
    /// countdown and Send now as for any message.
    /// </summary>
    public void ShowActionToast(string text, Action undo, Action? edit = null, int seconds = 8, long outboxId = 0) =>
        AddToast(new UndoToast(outboxId, text, DateTimeOffset.Now.AddSeconds(seconds), showCountdown: outboxId > 0) { UndoAction = undo, EditAction = edit });

    [RelayCommand]
    private void EditToast(UndoToast? toast)
    {
        if (toast?.EditAction == null) return;
        Toasts.Remove(toast);
        RefreshToasts();
        toast.EditAction();
    }

    [RelayCommand]
    private void UndoSend(UndoToast? toast)
    {
        toast ??= Toasts.LastOrDefault(t => !t.Done);
        if (toast == null) return;
        Toasts.Remove(toast);
        RefreshToasts();
        if (toast.UndoAction != null)
        {
            try { toast.UndoAction(); }
            catch (Exception ex) { Log.Error("undo", ex); Ui.Error("Undo", ex.Message); }
            return;
        }
        var d = _e.Recall(toast.OutboxId);
        if (d == null) { Ui.Error("Undo", "Too late — the message has already been sent."); return; }
        Views.ComposeWindow.OpenDraft(d);
    }

    /// <summary>"Send now" (design SN1): skip the rest of the undo wait; the row says "Sent ✓" for a moment.</summary>
    [RelayCommand]
    private void SendNow(UndoToast? toast)
    {
        toast ??= Toasts.LastOrDefault(t => t.CanSendNow);
        if (toast == null) return;
        SendOutboxNow(toast.OutboxId);
    }

    /// <summary>Send now from the status bar or a toast.</summary>
    public void SendOutboxNow(long outboxId)
    {
        var item = _e.OutboxSummary().FirstOrDefault(o => o.Id == outboxId);
        if (item == null || !_e.SendNow(outboxId)) { Ui.Error("Send now", "That message is already on its way."); return; }
        var toast = Toasts.FirstOrDefault(t => t.OutboxId == outboxId);
        if (toast == null) { toast = new UndoToast(outboxId, "", DateTimeOffset.Now.AddSeconds(3), false); AddToast(toast); }
        toast.MarkSent(Shorten(item.Subject, 40));
        if (_toastTimer?.IsEnabled != true) _toastTimer?.Start();
    }

    /// <summary>Undo from the status bar: take this message back into a compose window.</summary>
    public void UndoOutbox(long outboxId)
    {
        var toast = Toasts.FirstOrDefault(t => t.OutboxId == outboxId);
        if (toast?.UndoAction != null) { UndoSend(toast); return; }   // e.g. an invite answer: its own undo
        if (toast != null) { Toasts.Remove(toast); RefreshToasts(); }
        var d = _e.Recall(outboxId);
        if (d == null) { Ui.Error("Undo", "Too late — the message has already been sent."); return; }
        Views.ComposeWindow.OpenDraft(d);
    }

    // ───────────────────────── sidebar width (design H1) ─────────────────────────

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SidebarColumnWidth))] private double _sidebarWidth;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SidebarWide), nameof(SidebarColumnWidth), nameof(SidebarMode))] private bool _sidebarRail;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SidebarColumnWidth), nameof(SidebarMode))] private bool _sidebarHidden;
    /// <summary>Labels, numbers and section headings show only in the full sidebar; the rail shows icons with a badge.</summary>
    public bool SidebarWide => !SidebarRail;
    public double SidebarColumnWidth => SidebarHidden ? 0 : SidebarRail ? WindowPlacement.RailWidth : SidebarWidth;
    public string SidebarMode => SidebarHidden ? "hidden" : SidebarRail ? "icon rail" : $"{SidebarWidth:0} px";

    /// <summary>While dragging the edge: narrower than the minimum snaps to the icon rail.</summary>
    public void DragSidebar(double dragged)
    {
        var (w, rail) = WindowPlacement.SnapSidebar(dragged);
        if (!rail) SidebarWidth = w;
        SidebarRail = rail;
        SidebarHidden = false;
    }

    public void ResetSidebar() { SidebarWidth = WindowPlacement.SidebarDefault; SidebarRail = false; SidebarHidden = false; SaveSidebarWidth(); }

    /// <summary>Ctrl+Shift+← / →: 40 px steps; below the minimum it becomes the rail, and back.</summary>
    public void NudgeSidebar(int direction)
    {
        if (SidebarHidden) { SidebarHidden = false; SaveSidebarWidth(); return; }
        if (SidebarRail)
        {
            if (direction > 0) { SidebarRail = false; SidebarWidth = WindowPlacement.SidebarMin; }
        }
        else if (direction < 0 && SidebarWidth <= WindowPlacement.SidebarMin) SidebarRail = true;
        else SidebarWidth = Math.Clamp(SidebarWidth + 40 * direction, WindowPlacement.SidebarMin, WindowPlacement.SidebarMax);
        SaveSidebarWidth();
    }

    public void ToggleSidebarHidden() { SidebarHidden = !SidebarHidden; SaveSidebarWidth(); }

    public void SaveSidebarWidth()
    {
        var w = _e.Config.Window;
        w.SidebarWidth = SidebarWidth;
        w.SidebarRail = SidebarRail;
        w.SidebarHidden = SidebarHidden;
        _saveSidebar.Run(() => { try { _e.Settings.Save(notify: false); } catch (Exception ex) { Log.Warn("save sidebar width: " + ex.Message); } });
    }

    // ───────────────────────── folder details on hover (design H2) ─────────────────────────

    public HoverCardViewModel HoverCard { get; } = new();
    public bool HoverCardsOn => _e.Config.Appearance.FolderHover.Enabled || SidebarRail;
    private int _hoverGen;

    /// <summary>Builds the card for a sidebar row in the background and shows it (unless another row was hovered meanwhile).</summary>
    public void ShowHoverCard(NavItem item, Action<HoverCardViewModel> open)
    {
        var gen = ++_hoverGen;
        var fh = _e.Config.Appearance.FolderHover;
        var lines = fh.Enabled ? fh.Lines : new List<string> { "unread", "total" };
        var where = item.Kind switch
        {
            NavKind.Folder => _e.AccountById(item.AccountId ?? "")?.Email ?? "",
            NavKind.Tag => "tag",
            _ => _e.Accounts.Count > 1 ? "all accounts" : "",
        };
        _ = Task.Run(() =>
        {
            var now = DateTimeOffset.Now;
            var ids = item.Kind == NavKind.Pinned ? _e.AllMailFolderIds() : FolderIdsFor(item);
            var d = item.Kind switch
            {
                NavKind.Scheduled or NavKind.FollowUp or NavKind.DeletingSoon => null,
                NavKind.Tag => _e.Store.GetFolderDetails(ids, now, tag: item.TagName),
                NavKind.Pinned => _e.Store.GetFolderDetails(ids, now, flaggedOnly: true),
                NavKind.Snoozed => _e.Store.GetFolderDetails(ids, now, snoozedOnly: true),
                NavKind.SetAside => _e.Store.GetFolderDetails(ids, now, setAsideOnly: true),
                _ => _e.Store.GetFolderDetails(ids, now),
            };
            return (d, now);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted || t.Result.d == null || gen != _hoverGen) return;
            var (d, now) = t.Result;
            Ui.Post(() =>
            {
                if (gen != _hoverGen) return;
                HoverCard.Set(item, where, lines.Select(id => new HoverLine(FolderHoverSettings.NameOf(id),
                    d!.Line(id, now, x => HtmlRenderer.FriendlyDate(x, now)), id == "unread" && d.Unread > 0 ? Icons.Accent(item.IconKey) : Icons.Ink)));
                open(HoverCard);
            });
        }, TaskScheduler.Default);
    }

    public void HideHoverCard() { _hoverGen++; HoverCard.IsOpen = false; }

    // ───────────────────────── row actions + multi-select (design H3) ─────────────────────────

    /// <summary>The action buttons on every email row, in the user's order.</summary>
    public ObservableCollection<ToolbarButtonVm> RowActionButtons { get; } = new();
    /// <summary>Design RB1: the first two chosen buttons, then (after a divider) the rest; ⋯ offers every other action.</summary>
    public ObservableCollection<ToolbarButtonVm> RowActionsLead { get; } = new();
    public ObservableCollection<ToolbarButtonVm> RowActionsRest { get; } = new();
    public bool HasRowActionsRest => RowActionsRest.Count > 0;
    /// <summary>Actions for the ⋯ menu: every row action not already on the bar.</summary>
    public IEnumerable<ToolbarButtonVm> RowActionsMore =>
        RowActionsSettings.ActionIds.Where(id => RowActionButtons.All(b => b.Id != id)).Select(id => ToolbarButtonVm.For(id, ButtonStyle.IconOnly));
    public bool RowActionsAlways => _e.Config.Appearance.RowActions.Mode == RowActionsMode.Always;
    public bool RowActionsOnHover => _e.Config.Appearance.RowActions.Mode == RowActionsMode.OnHover;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectionText))] private int _selectedCount;
    public bool HasSelection => SelectedCount > 0 || _pendingBulk != null;
    public string SelectionText => SelectedCount == 1 ? "1 selected" : $"{SelectedCount} selected";

    public void ToggleCheck(ThreadItem item)
    {
        if (item.LocalDraftId != null) return;
        item.IsChecked = !item.IsChecked;
        RefreshSelectionCount();
    }

    public void RefreshSelectionCount()
    {
        SelectedCount = Threads.Count(t => t.IsChecked);
        OnPropertyChanged(nameof(HasSelection));
    }

    public void ClearSelection()
    {
        foreach (var t in Threads) t.IsChecked = false;
        RefreshSelectionCount();
    }

    public List<ThreadItem> CheckedItems => Threads.Where(t => t.IsChecked && t.LocalDraftId == null).ToList();

    /// <summary>Runs an action on one conversation (a hover button) or on the ticked ones (the bulk bar).</summary>
    public async Task RunOnAsync(IReadOnlyList<ThreadItem> items, string id, object? arg = null)
    {
        if (items.Count == 0) return;
        try
        {
            switch (id)
            {
                case "archive": case "delete": case "spam": case "move":
                    if (id == "delete" && items.Count > 1 && _e.Config.Appearance.RowActions.ConfirmDeleteOver > 0 && items.Count > _e.Config.Appearance.RowActions.ConfirmDeleteOver
                        && !Ui.Confirm("Delete", $"Move {items.Count} conversations to Trash?")) return;
                    StartPendingBulk(items, id, arg as MailFolder);
                    return;
                case "read":
                {
                    var anyUnread = items.Any(t => t.IsUnread);
                    foreach (var t in items) _e.SetRead(t.Row.AccountId, t.Row.ThreadKey, anyUnread);
                    break;
                }
                case "markread": foreach (var t in items) _e.SetRead(t.Row.AccountId, t.Row.ThreadKey, true); break;
                case "unread": foreach (var t in items) _e.MarkLatestUnread(t.Row.AccountId, t.Row.ThreadKey); break;
                case "pin":
                {
                    var pin = items.Any(t => !t.IsPinned);
                    foreach (var t in items) _e.SetPinned(t.Row.AccountId, t.Row.ThreadKey, pin);
                    if (Selected != null && items.Contains(Selected)) Reader.IsPinned = pin;
                    break;
                }
                case "setaside":
                {
                    // In the pile the button puts conversations back; elsewhere it sets them aside.
                    var aside = Current?.Kind != NavKind.SetAside;
                    foreach (var t in items) _e.SetAside(t.Row.AccountId, t.Row.ThreadKey, aside);
                    if (Selected != null && items.Contains(Selected)) SelectNeighbour();
                    break;
                }
                case "snooze" when arg is DateTimeOffset until:
                    foreach (var t in items) _e.Snooze(t.Row.AccountId, t.Row.ThreadKey, until);
                    if (Selected != null && items.Contains(Selected)) SelectNeighbour();
                    break;
                case "remind" when arg is (DateTimeOffset when, bool always):
                    foreach (var t in items) _e.RemindMe(t.Row.AccountId, t.Row.ThreadKey, t.Subject, when, always);
                    break;
                case "tag" when arg is string tag:
                {
                    var add = items.Any(t => !t.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                    foreach (var t in items)
                    {
                        var tags = t.Tags.ToList();
                        if (add) { if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) tags.Add(tag); }
                        else tags.RemoveAll(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase));
                        _e.SetTags(t.Row.AccountId, t.Row.ThreadKey, tags);
                    }
                    if (Selected != null && items.Contains(Selected)) Reader.RefreshIfShowing(Selected.Row);
                    break;
                }
                default: return;
            }
            ClearSelection();
            ReloadList();
        }
        catch (Exception ex)
        {
            Log.Error("row action " + id, ex);
            Ui.Error("Magpie", ex.Message);
        }
        await Task.CompletedTask;
    }

    /// <summary>A bulk archive / delete / move waits a few seconds so it can be undone; the rows vanish at once.</summary>
    private sealed class PendingBulk
    {
        public HashSet<string> Keys = new();
        public List<ThreadItem> Items = new();
        public string Action = "";
        public MailFolder? Folder;
        public DateTimeOffset Until;
    }

    private PendingBulk? _pendingBulk;
    private System.Windows.Threading.DispatcherTimer? _bulkTimer;
    [ObservableProperty] private string _bulkPendingText = "";
    public bool BulkPending => _pendingBulk != null;

    private void StartPendingBulk(IReadOnlyList<ThreadItem> items, string action, MailFolder? folder)
    {
        _ = CommitPendingBulk();   // one at a time: an earlier one goes ahead now
        var secs = _e.Config.Appearance.RowActions.BulkUndoSeconds;
        var pb = new PendingBulk { Action = action, Folder = folder, Until = DateTimeOffset.Now.AddSeconds(secs), Items = items.ToList() };
        foreach (var t in items) pb.Keys.Add(t.Key);
        _pendingBulk = pb;
        foreach (var t in items) t.IsChecked = false;
        if (Selected != null && pb.Keys.Contains(Selected.Key)) { Selected = null; Reader.Clear(); }
        ReloadList();
        if (secs <= 0) { _ = CommitPendingBulk(); return; }
        _bulkTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _bulkTimer.Tick -= OnBulkTick;
        _bulkTimer.Tick += OnBulkTick;
        _bulkTimer.Start();
        RefreshBulkText();
    }

    private void OnBulkTick(object? s, EventArgs e)
    {
        if (_pendingBulk == null) { _bulkTimer?.Stop(); return; }
        if (DateTimeOffset.Now >= _pendingBulk.Until) _ = CommitPendingBulk();
        else RefreshBulkText();
    }

    private void RefreshBulkText()
    {
        var pb = _pendingBulk;
        if (pb == null) { BulkPendingText = ""; }
        else
        {
            var n = pb.Items.Count;
            var what = pb.Action switch
            {
                "archive" => "Archiving", "delete" => "Deleting", "spam" => "Moving to Spam", "move" => "Moving to " + (pb.Folder?.Name ?? "folder"), _ => pb.Action,
            };
            var left = Math.Max(1, (int)Math.Ceiling((pb.Until - DateTimeOffset.Now).TotalSeconds));
            BulkPendingText = $"{what} {n} conversation{(n == 1 ? "" : "s")} · {left} s";
        }
        OnPropertyChanged(nameof(BulkPending));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>Undo in the bulk bar: nothing has happened yet, the rows just come back.</summary>
    public void UndoBulk()
    {
        _pendingBulk = null;
        _bulkTimer?.Stop();
        RefreshBulkText();
        ReloadList();
    }

    /// <summary>Runs the waiting bulk action now (time is up, or another one starts, or the window closes).</summary>
    public Task CommitPendingBulk()
    {
        var pb = _pendingBulk;
        if (pb == null) return Task.CompletedTask;
        _pendingBulk = null;
        _bulkTimer?.Stop();
        RefreshBulkText();
        return Task.Run(async () =>
        {
            foreach (var t in pb.Items)
            {
                try
                {
                    var folders = ActionFoldersFor(t.Row.AccountId);
                    switch (pb.Action)
                    {
                        case "archive": await _e.ArchiveAsync(t.Row.AccountId, t.Row.ThreadKey, folders); break;
                        case "delete": await _e.TrashAsync(t.Row.AccountId, t.Row.ThreadKey, folders); break;
                        case "spam":
                        {
                            var junk = _e.Folders(t.Row.AccountId).FirstOrDefault(f => f.Role == FolderRole.Junk);
                            if (junk == null) throw new InvalidOperationException("This account has no Spam folder.");
                            _e.MoveTo(t.Row.AccountId, t.Row.ThreadKey, folders, junk);
                            break;
                        }
                        case "move" when pb.Folder != null && pb.Folder.AccountId == t.Row.AccountId:
                            _e.MoveTo(t.Row.AccountId, t.Row.ThreadKey, folders, pb.Folder);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("bulk " + pb.Action, ex);
                    Ui.Post(() => Ui.Error("Magpie", ex.Message));
                    break;
                }
            }
            Ui.Post(ReloadList);
        });
    }

    /// <summary>Folders an action applies to for the current view — safe to call off the UI thread.</summary>
    private List<long> ActionFoldersFor(string accountId)
    {
        var cur = Current;
        if (cur == null) return new();
        var ids = cur.Kind switch
        {
            NavKind.Pinned or NavKind.Tag or NavKind.FollowUp => _e.Folders(accountId)
                .Where(f => f.Synced && f.Role is not (FolderRole.Sent or FolderRole.Drafts or FolderRole.All or FolderRole.Flagged or FolderRole.Important)).Select(f => f.Id).ToList(),
            _ => FolderIdsFor(cur),
        };
        var own = _e.Folders(accountId).Select(f => f.Id).ToHashSet();
        return ids.Where(own.Contains).ToList();
    }

    // ───────────────────────── toolbar (designs C1, C3) ─────────────────────────

    public void BuildToolbar()
    {
        var a = _e.Config.Appearance;
        ToolbarButtons.Clear();
        foreach (var b in a.Toolbar.Where(b => b.Visible)) ToolbarButtons.Add(ToolbarButtonVm.For(b.Id, a.ButtonStyle));
        HiddenButtons = a.Toolbar.Where(b => !b.Visible).Select(b => ToolbarButtonVm.For(b.Id, a.ButtonStyle)).ToList();
        RowActionButtons.Clear();
        foreach (var id in a.RowActions.Ids) RowActionButtons.Add(ToolbarButtonVm.For(id, ButtonStyle.IconOnly));
        RowActionsLead.Clear();
        RowActionsRest.Clear();
        foreach (var (b, i) in RowActionButtons.Select((b, i) => (b, i))) (i < 2 ? RowActionsLead : RowActionsRest).Add(b);
        OnPropertyChanged(nameof(HasRowActionsRest));
        OnPropertyChanged(nameof(RowActionsAlways));
        OnPropertyChanged(nameof(RowActionsOnHover));
        OnPropertyChanged(nameof(HoverCardsOn));
    }

    [RelayCommand]
    private void ShowScheduledView()
    {
        var item = Smart.FirstOrDefault(n => n.Kind == NavKind.Scheduled);
        if (item != null) Current = item;
    }
}

/// <summary>One line of the folder hover card.</summary>
public sealed record HoverLine(string Key, string Value, System.Windows.Media.Brush Brush);

/// <summary>The folder details card (design H2).</summary>
public partial class HoverCardViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _iconKey = "folder";
    [ObservableProperty] private string _where = "";
    [ObservableProperty] private string _tagColor = "#14606E";
    [ObservableProperty] private bool _isTag;
    public ObservableCollection<HoverLine> Lines { get; } = new();

    public void Set(NavItem item, string where, IEnumerable<HoverLine> lines)
    {
        Title = item.Label;
        IconKey = item.IconKey;
        Where = where;
        IsTag = item.Kind == NavKind.Tag;
        TagColor = item.TagColor;
        Lines.Clear();
        foreach (var l in lines) Lines.Add(l);
    }
}
