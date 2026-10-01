using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using MimeKit;

namespace Magpie.App.ViewModels;

/// <summary>The reading pane: one conversation, its toolbar actions and the AI summary / suggested replies.</summary>
public partial class ThreadViewModel : ObservableObject
{
    private readonly MailEngine _e = AppServices.Engine;
    private MainViewModel? _main;
    private CancellationTokenSource _cts = new();
    private CancellationTokenSource? _aiCts;
    private readonly Dictionary<string, List<string>> _replyCache = new();
    private bool _imagesAllowedOnce;

    public event Action? ThreadRemoved;
    /// <summary>Raised with a page URL for the WebView2 to show.</summary>
    public event Action<string>? PageReady;
    /// <summary>Another conversation was picked: show "Loading…" at once (HTML for the page body).</summary>
    public event Action<string>? Loading;

    public ObservableCollection<MessageRow> Messages { get; } = new();
    public ObservableCollection<string> Replies { get; } = new();

    [ObservableProperty] private bool _hasThread;
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isSnoozed;
    [ObservableProperty] private bool _isSetAside;
    [ObservableProperty] private string _tagsText = "";
    [ObservableProperty] private int _blockedImages;
    [ObservableProperty] private bool _canUnsubscribe;
    [ObservableProperty] private bool _isDraftFolder;

    // AI — summary (Option B) and suggested replies (Option C)
    [ObservableProperty] private bool _showSummarise;
    [ObservableProperty] private bool _summaryVisible;
    [ObservableProperty] private bool _summaryBusy;
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _summaryError = "";
    [ObservableProperty] private bool _summaryNeedsSetup;
    [ObservableProperty] private string _summaryNote = "";
    [ObservableProperty] private bool _showReplies;
    [ObservableProperty] private bool _repliesBusy;
    [ObservableProperty] private bool _repliesNeedClick;
    [ObservableProperty] private string _repliesError = "";

    public string AccountId { get; private set; } = "";
    public string ThreadKey { get; private set; } = "";
    public Account? Account => _e.AccountById(AccountId);
    public string ProviderLabel => _e.Ai.ProviderLabel;

    public ThreadViewModel() => RefreshAiVisibility();

    public void RefreshAiVisibility()
    {
        ShowSummarise = HasThread && _e.Ai.IsVisible(AiFeature.Summarise);
        var repliesVisible = _e.Ai.IsVisible(AiFeature.Replies);
        if (!repliesVisible) { Replies.Clear(); ShowReplies = false; }
        else if (HasThread && !ShowReplies) UpdateRepliesBar();
        UpdateQuickReplies();
        if (!_e.Ai.IsVisible(AiFeature.Summarise)) { SummaryVisible = false; _aiCts?.Cancel(); }
        OnPropertyChanged(nameof(ProviderLabel));
    }

    public void Clear(string title = "No conversation selected", string text = "Pick a conversation from the list.")
    {
        _cts.Cancel();
        _aiCts?.Cancel();
        HasThread = false;
        Messages.Clear();
        Replies.Clear();
        SummaryVisible = false;
        ShowReplies = false;
        ShowQuickReplies = false;
        HasDeleteTimer = false;
        HasInvite = false;
        _invite = null;
        BlockedImages = 0;
        AccountId = ThreadKey = "";
        ShowSummarise = false;
        _placeholder = (title, text);
        PageReady?.Invoke(WebHost.Publish(HtmlRenderer.Placeholder(title, text, ThemeManager.IsDark), "view"));
    }

    private (string Title, string Text) _placeholder = ("Welcome to Magpie", "Pick a conversation to read it here.");

    /// <summary>The theme changed (design B1): draw the open conversation, or the empty page, again in the new colours.</summary>
    public void Redraw()
    {
        if (!HasThread)
        {
            PageReady?.Invoke(WebHost.Publish(HtmlRenderer.Placeholder(_placeholder.Title, _placeholder.Text, ThemeManager.IsDark), "view"));
            return;
        }
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token, markRead: false);
    }

    public void Show(ThreadRow row, MainViewModel main)
    {
        _main = main;
        var same = row.AccountId == AccountId && row.ThreadKey == ThreadKey;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        if (!same)
        {
            _aiCts?.Cancel();
            SummaryVisible = false;
            SummaryText = SummaryError = SummaryNote = "";
            SummaryNeedsSetup = false;
            Replies.Clear();
            RepliesError = "";
            _imagesAllowedOnce = false;
        }
        AccountId = row.AccountId;
        ThreadKey = row.ThreadKey;
        HasThread = true;
        // Q38: the previous email never stays on screen. The pane switches at once (inside the current page, no
        // navigation) to the new conversation's subject and sender; "Loading…" only when it must be downloaded.
        if (!same)
        {
            var m = row.Latest;
            var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
            Loading?.Invoke(HtmlRenderer.LoadingBody(subject, m.Sender, m.Date.LocalDateTime.ToString("ddd d MMM, HH:mm"), ThemeManager.IsDark,
                downloading: _e.Store.HasUncachedBody(AccountId, ThreadKey)));
        }
        _ = LoadAsync(_cts.Token, markRead: true);
    }

    /// <summary>Q38: the conversations next to the open one in the list (set by the list after each pick); prepared
    /// in the background so they open at once.</summary>
    public IReadOnlyList<ThreadRow> Neighbours { get; set; } = Array.Empty<ThreadRow>();

    /// <summary>Called after list reloads: re-render if the open conversation changed (new reply, flags).</summary>
    public void RefreshIfShowing(ThreadRow row)
    {
        if (row.AccountId != AccountId || row.ThreadKey != ThreadKey) return;
        var current = _e.Store.GetThread(AccountId, ThreadKey);
        if (current.Count != Messages.Count || current.Any(m => m.IsFlagged) != IsPinned || current.LastOrDefault()?.Tags != Messages.LastOrDefault()?.Tags)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _ = LoadAsync(_cts.Token, markRead: false);
        }
    }

    private async Task LoadAsync(CancellationToken ct, bool markRead)
    {
        try
        {
            // Let the list highlight and the "Loading…" page paint before any work starts.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            ct.ThrowIfCancellationRequested();
            _loadErrors = new Dictionary<long, string>();
            var rows = _e.Store.GetThread(AccountId, ThreadKey);
            if (rows.Count == 0) { Clear(); return; }
            Messages.Clear();
            foreach (var r in rows) Messages.Add(r);
            var latest = rows[^1];
            Subject = SubjectOf(rows);
            var people = rows.Select(r => IsMine(r) ? "you" : r.Sender).Distinct().Take(5);
            Meta = $"{rows.Count} message{(rows.Count == 1 ? "" : "s")} · {string.Join(", ", people)}";
            IsPinned = rows.Any(r => r.IsFlagged);
            IsSnoozed = rows.Any(r => r.SnoozeUntil > DateTimeOffset.Now && r.SnoozeUntil < MessageRow.GateMark);   // not set aside, not at the door
            IsSetAside = rows.Any(r => r.IsSetAside);
            RefreshDeleteBar();
            TagsText = latest.Tags.Replace(",", " · ");
            CanUnsubscribe = rows.Any(r => r.ListUnsubscribe.Length > 0);
            var folders = _e.Folders(AccountId).ToDictionary(f => f.Id);
            IsDraftFolder = folders.TryGetValue(latest.FolderId, out var lf) && lf.Role == FolderRole.Drafts;
            RefreshAiVisibility();

            // 1. Render from what is on this PC: the text and the pictures inside each email (design RL1).
            var bodies = new Dictionary<long, (MessageBody?, Dictionary<string, string>)>();
            foreach (var r in rows)
            {
                var b = _e.Store.GetBody(r.Id);
                bodies[r.Id] = (b, b?.Images ?? new Dictionary<string, string>());
            }
            await RenderAsync(rows, bodies, ct);

            // 2. Only what isn't here yet is read: an email not downloaded, or one saved before its pictures were kept
            //    (Q39: its text and pictures only — attachments wait for a click). Pictures too big to keep with the
            //    text come from the message file on this PC, never the server.
            bool fetched = false;
            foreach (var r in rows)
            {
                ct.ThrowIfCancellationRequested();
                var (b, _) = bodies[r.Id];
                if (b != null && !MimeText.NeedsDownload(b))
                {
                    if (b.ImagesComplete && MimeText.MissingPictures(b).Any()
                        && await Task.Run(() => _e.LocalMimeAsync(r, ct), ct) is { } local)
                    {
                        var all = new Dictionary<string, string>(b.Images, StringComparer.OrdinalIgnoreCase);
                        foreach (var (cid, uri) in MimeText.InlineImages(local)) all.TryAdd(cid, uri);
                        bodies[r.Id] = (b, all);
                        fetched = true;
                    }
                    continue;
                }
                fetched = true;
                try
                {
                    var body = await Task.Run(() => _e.FetchBodyAsync(r, ct), ct);
                    bodies[r.Id] = (body ?? b, (body ?? b)?.Images ?? new Dictionary<string, string>());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("load body failed: " + ex.Message);
                    // Keep what we had (if anything); the page shows why and a "Try again" button (design R1).
                    if (bodies[r.Id].Item1 == null) _loadErrors[r.Id] = Connector.Friendly(ex);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (fetched) await RenderAsync(rows, bodies, ct);
            await UpdateInviteAsync(rows, bodies, ct);

            if (markRead && _e.Config.MarkReadOnOpen && rows.Any(r => !r.IsSeen))
                _e.SetRead(AccountId, ThreadKey, true);

            UpdateRepliesBar();
            if (ShowReplies && !RepliesNeedClick && Replies.Count == 0) _ = GenerateRepliesAsync();
            _ = WarmNeighboursAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("open conversation", ex);
            PageReady?.Invoke(WebHost.Publish(HtmlRenderer.Placeholder("Couldn't open this conversation", ex.Message, ThemeManager.IsDark), "view"));
        }
    }

    private Dictionary<long, (MessageBody?, Dictionary<string, string>)> _lastBodies = new();

    /// <summary>Design RL1: the last pages built, so going back to a conversation that hasn't changed skips the
    /// (slow) cleaning of its HTML. Keyed by conversation; the fingerprint says whether it is still current.</summary>
    private static readonly Dictionary<string, (string Fingerprint, string Html, int Blocked)> _pageCache = new();
    private static readonly Queue<string> _pageCacheOrder = new();
    private const int PageCacheSize = 24;

    private static string Fingerprint(string subject, List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies,
        bool allow, bool dark, Dictionary<long, string> errors)
    {
        var sb = new System.Text.StringBuilder(subject).Append('|').Append(allow).Append('|').Append(dark)
            .Append('|').Append(DateTime.Now.Ticks / (TimeSpan.TicksPerMinute * 10));   // relative times ("just now") stay fresh enough
        foreach (var r in rows)
        {
            var b = bodies.TryGetValue(r.Id, out var v) ? v : default;
            sb.Append('|').Append(r.Id).Append(':').Append((int)r.Flags).Append(':').Append(r.Tags).Append(':').Append(r.SnoozeUntil?.Ticks ?? 0)
              .Append(':').Append(b.body?.Html.Length ?? -1).Append(':').Append(b.images?.Count ?? 0)
              .Append(':').Append(errors.TryGetValue(r.Id, out var e) ? e : "");
        }
        return sb.ToString();
    }

    /// <summary>Builds the page off the UI thread (cleaning big newsletters can take a moment) and shows it
    /// only if the user is still on this conversation.</summary>
    private async Task RenderAsync(List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies, CancellationToken ct)
    {
        _lastBodies = bodies;
        var subject = Subject;
        var allow = ImagesAllowed(rows);
        var dark = ThemeManager.IsDark;
        var key = AccountId + "\n" + ThreadKey;
        var fp = Fingerprint(subject, rows, bodies, allow, dark, _loadErrors);
        string url; int blocked;
        if (_pageCache.TryGetValue(key, out var hit) && hit.Fingerprint == fp)
        {
            (url, blocked) = (WebHost.Publish(hit.Html, "view"), hit.Blocked);
        }
        else
        {
            var list = BuildRenderList(AccountId, rows, bodies, _loadErrors);
            var html = "";
            (url, blocked) = await Task.Run(() =>
            {
                var result = HtmlRenderer.BuildConversation(subject, list, allow, DateTimeOffset.Now, dark, HoverDelay);
                ct.ThrowIfCancellationRequested();
                html = result.Html;
                return (WebHost.Publish(result.Html, "view"), result.BlockedImages);
            }, ct);
            PutPage(key, fp, html, blocked);
        }
        ct.ThrowIfCancellationRequested();
        BlockedImages = allow ? 0 : blocked;
        PageReady?.Invoke(url);
    }

    /// <summary>
    /// Q38: gets the conversations next to the open one ready, one at a time, after it is shown: those not on this PC
    /// are downloaded first by the account's sync; the others have their page built now, so picking them only
    /// shows it.
    /// </summary>
    private async Task WarmNeighboursAsync(CancellationToken ct)
    {
        try
        {
            var dark = ThemeManager.IsDark;
            foreach (var t in Neighbours.Take(4))
            {
                ct.ThrowIfCancellationRequested();
                var key = t.AccountId + "\n" + t.ThreadKey;
                // Reading the emails (pictures included) happens off the UI thread; only the page cache is touched on it.
                var prep = await Task.Run(() =>
                {
                    var rows = _e.Store.GetThread(t.AccountId, t.ThreadKey);
                    if (rows.Count == 0) return null;
                    if (rows.Any(r => !r.BodyCached)) { _e.WantBodies(t.AccountId, rows.Where(r => !r.BodyCached).Select(r => r.Id)); return null; }
                    var bodies = new Dictionary<long, (MessageBody?, Dictionary<string, string>)>();
                    foreach (var r in rows) { var b = _e.Store.GetBody(r.Id); bodies[r.Id] = (b, b?.Images ?? new Dictionary<string, string>()); }
                    var subject = SubjectOf(rows);
                    var allow = _e.Config.RemoteImages == RemoteImages.Always
                        || (_e.Config.RemoteImages == RemoteImages.Ask && rows.All(r => IsMine(r) || _e.Config.TrustedImageSenders.Contains(r.FromAddress, StringComparer.OrdinalIgnoreCase)));
                    var errors = new Dictionary<long, string>();
                    return new { rows, bodies, subject, allow, errors, fp = Fingerprint(subject, rows, bodies, allow, dark, errors) };
                }, ct);
                if (prep == null) continue;
                if (_pageCache.TryGetValue(key, out var hit) && hit.Fingerprint == prep.fp) continue;
                var list = BuildRenderList(t.AccountId, prep.rows, prep.bodies, prep.errors);
                var result = await Task.Run(() => HtmlRenderer.BuildConversation(prep.subject, list, prep.allow, DateTimeOffset.Now, dark, HoverDelay), ct);
                ct.ThrowIfCancellationRequested();
                PutPage(key, prep.fp, result.Html, result.BlockedImages);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("prepare next conversations: " + ex.Message); }
    }

    private static void PutPage(string key, string fp, string html, int blocked)
    {
        if (!_pageCache.ContainsKey(key))
        {
            _pageCacheOrder.Enqueue(key);
            while (_pageCacheOrder.Count > PageCacheSize) _pageCache.Remove(_pageCacheOrder.Dequeue());
        }
        _pageCache[key] = (fp, html, blocked);
    }

    private static string SubjectOf(List<MessageRow> rows)
    {
        var latest = rows[^1];
        return string.IsNullOrWhiteSpace(latest.Subject) ? "(no subject)" : Threading.StripSubjectPrefixes(rows[0].Subject) is { Length: > 0 } s ? s : latest.Subject;
    }

    /// <summary>Design HM1: hover cards open after the same delay as the folder card (Settings → Appearance).</summary>
    private int HoverDelay => _e.Config.Appearance.FolderHover.DelayMs;

    private bool ImagesAllowed(List<MessageRow> rows) =>
        _imagesAllowedOnce || _e.Config.RemoteImages == RemoteImages.Always
        || (_e.Config.RemoteImages == RemoteImages.Ask && rows.All(r => IsMine(r) || _e.Config.TrustedImageSenders.Contains(r.FromAddress, StringComparer.OrdinalIgnoreCase)));

    private Dictionary<long, string> _loadErrors = new();

    private List<RenderMessage> BuildRenderList(string accountId, List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies,
        Dictionary<long, string> errors)
    {
        var account = _e.AccountById(accountId);
        var where = account?.Kind switch
        {
            AccountKind.Gmail => "Gmail",
            AccountKind.Microsoft => "Outlook",
            _ => account?.ImapHost is { Length: > 0 } h ? h : "the server",
        };
        return rows.Select((r, i) => new RenderMessage
        {
            Row = r,
            Body = bodies.TryGetValue(r.Id, out var b) ? b.body : null,
            InlineImages = bodies.TryGetValue(r.Id, out var b2) ? b2.images : new(),
            Expanded = i == rows.Count - 1 || !r.IsSeen || rows.Count <= 2,
            IsMine = IsMine(r),
            LoadingText = $"Downloading from {where}…",
            LoadError = errors.TryGetValue(r.Id, out var err) ? err : null,
        }).ToList();
    }

    /// <summary>"Try again" on a message that couldn't be downloaded.</summary>
    public void RetryLoad()
    {
        if (!HasThread) return;
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token, markRead: false);
    }

    /// <summary>Re-render the open conversation right away (e.g. after "Show images").</summary>
    private void Render(List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies)
    {
        _lastBodies = bodies;
        var allow = ImagesAllowed(rows);
        var result = HtmlRenderer.BuildConversation(Subject, BuildRenderList(AccountId, rows, bodies, _loadErrors), allow, DateTimeOffset.Now, ThemeManager.IsDark, HoverDelay);
        BlockedImages = allow ? 0 : result.BlockedImages;
        PageReady?.Invoke(WebHost.Publish(result.Html, "view"));
    }

    private bool IsMine(MessageRow r) => _e.MyAddresses.Contains(r.FromAddress.ToLowerInvariant());

    // ───────────────────────── toolbar actions ─────────────────────────

    private async Task Act(Func<Task> action, bool removes)
    {
        try
        {
            await action();
            if (removes) ThreadRemoved?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("action failed", ex);
            Ui.Error("Magpie", ex.Message);
        }
    }

    private List<long> Folders() => _main?.ActionFolders(AccountId) ?? new();

    [RelayCommand] private Task Archive() => Act(() => _e.ArchiveAsync(AccountId, ThreadKey, Folders()), true);
    [RelayCommand] private Task Delete() => Act(() => _e.TrashAsync(AccountId, ThreadKey, Folders()), true);

    [RelayCommand]
    private void TogglePin()
    {
        _e.SetPinned(AccountId, ThreadKey, !IsPinned);
        IsPinned = !IsPinned;
    }

    [RelayCommand] private void MarkUnread() => _e.MarkLatestUnread(AccountId, ThreadKey);

    public void Snooze(DateTimeOffset until)
    {
        _e.Snooze(AccountId, ThreadKey, until);
        ThreadRemoved?.Invoke();
    }

    // ───────────────────────── auto-delete bar (design AD3) ─────────────────────────

    [ObservableProperty] private bool _hasDeleteTimer;
    [ObservableProperty] private string _deleteBarText = "";
    [ObservableProperty] private string _deleteRuleText = "";
    public string DeleteRuleId { get; private set; } = "";

    public void RefreshDeleteBar()
    {
        var timers = HasThread ? _e.Store.DeleteTimers(AccountId, ThreadKey) : new();
        HasDeleteTimer = timers.Count > 0;
        if (!HasDeleteTimer) { DeleteBarText = DeleteRuleText = DeleteRuleId = ""; return; }
        var (_, at, ruleId) = timers[0];
        DeleteRuleId = ruleId;
        DeleteBarText = AutoDelete.BarText(at, DateTimeOffset.Now);
        var rule = _e.AutoDeleteRules().FirstOrDefault(r => r.Id == ruleId);
        DeleteRuleText = rule == null ? "Rule: removed (this email keeps its date)"
            : $"Rule: {AutoDelete.Who(rule.Pattern)} · {AutoDelete.Describe(rule)}" + (rule.Paused ? " · paused" : "");
    }

    /// <summary>"Keep this one": the timer comes off this conversation only (pinning keeps it too).</summary>
    [RelayCommand]
    private void KeepFromAutoDelete()
    {
        if (!HasThread) return;
        _e.KeepFromAutoDelete(AccountId, ThreadKey);
        RefreshDeleteBar();
    }

    /// <summary>Set aside (design B7, key L): out of the Inbox without a date; in the pile it puts the conversation back.</summary>
    [RelayCommand]
    private void ToggleSetAside()
    {
        var aside = !IsSetAside;
        _e.SetAside(AccountId, ThreadKey, aside);
        IsSetAside = aside;
        ThreadRemoved?.Invoke();
    }

    [RelayCommand]
    private void Unsnooze()
    {
        _e.Unsnooze(AccountId, ThreadKey);
        IsSnoozed = false;
    }

    public void RemindMe(DateTimeOffset when, bool onlyIfNoReply) =>
        _e.RemindMe(AccountId, ThreadKey, Subject, when, always: !onlyIfNoReply);

    public bool LatestIsMine => Messages.Count > 0 && IsMine(Messages[^1]);

    public void ToggleTag(string tag)
    {
        var current = (Messages.LastOrDefault()?.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!current.Remove(tag)) current.Add(tag);
        _e.SetTags(AccountId, ThreadKey, current);
        TagsText = string.Join(" · ", current);
        foreach (var m in Messages) m.Tags = string.Join(",", current);
    }

    public IReadOnlyList<string> CurrentTags => (Messages.LastOrDefault()?.Tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);

    public void MoveTo(MailFolder dest)
    {
        _e.MoveTo(AccountId, ThreadKey, Folders(), dest);
        ThreadRemoved?.Invoke();
    }

    public void SetSenderCategory(Category cat)
    {
        var from = Messages.LastOrDefault(m => !IsMine(m))?.FromAddress;
        if (from != null) _e.SetSenderCategory(AccountId, from, cat);
    }

    public string? SenderAddress => Messages.LastOrDefault(m => !IsMine(m))?.FromAddress;

    [RelayCommand]
    private void Unsubscribe()
    {
        var header = Messages.LastOrDefault(m => m.ListUnsubscribe.Length > 0)?.ListUnsubscribe ?? "";
        var links = System.Text.RegularExpressions.Regex.Matches(header, "<([^>]+)>").Select(m => m.Groups[1].Value).ToList();
        var http = links.FirstOrDefault(l => l.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        var mailto = links.FirstOrDefault(l => l.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
        if (http != null)
        {
            if (Ui.Confirm("Unsubscribe", $"Open the sender's unsubscribe page?\n\n{http}")) Ui.OpenExternal(http);
        }
        else if (mailto != null) Views.ComposeWindow.OpenMailto(mailto, AccountId);
        else Ui.Error("Unsubscribe", "This sender didn't include an unsubscribe link.");
    }

    [RelayCommand]
    private void Reply() => Compose(ComposeMode.Reply, null);
    [RelayCommand]
    private void ReplyAll() => Compose(ComposeMode.ReplyAll, null);
    [RelayCommand]
    private void Forward() => Compose(ComposeMode.Forward, null);

    public void Compose(ComposeMode mode, long? rowId, string? prefillText = null)
    {
        var row = rowId is { } id ? Messages.FirstOrDefault(m => m.Id == id) : Messages.LastOrDefault(m => !IsMine(m)) ?? Messages.LastOrDefault();
        if (row == null) return;
        Views.ComposeWindow.Open(mode, row, prefillText);
    }

    [RelayCommand]
    private async Task EditDraft()
    {
        var row = Messages.LastOrDefault();
        if (row == null) return;
        try
        {
            var (_, mime) = await _e.LoadAsync(row, true, CancellationToken.None);
            if (mime == null) return;
            var d = Composer.FromMime(mime, AccountId, ThreadKey);
            d.SourceDraftRow = row.Id;
            Views.ComposeWindow.OpenDraft(d);
        }
        catch (Exception ex) { Ui.Error("Edit draft", Connector.Friendly(ex)); }
    }

    public async Task OpenAttachmentAsync(long rowId, int index)
    {
        if (await ExtractAttachmentAsync(rowId, index) is { } f) Views.AttachmentDialog.Show(f.Path, f.Name);
    }

    /// <summary>An attachment written to a new temporary folder (downloaded first when it isn't on this PC).
    /// Null (after telling the user why) when it can't be had.</summary>
    public async Task<(string Path, string Name)?> ExtractAttachmentAsync(long rowId, int index)
    {
        var row = Messages.FirstOrDefault(m => m.Id == rowId);
        if (row == null) return null;
        try
        {
            var (_, mime) = await _e.LoadAsync(row, true, CancellationToken.None);
            if (mime == null || MimeText.PartAt(mime, MimeText.ResolveIndex(mime, index)) is not { } entity) return null;
            var name = entity switch
            {
                MimePart p => p.FileName ?? "attachment",
                MessagePart m => (m.Message?.Subject ?? "message") + ".eml",
                _ => "attachment",
            };
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var dir = Path.Combine(Path.GetTempPath(), "Magpie", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            await using (var fs = File.Create(path))
            {
                if (entity is MimePart part) await part.Content!.DecodeToAsync(fs);
                else if (entity is MessagePart mp && mp.Message != null) await mp.Message.WriteToAsync(fs);
            }
            return (path, name);
        }
        catch (Exception ex) { Ui.Error("Attachment", Connector.Friendly(ex)); return null; }
    }

    /// <summary>Design HM1: the email's own files (not the pictures inside the text), as listed on this PC.</summary>
    public List<AttachmentInfo> AttachmentsOf(long rowId) =>
        _e.Store.GetBody(rowId)?.Attachments.Where(a => !a.Inline).ToList() ?? new();

    public MessageRow? RowById(long rowId) => Messages.FirstOrDefault(m => m.Id == rowId);

    public bool IsMyAddress(string address) => _e.MyAddresses.Contains(address.Trim().ToLowerInvariant());

    public bool PicturesTrusted(string address) =>
        _e.Config.RemoteImages == RemoteImages.Always || _e.Config.TrustedImageSenders.Contains(address, StringComparer.OrdinalIgnoreCase);

    /// <summary>E8: this sender's pictures load without asking from now on.</summary>
    public void TrustAddress(string address)
    {
        if (!_e.Config.TrustedImageSenders.Contains(address, StringComparer.OrdinalIgnoreCase)) _e.Config.TrustedImageSenders.Add(address);
        _e.Settings.Save();
        if (Messages.Count > 0 && BlockedImages > 0) Render(Messages.ToList(), _lastBodies);
    }

    /// <summary>The folder the newest email is in ("Inbox"), for the subject card.</summary>
    public string FolderName => Messages.LastOrDefault() is { } last && _e.Folders(AccountId).FirstOrDefault(f => f.Id == last.FolderId) is { } f ? f.Name : "";

    /// <summary>S7: everyone in the conversation except you.</summary>
    public List<(string Name, string Email)> People()
    {
        var list = new List<(string, string)>();
        foreach (var m in Messages)
        {
            if (!IsMine(m)) list.Add((m.FromName, m.FromAddress));
            foreach (var mb in Composer.ParseAddresses(m.To + "," + m.Cc).Mailboxes)
                if (!IsMyAddress(mb.Address)) list.Add((mb.Name ?? "", mb.Address));
        }
        return list.Where(p => p.Item2.Contains('@')).DistinctBy(p => p.Item2.ToLowerInvariant()).ToList();
    }

    /// <summary>S3: a second reader for its own window, showing this conversation.</summary>
    public ThreadRow? CurrentThreadRow => Messages.Count == 0 ? null : new ThreadRow
    {
        AccountId = AccountId, ThreadKey = ThreadKey, Latest = Messages[^1], Count = Messages.Count,
        UnreadCount = Messages.Count(m => !m.IsSeen), Flagged = IsPinned,
    };

    public void OnLink(string href)
    {
        if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) Views.ComposeWindow.OpenMailto(href, AccountId);
        else Ui.OpenExternal(href);
    }

    [RelayCommand]
    private void LoadImages()
    {
        _imagesAllowedOnce = true;
        if (Messages.Count > 0) Render(Messages.ToList(), _lastBodies);
    }

    [RelayCommand]
    private void TrustSender()
    {
        foreach (var a in Messages.Where(m => !IsMine(m)).Select(m => m.FromAddress).Distinct())
            if (!_e.Config.TrustedImageSenders.Contains(a, StringComparer.OrdinalIgnoreCase)) _e.Config.TrustedImageSenders.Add(a);
        _e.Settings.Save();
        LoadImages();
    }

    // ───────────────────────── AI: summarise (Option B) ─────────────────────────

    private ThreadForAi BuildAiThread() =>
        AiService.BuildThread(Subject, Messages.Select(m => (m, _lastBodies.TryGetValue(m.Id, out var b) ? b.Item1 : _e.Store.GetBody(m.Id))).ToList());

    [RelayCommand]
    private async Task Summarise()
    {
        SummaryVisible = true;
        SummaryError = "";
        SummaryNeedsSetup = false;
        var availability = _e.Ai.Availability(AiFeature.Summarise);
        if (availability == AiAvailability.NotConfigured)
        {
            SummaryNeedsSetup = true;
            SummaryText = "";
            return;
        }
        if (availability != AiAvailability.Ready) { SummaryVisible = false; return; }
        var thread = BuildAiThread();
        var cached = _e.Store.GetSummary(AccountId, ThreadKey, thread.Digest);
        SummaryNote = thread.Truncated ? $"Long thread — summarised the newest {thread.Included} of {thread.Total} messages." : "";
        if (cached != null) { SummaryText = cached; return; }
        if (_e.Ai.NeedsConsent(AiFeature.Summarise) && !Views.ConsentDialog.Ask(AiFeature.Summarise, thread.Included))
        {
            SummaryVisible = false;
            return;
        }
        await RunSummaryAsync(thread);
    }

    [RelayCommand]
    private async Task RegenerateSummary()
    {
        if (_e.Ai.Availability(AiFeature.Summarise) != AiAvailability.Ready) return;
        var thread = BuildAiThread();
        if (_e.Ai.NeedsConsent(AiFeature.Summarise) && !Views.ConsentDialog.Ask(AiFeature.Summarise, thread.Included)) return;
        _e.Store.ClearSummary(AccountId, ThreadKey);
        SummaryError = "";
        await RunSummaryAsync(thread);
    }

    private async Task RunSummaryAsync(ThreadForAi thread)
    {
        _aiCts?.Cancel();
        _aiCts = new CancellationTokenSource();
        var ct = _aiCts.Token;
        var key = (AccountId, ThreadKey);
        SummaryBusy = true;
        SummaryText = "";
        var sb = new StringBuilder();
        try
        {
            var text = await Task.Run(() => _e.Ai.SummariseAsync(thread, t => Ui.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                sb.Append(t);
                SummaryText = sb.ToString();
            }), ct), ct);
            if (!ct.IsCancellationRequested)
            {
                SummaryText = text.Trim();
                _e.Store.SaveSummary(key.AccountId, key.ThreadKey, thread.Digest, SummaryText);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("summary failed: " + ex.Message);
            SummaryError = ex is AiException ? ex.Message : "The AI provider could not be reached: " + ex.Message;
        }
        finally
        {
            if (!ct.IsCancellationRequested) SummaryBusy = false;
        }
    }

    [RelayCommand] private void HideSummary() { _aiCts?.Cancel(); SummaryBusy = false; SummaryVisible = false; }

    [RelayCommand] private void CopySummary() { try { System.Windows.Clipboard.SetText(SummaryText); } catch { } }

    [RelayCommand] private void OpenAiSettings() => Views.SettingsWindow.Open("AI");

    // ───────────────────────── AI: suggested replies (Option C) ─────────────────────────

    // ───────────────────────── quick replies (design B6) ─────────────────────────

    public ObservableCollection<string> QuickReplies { get; } = new();
    [ObservableProperty] private bool _showQuickReplies;
    private bool _quickSending;

    private void UpdateQuickReplies()
    {
        var list = _e.Config.QuickReplies;
        if (!QuickReplies.SequenceEqual(list)) { QuickReplies.Clear(); foreach (var q in list) QuickReplies.Add(q); }
        ShowQuickReplies = HasThread && QuickReplies.Count > 0 && Messages.Count > 0 && !IsMine(Messages[^1]) && Messages[^1].Category == Category.People;
    }

    /// <summary>One click sends the reply to the newest message from the thread, through the outbox (Undo in the toast).</summary>
    [RelayCommand]
    private async Task SendQuickReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || _quickSending || Messages.Count == 0) return;
        var original = Messages[^1];
        if (IsMine(original)) return;
        _quickSending = true;
        try
        {
            var id = await _e.QuickReplyAsync(original, text);
            if (System.Windows.Application.Current.MainWindow?.DataContext is MainViewModel vm)
                vm.ShowUndo(id, _e.Config.UndoSendSeconds, Threading.ReplySubject(original.Subject));
        }
        catch (Exception ex)
        {
            Log.Error("quick reply", ex);
            Ui.Error("Quick reply", Connector.Friendly(ex));
        }
        finally { _quickSending = false; }
    }

    private void UpdateRepliesBar()
    {
        UpdateQuickReplies();
        var visible = HasThread && _e.Ai.IsVisible(AiFeature.Replies) && Messages.Count > 0 && !IsMine(Messages[^1])
                      && Messages[^1].Category == Category.People;
        ShowReplies = visible;
        RepliesNeedClick = visible && (_e.Ai.NeedsConsent(AiFeature.Replies) || _e.Ai.Availability(AiFeature.Replies) == AiAvailability.NotConfigured);
    }

    [RelayCommand]
    private async Task GenerateReplies()
    {
        if (_e.Ai.Availability(AiFeature.Replies) == AiAvailability.NotConfigured)
        {
            RepliesError = "Set up an AI provider in Settings → AI features first.";
            return;
        }
        if (_e.Ai.NeedsConsent(AiFeature.Replies) && !Views.ConsentDialog.Ask(AiFeature.Replies, Messages.Count)) return;
        RepliesNeedClick = false;
        await GenerateRepliesAsync();
    }

    private async Task GenerateRepliesAsync()
    {
        var thread = BuildAiThread();
        if (_replyCache.TryGetValue(thread.Digest, out var cached)) { SetReplies(cached); return; }
        var key = ThreadKey;
        RepliesBusy = true;
        RepliesError = "";
        try
        {
            var myName = Account?.DisplayName is { Length: > 0 } n ? n : Account?.Email ?? "";
            var list = await Task.Run(() => _e.Ai.SuggestRepliesAsync(thread, myName, CancellationToken.None));
            _replyCache[thread.Digest] = list;
            if (key == ThreadKey) SetReplies(list);
        }
        catch (Exception ex)
        {
            if (key == ThreadKey) RepliesError = ex is AiException ? ex.Message : "Couldn't get suggestions: " + ex.Message;
        }
        finally { RepliesBusy = false; }
    }

    private void SetReplies(List<string> list)
    {
        Replies.Clear();
        foreach (var r in list) Replies.Add(r);
    }

    /// <summary>Opens a reply with the suggestion in it — never sends by itself.</summary>
    [RelayCommand]
    private void UseReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Compose(ComposeMode.Reply, null, text);
    }
}
