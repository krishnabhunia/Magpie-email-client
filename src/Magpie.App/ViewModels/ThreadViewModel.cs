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
        if (!same)
        {
            var m = row.Latest;
            var subject = string.IsNullOrWhiteSpace(m.Subject) ? "(no subject)" : m.Subject;
            Loading?.Invoke(HtmlRenderer.LoadingBody(subject, m.Sender, m.Date.LocalDateTime.ToString("ddd d MMM, HH:mm"), ThemeManager.IsDark));
        }
        _ = LoadAsync(_cts.Token, markRead: true);
    }

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
            Subject = string.IsNullOrWhiteSpace(latest.Subject) ? "(no subject)" : Threading.StripSubjectPrefixes(rows[0].Subject) is { Length: > 0 } s ? s : latest.Subject;
            var people = rows.Select(r => IsMine(r) ? "you" : r.Sender).Distinct().Take(5);
            Meta = $"{rows.Count} message{(rows.Count == 1 ? "" : "s")} · {string.Join(", ", people)}";
            IsPinned = rows.Any(r => r.IsFlagged);
            IsSnoozed = rows.Any(r => r.SnoozeUntil > DateTimeOffset.Now && !r.IsSetAside);
            IsSetAside = rows.Any(r => r.IsSetAside);
            RefreshDeleteBar();
            TagsText = latest.Tags.Replace(",", " · ");
            CanUnsubscribe = rows.Any(r => r.ListUnsubscribe.Length > 0);
            var folders = _e.Folders(AccountId).ToDictionary(f => f.Id);
            IsDraftFolder = folders.TryGetValue(latest.FolderId, out var lf) && lf.Role == FolderRole.Drafts;
            RefreshAiVisibility();

            // 1. Instant render from what is stored locally.
            var bodies = new Dictionary<long, (MessageBody?, Dictionary<string, string>)>();
            foreach (var r in rows) bodies[r.Id] = (_e.Store.GetBody(r.Id), new Dictionary<string, string>());
            await RenderAsync(rows, bodies, ct);

            // 2. Download what is missing and inline images, then render again.
            bool fetched = false;
            foreach (var r in rows)
            {
                ct.ThrowIfCancellationRequested();
                var (b, _) = bodies[r.Id];
                var needsImages = b?.Attachments.Any(a => a.Inline) == true;
                if (b != null && !needsImages) continue;
                fetched = true;
                try
                {
                    var (body, mime) = await Task.Run(() => _e.LoadAsync(r, needsImages || b == null, ct), ct);
                    var images = mime != null ? MimeText.InlineImages(mime) : new Dictionary<string, string>();
                    bodies[r.Id] = (body, images);
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

            if (markRead && _e.Config.MarkReadOnOpen && rows.Any(r => !r.IsSeen))
                _e.SetRead(AccountId, ThreadKey, true);

            UpdateRepliesBar();
            if (ShowReplies && !RepliesNeedClick && Replies.Count == 0) _ = GenerateRepliesAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("open conversation", ex);
            PageReady?.Invoke(WebHost.Publish(HtmlRenderer.Placeholder("Couldn't open this conversation", ex.Message, ThemeManager.IsDark), "view"));
        }
    }

    private Dictionary<long, (MessageBody?, Dictionary<string, string>)> _lastBodies = new();

    /// <summary>Builds the page off the UI thread (cleaning big newsletters can take a moment) and shows it
    /// only if the user is still on this conversation.</summary>
    private async Task RenderAsync(List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies, CancellationToken ct)
    {
        _lastBodies = bodies;
        var subject = Subject;
        var allow = ImagesAllowed(rows);
        var list = BuildRenderList(rows, bodies);
        var dark = ThemeManager.IsDark;
        var (url, blocked) = await Task.Run(() =>
        {
            var result = HtmlRenderer.BuildConversation(subject, list, allow, DateTimeOffset.Now, dark);
            ct.ThrowIfCancellationRequested();
            return (WebHost.Publish(result.Html, "view"), result.BlockedImages);
        }, ct);
        ct.ThrowIfCancellationRequested();
        BlockedImages = allow ? 0 : blocked;
        PageReady?.Invoke(url);
    }

    private bool ImagesAllowed(List<MessageRow> rows) =>
        _imagesAllowedOnce || _e.Config.RemoteImages == RemoteImages.Always
        || (_e.Config.RemoteImages == RemoteImages.Ask && rows.All(r => IsMine(r) || _e.Config.TrustedImageSenders.Contains(r.FromAddress, StringComparer.OrdinalIgnoreCase)));

    private Dictionary<long, string> _loadErrors = new();

    private List<RenderMessage> BuildRenderList(List<MessageRow> rows, Dictionary<long, (MessageBody? body, Dictionary<string, string> images)> bodies)
    {
        var account = _e.AccountById(AccountId);
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
            LoadError = _loadErrors.TryGetValue(r.Id, out var err) ? err : null,
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
        var result = HtmlRenderer.BuildConversation(Subject, BuildRenderList(rows, bodies), allow, DateTimeOffset.Now, ThemeManager.IsDark);
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
        var row = Messages.FirstOrDefault(m => m.Id == rowId);
        if (row == null) return;
        try
        {
            var (_, mime) = await _e.LoadAsync(row, true, CancellationToken.None);
            if (mime == null || MimeText.PartAt(mime, index) is not { } entity) return;
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
            Views.AttachmentDialog.Show(path, name);
        }
        catch (Exception ex) { Ui.Error("Attachment", Connector.Friendly(ex)); }
    }

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
