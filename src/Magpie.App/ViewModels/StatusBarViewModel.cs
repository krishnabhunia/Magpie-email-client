using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

/// <summary>One piece of the status bar (design S1), e.g. "Syncing kdb3107 · Inbox 120 / 212".</summary>
public sealed class StatusSegment
{
    public string Key { get; init; } = "";
    public string Text { get; init; } = "";
    public string IconKey { get; init; } = "";
    public bool HasIcon => IconKey.Length > 0;
    public Brush Brush { get; init; } = Brushes.Black;
    public bool Bold { get; init; }
    /// <summary>0..1, or null for no progress bar.</summary>
    public double? Progress { get; init; }
    public bool HasProgress => Progress != null;
    public double ProgressWidth => 60 * Math.Clamp(Progress ?? 0, 0, 1);
    public string Link { get; init; } = "";
    public bool HasLink => Link.Length > 0;
    public IRelayCommand? LinkCommand { get; init; }
    public bool IsFirst { get; set; }

    public string Signature => $"{Key}|{Text}|{Link}|{Progress:0.00}|{Bold}";
}

public sealed class AccountActivity
{
    public string Email { get; init; } = "";
    public Brush Dot { get; init; } = Brushes.Gray;
    public string Now { get; init; } = "";
    public Brush NowBrush { get; init; } = Brushes.Black;
    public string LastSync { get; init; } = "";
    public string NewToday { get; init; } = "";
    public string Outbox { get; init; } = "";
}

public sealed class ActivityEntry
{
    public string Time { get; init; } = "";
    public string Text { get; init; } = "";
}

/// <summary>
/// The status bar (design S1): one row that says what Magpie is doing right now — online/offline, syncing,
/// what just arrived, what is being sent, problems (with their fix) and background jobs. Clicking it opens the
/// activity panel (per-account state and a short log).
/// </summary>
public partial class StatusBarViewModel : ObservableObject
{
    private readonly MailEngine _e = AppServices.Engine;
    private readonly MainViewModel _main;
    private readonly Dictionary<string, SyncStatus> _status = new();
    private readonly Dictionary<string, DateTimeOffset> _syncSince = new();
    private readonly Dictionary<string, int> _newToday = new();
    private DateTime _today = DateTime.Today;
    private (DateTimeOffset At, int Count, List<string> Senders)? _received;
    private (DateTimeOffset At, int Count)? _sent;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ObservableCollection<StatusSegment> Segments { get; } = new();
    public ObservableCollection<AccountActivity> Accounts { get; } = new();
    public ObservableCollection<ActivityEntry> Log { get; } = new();

    [ObservableProperty] private string _rightText = "";
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _panelOpen;

    /// <summary>"Sign in again" was clicked for this account id.</summary>
    public event Action<string>? SignInRequested;

    public StatusBarViewModel(MainViewModel main)
    {
        _main = main;
        IsVisible = _e.Config.Appearance.ShowStatusBar;
        foreach (var a in _e.Accounts)
            if (_e.StatusOf(a.Id) is { } st) _status[a.Id] = st;
        _e.StatusChanged += (id, st) => Ui.Post(() => OnStatus(id, st));
        _e.NewMail += rows => Ui.Post(() => OnNewMail(rows));
        _e.Sent += item => Ui.Post(() => { _cacheAt = DateTimeOffset.MinValue; OnSent(item); });
        _e.SendFailed += (item, err) => Ui.Post(() => AddLog($"Couldn't send \"{Short(item.Subject, 40)}\" — {err}"));
        _e.OutboxChanged += () => Ui.Post(() => { _cacheAt = DateTimeOffset.MinValue; Rebuild(); });
        _e.Settings.Changed += () => Ui.Post(() => { IsVisible = _e.Config.Appearance.ShowStatusBar; Rebuild(); });
        if (AppServices.Updates is { } u) u.PropertyChanged += OnOtherChanged;
        main.Reader.PropertyChanged += OnOtherChanged;
        _timer.Tick += (_, _) => Rebuild();
        // The timer starts when the window is shown (SetWindowVisible): not while Magpie starts hidden in the tray.
        Rebuild();
    }

    /// <summary>The window is hidden in the tray: nothing to show, so stop the once-a-second refresh.</summary>
    public void SetWindowVisible(bool visible)
    {
        if (visible) { _timer.Start(); _cacheAt = DateTimeOffset.MinValue; Rebuild(); }
        else _timer.Stop();
    }

    // Read at most every few seconds (and whenever the outbox changes), not on every tick.
    private DateTimeOffset _cacheAt = DateTimeOffset.MinValue;
    private List<OutboxItem> _outbox = new();
    private int _pendingOps, _pendingDrafts;

    private void RefreshCache(bool force)
    {
        var now = DateTimeOffset.Now;
        if (!force && now - _cacheAt < TimeSpan.FromSeconds(5)) return;
        _cacheAt = now;
        _outbox = _e.OutboxSummary();
        _pendingOps = _e.Accounts.Where(a => a.Enabled).Sum(a => _e.Store.PendingOpCount(a.Id));
        _pendingDrafts = _e.Store.GetLocalDrafts(pendingOnly: true).Count;
    }

    /// <summary>The update service is created after the main window; hook it once it exists.</summary>
    public void AttachUpdates(UpdateService u) { u.PropertyChanged -= OnOtherChanged; u.PropertyChanged += OnOtherChanged; Rebuild(); }

    private void OnOtherChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdateService.State) or nameof(UpdateService.Progress) or nameof(ThreadViewModel.SummaryBusy)) Rebuild();
    }

    private void OnStatus(string id, SyncStatus st)
    {
        var busy = st.State is SyncState.Syncing or SyncState.Connecting;
        if (busy && !_syncSince.ContainsKey(id)) _syncSince[id] = DateTimeOffset.Now;
        if (!busy) _syncSince.Remove(id);
        var before = _status.TryGetValue(id, out var old) ? old.State : SyncState.Idle;
        _status[id] = st;
        if (st.State != before && st.State is SyncState.NeedsSignIn or SyncState.Error or SyncState.Offline)
            AddLog($"{Email(id)}: {st.Message}");
        Rebuild();
    }

    private void OnNewMail(IReadOnlyList<MessageRow> rows)
    {
        if (rows.Count == 0) return;
        RollDay();
        foreach (var g in rows.GroupBy(r => r.AccountId)) _newToday[g.Key] = _newToday.GetValueOrDefault(g.Key) + g.Count();
        var senders = rows.OrderByDescending(r => r.Date).Select(r => r.Sender).Distinct().ToList();
        if (_received is { } prev && DateTimeOffset.Now - prev.At < TimeSpan.FromSeconds(30))
            _received = (DateTimeOffset.Now, prev.Count + rows.Count, senders.Concat(prev.Senders).Distinct().ToList());
        else
            _received = (DateTimeOffset.Now, rows.Count, senders);
        AddLog($"Received {rows.Count} new — " + string.Join(", ", rows.Take(3).Select(r => $"{r.Sender}: {Short(r.Subject, 40)}")) + (rows.Count > 3 ? $", +{rows.Count - 3}" : ""));
        Rebuild();
    }

    private void OnSent(OutboxItem item)
    {
        _sent = _sent is { } p && DateTimeOffset.Now - p.At < TimeSpan.FromSeconds(30) ? (DateTimeOffset.Now, p.Count + 1) : (DateTimeOffset.Now, 1);
        AddLog($"Sent \"{Short(item.Subject, 50)}\" to {item.ToText}");
        Rebuild();
    }

    private void RollDay()
    {
        if (_today == DateTime.Today) return;
        _today = DateTime.Today;
        _newToday.Clear();
    }

    private void AddLog(string text)
    {
        Log.Insert(0, new ActivityEntry { Time = DateTime.Now.ToString("HH:mm"), Text = text });
        while (Log.Count > 30) Log.RemoveAt(Log.Count - 1);
    }

    private string Email(string id) => _e.AccountById(id)?.Email ?? id;
    private string ShortName(string id) => Email(id).Split('@')[0];
    private static string Short(string s, int n) => string.IsNullOrEmpty(s) ? "(no subject)" : s.Length > n ? s[..n] + "…" : s;

    [RelayCommand] private void TogglePanel() { PanelOpen = !PanelOpen; if (PanelOpen) RebuildAccounts(); }
    [RelayCommand] private void SyncAll() => _e.SyncNow();

    public void Rebuild()
    {
        var now = DateTimeOffset.Now;
        var segs = new List<StatusSegment>();
        var accounts = _e.Accounts.Where(a => a.Enabled).ToList();
        var offline = accounts.Where(a => _status.TryGetValue(a.Id, out var s) && s.State == SyncState.Offline).ToList();
        var allOffline = accounts.Count > 0 && offline.Count == accounts.Count;
        RefreshCache(force: false);
        var outbox = _outbox;

        // 1. Online / offline
        if (allOffline)
        {
            segs.Add(Seg("offline", "Offline", "offline", bold: true));
            var ops = _pendingOps;
            var toSend = outbox.Count(o => o.Status is OutboxStatus.Queued or OutboxStatus.Failed && o.SendAt <= now.AddMinutes(1));
            var drafts = _pendingDrafts;
            var parts = new List<string>();
            if (toSend > 0) parts.Add($"{toSend} message{S(toSend)} to send");
            if (drafts > 0) parts.Add($"{drafts} draft{S(drafts)} to upload");
            if (ops > 0) parts.Add($"{ops} change{S(ops)} (archive, pin, read…)");
            if (parts.Count > 0) segs.Add(Seg("waiting", "Waiting: " + string.Join(" · ", parts), "clock"));
            segs.Add(Seg("retry", "Nothing is lost — it goes out when you're back online", "", neutral: true, link: "Retry now", cmd: SyncAllCommand));
        }
        else if (accounts.Count > 0)
            segs.Add(Seg("online", "Online", "check"));

        // 2. Problems (stay until fixed, with the fix)
        foreach (var a in accounts)
        {
            if (!_status.TryGetValue(a.Id, out var st)) continue;
            if (st.State == SyncState.NeedsSignIn)
                segs.Add(Seg("signin" + a.Id, $"{a.Email}: sign-in expired — mail is not syncing", "warn", bold: true, link: "Sign in again",
                    cmd: new RelayCommand(() => SignInRequested?.Invoke(a.Id))));
            else if (st.State == SyncState.Error)
                segs.Add(Seg("error" + a.Id, $"{a.Email}: {st.Message}", "warn", link: "Retry", cmd: new RelayCommand(() => _e.SyncNow(a.Id))));
            else if (st.State == SyncState.Offline && !allOffline)
                segs.Add(Seg("off" + a.Id, $"{ShortName(a.Id)}: can't reach the server — {st.Message}", "offline", link: "Retry", cmd: new RelayCommand(() => _e.SyncNow(a.Id))));
        }
        foreach (var f in outbox.Where(o => o.Status == OutboxStatus.Failed).Take(1))
            segs.Add(Seg("fail" + f.Id, $"Couldn't send \"{Short(f.Subject, 36)}\" — will retry", "warn", link: "Open Scheduled", cmd: _main.ShowScheduledViewCommand));

        // 3. Sending
        foreach (var o in outbox.Where(o => o.Status == OutboxStatus.Sending).Take(1))
            segs.Add(Seg("sending" + o.Id, $"Sending \"{Short(o.Subject, 40)}\" to {Short(o.ToText, 30)}…", "sending", bold: true));
        var undoWindow = outbox.Where(o => o.Status == OutboxStatus.Queued && o.SendAt > now && o.SendAt <= now.AddSeconds(31)).OrderBy(o => o.SendAt).ToList();
        if (undoWindow.Count > 0)
        {
            var o = undoWindow[0];
            var left = Math.Max(1, (int)Math.Ceiling((o.SendAt - now).TotalSeconds));
            var more = undoWindow.Count > 1 ? $" (+{undoWindow.Count - 1})" : "";
            segs.Add(Seg("undo" + o.Id, $"Sending \"{Short(o.Subject, 36)}\" to {Short(o.ToText, 28)} · undo {left} s{more}", "sending", link: "Undo",
                cmd: new RelayCommand(() => _main.UndoOutbox(o.Id))));
        }
        if (_sent is { } sent && now - sent.At < TimeSpan.FromSeconds(30))
            segs.Add(Seg("sent", $"Sent {sent.Count} at {sent.At.LocalDateTime:HH:mm} ✓", "sent"));

        // 4. Syncing (only if it takes a moment, so quick checks don't flicker)
        if (!allOffline)
            foreach (var a in accounts)
            {
                if (!_status.TryGetValue(a.Id, out var st)) continue;
                if (st.State == SyncState.Idle && st.Backlog > 0)
                {
                    // Older emails still being listed (headers only) — shown without the 1 s delay: it lasts a while.
                    segs.Add(Seg("sync" + a.Id, $"Getting older emails · {ShortName(a.Id)}" + (st.Folder.Length > 0 ? " · " + st.Folder : "") + $" · {st.Backlog:#,0} left", "sync"));
                    break;
                }
                if (st.State is not (SyncState.Syncing or SyncState.Connecting)) continue;
                if (!_syncSince.TryGetValue(a.Id, out var since) || now - since < TimeSpan.FromSeconds(1)) continue;
                var what = st.State == SyncState.Connecting && st.Folder.Length == 0 ? $"Connecting {ShortName(a.Id)}…"
                    : st.Total > 0 ? $"Syncing {ShortName(a.Id)} · {st.Folder} {st.Done:#,0} / {st.Total:#,0}"
                    : $"Checking {ShortName(a.Id)}" + (st.Folder.Length > 0 ? " · " + st.Folder : "…");
                segs.Add(Seg("sync" + a.Id, what, "sync", progress: st.Total > 0 ? (double)st.Done / st.Total : null));
                break;
            }

        // 5. Received
        if (_received is { } rec && now - rec.At < TimeSpan.FromSeconds(30))
        {
            var names = string.Join(", ", rec.Senders.Take(2)) + (rec.Senders.Count > 2 ? $", +{rec.Senders.Count - 2}" : "");
            segs.Add(Seg("recv", $"Received {rec.Count} new · {names}", "received", bold: true));
        }

        // 6. Background jobs
        if (AppServices.Updates is { } up)
        {
            if (up.State == UpdateState.Downloading)
                segs.Add(Seg("upd", $"Downloading update {up.NewVersion} · {up.Progress:P0}", "update", progress: up.Progress));
            else if (up.State == UpdateState.Ready)
                segs.Add(Seg("upd", $"Update {up.NewVersion} ready", "update", link: "Restart now", cmd: up.PrimaryCommand));
        }
        if (_main.Reader.SummaryBusy) segs.Add(Seg("ai", "AI: summarising thread…", "summarise"));

        if (segs.Count > 0) segs[0].IsFirst = true;
        if (!segs.Select(s => s.Signature).SequenceEqual(Segments.Select(s => s.Signature)))
        {
            Segments.Clear();
            foreach (var s in segs) Segments.Add(s);
        }

        // Right side: each account's state + what's in view
        var right = accounts.Select(a =>
        {
            if (!_status.TryGetValue(a.Id, out var st)) return ShortName(a.Id) + " …";
            return ShortName(a.Id) + st.State switch
            {
                SyncState.Syncing or SyncState.Connecting => " ↻",
                SyncState.Idle => st.LastSuccess is { } t ? $" ✓ {t.LocalDateTime:HH:mm}" : " ✓",
                _ => " ⚠",
            };
        }).ToList();
        right.Add($"{_main.Threads.Count:#,0} in view");
        RightText = string.Join(" · ", right);
        if (PanelOpen && now.Second % 5 == 0) RebuildAccounts();
    }

    private void RebuildAccounts()
    {
        RollDay();
        RefreshCache(force: true);
        var outbox = _outbox;
        Accounts.Clear();
        foreach (var a in _e.Accounts)
        {
            _status.TryGetValue(a.Id, out var st);
            var sending = outbox.Count(o => o.AccountId == a.Id && o.Status is OutboxStatus.Sending || o.AccountId == a.Id && o.Status == OutboxStatus.Queued && o.SendAt <= DateTimeOffset.Now.AddSeconds(31));
            var scheduled = outbox.Count(o => o.AccountId == a.Id && o.Status == OutboxStatus.Queued && o.SendAt > DateTimeOffset.Now.AddSeconds(31));
            var failed = outbox.Count(o => o.AccountId == a.Id && o.Status == OutboxStatus.Failed);
            var ob = new List<string>();
            if (sending > 0) ob.Add($"{sending} sending");
            if (scheduled > 0) ob.Add($"{scheduled} scheduled");
            if (failed > 0) ob.Add($"{failed} failed");
            Accounts.Add(new AccountActivity
            {
                Email = a.Email,
                Dot = Icons.Brush(string.IsNullOrEmpty(a.Color) ? "#14606E" : a.Color),
                Now = !a.Enabled ? "Paused" : st == null ? "Starting…" : st.State switch
                {
                    SyncState.Idle => st.Backlog > 0 ? $"Getting older emails · {st.Backlog:#,0} left" : "Up to date · watching Inbox",
                    SyncState.Syncing or SyncState.Connecting => st.Total > 0 ? $"Syncing {st.Folder} {st.Done:#,0} / {st.Total:#,0}" : st.Folder.Length > 0 ? "Checking " + st.Folder : st.Message,
                    _ => st.Message,
                },
                NowBrush = st?.State switch
                {
                    SyncState.Idle => Icons.Accent("sent"),
                    SyncState.Syncing or SyncState.Connecting => Icons.Accent("sync"),
                    null => Icons.Accent("drafts"),
                    _ => Icons.Accent("warn"),
                },
                LastSync = st?.LastSuccess is { } t ? t.LocalDateTime.ToString("HH:mm") : "—",
                NewToday = _newToday.GetValueOrDefault(a.Id).ToString(),
                Outbox = ob.Count == 0 ? "—" : string.Join(" · ", ob),
            });
        }
    }

    private static string S(int n) => n == 1 ? "" : "s";

    private static StatusSegment Seg(string key, string text, string icon, bool bold = false, double? progress = null, string link = "",
        IRelayCommand? cmd = null, bool neutral = false) => new()
    {
        Key = key, Text = text, IconKey = icon, Bold = bold, Progress = progress, Link = link, LinkCommand = cmd,
        Brush = neutral || icon.Length == 0 ? Icons.Accent("drafts") : Icons.Accent(icon),
    };
}
