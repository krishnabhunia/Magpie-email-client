using System.Collections.Concurrent;
using Magpie.Core.Ai;
using Magpie.Core.Auth;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using MimeKit;

namespace Magpie.Core;

/// <summary>
/// The app's single entry point to mail: accounts, sync, local-first actions, outbox (undo send and
/// send later), snooze, follow-up reminders and AI. Events fire on background threads.
/// </summary>
public sealed class MailEngine : IDisposable
{
    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public SecretVault Vault { get; }
    public MailStore Store { get; }
    public OAuthService OAuth { get; }
    public Connector Connector { get; }
    public AiService Ai { get; }
    public HttpClient Http { get; }

    private readonly ConcurrentDictionary<string, AccountSync> _syncs = new();
    private HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _knownGate = new();
    private CancellationTokenSource? _cts;
    private Task? _timers;
    private int _promotePending;

    public event Action<ChangeSet>? Changed;
    public event Action<string, SyncStatus>? StatusChanged;
    public event Action<IReadOnlyList<MessageRow>>? NewMail;
    public event Action? OutboxChanged;
    public event Action<OutboxItem>? Sent;
    public event Action<OutboxItem, string>? SendFailed;
    public event Action<IReadOnlyList<MessageRow>>? SnoozeWoke;
    public event Action<Reminder>? ReminderDue;

    public MailEngine(AppPaths paths, ISecretProtector protector, HttpMessageHandler? httpHandler = null)
    {
        Paths = paths;
        Log.Init(paths.Logs);
        Settings = new SettingsStore(paths.Settings);
        Settings.Load();
        Vault = new SecretVault(paths.Secrets, protector);
        Store = new MailStore(paths.Database);
        Http = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler);
        Http.Timeout = TimeSpan.FromMinutes(3);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Magpie/1.0");
        OAuth = new OAuthService(Http, id => Vault.Get(SecretVault.RefreshKey(id)), (id, t) => Vault.Set(SecretVault.RefreshKey(id), t))
        {
            ConfigFor = kind => kind switch
            {
                AccountKind.Gmail => OAuthService.Google(Settings.Current.GoogleClientId, Settings.Current.GoogleClientSecret),
                AccountKind.Microsoft => OAuthService.Microsoft(Settings.Current.MicrosoftClientId),
                _ => null,
            },
        };
        Connector = new Connector(Vault, OAuth);
        Ai = new AiService(Http, () => Settings.Current.Ai, () => Vault.Get(SecretVault.AiKey));
    }

    public AppSettings Config => Settings.Current;
    public IReadOnlyList<Account> Accounts => Settings.Current.Accounts;
    public Account? AccountById(string id) => Settings.Current.Accounts.FirstOrDefault(a => a.Id == id);

    public IReadOnlyCollection<string> MyAddresses =>
        Settings.Current.Accounts.Select(a => a.Email.ToLowerInvariant()).Distinct().ToList();

    public void Start()
    {
        _cts = new CancellationTokenSource();
        Store.RecoverStuckOutbox();
        lock (_knownGate) _known = Store.KnownContacts();
        foreach (var a in Accounts.Where(a => a.Enabled)) StartAccount(a);
        _timers = Task.Run(() => TimersAsync(_cts.Token));
    }

    private void StartAccount(Account a)
    {
        var sync = new AccountSync(a, Store, Connector, IsKnownContact, SenderOverride)
        {
            MimePathFor = Paths.MimePath,
            PollInterval = TimeSpan.FromMinutes(Settings.Current.SyncIntervalMinutes),
        };
        sync.Changed += cs =>
        {
            Changed?.Invoke(cs);
            if (cs.NewInboxMessages.Count > 0) NewMail?.Invoke(cs.NewInboxMessages);
        };
        sync.StatusChanged += (s, st) => StatusChanged?.Invoke(s.Account.Id, st);
        sync.ContactsLearned += addrs =>
        {
            lock (_knownGate) foreach (var x in addrs) _known.Add(x);
            Interlocked.Exchange(ref _promotePending, 1);
        };
        if (_syncs.TryAdd(a.Id, sync)) sync.Start();
    }

    public SyncStatus? StatusOf(string accountId) => _syncs.TryGetValue(accountId, out var s) ? s.Status : null;

    private bool IsKnownContact(string addr)
    {
        lock (_knownGate) return _known.Contains(addr);
    }

    private Category? SenderOverride(string addr) =>
        Settings.Current.SenderCategories.TryGetValue(addr, out var c) ? c : null;

    public void SyncNow(string? accountId = null)
    {
        foreach (var (id, s) in _syncs)
            if (accountId == null || id == accountId) s.Poke();
    }

    /// <summary>The user opened these folders: list their older emails first.</summary>
    public void Prioritise(IEnumerable<long> folderIds)
    {
        var folders = Store.GetFolders().ToDictionary(f => f.Id);
        foreach (var id in folderIds)
            if (folders.TryGetValue(id, out var f) && _syncs.TryGetValue(f.AccountId, out var s)) { s.Prioritise(id); break; }
    }

    /// <summary>True while any of these folders is still being listed from the server (not checked yet, or older emails still coming).</summary>
    public bool StillListing(IEnumerable<long> folderIds)
    {
        var folders = Store.GetFolders().ToDictionary(f => f.Id);
        foreach (var id in folderIds)
        {
            if (!folders.TryGetValue(id, out var f) || !_syncs.TryGetValue(f.AccountId, out var s)) continue;
            if (!Accounts.Any(a => a.Id == f.AccountId && a.Enabled)) continue;
            var st = s.Status.State;
            if (st is SyncState.Offline or SyncState.NeedsSignIn or SyncState.Error) continue;
            if (s.Remaining(id) != 0) return true;
        }
        return false;
    }

    // ───────────────────────── accounts ─────────────────────────

    public void AddAccount(Account a, string? password, OAuthTokens? tokens)
    {
        if (Accounts.Any(x => x.Email.Equals(a.Email, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{a.Email} is already added.");
        if (password != null) Vault.Set(SecretVault.PasswordKey(a.Id), password);
        if (tokens != null) OAuth.Remember(a.Id, tokens);
        Settings.Current.Accounts.Add(a);
        Settings.Save();
        StartAccount(a);
        Changed?.Invoke(new ChangeSet { AccountId = a.Id, FoldersChanged = true });
    }

    public void UpdateAccount(Account a, string? newPassword = null, OAuthTokens? tokens = null)
    {
        var list = Settings.Current.Accounts;
        var i = list.FindIndex(x => x.Id == a.Id);
        if (i < 0) return;
        list[i] = a;
        if (newPassword != null) Vault.Set(SecretVault.PasswordKey(a.Id), newPassword);
        if (tokens != null) OAuth.Remember(a.Id, tokens);
        Settings.Save();
        if (_syncs.TryRemove(a.Id, out var old)) old.Dispose();
        if (a.Enabled) StartAccount(a);
        Changed?.Invoke(new ChangeSet { AccountId = a.Id, FoldersChanged = true });
    }

    public void RemoveAccount(string accountId)
    {
        if (_syncs.TryRemove(accountId, out var s)) s.Dispose();
        Settings.Current.Accounts.RemoveAll(a => a.Id == accountId);
        Settings.Save();
        Vault.RemovePrefix($"account:{accountId}:");
        OAuth.Forget(accountId);
        Store.DeleteAccount(accountId);
        Store.DeleteLocalDraftsForAccount(accountId);
        try
        {
            var dir = Path.GetDirectoryName(Paths.MimePath(accountId, 0));
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) { Log.Warn("could not delete message cache: " + ex.Message); }
        Changed?.Invoke(new ChangeSet { AccountId = accountId, FoldersChanged = true });
    }

    // ───────────────────────── folder helpers ─────────────────────────

    public List<MailFolder> Folders(string? accountId = null) => Store.GetFolders(accountId);

    public List<long> FolderIds(FolderRole role, string? accountId = null) =>
        Store.GetFolders(accountId).Where(f => f.Role == role && (accountId == null || f.AccountId == accountId)).Select(f => f.Id).ToList();

    /// <summary>Every mirrored folder except Trash and Junk (for Pinned and search-everywhere).</summary>
    public List<long> AllMailFolderIds(string? accountId = null) =>
        Store.GetFolders(accountId).Where(f => f.Synced && f.Role is not (FolderRole.Trash or FolderRole.Junk)).Select(f => f.Id).ToList();

    // ───────────────────────── local-first actions ─────────────────────────

    private void Queue(MessageRow m, PendingOpKind kind, long arg = 0) =>
        Store.AddPendingOp(new PendingOp { AccountId = m.AccountId, FolderId = m.FolderId, Uid = m.Uid, Kind = kind, Arg = arg });

    private void Touched(string accountId, IEnumerable<long> folderIds)
    {
        var cs = new ChangeSet { AccountId = accountId };
        foreach (var f in folderIds) { cs.FolderIds.Add(f); Store.RefreshFolderCounts(f); }
        Changed?.Invoke(cs);
        SyncNow(accountId);
    }

    public void SetRead(string accountId, string threadKey, bool read)
    {
        var rows = Store.GetThreadCopies(accountId, threadKey);
        var folders = new HashSet<long>();
        foreach (var m in rows)
        {
            if (m.IsSeen == read) continue;
            Store.SetLocalFlags(m.Id, read ? m.Flags | MessageFlags.Seen : m.Flags & ~MessageFlags.Seen);
            Queue(m, read ? PendingOpKind.SetSeen : PendingOpKind.ClearSeen);
            folders.Add(m.FolderId);
        }
        if (folders.Count > 0) Touched(accountId, folders);
    }

    /// <summary>Marks only the newest message unread (Spark behaviour for "mark unread").</summary>
    public void MarkLatestUnread(string accountId, string threadKey)
    {
        var rows = Store.GetThreadCopies(accountId, threadKey);
        var latest = rows.OrderByDescending(r => r.Date).FirstOrDefault();
        if (latest == null) return;
        var copies = rows.Where(r => r.MessageId == latest.MessageId && r.IsSeen).ToList();
        foreach (var m in copies)
        {
            Store.SetLocalFlags(m.Id, m.Flags & ~MessageFlags.Seen);
            Queue(m, PendingOpKind.ClearSeen);
        }
        if (copies.Count > 0) Touched(accountId, copies.Select(c => c.FolderId));
    }

    /// <summary>Pin = IMAP \Flagged (Gmail star, Outlook flag), so it shows on every device.</summary>
    public void SetPinned(string accountId, string threadKey, bool pinned)
    {
        var rows = Store.GetThreadCopies(accountId, threadKey);
        var changed = new List<MessageRow>();
        if (pinned)
        {
            if (rows.Any(r => r.IsFlagged)) return;
            var latest = rows.OrderByDescending(r => r.Date).FirstOrDefault();
            if (latest != null)
                changed.AddRange(latest.MessageId.Length > 0 ? rows.Where(r => r.MessageId == latest.MessageId) : new[] { latest });
        }
        else changed.AddRange(rows.Where(r => r.IsFlagged));
        foreach (var m in changed.DistinctBy(m => m.Id))
        {
            Store.SetLocalFlags(m.Id, pinned ? m.Flags | MessageFlags.Flagged : m.Flags & ~MessageFlags.Flagged);
            Queue(m, pinned ? PendingOpKind.SetFlagged : PendingOpKind.ClearFlagged);
        }
        if (changed.Count > 0) Touched(accountId, changed.Select(c => c.FolderId));
    }

    private async Task<MailFolder?> DestinationAsync(string accountId, FolderRole role, CancellationToken ct)
    {
        var existing = Store.GetFolders(accountId).FirstOrDefault(f => f.Role == role);
        if (existing != null) return existing;
        if (role == FolderRole.Archive && _syncs.TryGetValue(accountId, out var sync))
        {
            // Gmail archives into All Mail (removing the Inbox label).
            var all = Store.GetFolders(accountId).FirstOrDefault(f => f.Role == FolderRole.All);
            if (all != null) return all;
            return await sync.EnsureFolderAsync("Archive", FolderRole.Archive, ct);
        }
        return null;
    }

    /// <summary>Moves the conversation's copies in <paramref name="fromFolders"/> to Archive (Gmail: All Mail).</summary>
    public async Task ArchiveAsync(string accountId, string threadKey, IReadOnlyCollection<long> fromFolders, CancellationToken ct = default)
    {
        var dest = await DestinationAsync(accountId, FolderRole.Archive, ct) ?? throw new InvalidOperationException("This account has no Archive folder.");
        MoveCopies(accountId, threadKey, fromFolders.Where(f => f != dest.Id).ToList(), dest);
    }

    /// <summary>Trash; in Trash (or Junk) itself it deletes permanently.</summary>
    public async Task TrashAsync(string accountId, string threadKey, IReadOnlyCollection<long> fromFolders, CancellationToken ct = default)
    {
        var trash = await DestinationAsync(accountId, FolderRole.Trash, ct);
        var folders = Store.GetFolders(accountId).ToDictionary(f => f.Id);
        var copies = Store.GetThreadCopies(accountId, threadKey).Where(m => fromFolders.Contains(m.FolderId)).ToList();
        var touched = new HashSet<long>();
        foreach (var m in copies)
        {
            var inTrash = folders.TryGetValue(m.FolderId, out var f) && f.Role is FolderRole.Trash or FolderRole.Junk;
            if (trash == null || inTrash) Queue(m, PendingOpKind.Delete);
            else Queue(m, PendingOpKind.Move, trash.Id);
            Store.DeleteRow(m.Id);
            touched.Add(m.FolderId);
        }
        if (touched.Count > 0) Touched(accountId, touched);
    }

    public void MoveTo(string accountId, string threadKey, IReadOnlyCollection<long> fromFolders, MailFolder dest) =>
        MoveCopies(accountId, threadKey, fromFolders.Where(f => f != dest.Id).ToList(), dest);

    private void MoveCopies(string accountId, string threadKey, IReadOnlyCollection<long> fromFolders, MailFolder dest)
    {
        var copies = Store.GetThreadCopies(accountId, threadKey).Where(m => fromFolders.Contains(m.FolderId)).ToList();
        foreach (var m in copies)
        {
            Queue(m, PendingOpKind.Move, dest.Id);
            Store.DeleteRow(m.Id);
        }
        if (copies.Count > 0) Touched(accountId, copies.Select(c => c.FolderId).Append(dest.Id));
    }

    public void Snooze(string accountId, string threadKey, DateTimeOffset until)
    {
        var inbox = FolderIds(FolderRole.Inbox, accountId);
        Store.SetSnooze(accountId, threadKey, inbox, until);
        Touched(accountId, inbox);
    }

    public void Unsnooze(string accountId, string threadKey)
    {
        var inbox = FolderIds(FolderRole.Inbox, accountId);
        Store.SetSnooze(accountId, threadKey, inbox, null);
        Store.BumpThread(accountId, threadKey, DateTimeOffset.Now);
        Touched(accountId, inbox);
    }

    /// <param name="always">True: remind at that time regardless. False: only if nobody replied.</param>
    public void RemindMe(string accountId, string threadKey, string subject, DateTimeOffset due, bool always)
    {
        Store.AddReminder(new Reminder { AccountId = accountId, ThreadKey = threadKey, Subject = subject, After = DateTimeOffset.Now, Due = due, Always = always });
        Changed?.Invoke(new ChangeSet { AccountId = accountId });
    }

    public void SetTags(string accountId, string threadKey, IEnumerable<string> tags)
    {
        var joined = string.Join(",", tags.Select(t => t.Replace(",", " ").Trim()).Where(t => t.Length > 0).Distinct());
        var rows = Store.GetThreadCopies(accountId, threadKey);
        foreach (var r in rows) Store.SetTags(r.Id, joined);
        var cs = new ChangeSet { AccountId = accountId };
        foreach (var f in rows.Select(r => r.FolderId).Distinct()) cs.FolderIds.Add(f);
        Changed?.Invoke(cs);
    }

    /// <summary>Smart inbox correction: "this sender belongs in People/Notifications/Newsletters" (remembered).</summary>
    public void SetSenderCategory(string accountId, string address, Category cat)
    {
        Settings.Current.SenderCategories[address] = cat;
        Settings.Save();
        foreach (var a in Accounts) Store.SetCategory(a.Id, address, cat);
        var cs = new ChangeSet { AccountId = accountId };
        foreach (var f in FolderIds(FolderRole.Inbox)) cs.FolderIds.Add(f);
        Changed?.Invoke(cs);
    }

    // ───────────────────────── reading ─────────────────────────

    /// <summary>Body from the local store, downloading it first if needed.</summary>
    public async Task<(MessageBody? body, MimeMessage? mime)> LoadAsync(MessageRow row, bool needMime, CancellationToken ct)
    {
        var body = Store.GetBody(row.Id);
        if (body != null && !needMime) return (body, null);
        if (!_syncs.TryGetValue(row.AccountId, out var sync)) return (body, null);
        var mime = await sync.GetMimeAsync(row, ct);
        return (Store.GetBody(row.Id) ?? body, mime);
    }

    // ───────────────────────── sending ─────────────────────────

    /// <summary>
    /// Queues a message. <paramref name="sendAt"/> = now + undo window for a normal send, or a later time
    /// for "send later". Returns the outbox id (used for Undo / Cancel).
    /// </summary>
    public long QueueSend(Draft d, DateTimeOffset sendAt, DateTimeOffset? remindIfNoReply)
    {
        var account = AccountById(d.AccountId) ?? throw new InvalidOperationException("Choose an account to send from.");
        var msg = Composer.Build(d, account);
        var threadKey = string.IsNullOrEmpty(d.ThreadKey) ? "r:" + Threading.NormalizeId(msg.MessageId) : d.ThreadKey;
        var item = new OutboxItem
        {
            AccountId = account.Id,
            Mime = Composer.ToBytes(msg),
            SendAt = sendAt,
            Subject = msg.Subject ?? "",
            ToText = string.Join(", ", msg.To.Mailboxes.Select(m => string.IsNullOrEmpty(m.Name) ? m.Address : m.Name)),
            MessageId = Threading.NormalizeId(msg.MessageId),
            ThreadKey = threadKey,
            RemindAt = remindIfNoReply,
        };
        Store.AddOutbox(item);
        DropStaleLocalCopies(msg.MessageId, d.LocalDraftId);
        var people = msg.To.Mailboxes.Concat(msg.Cc.Mailboxes).Concat(msg.Bcc.Mailboxes).Select(m => (m.Address, m.Name ?? "")).ToList();
        Store.TouchContacts(people, DateTimeOffset.Now, sentTo: true);
        lock (_knownGate) foreach (var p in people) _known.Add(p.Address);
        OutboxChanged?.Invoke();
        return item.Id;
    }

    /// <summary>Undo send / cancel a scheduled message. Returns the draft to reopen, or null if it already went.</summary>
    public Draft? Recall(long outboxId)
    {
        var item = Store.GetOutbox().FirstOrDefault(o => o.Id == outboxId);
        if (item == null || !Store.CancelOutbox(outboxId)) return null;
        OutboxChanged?.Invoke();
        var d = Composer.FromMime(Composer.FromBytes(item.Mime), item.AccountId, item.ThreadKey);
        d.Mode = ComposeMode.EditDraft;
        return d;
    }

    /// <summary>Saves the message in the account's Drafts folder on the server (replacing the draft it was opened from).</summary>
    public async Task SaveDraftAsync(Draft d, CancellationToken ct = default)
    {
        var account = AccountById(d.AccountId) ?? throw new InvalidOperationException("Choose an account first.");
        if (!_syncs.TryGetValue(account.Id, out var sync)) throw new InvalidOperationException("This account is not connected.");
        var msg = Composer.Build(d, account);
        // Copies on this PC saved after this one (e.g. in another window) are newer: leave those.
        var cutoff = d.LocalDraftId is { } lid ? Store.GetLocalDraft(lid)?.Updated : null;
        if (!await sync.ReplaceDraftAsync(msg, ct))
            throw new InvalidOperationException("This account has no Drafts folder.");
        if (d.SourceDraftRow is { } old) { DeleteDraft(old); d.SourceDraftRow = null; }
        DropStaleLocalCopies(msg.MessageId, d.LocalDraftId, cutoff);
        SyncNow(account.Id);
    }

    /// <summary>
    /// A message was sent or saved to the server: older copies of it kept on this PC (same Message-ID) are out of
    /// date and must not upload later over the newer version. Copies open in a compose window are left to it.
    /// </summary>
    private void DropStaleLocalCopies(string? messageId, long? except, DateTimeOffset? notNewerThan = null)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;
        try
        {
            var dropped = false;
            foreach (var l in Store.GetLocalDrafts())
                if (l.Id != except && l.MessageId == messageId && !IsLocalDraftOpen(l.Id) && (notNewerThan == null || l.Updated <= notNewerThan))
                { Store.DeleteLocalDraft(l.Id); dropped = true; }
            if (dropped) Changed?.Invoke(new ChangeSet());
        }
        catch (Exception ex) { Log.Warn("local draft cleanup: " + ex.Message); }
    }

    // ───────────────────────── drafts kept on this PC (design F1) ─────────────────────────

    /// <summary>Saves the compose window's content on this PC. Returns the local draft id.</summary>
    /// <param name="insertIfMissing">false for an autosave of an existing copy: if that copy was removed meanwhile
    /// (sent, discarded) nothing is written and 0 is returned.</param>
    public long SaveLocalDraft(Draft d, bool pendingUpload, bool insertIfMissing = true)
    {
        var account = AccountById(d.AccountId) ?? throw new InvalidOperationException("Choose an account first.");
        var msg = Composer.Build(d, account);
        var text = MimeText.HtmlToText(d.Html);
        var preview = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        var local = new LocalDraft
        {
            Id = d.LocalDraftId ?? 0,
            AccountId = d.AccountId,
            Mime = Composer.ToBytes(msg),
            Subject = d.Subject ?? "",
            ToText = d.To ?? "",
            Preview = preview.Length > 160 ? preview[..160] : preview,
            ThreadKey = d.ThreadKey ?? "",
            SourceDraftRow = d.SourceDraftRow,
            PendingUpload = pendingUpload,
            MessageId = msg.MessageId ?? "",
        };
        var id = Store.SaveLocalDraft(local, insertIfMissing);
        if (id == 0) return 0;
        d.LocalDraftId = id;
        Changed?.Invoke(new ChangeSet { AccountId = d.AccountId });
        return id;
    }

    public List<LocalDraft> LocalDrafts() => Store.GetLocalDrafts();

    public void NotifyLocalDraftsChanged() => Changed?.Invoke(new ChangeSet());

    /// <summary>Turns a local draft back into something the compose window can open.</summary>
    public Draft? OpenLocalDraft(long id)
    {
        var l = Store.GetLocalDraft(id);
        return l == null ? null : DraftFromLocal(l);
    }

    private static Draft DraftFromLocal(LocalDraft l)
    {
        var d = Composer.FromMime(Composer.FromBytes(l.Mime), l.AccountId, l.ThreadKey);
        d.Mode = ComposeMode.EditDraft;
        d.SourceDraftRow = l.SourceDraftRow;
        d.LocalDraftId = l.Id;
        return d;
    }

    public void DeleteLocalDraft(long id)
    {
        Store.DeleteLocalDraft(id);
        Changed?.Invoke(new ChangeSet());
    }

    private int _flushingDrafts;
    private readonly ConcurrentDictionary<long, byte> _openLocalDrafts = new();

    /// <summary>A compose window is editing this local draft: the background upload leaves it alone until released.</summary>
    public void ClaimLocalDraft(long id) => _openLocalDrafts[id] = 0;
    public void ReleaseLocalDraft(long id) => _openLocalDrafts.TryRemove(id, out _);
    public bool IsLocalDraftOpen(long id) => _openLocalDrafts.ContainsKey(id);

    /// <summary>Uploads drafts that were kept on this PC while offline, once their account is connected again.</summary>
    private async Task FlushPendingDraftsAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _flushingDrafts, 1) == 1) return;
        try
        {
            foreach (var listed in Store.GetLocalDrafts(pendingOnly: true))
            {
                if (StatusOf(listed.AccountId)?.State != SyncState.Idle) continue;   // offline / signing in / busy: try later
                if (IsLocalDraftOpen(listed.Id)) continue;                          // its compose window decides
                var l = Store.GetLocalDraft(listed.Id);                              // re-read: it may have changed or gone
                if (l == null || !l.PendingUpload) continue;
                var d = DraftFromLocal(l);
                try
                {
                    await SaveDraftAsync(d, ct);
                    // Only remove the local copy if nobody changed or opened it while it was uploading.
                    // (A window that opens it later re-creates it on its next autosave.)
                    if (!IsLocalDraftOpen(l.Id)) Store.DeleteLocalDraftIfUnchanged(l.Id, l.Updated);
                    Log.Info($"uploaded draft kept on this PC: '{l.Subject}'");
                    Changed?.Invoke(new ChangeSet { AccountId = l.AccountId });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn("draft upload still failing: " + ex.Message); }
            }
        }
        finally { Interlocked.Exchange(ref _flushingDrafts, 0); }
    }

    /// <summary>Deletes a server draft (after it was sent or re-saved).</summary>
    public void DeleteDraft(long rowId)
    {
        var row = Store.GetMessage(rowId);
        if (row == null) return;
        Queue(row, PendingOpKind.Delete);
        Store.DeleteRow(row.Id);
        Touched(row.AccountId, new[] { row.FolderId });
    }

    public void Reschedule(long outboxId, DateTimeOffset when)
    {
        Store.RescheduleOutbox(outboxId, when);
        OutboxChanged?.Invoke();
    }

    public List<OutboxItem> Outbox() => Store.GetOutbox();
    /// <summary>The outbox without the messages themselves — cheap enough to read every second (status bar).</summary>
    public List<OutboxItem> OutboxSummary() => Store.GetOutbox(withMime: false);

    private async Task ProcessOutboxAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        foreach (var item in Store.GetOutbox().Where(o => o.SendAt <= now))
        {
            if (item.Status == OutboxStatus.Failed && item.Attempts >= 6) continue;
            if (item.Status == OutboxStatus.Sending) continue;
            if (!Store.TryClaimOutbox(item.Id)) continue;
            OutboxChanged?.Invoke();
            var account = AccountById(item.AccountId);
            try
            {
                if (account == null) throw new InvalidOperationException("The account for this message was removed.");
                var msg = Composer.FromBytes(item.Mime);
                await Connector.SendAsync(account, msg, ct);
                Store.SetOutboxResult(item.Id, OutboxStatus.Sent);
                Log.Info($"sent '{item.Subject}' from {account.Email}");
                if (!account.ServerSavesSent && _syncs.TryGetValue(account.Id, out var sync))
                {
                    try { await sync.AppendToSentAsync(msg, ct); }
                    catch (Exception ex) { Log.Warn("could not save a copy in Sent: " + ex.Message); }
                }
                if (_syncs.TryGetValue(account.Id, out var draftsSync))
                {
                    try { await draftsSync.DeleteDraftsByMessageIdAsync(msg.MessageId ?? "", ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("could not remove the sent message's draft: " + ex.Message); }
                }
                DropStaleLocalCopies(msg.MessageId, null);
                if (item.RemindAt is { } due)
                    Store.AddReminder(new Reminder { AccountId = item.AccountId, ThreadKey = item.ThreadKey, Subject = item.Subject, After = DateTimeOffset.Now, Due = due });
                Sent?.Invoke(item);
                SyncNow(item.AccountId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Store.SetOutboxResult(item.Id, OutboxStatus.Failed, "Interrupted", DateTimeOffset.Now);
                throw;
            }
            catch (Exception ex)
            {
                var attempts = item.Attempts + 1;
                var retry = DateTimeOffset.Now.AddMinutes(Math.Min(60, Math.Pow(2, attempts)));
                var msgText = Connector.Friendly(ex);
                Log.Error($"send failed ({attempts}) '{item.Subject}'", ex);
                Store.SetOutboxResult(item.Id, OutboxStatus.Failed, msgText, retry);
                SendFailed?.Invoke(item, msgText);
            }
            OutboxChanged?.Invoke();
        }
    }

    // ───────────────────────── timers ─────────────────────────

    private async Task TimersAsync(CancellationToken ct)
    {
        var lastMinute = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessOutboxAsync(ct);

                var woke = Store.WakeDueSnoozes(DateTimeOffset.Now);
                if (woke.Count > 0)
                {
                    foreach (var g in woke.GroupBy(w => (w.AccountId, w.ThreadKey)))
                        MarkLatestUnread(g.Key.AccountId, g.Key.ThreadKey);
                    SnoozeWoke?.Invoke(woke);
                    foreach (var g in woke.GroupBy(w => w.AccountId))
                    {
                        var cs = new ChangeSet { AccountId = g.Key };
                        foreach (var f in g.Select(x => x.FolderId).Distinct()) cs.FolderIds.Add(f);
                        Changed?.Invoke(cs);
                    }
                }

                if (DateTimeOffset.Now - lastMinute > TimeSpan.FromSeconds(30))
                {
                    lastMinute = DateTimeOffset.Now;
                    CheckReminders();
                    if (Interlocked.Exchange(ref _promotePending, 0) == 1) PromoteKnownSenders();
                    _ = Task.Run(async () =>
                    {
                        try { await FlushPendingDraftsAsync(ct); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Log.Warn("draft upload: " + ex.Message); }
                    });
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error("timer loop", ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void CheckReminders()
    {
        var mine = MyAddresses;
        foreach (var r in Store.GetReminders(ReminderState.Waiting).Where(r => r.Due <= DateTimeOffset.Now))
        {
            if (!r.Always && Store.HasReplyAfter(r.AccountId, r.ThreadKey, r.After, mine))
            {
                Store.SetReminderState(r.Id, ReminderState.Done);
                continue;
            }
            Store.SetReminderState(r.Id, ReminderState.Due);
            Store.BumpThread(r.AccountId, r.ThreadKey, DateTimeOffset.Now);
            ReminderDue?.Invoke(r);
            var cs = new ChangeSet { AccountId = r.AccountId };
            foreach (var f in FolderIds(FolderRole.Inbox, r.AccountId)) cs.FolderIds.Add(f);
            Changed?.Invoke(cs);
        }
    }

    public List<Reminder> DueReminders() => Store.GetReminders(ReminderState.Due);
    public List<Reminder> WaitingReminders() => Store.GetReminders(ReminderState.Waiting);
    public void DismissReminder(long id) { Store.SetReminderState(id, ReminderState.Done); Changed?.Invoke(new ChangeSet()); }

    /// <summary>People we have written to move to People (unless the user chose otherwise).</summary>
    private void PromoteKnownSenders()
    {
        var changed = Store.PromoteKnownSenders(Settings.Current.SenderCategories.Keys);
        if (changed == 0) return;
        foreach (var a in Accounts)
        {
            var cs = new ChangeSet { AccountId = a.Id };
            foreach (var f in FolderIds(FolderRole.Inbox, a.Id)) cs.FolderIds.Add(f);
            Changed?.Invoke(cs);
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        foreach (var s in _syncs.Values) s.Dispose();
        _syncs.Clear();
    }
}
