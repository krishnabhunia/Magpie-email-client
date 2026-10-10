using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core;
using Magpie.Core.Caching;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Mac.Services;
using MimeKit;

namespace Magpie.Mac.ViewModels;

/// <summary>
/// The reading pane: one conversation, drawn by Core's <see cref="HtmlRenderer"/> (the same pages as on Windows) and
/// shown in the web view by the reader shell. Text and pictures already on this Mac come first; only what is missing
/// is downloaded (design RL1). Remote pictures follow Settings (Ask / Always / Never) and "Show pictures".
/// </summary>
public sealed partial class ReaderViewModel : ObservableObject
{
    private static MailEngine E => AppServices.Engine;
    private CancellationTokenSource _cts = new();
    private Func<string, List<long>> _actionFolders = _ => new();
    private bool _imagesAllowedOnce;
    private Dictionary<long, string> _loadErrors = new();
    private Dictionary<long, (MessageBody? Body, Dictionary<string, string> Images)> _lastBodies = new();
    private readonly LruCache<string, ReaderPage> _pageCache = new(64, 64 * 1024 * 1024);

    /// <summary>A page to show (the view hands it to the reader shell).</summary>
    public event Action<ReaderPage>? PageReady;
    /// <summary>The open conversation left this view (archived, deleted): the list picks the next one.</summary>
    public event Action? ThreadRemoved;

    public List<MessageRow> Messages { get; private set; } = new();
    public string AccountId { get; private set; } = "";
    public string ThreadKey { get; private set; } = "";

    [ObservableProperty] private bool _hasThread;
    [ObservableProperty] private string _subject = "";
    [ObservableProperty] private string _meta = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PinText))] private bool _isPinned;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReadText))] private bool _isRead = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowImagesBar), nameof(ImagesText))] private int _blockedImages;

    /// <summary>The open conversation is a draft on the server (its newest email is in Drafts): Edit draft opens it.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAnyDraft), nameof(ShowMessageActions))] private bool _isDraft;
    /// <summary>A draft kept on this Mac (saved while offline), shown instead of a conversation.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsAnyDraft), nameof(ShowMessageActions))] private long? _localDraftId;
    /// <summary>Deleting here removes the emails for good (they are in Trash or Spam): the button says so and asks first.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(DeleteText))] private bool _deletesForever;

    public bool IsAnyDraft => IsDraft || LocalDraftId != null;
    public bool ShowMessageActions => !IsAnyDraft;
    public string DeleteText => DeletesForever ? "Delete forever" : "Delete";
    public string PinText => IsPinned ? "Unpin" : "Pin";
    public string ReadText => IsRead ? "Mark unread" : "Mark read";
    public bool ShowImagesBar => HasThread && BlockedImages > 0;
    public string ImagesText => BlockedImages == 1 ? "1 picture from the internet is hidden." : $"{BlockedImages} pictures from the internet are hidden.";

    /// <summary>True when Magpie draws its pages dark (follows the Mac's appearance).</summary>
    public static bool Dark => App.IsDark;

    private string _placeholderTitle = "No conversation selected", _placeholderText = "Pick a conversation from the list.";

    public void Clear(string title = "No conversation selected", string text = "Pick a conversation from the list.")
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        HasThread = false;
        IsDraft = DeletesForever = false;
        LocalDraftId = null;
        AccountId = ThreadKey = "";
        Messages = new();
        Subject = Meta = "";
        BlockedImages = 0;
        _placeholderTitle = title;
        _placeholderText = text;
        PageReady?.Invoke(new ReaderPage("placeholder", "", HtmlRenderer.Placeholder(title, text, Dark), 0, Retain: false));
    }

    /// <summary>The appearance changed: draw the page again in the new colours.</summary>
    public void Redraw()
    {
        if (!HasThread) { Clear(_placeholderTitle, _placeholderText); return; }
        _pageCache.Clear();
        Reload(markRead: false);
    }

    public void Show(ThreadRow row, Func<string, List<long>> actionFolders)
    {
        _actionFolders = actionFolders;
        var same = row.AccountId == AccountId && row.ThreadKey == ThreadKey;
        if (!same) _imagesAllowedOnce = false;
        AccountId = row.AccountId;
        ThreadKey = row.ThreadKey;
        LocalDraftId = null;
        HasThread = true;
        Reload(markRead: true);
    }

    /// <summary>A draft kept on this Mac: no page to read (it opens in a compose window), a note and Edit draft.</summary>
    public void ShowLocalDraft(long id, string subject, string badge)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        AccountId = "";
        ThreadKey = "local:" + id;
        Messages = new();
        IsDraft = DeletesForever = false;
        LocalDraftId = id;
        HasThread = true;
        Subject = subject;
        Meta = badge;
        BlockedImages = 0;
        PageReady?.Invoke(new ReaderPage("placeholder", "", HtmlRenderer.Placeholder("Draft kept on this Mac",
            "It was saved here while the server couldn't be reached. Choose Edit draft to keep writing; it uploads to Drafts by itself once you're online.", Dark), 0, Retain: false));
    }

    /// <summary>After list reloads: draw again if the open conversation changed (a new reply, flags).</summary>
    public void RefreshIfShowing(ThreadRow row)
    {
        if (row.AccountId == AccountId && row.ThreadKey == ThreadKey) Reload(markRead: false);
    }

    private void Reload(bool markRead)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token, markRead);
    }

    private async Task LoadAsync(CancellationToken ct, bool markRead)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            _loadErrors = new Dictionary<long, string>();
            var rows = await Task.Run(() => E.Store.GetThread(AccountId, ThreadKey), ct);
            ct.ThrowIfCancellationRequested();
            if (rows.Count == 0) { Clear(); return; }
            Messages = rows;
            var latest = rows[^1];
            Subject = SubjectOf(rows);
            var people = rows.Select(r => IsMine(r) ? "you" : r.Sender).Distinct().Take(5);
            Meta = $"{rows.Count} message{(rows.Count == 1 ? "" : "s")} · {string.Join(", ", people)}";
            IsPinned = rows.Any(r => r.IsFlagged);
            IsRead = rows.All(r => r.IsSeen);
            var folders = E.Folders(AccountId).ToDictionary(f => f.Id);
            IsDraft = folders.TryGetValue(latest.FolderId, out var lf) && lf.Role == FolderRole.Drafts;
            var scope = _actionFolders(AccountId);
            DeletesForever = E.Store.GetThreadCopies(AccountId, ThreadKey)
                .Any(m => scope.Contains(m.FolderId) && folders.TryGetValue(m.FolderId, out var f) && f.Role is FolderRole.Trash or FolderRole.Junk);

            // 1. What is on this Mac already.
            var bodies = await Task.Run(() =>
            {
                var loaded = new Dictionary<long, (MessageBody?, Dictionary<string, string>)>();
                foreach (var r in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    var b = E.Store.GetBody(r.Id);
                    loaded[r.Id] = (b, b?.Images ?? new Dictionary<string, string>());
                }
                return loaded;
            }, ct);
            ct.ThrowIfCancellationRequested();
            if (bodies.Values.Any(b => b.Item1 == null))
                PageReady?.Invoke(new ReaderPage("loading", "", "<body>" + HtmlRenderer.LoadingBody(Subject, latest.Sender,
                    latest.Date.LocalDateTime.ToString("ddd d MMM, HH:mm"), Dark, downloading: true) + "</body>", 0, Retain: false));
            else await RenderAsync(rows, bodies, ct);

            // 2. Only what isn't here yet is downloaded (text and pictures; attachments wait for a click).
            var fetched = false;
            foreach (var r in rows)
            {
                ct.ThrowIfCancellationRequested();
                var (b, _) = bodies[r.Id];
                if (b != null && !MimeText.NeedsDownload(b)) continue;
                fetched = true;
                try
                {
                    var body = await Task.Run(() => E.FetchBodyAsync(r, ct), ct);
                    bodies[r.Id] = (body ?? b, (body ?? b)?.Images ?? new Dictionary<string, string>());
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn("load body failed: " + ex.Message);
                    if (bodies[r.Id].Item1 == null) _loadErrors[r.Id] = Connector.Friendly(ex);
                }
            }
            ct.ThrowIfCancellationRequested();
            if (fetched || bodies.Values.Any(b => b.Item1 == null)) await RenderAsync(rows, bodies, ct);

            if (markRead && E.Config.MarkReadOnOpen && rows.Any(r => !r.IsSeen))
            {
                E.SetRead(AccountId, ThreadKey, true);
                IsRead = true;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("open conversation", ex);
            if (!ct.IsCancellationRequested)
                PageReady?.Invoke(new ReaderPage("error", "", HtmlRenderer.Placeholder("Couldn't open this conversation", ex.Message, Dark), 0, Retain: false));
        }
    }

    private string PageContext => JsonSerializer.Serialize(new
    {
        Dark, E.Config.RemoteImages, E.Config.Appearance.FolderHover.DelayMs,
        Trusted = E.Config.TrustedImageSenders.OrderBy(s => s, StringComparer.OrdinalIgnoreCase),
    });

    private async Task RenderAsync(List<MessageRow> rows, Dictionary<long, (MessageBody? Body, Dictionary<string, string> Images)> bodies, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _lastBodies = bodies;
        var subject = Subject;
        var allow = ImagesAllowed(rows);
        var dark = Dark;
        var context = PageContext;
        var hover = E.Config.Appearance.FolderHover.DelayMs;
        var key = AccountId + "\n" + ThreadKey;
        var list = BuildRenderList(rows, bodies);
        var fp = ReaderPage.ContentFingerprint(subject, list, allow, dark, hover, DateTimeOffset.Now, E.Store.BodyFingerprint);
        ReaderPage page;
        if (_pageCache.TryGet(key, out var hit) && hit.Fingerprint == fp && hit.Context == context) page = hit;
        else
        {
            var result = await Task.Run(() => HtmlRenderer.BuildConversation(subject, list, allow, DateTimeOffset.Now, dark, hover), ct);
            ct.ThrowIfCancellationRequested();
            page = new ReaderPage(key, fp, result.Html, result.BlockedImages, context);
            _pageCache.Set(key, page, page.Bytes);
        }
        BlockedImages = allow ? 0 : page.BlockedImages;
        PageReady?.Invoke(page with { ReadStates = rows.ToDictionary(r => r.Id, r => r.IsSeen) });
    }

    private List<RenderMessage> BuildRenderList(List<MessageRow> rows, Dictionary<long, (MessageBody? Body, Dictionary<string, string> Images)> bodies)
    {
        var account = E.AccountById(AccountId);
        var where = account?.Kind switch
        {
            AccountKind.Gmail => "Gmail",
            AccountKind.Microsoft => "Outlook",
            _ => account?.ImapHost is { Length: > 0 } h ? h : "the server",
        };
        return rows.Select((r, i) => new RenderMessage
        {
            Row = r,
            Body = bodies.TryGetValue(r.Id, out var b) ? b.Body : null,
            InlineImages = bodies.TryGetValue(r.Id, out var b2) ? b2.Images : new(),
            Expanded = i == rows.Count - 1 || !r.IsSeen || rows.Count <= 2,
            IsMine = IsMine(r),
            LoadingText = $"Downloading from {where}…",
            LoadError = _loadErrors.TryGetValue(r.Id, out var err) ? err : null,
        }).ToList();
    }

    private static string SubjectOf(List<MessageRow> rows)
    {
        var latest = rows[^1];
        return string.IsNullOrWhiteSpace(latest.Subject) ? "(no subject)" : Threading.StripSubjectPrefixes(rows[0].Subject) is { Length: > 0 } s ? s : latest.Subject;
    }

    private bool ImagesAllowed(List<MessageRow> rows) =>
        _imagesAllowedOnce || E.Config.RemoteImages == RemoteImages.Always
        || (E.Config.RemoteImages == RemoteImages.Ask && rows.All(r => IsMine(r) || E.Config.TrustedImageSenders.Contains(r.FromAddress, StringComparer.OrdinalIgnoreCase)));

    private static bool IsMine(MessageRow r) => E.MyAddresses.Contains(r.FromAddress.ToLowerInvariant());

    // ───────────────────────── actions ─────────────────────────

    [RelayCommand] private void Reply() => Compose(ComposeMode.Reply, null);
    [RelayCommand] private void ReplyAll() => Compose(ComposeMode.ReplyAll, null);
    [RelayCommand] private void Forward() => Compose(ComposeMode.Forward, null);

    public void Compose(ComposeMode mode, long? rowId)
    {
        if (!HasThread) return;
        var row = rowId is { } id && id > 0 ? Messages.FirstOrDefault(m => m.Id == id) : Messages.LastOrDefault(m => !IsMine(m)) ?? Messages.LastOrDefault();
        if (row != null) Views.ComposeWindow.Open(mode, row);
    }

    private async Task Act(Func<Task> action, bool removes)
    {
        if (!HasThread) return;
        try
        {
            await action();
            if (removes) ThreadRemoved?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("action failed", ex);
            await Dialogs.Error("Magpie", ex.Message);
        }
    }

    [RelayCommand] private Task Archive() => Act(() => E.ArchiveAsync(AccountId, ThreadKey, _actionFolders(AccountId)), true);
    /// <summary>Delete (⌘⌫, ⌫ in the list): to Trash; in Trash or Spam it removes the emails for good, so it asks first
    /// (Cancel is the default). A draft kept on this Mac is deleted from this Mac after asking.</summary>
    [RelayCommand]
    private async Task Delete()
    {
        if (!HasThread) return;
        if (LocalDraftId is { } local)
        {
            if (Views.ComposeWindow.ActivateLocal(local)) return;   // its window decides
            if (!await Dialogs.ConfirmDanger("Delete draft", "Delete this draft saved on this Mac? It hasn't reached the server, so it can't be restored.", "Delete")) return;
            E.DeleteLocalDraft(local);
            ThreadRemoved?.Invoke();
            return;
        }
        var folders = _actionFolders(AccountId);
        if (DeletesForever && !await Dialogs.ConfirmDanger("Delete forever",
                $"Delete “{Subject}” for good? It is in Trash or Spam, so it is removed from this Mac and the server. This can't be undone.", "Delete forever"))
            return;
        await Act(() => E.TrashAsync(AccountId, ThreadKey, folders), true);
    }

    /// <summary>Edit draft: a server draft is read from its message (MIME) into a compose window, which replaces it when
    /// saved or sent; a draft kept on this Mac opens (or comes to the front if it is open already).</summary>
    [RelayCommand]
    private async Task EditDraft()
    {
        if (LocalDraftId is { } local)
        {
            if (Views.ComposeWindow.ActivateLocal(local)) return;
            if (E.OpenLocalDraft(local) is { } ld) Views.ComposeWindow.OpenDraft(ld);
            else AppServices.Main?.ReloadList();   // sent or uploaded meanwhile
            return;
        }
        var row = Messages.LastOrDefault();
        if (row == null || !IsDraft) return;
        try
        {
            var (_, mime) = await E.LoadAsync(row, true, CancellationToken.None);
            if (mime == null) return;
            var d = Composer.FromMime(mime, AccountId, ThreadKey);
            d.SourceDraftRow = row.Id;
            Views.ComposeWindow.OpenDraft(d);
        }
        catch (Exception ex) { await Dialogs.Error("Edit draft", Connector.Friendly(ex)); }
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (!HasThread) return;
        E.SetPinned(AccountId, ThreadKey, !IsPinned);
        IsPinned = !IsPinned;
    }

    [RelayCommand]
    private void ToggleRead()
    {
        if (!HasThread) return;
        if (IsRead) E.MarkLatestUnread(AccountId, ThreadKey);
        else E.SetRead(AccountId, ThreadKey, true);
        IsRead = !IsRead;
    }

    /// <summary>"Show pictures": this conversation only.</summary>
    [RelayCommand]
    private void LoadImages()
    {
        _imagesAllowedOnce = true;
        if (HasThread) _ = RenderAsync(Messages, _lastBodies, _cts.Token);
    }

    /// <summary>"Always show pictures from these senders".</summary>
    [RelayCommand]
    private void TrustSenders()
    {
        foreach (var r in Messages.Where(r => !IsMine(r)))
            if (!E.Config.TrustedImageSenders.Contains(r.FromAddress, StringComparer.OrdinalIgnoreCase)) E.Config.TrustedImageSenders.Add(r.FromAddress);
        E.Settings.Save();
        LoadImages();
    }

    /// <summary>A message from the page: a link, the per-message buttons, an attachment, Try again.</summary>
    public void OnPageMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            long Num(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
            switch (Str("t"))
            {
                case "link": OnLink(Str("href")); break;
                case "reply": Compose(ComposeMode.Reply, Num("id")); break;
                case "replyall": Compose(ComposeMode.ReplyAll, Num("id")); break;
                case "forward": Compose(ComposeMode.Forward, Num("id")); break;
                case "att": _ = OpenAttachmentAsync(Num("id"), (int)Num("i")); break;
                case "retry": Reload(markRead: false); break;
            }
        }
        catch (Exception ex) { Log.Warn("reader message: " + ex.Message); }
    }

    public void OnLink(string href)
    {
        if (href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) Views.ComposeWindow.OpenMailto(href, AccountId);
        else Shell.OpenWeb(href);   // email content: http(s) only, anything else is ignored
    }

    /// <summary>An attachment: written to a new temporary folder (downloaded first when needed) and opened in its app.</summary>
    public async Task OpenAttachmentAsync(long rowId, int index)
    {
        var row = Messages.FirstOrDefault(m => m.Id == rowId);
        if (row == null) return;
        try
        {
            var (_, mime) = await E.LoadAsync(row, true, CancellationToken.None);
            if (mime == null || MimeText.PartAt(mime, MimeText.ResolveIndex(mime, index)) is not { } entity) return;
            var name = entity switch
            {
                MimePart p => p.FileName ?? "attachment",
                MessagePart m => (m.Message?.Subject ?? "message") + ".eml",
                _ => "attachment",
            };
            foreach (var c in Path.GetInvalidFileNameChars().Append(':')) name = name.Replace(c, '_');
            var dir = Path.Combine(Path.GetTempPath(), "Magpie", Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            await using (var fs = File.Create(path))
            {
                if (entity is MimePart part) await part.Content!.DecodeToAsync(fs);
                else if (entity is MessagePart mp && mp.Message != null) await mp.Message.WriteToAsync(fs);
            }
            Shell.OpenLocal(path);
        }
        catch (Exception ex) { await Dialogs.Error("Attachment", Connector.Friendly(ex)); }
    }
}
