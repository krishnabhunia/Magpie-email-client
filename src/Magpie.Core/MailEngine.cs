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
    private Task? _rules;
    private readonly object _ruleGate = new();
    private readonly SemaphoreSlim _deferredRulesGate = new(1, 1);
    private long _lastDeferredRuleMessage;
    private int _promotePending;
    private readonly Func<Account, MimeMessage, CancellationToken, Task> _sendMessage;

    public event Action<ChangeSet>? Changed;
    public event Action<string, SyncStatus>? StatusChanged;
    public event Action<IReadOnlyList<MessageRow>>? NewMail;
    public event Action? OutboxChanged;
    public event Action<OutboxItem>? Sent;
    public event Action<OutboxItem, string>? SendFailed;
    public event Action<IReadOnlyList<MessageRow>>? SnoozeWoke;
    public event Action<Reminder>? ReminderDue;
    /// <summary>Design B2: a calendar event starts soon (its reminder is due).</summary>
    public event Action<IReadOnlyList<CalendarEvent>>? EventReminderDue;
    /// <summary>Design B2: the Google calendars of the Google accounts.</summary>
    public Calendar.CalendarService Calendar { get; }

    public MailEngine(AppPaths paths, ISecretProtector protector, HttpMessageHandler? httpHandler = null,
        Func<Account, MimeMessage, CancellationToken, Task>? sendMessage = null)
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
        _sendMessage = sendMessage ?? Connector.SendAsync;
        Ai = new AiService(Http, () => Settings.Current.Ai, id => Vault.Get(SecretVault.AiKeyFor(id)));
        Calendar = new Calendar.CalendarService(Store, Http, () => Accounts, (a, ct) => OAuth.GetAccessTokenAsync(a, ct), OAuth.GrantedScopes);
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
        try { MergeDuplicateAutoDeleteRules(); } catch (Exception ex) { Log.Warn("auto-delete rules: " + ex.Message); }
        lock (_knownGate) _known = Store.KnownContacts();
        foreach (var a in Accounts.Where(a => a.Enabled)) StartAccount(a);
        _timers = Task.Run(() => TimersAsync(_cts.Token));
        _rules = Task.Run(() => DeferredRulesLoopAsync(_cts.Token));
        Calendar.Start(_cts.Token);
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
            // Rules first (design B5), so the list shows where mail ended up and "Skip notification" works.
            IReadOnlyList<MessageRow> notify = cs.NewInboxMessages;
            if (cs.NewInboxArrivals.Count > 0)
            {
                var quiet = new HashSet<long>();
                try { quiet.UnionWith(RunGatekeeper(cs.AccountId, cs.NewInboxArrivals)); }
                catch (Exception ex) { Log.Error("gatekeeper failed", ex); }
                try { quiet.UnionWith(RunRules(cs.AccountId, cs.NewInboxArrivals.Where(m => !quiet.Contains(m.Id)).ToList(), notifyWhenReady: cs.NewInboxMessages.Select(m => m.Id).ToHashSet())); }
                catch (Exception ex) { Log.Error("rules failed", ex); }
                try { TagArrivals(cs.AccountId, cs.NewInboxArrivals); }
                catch (Exception ex) { Log.Error("auto-delete tagging failed", ex); }
                if (quiet.Count > 0) notify = cs.NewInboxMessages.Where(m => !quiet.Contains(m.Id)).ToList();
            }
            Changed?.Invoke(cs);
            if (notify.Count > 0) NewMail?.Invoke(notify);
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
        Calendar.Poke();
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
        Calendar.Poke();   // a new sign-in may now allow the calendar (design B2)
        Changed?.Invoke(new ChangeSet { AccountId = a.Id, FoldersChanged = true });
    }

    public void RemoveAccount(string accountId)
    {
        if (_syncs.TryRemove(accountId, out var s)) s.Dispose();
        Settings.Current.Accounts.RemoveAll(a => a.Id == accountId);
        Settings.Save();
        Vault.RemovePrefix($"account:{accountId}:");
        OAuth.Forget(accountId);
        Calendar.ForgetAccount(accountId);
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
        if (trash != null)
            Store.RecordTrashed(copies.Where(m => !(folders.TryGetValue(m.FolderId, out var f0) && f0.Role is FolderRole.Trash or FolderRole.Junk)), DateTimeOffset.Now);
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

    // ───────────────────────── delete for good (design DX1-A: Shift+Delete, "Delete forever…") ─────────────────────────

    /// <summary>
    /// The emails that <see cref="DeleteForeverAsync"/> would remove: those of the conversations in <paramref name="inFolders"/>
    /// (the folders of the current view), and every other copy of each of them (same Message-ID in any folder of the account).
    /// </summary>
    public List<MessageRow> CopiesToDeleteForever(string accountId, IEnumerable<string> threadKeys, IReadOnlyCollection<long> inFolders)
    {
        var seen = new HashSet<long>();
        var list = new List<MessageRow>();
        foreach (var key in threadKeys.Distinct())
            foreach (var m in Store.GetThreadCopies(accountId, key).Where(m => inFolders.Contains(m.FolderId)))
                foreach (var copy in Store.CopiesOf(m))
                    if (seen.Add(copy.Id)) list.Add(copy);
        return list;
    }

    /// <summary>How many emails (copies of one counted once) a delete for good would take — the number in the question.</summary>
    public int CountDeleteForever(string accountId, IEnumerable<string> threadKeys, IReadOnlyCollection<long> inFolders) =>
        AutoDelete.PastEmails.Of(CopiesToDeleteForever(accountId, threadKeys, inFolders)).Count;

    /// <summary>
    /// Deletes for good, skipping Trash (design DX1-A): every copy of each email goes (\Deleted + expunge on the server, queued),
    /// the rows leave this PC at once, and there is no undo. Pinned emails go too — the user confirmed this one by hand.
    /// Returns how many emails (copies of one counted once) were deleted.
    /// </summary>
    public Task<int> DeleteForeverAsync(string accountId, IEnumerable<string> threadKeys, IReadOnlyCollection<long> inFolders)
    {
        var copies = CopiesToDeleteForever(accountId, threadKeys, inFolders);
        if (copies.Count == 0) return Task.FromResult(0);
        var touched = new HashSet<long>();
        foreach (var m in copies)
        {
            Queue(m, PendingOpKind.Delete);
            Store.DeleteRow(m.Id);
            touched.Add(m.FolderId);
        }
        Store.ForgetTrashed(accountId, copies.Select(m => m.MessageId));
        var n = AutoDelete.PastEmails.Of(copies).Count;
        Log.Info($"deleted for good: {n} email(s) ({copies.Count} copies) in {AccountById(accountId)?.Email ?? accountId}, skipping Trash");
        Touched(accountId, touched);
        return Task.FromResult(n);
    }

    // ───────────────────────── Trash and Spam (design TB1) ─────────────────────────

    private DateTimeOffset _lastAutoEmpty = DateTimeOffset.MinValue;

    /// <summary>Empty Trash / Empty Spam (T1, T6): every email there is deleted for good, on this PC and on the server
    /// (also those not listed here yet). Returns how many were on this PC.</summary>
    public int EmptyFolder(string accountId, FolderRole role)
    {
        if (role is not (FolderRole.Trash or FolderRole.Junk)) throw new ArgumentException("Only Trash and Spam can be emptied.");
        var folder = Store.GetFolders(accountId).FirstOrDefault(f => f.Role == role);
        if (folder == null) return 0;
        var rows = Store.MessagesInFolder(folder.Id);
        foreach (var m in rows) Store.DeleteRow(m.Id);
        Store.ForgetTrashed(accountId, rows.Select(m => m.MessageId));
        Store.AddPendingOp(new PendingOp { AccountId = accountId, FolderId = folder.Id, Kind = PendingOpKind.EmptyFolder });
        Log.Info($"emptied {role} of {AccountById(accountId)?.Email}: {rows.Count} email(s) on this PC");
        Touched(accountId, new[] { folder.Id });
        return rows.Count;
    }

    /// <summary>Restore (T2): the conversation's copies in Trash go back to the folder they came from (Inbox when not known).</summary>
    public void Restore(string accountId, string threadKey)
    {
        var folders = Store.GetFolders(accountId);
        var trash = folders.Where(f => f.Role == FolderRole.Trash).Select(f => f.Id).ToHashSet();
        var inbox = folders.FirstOrDefault(f => f.Role == FolderRole.Inbox) ?? throw new InvalidOperationException("This account has no Inbox.");
        var copies = Store.GetThreadCopies(accountId, threadKey).Where(m => trash.Contains(m.FolderId)).ToList();
        var touched = new HashSet<long>();
        foreach (var m in copies)
        {
            var origin = Store.TrashOrigin(accountId, m.MessageId);
            var dest = origin is { } o && folders.FirstOrDefault(f => f.Id == o && !trash.Contains(f.Id)) is { } back ? back : inbox;
            Queue(m, PendingOpKind.Move, dest.Id);
            Store.DeleteRow(m.Id);
            touched.Add(m.FolderId);
            touched.Add(dest.Id);
        }
        Store.ForgetTrashed(accountId, copies.Select(m => m.MessageId));
        if (touched.Count > 0) Touched(accountId, touched);
    }

    /// <summary>Not spam (T6): the conversation goes to the Inbox and its senders get through the Gatekeeper from now on.</summary>
    public void NotSpam(string accountId, string threadKey)
    {
        var folders = Store.GetFolders(accountId);
        var junk = folders.Where(f => f.Role == FolderRole.Junk).Select(f => f.Id).ToList();
        var inbox = folders.FirstOrDefault(f => f.Role == FolderRole.Inbox) ?? throw new InvalidOperationException("This account has no Inbox.");
        var senders = Store.GetThreadCopies(accountId, threadKey).Where(m => junk.Contains(m.FolderId))
            .Select(m => m.FromAddress.Trim().ToLowerInvariant()).Where(a => a.Contains('@') && !MyAddresses.Contains(a)).Distinct().ToList();
        MoveCopies(accountId, threadKey, junk, inbox);
        foreach (var a in senders) AllowSender(a);
    }

    /// <summary>Emptying Trash by itself (T7): emails in Trash for longer than the chosen days are deleted for good.
    /// Time in Trash is counted from when Magpie moved it there, or first saw it there.</summary>
    public int AutoEmptyTrash(DateTimeOffset now)
    {
        var days = Config.EmptyTrashAfterDays;
        if (days <= 0) return 0;
        var n = 0;
        foreach (var a in Accounts)
        {
            var trash = Store.GetFolders(a.Id).FirstOrDefault(f => f.Role == FolderRole.Trash);
            if (trash == null) continue;
            Store.StampTrash(trash.Id, now);
            var old = Store.TrashedBefore(trash.Id, now.AddDays(-days));
            if (old.Count == 0) continue;
            foreach (var m in old) { Queue(m, PendingOpKind.Delete); Store.DeleteRow(m.Id); }
            Store.ForgetTrashed(a.Id, old.Select(m => m.MessageId));
            Touched(a.Id, new[] { trash.Id });
            n += old.Count;
        }
        if (n > 0) Log.Info($"Trash emptied by itself: {n} email(s) older than {days} days");
        return n;
    }

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

    /// <summary>Set aside (design B7, key L): out of the Inbox without a date, into one pile; false puts it back on top of the Inbox.</summary>
    public void SetAside(string accountId, string threadKey, bool aside)
    {
        var touched = new HashSet<long>();
        SetAsideRows(accountId, threadKey, aside, touched);
        Touched(accountId, touched);
    }

    private void SetAsideRows(string accountId, string threadKey, bool aside, HashSet<long> touched)
    {
        var inbox = FolderIds(FolderRole.Inbox, accountId);
        Store.SetSnooze(accountId, threadKey, inbox, aside ? MessageRow.SetAsideMark : null);
        if (!aside) Store.BumpThread(accountId, threadKey, DateTimeOffset.Now);
        foreach (var f in inbox) touched.Add(f);
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

    // ───────────────────────── meeting invites (design B3) ─────────────────────────

    /// <summary>
    /// Keeps our copy of an event in step with what arrived: a cancellation marks it cancelled; an update (higher
    /// SEQUENCE) takes the new details, and a changed time asks for a new answer. Returns the event as we now know it.
    /// </summary>
    public MailStore.LocalEvent? TrackInvite(string accountId, CalendarInvite inv)
    {
        if (inv.Uid.Length == 0) return null;
        var ev = Store.GetEvent(accountId, inv.Uid);
        if (inv.IsCancel)
        {
            if (ev == null || inv.Sequence >= ev.Sequence)
            {
                ev = (ev ?? ToEvent(accountId, inv, "")) with { Cancelled = true, Sequence = inv.Sequence };
                Store.SaveEvent(ev);
            }
            return ev;
        }
        if (inv.IsRequest && ev != null && inv.Sequence > ev.Sequence)
        {
            // A new time needs a new answer: "UPDATED" = you had answered, the organiser has since moved it.
            var moved = ev.Start != inv.Start || ev.End != inv.End;
            ev = ToEvent(accountId, inv, !moved ? ev.Answer : ev.Answer.Length > 0 ? "UPDATED" : "");
            Store.SaveEvent(ev);
        }
        return ev;
    }

    private static MailStore.LocalEvent ToEvent(string accountId, CalendarInvite inv, string answer) =>
        new(accountId, inv.Uid, inv.Summary, inv.Start, inv.End, inv.AllDay, inv.Location, inv.Organizer?.Email ?? "", answer, inv.Sequence, false);

    /// <summary>
    /// Accept / Maybe / Decline: sends the iCalendar reply to the organiser through the outbox (normal undo window),
    /// optionally with a note, and remembers the answer. Returns the outbox id and the event as it was (for Undo).
    /// </summary>
    public (long OutboxId, MailStore.LocalEvent? Before) AnswerInvite(MessageRow message, CalendarInvite inv, InviteAnswer answer, string? note)
    {
        var account = AccountById(message.AccountId) ?? throw new InvalidOperationException("This account is no longer in Magpie.");
        var organizer = inv.Organizer?.Email;
        if (string.IsNullOrWhiteSpace(organizer) || !organizer.Contains('@')) throw new InvalidOperationException("This invite doesn't say who organised it, so there is nobody to answer.");
        var me = inv.Attendees.FirstOrDefault(a => MyAddresses.Contains(a.Email, StringComparer.OrdinalIgnoreCase))?.Email ?? account.Email;
        var now = DateTimeOffset.Now;
        var text = Composer.TextToParagraphs(string.IsNullOrWhiteSpace(note)
            ? answer switch { InviteAnswer.Accepted => "Accepted.", InviteAnswer.Tentative => "Tentatively accepted.", _ => "Declined." }
            : note);
        var mid = message.MessageId;
        var d = new Draft
        {
            AccountId = account.Id, Mode = ComposeMode.Reply, To = organizer, Subject = Invites.SubjectFor(answer, inv.Summary), Html = text,
            InReplyTo = mid.Length > 0 ? "<" + mid + ">" : "", References = mid.Length > 0 ? "<" + mid + ">" : "", ThreadKey = message.ThreadKey,
            CalendarReply = Invites.BuildReply(inv, me, account.DisplayName, answer, note, now),
        };
        var before = Store.GetEvent(account.Id, inv.Uid);
        var id = QueueSend(d, now.AddSeconds(Config.UndoSendSeconds), null);
        var partstat = answer switch { InviteAnswer.Accepted => "ACCEPTED", InviteAnswer.Tentative => "TENTATIVE", _ => "DECLINED" };
        Store.SaveEvent(ToEvent(account.Id, inv, partstat));
        Log.Info($"invite answered: {partstat}");
        return (id, before);
    }

    /// <summary>Undo an answer still in its undo window: the reply isn't sent and the old answer comes back.</summary>
    public bool UndoInviteAnswer(long outboxId, string accountId, string uid, MailStore.LocalEvent? before)
    {
        if (Recall(outboxId) == null) return false;
        if (before != null) Store.SaveEvent(before);
        else if (Store.GetEvent(accountId, uid) is { } ev) Store.SaveEvent(ev with { Answer = "" });
        return true;
    }

    // ───────────────────────── quick replies (design B6) ─────────────────────────

    /// <summary>Sends a quick reply to <paramref name="original"/> through the outbox (so Undo works). Returns the outbox id.</summary>
    public async Task<long> QuickReplyAsync(MessageRow original, string text, CancellationToken ct = default)
    {
        var account = AccountById(original.AccountId) ?? throw new InvalidOperationException("This account is no longer in Magpie.");
        var (body, _) = await LoadAsync(original, false, ct);
        var d = Composer.QuickReply(account, original, body, MyAddresses, text);
        return QueueSend(d, DateTimeOffset.Now.AddSeconds(Config.UndoSendSeconds), null);
    }

    // ───────────────────────── rules (design B5) ─────────────────────────

    /// <summary>
    /// Runs rules over these messages (new Inbox mail, or mail already there when asked), top to bottom. A message
    /// that a rule moves or deletes is not seen by later rules. Returns the ids that should not be announced
    /// (skip notification, marked read, moved, deleted, snoozed or set aside).
    /// </summary>
    public HashSet<long> RunRules(string accountId, IReadOnlyList<MessageRow> messages, IReadOnlyList<MailRule>? only = null, IReadOnlySet<long>? notifyWhenReady = null)
    {
        lock (_ruleGate) return RunRulesCore(accountId, messages, only, notifyWhenReady);
    }

    private HashSet<long> RunRulesCore(string accountId, IReadOnlyList<MessageRow> messages,
        IReadOnlyList<MailRule>? only, IReadOnlySet<long>? notifyWhenReady = null)
    {
        var quiet = new HashSet<long>();
        var rules = (only ?? Config.Rules).Where(r => RuleEngine.IsRunnable(r) && RuleEngine.AppliesToAccount(r, accountId)).ToList();
        if (rules.Count == 0 || messages.Count == 0) return quiet;
        var email = AccountById(accountId)?.Email ?? "";
        var folders = Store.GetFolders(accountId);
        var needsBody = rules.Any(r => r.Conditions.Any(c => c.Field == RuleField.Body));
        var touched = new HashSet<long>();
        var now = DateTime.Now;
        foreach (var arrived in messages)
        {
            var m = Store.GetMessage(arrived.Id);
            if (m == null) continue;
            if (needsBody && !m.BodyCached)
            {
                Store.DeferRules(m.Id, notifyWhenReady?.Contains(m.Id) == true && !m.IsSeen, rules);
                quiet.Add(m.Id);
                continue;
            }
            var body = needsBody && m.BodyCached ? Store.GetBody(m.Id)?.Text ?? "" : "";
            foreach (var rule in rules)
            {
                if (!RuleEngine.Matches(rule, m, new RuleContext(email, body))) continue;
                Log.Info($"rule \"{rule.Name}\" matched a message in {email}");
                if (ApplyRule(rule, m, folders, touched, quiet, now)) break;
                m = Store.GetMessage(m.Id) ?? m;
            }
        }
        if (touched.Count > 0) Touched(accountId, touched);
        return quiet;
    }

    internal async Task ProcessDeferredRulesAsync(CancellationToken ct)
    {
        await _deferredRulesGate.WaitAsync(ct);
        try
        {
            foreach (var pending in Store.DeferredRules(afterMessageId: _lastDeferredRuleMessage))
            {
                ct.ThrowIfCancellationRequested();
                // Unavailable bodies must not keep later messages behind the first batch forever.
                _lastDeferredRuleMessage = pending.Row.Id;
                try
                {
                    var row = pending.Row;
                    if (!row.BodyCached) await FetchBodyAsync(row, ct);
                    row = Store.GetMessage(row.Id)!;
                    if (row == null || !row.BodyCached) continue;
                    MessageRow? announcement = null;
                    lock (_ruleGate)
                    {
                        var work = Store.DeferredRules(row.Id).SingleOrDefault();
                        if (work.Row == null) continue;
                        if (!Store.GetFolders(row.AccountId).Any(f => f.Id == row.FolderId && f.Role == FolderRole.Inbox))
                        {
                            Store.CompleteDeferredRules(row.Id);
                            continue;
                        }
                        var quiet = RunRulesCore(row.AccountId, new[] { row }, work.Rules);
                        Store.CompleteDeferredRules(row.Id);
                        var current = Store.GetMessage(row.Id);
                        if (work.Notify && current is { IsSeen: false } && !quiet.Contains(row.Id)
                            && Store.GetFolders(row.AccountId).Any(f => f.Id == current.FolderId && f.Role == FolderRole.Inbox))
                            announcement = current;
                    }
                    if (announcement != null) NewMail?.Invoke(new[] { announcement });
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn("waiting to apply mail rules: " + ex.Message); }
            }
        }
        finally { _deferredRulesGate.Release(); }
    }

    private async Task DeferredRulesLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await ProcessDeferredRulesAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error("deferred rules", ex); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    /// <summary>True when the message left its folder (moved or deleted).</summary>
    private bool ApplyRule(MailRule rule, MessageRow m, List<MailFolder> folders, HashSet<long> touched, HashSet<long> quiet, DateTime now)
    {
        var copies = Store.GetThreadCopies(m.AccountId, m.ThreadKey)
            .Where(r => r.Id == m.Id || (m.MessageId.Length > 0 && r.MessageId == m.MessageId)).ToList();
        foreach (var a in rule.Actions.Where(a => a.Kind is not (RuleActionKind.MoveToFolder or RuleActionKind.Delete)))
        {
            switch (a.Kind)
            {
                case RuleActionKind.MarkRead:
                    foreach (var r in copies.Where(r => !r.IsSeen))
                    {
                        Store.SetLocalFlags(r.Id, r.Flags | MessageFlags.Seen);
                        Queue(r, PendingOpKind.SetSeen);
                        touched.Add(r.FolderId);
                    }
                    quiet.Add(m.Id);
                    break;
                case RuleActionKind.Pin:
                    foreach (var r in copies.Where(r => !r.IsFlagged))
                    {
                        Store.SetLocalFlags(r.Id, r.Flags | MessageFlags.Flagged);
                        Queue(r, PendingOpKind.SetFlagged);
                        touched.Add(r.FolderId);
                    }
                    break;
                case RuleActionKind.Tag when a.Target.Trim().Length > 0:
                    foreach (var r in copies)
                    {
                        var tags = r.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        if (tags.Contains(a.Target.Trim(), StringComparer.OrdinalIgnoreCase)) continue;
                        tags.Add(a.Target.Trim().Replace(",", " "));
                        Store.SetTags(r.Id, string.Join(",", tags));
                        touched.Add(r.FolderId);
                    }
                    break;
                case RuleActionKind.Snooze:
                {
                    var inbox = folders.Where(f => f.Role == FolderRole.Inbox).Select(f => f.Id).ToList();
                    Store.SetSnooze(m.AccountId, m.ThreadKey, inbox, RuleEngine.SnoozeUntil(a.Target, now));
                    foreach (var f in inbox) touched.Add(f);
                    quiet.Add(m.Id);
                    break;
                }
                case RuleActionKind.SetAside:
                    SetAsideRows(m.AccountId, m.ThreadKey, true, touched);
                    quiet.Add(m.Id);
                    break;
                case RuleActionKind.SkipNotification:
                    quiet.Add(m.Id);
                    break;
            }
        }
        if (rule.Actions.Any(a => a.Kind == RuleActionKind.Delete))
        {
            // Design DX1-A: every copy of the email goes to Trash (same Message-ID in any folder), so a Gmail email does
            // not linger in All Mail with only its Inbox label gone.
            var trash = folders.FirstOrDefault(f => f.Role == FolderRole.Trash);
            var all = m.MessageId.Length > 0 ? Store.CopiesOf(m) : new List<MessageRow> { m };
            var left = false;
            if (trash != null) Store.RecordTrashed(all.Where(r => r.FolderId != trash.Id).OrderBy(r => r.Id == m.Id ? 1 : 0), DateTimeOffset.Now);   // the matched copy's folder is the one Restore uses
            foreach (var r in all)
            {
                if (trash?.Id == r.FolderId) continue;
                if (trash == null) Queue(r, PendingOpKind.Delete); else Queue(r, PendingOpKind.Move, trash.Id);
                Store.DeleteRow(r.Id);
                touched.Add(r.FolderId);
                if (r.Id == m.Id) left = true;
            }
            if (left) quiet.Add(m.Id);
            return left;
        }
        var move = rule.Actions.FirstOrDefault(a => a.Kind == RuleActionKind.MoveToFolder && a.Target.Trim().Length > 0);
        if (move != null)
        {
            var t = move.Target.Trim();
            var dest = folders.FirstOrDefault(f => f.Path.Equals(t, StringComparison.OrdinalIgnoreCase))
                       ?? folders.FirstOrDefault(f => f.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
            if (dest == null) { Log.Warn($"rule \"{rule.Name}\": no folder \"{t}\" in this account"); return false; }
            if (dest.Id == m.FolderId) return false;
            Queue(m, PendingOpKind.Move, dest.Id);
            Store.DeleteRow(m.Id);
            touched.Add(m.FolderId);
            touched.Add(dest.Id);
            quiet.Add(m.Id);
            return true;
        }
        return false;
    }

    // ───────────────────────── auto-delete / OTP delete (designs AD1–AD4) ─────────────────────────

    /// <summary>Raised when auto-delete rules or timers change (Settings table, sidebar "Deleting soon").</summary>
    public event Action? AutoDeleteChanged;

    public List<AutoDeleteRule> AutoDeleteRules() => Store.GetAutoDeleteRules();

    /// <summary>Rules saved twice for one sender before rules were kept unique: the newest stays, the others' emails join it.</summary>
    public int MergeDuplicateAutoDeleteRules()
    {
        var merged = 0;
        foreach (var g in Store.GetAutoDeleteRules().GroupBy(r => (r.Pattern, r.AccountId)).Where(g => g.Count() > 1))
        {
            var keep = g.OrderByDescending(r => r.Created).First();
            foreach (var r in g.Where(r => r.Id != keep.Id))
            {
                Store.MoveDeleteTimers(r.Id, keep.Id);
                Store.DeleteAutoDeleteRule(r.Id, clearTimers: false);
                merged++;
            }
        }
        if (merged > 0) { Log.Info($"auto-delete: {merged} duplicate rule(s) merged"); AutoDeleteChanged?.Invoke(); }
        return merged;
    }

    /// <summary>
    /// Creates or updates a rule. With <paramref name="startOnExisting"/> the emails already in the Inbox from that
    /// sender get a timer too, counted from now. Returns how many emails got a timer.
    /// </summary>
    public int SaveAutoDeleteRule(AutoDeleteRule rule, bool startOnExisting)
    {
        rule.Pattern = AutoDelete.NormalisePattern(rule.Pattern) ?? throw new ArgumentException("Write an address like name@example.com, or *@example.com for everyone there.");
        rule.Amount = Math.Clamp(rule.Amount, 1, 100);
        rule.AccountId ??= "";
        // One rule per sender (and account): a second one for the same sender changes the first instead of adding a row.
        if (Store.GetAutoDeleteRules().FirstOrDefault(r => r.Id != rule.Id && r.Pattern == rule.Pattern && r.AccountId == rule.AccountId) is { } same)
        {
            Store.DeleteAutoDeleteRule(rule.Id, clearTimers: false);   // editing a rule into an existing one's sender: they merge
            Store.MoveDeleteTimers(rule.Id, same.Id);
            rule.Id = same.Id;
            rule.Created = same.Created;
        }
        Store.SaveAutoDeleteRule(rule);
        var n = 0;
        if (startOnExisting && !rule.Paused)
        {
            var now = DateTimeOffset.Now;
            // Design DP1 (D7): counted from when each email arrived, so those already past their time go to Trash at once.
            n = Store.SetDeleteTimers(ExistingFor(rule).Select(m => (m.Id, AutoDelete.DeleteAt(rule, m.Date < now ? m.Date : now))), rule.Id);
        }
        Log.Info($"auto-delete rule saved: {rule.Pattern}, {AutoDelete.Describe(rule)}" + (n > 0 ? $", {n} existing email(s)" : ""));
        AutoDeleteTouched();
        return n;
    }

    // ───────────────────────── emails already here (design DP1) ─────────────────────────

    /// <summary>D2–D4: the emails already here from an address or "*@domain", in every folder except Trash, Spam, Sent and
    /// Drafts; pinned ones are kept. <paramref name="olderThan"/> keeps only those that arrived before it;
    /// <paramref name="accountId"/> "" (or null) = every account (design DX1).</summary>
    public AutoDelete.PastEmails PastFrom(string pattern, DateTimeOffset? olderThan = null, string? accountId = null)
    {
        var p = AutoDelete.NormalisePattern(pattern);
        if (p == null) return AutoDelete.PastEmails.Of(Array.Empty<MessageRow>());
        var rows = Store.MessagesFrom(PastFolders(accountId), p).Where(m => !m.IsFlagged && (olderThan == null || m.Date < olderThan)).ToList();
        return AutoDelete.PastEmails.Of(rows);
    }

    /// <summary>The folders a "delete emails from…" covers: everything except Trash, Spam, Sent and Drafts (design DX1).</summary>
    private List<long> PastFolders(string? accountId) =>
        Store.GetFolders(string.IsNullOrEmpty(accountId) ? null : accountId)
            .Where(f => (string.IsNullOrEmpty(accountId) || f.AccountId == accountId) && f.Role is not (FolderRole.Trash or FolderRole.Junk or FolderRole.Sent or FolderRole.Drafts))
            .Select(f => f.Id).ToList();

    /// <summary>The counts the "Delete emails from…" dialog shows (design DX1-B1): all the emails already here from the
    /// sender, how many of them are older than <paramref name="olderThan"/> (all, when null) and the oldest of those.</summary>
    public AutoDelete.PastSummary PastSummary(string pattern, string? accountId, DateTimeOffset? olderThan)
    {
        var all = PastFrom(pattern, null, accountId);
        if (olderThan == null) return new AutoDelete.PastSummary(all.Count, all.Count, all.Oldest);
        var older = AutoDelete.PastEmails.Of(all.Rows.Where(m => m.Date < olderThan).ToList());
        return new AutoDelete.PastSummary(all.Count, older.Count, older.Oldest);
    }

    /// <summary>
    /// One run of the "Delete emails from…" dialog (design DX1-B1). PAST: the emails older than the kept period are listed
    /// in the result for the caller to trash after its undo wait (<see cref="TrashEmails"/>). FUTURE: the one rule for the
    /// sender and account is created or updated, and the emails already here that are not yet past the time get their
    /// timer (counted from arrival) — in every folder PAST covers, not the Inbox only. Those already past the time are left
    /// alone unless PAST is on: that tick is the only thing that deletes now.
    /// </summary>
    public AutoDelete.DeleteFromResult ApplyDeleteFrom(AutoDelete.DeleteFromRequest req, DateTimeOffset now)
    {
        var pattern = AutoDelete.NormalisePattern(req.Pattern) ?? throw new ArgumentException("Write an address like name@example.com, or *@example.com for everyone there.");
        var accountId = req.AccountId ?? "";
        var cut = req.KeepNothing ? (DateTimeOffset?)null : AutoDelete.KeepSince(req.Otp, req.Amount, req.Unit, now);
        var past = req.Past ? PastFrom(pattern, cut, accountId) : AutoDelete.PastEmails.Of(Array.Empty<MessageRow>());
        AutoDeleteRule? rule = null;
        var isNew = false;
        var timers = 0;
        if (req.Future && !req.KeepNothing)
        {
            var rules = Store.GetAutoDeleteRules();
            var existing = rules.FirstOrDefault(r => r.Id == req.RuleId) ?? rules.FirstOrDefault(r => r.Pattern == pattern && r.AccountId == accountId);
            rule = new AutoDeleteRule
            {
                Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Pattern = pattern, AccountId = accountId, Otp = req.Otp,
                Amount = Math.Clamp(req.Amount, 1, 100), Unit = req.Unit, Paused = existing?.Paused ?? false, Created = existing?.Created ?? now,
            };
            isNew = existing == null;
            SaveAutoDeleteRule(rule, startOnExisting: false);
            if (!rule.Paused && (req.Past || req.StartOnExisting))
            {
                var r = rule;
                timers = Store.SetDeleteTimers(ExistingFor(r).Select(m => (m.Id, AutoDelete.DeleteAt(r, m.Date < now ? m.Date : now)))
                    .Where(t => t.Item2 > now), r.Id);
                if (timers > 0) AutoDeleteTouched(accountId.Length == 0 ? null : accountId);
            }
        }
        var what = req.KeepNothing ? "all" : "older than " + AutoDelete.KeepLabel(req.Otp, req.Amount, req.Unit);
        Log.Info($"delete emails from {pattern}" + (accountId.Length > 0 ? $" ({AccountById(accountId)?.Email ?? accountId})" : "") + ": "
                 + (req.Past ? $"{past.Count} {what} to Trash" : "none now")
                 + (rule != null ? $"; rule {(isNew ? "created" : "updated")} ({AutoDelete.Describe(rule)}), {timers} already here timed" : "; no rule"));
        return new AutoDelete.DeleteFromResult(past, rule, isNew, timers);
    }

    /// <summary>Moves these emails (one by one, not whole conversations) to Trash; in accounts without Trash they are deleted.</summary>
    public int TrashEmails(IReadOnlyList<MessageRow> rows)
    {
        var now = DateTimeOffset.Now;
        foreach (var acc in rows.GroupBy(m => m.AccountId))
        {
            var trash = Store.GetFolders(acc.Key).FirstOrDefault(f => f.Role == FolderRole.Trash);
            var list = acc.Where(m => trash == null || m.FolderId != trash.Id).ToList();
            if (trash != null) Store.RecordTrashed(list, now);
            var touched = new HashSet<long>();
            foreach (var m in list)
            {
                if (trash == null) Queue(m, PendingOpKind.Delete);
                else Queue(m, PendingOpKind.Move, trash.Id);
                Store.DeleteRow(m.Id);
                touched.Add(m.FolderId);
            }
            if (trash != null) touched.Add(trash.Id);
            if (touched.Count > 0) Touched(acc.Key, touched);
        }
        Log.Info($"deleted {rows.Count} email(s) already here (design DP1)");
        return rows.Count;
    }

    /// <summary>Emails already here (not pinned) that a rule would put a timer on: the same folders as <see cref="PastFrom"/>
    /// — everything except Trash, Spam, Sent and Drafts (design DX1; the Inbox only before 5.0.0).</summary>
    public List<MessageRow> ExistingFor(AutoDeleteRule rule)
    {
        var pattern = AutoDelete.NormalisePattern(rule.Pattern);
        if (pattern == null) return new();
        return Accounts.Where(a => AutoDelete.AppliesToAccount(rule, a.Id))
            .SelectMany(a => Store.MessagesFrom(PastFolders(a.Id), pattern))
            .Where(m => !m.IsFlagged).ToList();
    }

    public void PauseAutoDeleteRule(string id, bool paused)
    {
        var rule = Store.GetAutoDeleteRules().FirstOrDefault(r => r.Id == id);
        if (rule == null) return;
        rule.Paused = paused;
        Store.SaveAutoDeleteRule(rule);
        AutoDeleteTouched();
    }

    /// <summary>Remove asks (design AD4): keep the timers already on emails, or clear them all.</summary>
    public void RemoveAutoDeleteRule(string id, bool clearTimers)
    {
        Store.DeleteAutoDeleteRule(id, clearTimers);
        AutoDeleteTouched();
    }

    /// <summary>New Inbox mail from a sender with a rule gets its timer (the earliest, if several rules match).</summary>
    public void TagArrivals(string accountId, IReadOnlyList<MessageRow> arrivals)
    {
        if (arrivals.Count == 0) return;
        var rules = Store.GetAutoDeleteRules().Where(r => !r.Paused && AutoDelete.AppliesToAccount(r, accountId)).ToList();
        if (rules.Count == 0) return;
        var now = DateTimeOffset.Now;
        var tagged = 0;
        foreach (var rule in rules)
        {
            var rows = arrivals.Where(m => !m.IsFlagged && AutoDelete.Matches(rule.Pattern, m.FromAddress))
                .Select(m => (m.Id, AutoDelete.DeleteAt(rule, m.Date < now ? m.Date : now))).ToList();
            if (rows.Count > 0) tagged += Store.SetDeleteTimers(rows, rule.Id);
        }
        if (tagged > 0) AutoDeleteTouched(accountId);
    }

    /// <summary>"Keep this one" (design AD3): the conversation's timers come off, for good.</summary>
    public void KeepFromAutoDelete(string accountId, string threadKey)
    {
        Store.KeepRows(Store.DeleteTimers(accountId, threadKey).Select(t => t.Id));
        AutoDeleteTouched(accountId);
    }

    /// <summary>"Keep all" in Deleting soon: every timer due in the next 7 days comes off.</summary>
    public void KeepAllDeletingSoon()
    {
        Store.KeepRows(Store.RowsDeletingBefore(DateTimeOffset.Now.AddDays(7)));
        AutoDeleteTouched();
    }

    /// <summary>The timer pass: due emails move to Trash (queued for the server). Pinned ones and paused rules are skipped.</summary>
    public int RunDueDeletes(DateTimeOffset now)
    {
        var due = Store.DueDeletes(now);
        if (due.Count == 0) return 0;
        foreach (var acc in due.GroupBy(m => m.AccountId))
        {
            var folders = Store.GetFolders(acc.Key);
            var trash = folders.FirstOrDefault(f => f.Role == FolderRole.Trash);
            var touched = new HashSet<long>();
            if (trash != null) Store.RecordTrashed(acc.Where(m => m.FolderId != trash.Id), now);
            foreach (var m in acc)
            {
                if (trash == null) Queue(m, PendingOpKind.Delete);
                else if (m.FolderId != trash.Id) Queue(m, PendingOpKind.Move, trash.Id);
                Store.DeleteRow(m.Id);
                touched.Add(m.FolderId);
                if (trash != null) touched.Add(trash.Id);
            }
            Touched(acc.Key, touched);
        }
        Log.Info($"auto-delete: {due.Count} email(s) moved to Trash");
        AutoDeleteChanged?.Invoke();
        return due.Count;
    }

    private void AutoDeleteTouched(string? accountId = null)
    {
        foreach (var a in Accounts.Where(a => accountId == null || a.Id == accountId))
        {
            var cs = new ChangeSet { AccountId = a.Id };
            foreach (var f in PastFolders(a.Id)) cs.FolderIds.Add(f);   // a timer can sit in any folder a rule covers (design DX1)
            Changed?.Invoke(cs);
        }
        AutoDeleteChanged?.Invoke();
    }

    // ───────────────────────── Gatekeeper (design B7) ─────────────────────────

    /// <summary>Raised when senders arrive at the door or are allowed / blocked (the Inbox banner counts them).</summary>
    public event Action? GateChanged;

    /// <summary>
    /// New Inbox mail from a blocked sender goes to Spam; with the Gatekeeper on, mail from someone never written to or
    /// heard from (and not allowed) waits at the door instead of landing in the Inbox. Returns the ids kept out.
    /// </summary>
    public HashSet<long> RunGatekeeper(string accountId, IReadOnlyList<MessageRow> arrivals)
    {
        var kept = new HashSet<long>();
        var g = Config.Gatekeeper;
        if (arrivals.Count == 0 || (!g.Enabled && g.Blocked.Count == 0)) return kept;
        var mine = MyAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = arrivals.Select(m => m.Id).ToList();
        var mids = arrivals.Select(m => m.MessageId).ToList();
        var gate = new List<long>();
        var touched = new HashSet<long>();
        var junk = Store.GetFolders(accountId).FirstOrDefault(f => f.Role == FolderRole.Junk);
        foreach (var m in arrivals)
        {
            var addr = m.FromAddress.Trim().ToLowerInvariant();
            if (addr.Length == 0 || mine.Contains(addr)) continue;
            if (g.Blocked.Contains(addr))
            {
                if (junk?.Id == m.FolderId) continue;
                if (junk == null)
                {
                    // No Spam folder to move it to: it waits at the door rather than landing in the Inbox.
                    gate.Add(m.Id);
                    kept.Add(m.Id);
                    touched.Add(m.FolderId);
                    continue;
                }
                Queue(m, PendingOpKind.Move, junk.Id);
                Store.DeleteRow(m.Id);
                touched.Add(m.FolderId);
                touched.Add(junk.Id);
                kept.Add(m.Id);
                continue;
            }
            if (!g.Enabled || g.Allowed.Contains(addr) || IsKnownContact(addr) || Store.HasMailFrom(addr, ids, mids)) continue;
            gate.Add(m.Id);
            kept.Add(m.Id);
            touched.Add(m.FolderId);
        }
        if (gate.Count > 0)
        {
            Store.SetAtGate(gate, true, DateTimeOffset.Now);
            Log.Info($"gatekeeper: {gate.Count} email(s) from new senders wait at the door");
        }
        if (touched.Count > 0) Touched(accountId, touched);
        if (gate.Count > 0) GateChanged?.Invoke();
        return kept;
    }

    public List<GateSender> GateSenders() => Store.GateSenders();

    /// <summary>Allow: this sender goes straight in from now on; their waiting mail moves to the Inbox.</summary>
    public void AllowSender(string address)
    {
        var a = address.Trim().ToLowerInvariant();
        var g = Config.Gatekeeper;
        g.Blocked.Remove(a);
        if (!g.Allowed.Contains(a)) g.Allowed.Add(a);
        Settings.Save(notify: false);
        var rows = Store.GateMessages(a);
        Store.SetAtGate(rows.Select(r => r.Id), false, DateTimeOffset.Now);
        LetIn(rows);
        GateChanged?.Invoke();
    }

    /// <summary>Mail let in from the door is new to the Inbox: rules and auto-delete see it now.</summary>
    private void LetIn(List<MessageRow> rows)
    {
        foreach (var acc in rows.GroupBy(r => r.AccountId))
        {
            var list = acc.ToList();
            try { RunRules(acc.Key, list); } catch (Exception ex) { Log.Error("rules failed", ex); }
            try { TagArrivals(acc.Key, list); } catch (Exception ex) { Log.Error("auto-delete tagging failed", ex); }
            Touched(acc.Key, list.Select(r => r.FolderId));
        }
    }

    /// <summary>Block: this sender's mail moves to Spam on the server and keeps doing so. Nothing is deleted.</summary>
    public void BlockSender(string address)
    {
        var a = address.Trim().ToLowerInvariant();
        var g = Config.Gatekeeper;
        g.Allowed.Remove(a);
        if (!g.Blocked.Contains(a)) g.Blocked.Add(a);
        Settings.Save(notify: false);
        foreach (var acc in Store.GateMessages(a).GroupBy(r => r.AccountId))
        {
            var junk = Store.GetFolders(acc.Key).FirstOrDefault(f => f.Role == FolderRole.Junk);
            if (junk == null) { Log.Warn("block: no Spam folder in " + acc.Key + " — mail stays at the door"); continue; }
            foreach (var m in acc)
            {
                Queue(m, PendingOpKind.Move, junk.Id);
                Store.DeleteRow(m.Id);
            }
            Touched(acc.Key, acc.Select(r => r.FolderId).Append(junk.Id));
        }
        GateChanged?.Invoke();
    }

    /// <summary>The Gatekeeper was switched off: everything waiting comes into the Inbox (senders aren't marked allowed).</summary>
    public void OpenGate()
    {
        var rows = Store.GateMessages();
        if (rows.Count == 0) return;
        Store.SetAtGate(rows.Select(r => r.Id), false, DateTimeOffset.Now);
        LetIn(rows);
        GateChanged?.Invoke();
    }

    /// <summary>The Inbox messages a rule would act on now (Preview matches / "Also apply to…"), newest first.</summary>
    public List<MessageRow> RuleMatchesInInbox(MailRule rule, int limit = 5000)
    {
        var found = new List<MessageRow>();
        if (rule.Conditions.Count == 0) return found;
        var needsBody = rule.Conditions.Any(c => c.Field == RuleField.Body);
        foreach (var acc in Accounts.Where(a => RuleEngine.AppliesToAccount(rule, a.Id)))
        {
            foreach (var m in Store.GetMessagesIn(FolderIds(FolderRole.Inbox, acc.Id), limit))
            {
                var body = needsBody && m.BodyCached ? Store.GetBody(m.Id)?.Text ?? "" : "";
                if (RuleEngine.Matches(rule, m, new RuleContext(acc.Email, body, !needsBody || m.BodyCached))) found.Add(m);
                if (found.Count >= limit) return found;
            }
        }
        return found;
    }

    /// <summary>"Also apply to the N matching messages already in Inbox": runs just this rule over them. Returns how many matched.</summary>
    public int ApplyRuleToInbox(MailRule rule)
    {
        var matches = RuleMatchesInInbox(rule);
        var once = rule.Clone();
        once.Enabled = true;   // asked for explicitly, so it runs now even if the rule is switched off for new mail
        foreach (var g in matches.GroupBy(m => m.AccountId))
            RunRules(g.Key, g.ToList(), new[] { once });
        return matches.Count;
    }

    // ───────────────────────── reading ─────────────────────────

    /// <summary>Body from the local store, downloading it first if needed.</summary>
    public async Task<(MessageBody? body, MimeMessage? mime)> LoadAsync(MessageRow row, bool needMime, CancellationToken ct)
    {
        var body = Store.GetBody(row.Id);
        if (body != null && !needMime) return (body, null);
        if (!_syncs.TryGetValue(row.AccountId, out var sync)) return (body, null);
        // Design RL1: an email saved before its pictures were kept gets them saved now, from the file on this PC.
        var mime = await sync.GetMimeAsync(row, ct, refreshBody: body != null && MimeText.NeedsDownload(body));
        return (Store.GetBody(row.Id) ?? body, mime);
    }

    /// <summary>Q39: gets an email ready to read — from this PC when its message file is here, else from the server
    /// (its text and pictures only when attachments come on opening). Returns the saved body.</summary>
    public async Task<MessageBody?> FetchBodyAsync(MessageRow row, CancellationToken ct) =>
        _syncs.TryGetValue(row.AccountId, out var sync) ? await sync.FetchBodyAsync(row, ct) : Store.GetBody(row.Id);

    /// <summary>The email from its message file on this PC, or null; never the server.</summary>
    public async Task<MimeMessage?> LocalMimeAsync(MessageRow row, CancellationToken ct) =>
        _syncs.TryGetValue(row.AccountId, out var sync) ? await sync.LocalMimeAsync(row, ct) : null;

    /// <summary>Design HM1: the whole email (attachments too) is on this PC as a message file.</summary>
    public bool HasMessageFile(MessageRow row)
    {
        try { return File.Exists(Paths.MimePath(row.AccountId, row.Id)); } catch { return false; }
    }

    /// <summary>Q38: emails the reader will probably open next; downloaded first.</summary>
    public void WantBodies(string accountId, IEnumerable<long> rowIds)
    {
        if (_syncs.TryGetValue(accountId, out var sync)) sync.WantBodies(rowIds);
    }

    /// <summary>Q40: emails of the account's download window on this PC / in the window.</summary>
    public (int OnPc, int Total) WindowProgress(string accountId)
    {
        var a = AccountById(accountId);
        return Store.WindowProgress(accountId, a is { SyncDays: > 0 } ? DateTimeOffset.Now.AddDays(-a.SyncDays) : null);
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
        var normalizedId = Threading.NormalizeId(messageId);
        try
        {
            var dropped = false;
            foreach (var l in Store.GetLocalDrafts())
                if (l.Id != except && Threading.NormalizeId(l.MessageId) == normalizedId && !IsLocalDraftOpen(l.Id) && (notNewerThan == null || l.Updated <= notNewerThan))
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

    /// <summary>"Send now" (design SN1): the message goes out at once instead of at the next 2 s pass. False when it is already on its way.</summary>
    public bool SendNow(long outboxId)
    {
        var item = Store.GetOutbox().FirstOrDefault(o => o.Id == outboxId);
        if (item == null || item.Status is not (OutboxStatus.Queued or OutboxStatus.Failed)) return false;
        Store.RescheduleOutbox(outboxId, DateTimeOffset.Now);
        OutboxChanged?.Invoke();
        _ = Task.Run(async () =>
        {
            try { await ProcessOutboxAsync(_cts?.Token ?? CancellationToken.None); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Warn("send now: " + ex.Message); }
        });
        return true;
    }

    public List<OutboxItem> Outbox() => Store.GetOutbox();
    /// <summary>The outbox without the messages themselves — cheap enough to read every second (status bar).</summary>
    public List<OutboxItem> OutboxSummary() => Store.GetOutbox(withMime: false);

    internal async Task ProcessOutboxAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.Now;
        foreach (var item in Store.GetOutbox().Where(o => o.SendAt <= now))
        {
            if (item.Status == OutboxStatus.Failed && item.Attempts >= 6) continue;
            if (item.Status == OutboxStatus.Sending) continue;
            if (!Store.TryClaimOutbox(item.Id)) continue;
            OutboxChanged?.Invoke();
            var account = AccountById(item.AccountId);
            var accepted = false;
            try
            {
                if (account == null) throw new InvalidOperationException("The account for this message was removed.");
                var msg = Composer.FromBytes(item.Mime);
                await _sendMessage(account, msg, ct);
                accepted = true;
                Store.SetOutboxResult(item.Id, OutboxStatus.Sent);
                Log.Info($"sent '{item.Subject}' from {account.Email}");
                // Local completion must survive cancellation of the subsequent IMAP cleanup.
                DropStaleLocalCopies(msg.MessageId, null);
                if (item.RemindAt is { } due)
                    Store.AddReminder(new Reminder { AccountId = item.AccountId, ThreadKey = item.ThreadKey, Subject = item.Subject, After = DateTimeOffset.Now, Due = due });
                if (!account.ServerSavesSent && _syncs.TryGetValue(account.Id, out var sync))
                {
                    try { await sync.AppendToSentAsync(msg, ct); }
                    catch (Exception ex) { Log.Warn("could not save a copy in Sent: " + ex.Message); }
                }
                if (_syncs.TryGetValue(account.Id, out var draftsSync))
                {
                    try { await draftsSync.DeleteDraftsByMessageIdAsync(msg.MessageId ?? "", ct); }
                    catch (Exception ex) { Log.Warn("could not remove the sent message's draft: " + ex.Message); }
                }
                Sent?.Invoke(item);
                SyncNow(item.AccountId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !accepted)
            {
                Store.SetOutboxResult(item.Id, OutboxStatus.Failed, "Interrupted", DateTimeOffset.Now);
                throw;
            }
            catch (Exception ex)
            {
                if (accepted)
                {
                    // SMTP accepted the message. A failed cleanup must never send it again.
                    Log.Error($"message sent; cleanup failed for '{item.Subject}'", ex);
                    try { Store.SetOutboxResult(item.Id, OutboxStatus.Sent); }
                    catch (Exception saveError) { Log.Error("could not record the accepted send", saveError); }
                }
                else
                {
                    var attempts = item.Attempts + 1;
                    var retry = DateTimeOffset.Now.AddMinutes(Math.Min(60, Math.Pow(2, attempts)));
                    var msgText = Connector.Friendly(ex);
                    Log.Error($"send failed ({attempts}) '{item.Subject}'", ex);
                    Store.SetOutboxResult(item.Id, OutboxStatus.Failed, msgText, retry);
                    SendFailed?.Invoke(item, msgText);
                }
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
                    var soon = Store.TakeDueEventReminders(DateTimeOffset.Now);   // design B2
                    if (soon.Count > 0) EventReminderDue?.Invoke(soon);
                    RunDueDeletes(DateTimeOffset.Now);   // also the overdue ones at start (this runs straight away)
                    if (DateTimeOffset.Now - _lastAutoEmpty > TimeSpan.FromHours(1))
                    {
                        _lastAutoEmpty = DateTimeOffset.Now;
                        try { AutoEmptyTrash(DateTimeOffset.Now); } catch (Exception ex) { Log.Warn("emptying Trash: " + ex.Message); }   // design TB1 (T7)
                    }
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
