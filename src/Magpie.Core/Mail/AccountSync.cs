using MailKit;
using MailKit.Net.Imap;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using MimeKit;
using KitFlags = MailKit.MessageFlags;
using KitSearch = MailKit.Search.SearchQuery;
using MFlags = Magpie.Core.Models.MessageFlags;
using MailFolder = Magpie.Core.Models.MailFolder;
using MailStore = Magpie.Core.Storage.MailStore;

namespace Magpie.Core.Mail;

public enum SyncState { Idle, Connecting, Syncing, Offline, NeedsSignIn, Error }

public sealed class SyncStatus
{
    public SyncState State { get; init; }
    public string Message { get; init; } = "";
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    /// <summary>While syncing (status bar, design S1): the folder being checked, and headers downloaded of those found new.</summary>
    public string Folder { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    /// <summary>When this account last finished a sync without error.</summary>
    public DateTimeOffset? LastSuccess { get; init; }
    /// <summary>Older emails still to be listed (headers only) across this account's folders; 0 when every folder is complete.</summary>
    public int Backlog { get; init; }
}

public sealed class ChangeSet
{
    public string AccountId { get; init; } = "";
    public HashSet<long> FolderIds { get; } = new();
    public List<MessageRow> NewInboxMessages { get; } = new();
    /// <summary>Every message that arrived in the Inbox since the last check, read or not (rules run on these, design B5).</summary>
    public List<MessageRow> NewInboxArrivals { get; } = new();
    public bool FoldersChanged { get; set; }
}

/// <summary>A connection guarded by a lock, reconnected on demand.</summary>
internal sealed class ImapLease : IDisposable
{
    private readonly Func<CancellationToken, Task<ImapClient>> _open;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ImapClient? _client;

    public ImapLease(Func<CancellationToken, Task<ImapClient>> open) { _open = open; }

    public async Task<T> UseAsync<T>(Func<ImapClient, Task<T>> work, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                if (_client is not { IsConnected: true, IsAuthenticated: true })
                {
                    try { _client?.Dispose(); } catch { }
                    _client = await _open(ct);
                }
                try { return await work(_client); }
                catch (Exception ex) when (attempt == 0 && IsConnectionError(ex) && !ct.IsCancellationRequested)
                {
                    Log.Info("IMAP connection dropped, reconnecting: " + ex.Message);
                    try { _client?.Dispose(); } catch { }
                    _client = null;
                }
            }
        }
        finally { _lock.Release(); }
    }

    public Task UseAsync(Func<ImapClient, Task> work, CancellationToken ct) =>
        UseAsync<bool>(async c => { await work(c); return true; }, ct);

    public static bool IsConnectionError(Exception ex) =>
        ex is ServiceNotConnectedException or ServiceNotAuthenticatedException or IOException or ImapProtocolException
            or System.Net.Sockets.SocketException;

    public void Dispose()
    {
        try { _client?.Disconnect(false); } catch { }
        try { _client?.Dispose(); } catch { }
        _client = null;
    }
}

/// <summary>
/// Mirrors one account into the local store: folder list, new mail, flag changes and deletions,
/// plus IDLE push on the Inbox and a queue of local changes (read, pin, move, delete) replayed to the server.
/// Three connections: background sync, interactive (open message / actions), and IDLE.
/// </summary>
public sealed class AccountSync : IDisposable
{
    public const int InitialInboxLimit = 3000;
    public const int InitialFolderLimit = 800;
    /// <summary>
    /// Older emails listed per sync round (headers only — bodies download when a message is opened).
    /// The rest follow in the next rounds, which run back to back until every folder is complete,
    /// so new mail is still checked every few seconds while a big mailbox fills in.
    /// </summary>
    public static int BackfillPerRound { get; set; } = 1000;

    private static readonly string[] ExtraHeaders =
    {
        "List-Unsubscribe", "List-Id", "Precedence", "Auto-Submitted", "X-Mailer", "Feedback-ID", "X-Campaign",
        "X-CampaignID", "X-Mailgun-Tag", "X-MC-User", "X-SG-EID",
    };

    public Account Account { get; private set; }
    private readonly MailStore _store;
    private readonly Connector _connector;
    private readonly Func<string, bool> _isKnownContact;
    private readonly Func<string, Category?> _senderOverride;
    private readonly ImapLease _sync;
    private readonly ImapLease _ui;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private Task? _loop, _idleLoop;
    private bool _initialDone;
    /// <summary>Folder id → older emails still to list. Missing = not checked yet in this session.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, int> _remaining = new();
    /// <summary>UIDs already tried this session, so one message the server won't return can't loop forever.</summary>
    private readonly HashSet<(long folder, uint uid)> _tried = new();
    private long _priorityFolder;
    private int _budget;
    /// <summary>Per folder: server UIDs still to list, newest first (filled by a full check, drained by backfill rounds).</summary>
    private readonly Dictionary<long, List<UniqueId>> _queue = new();
    private DateTimeOffset _lastFullCheck = DateTimeOffset.MinValue;
    private int _pokes;
    private DateTimeOffset _lastChanged = DateTimeOffset.MinValue;

    public SyncStatus Status { get; private set; } = new() { State = SyncState.Idle };
    public event Action<AccountSync, SyncStatus>? StatusChanged;
    public event Action<ChangeSet>? Changed;
    public event Action<IEnumerable<string>>? ContactsLearned;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);

    public AccountSync(Account account, MailStore store, Connector connector, Func<string, bool> isKnownContact, Func<string, Category?> senderOverride)
    {
        Account = account;
        _store = store;
        _connector = connector;
        _isKnownContact = isKnownContact;
        _senderOverride = senderOverride;
        _sync = new ImapLease(ct => _connector.OpenImapAsync(Account, ct));
        _ui = new ImapLease(ct => _connector.OpenImapAsync(Account, ct));
    }

    public void UpdateAccount(Account a) => Account = a;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _idleLoop = Task.Run(() => IdleAsync(_cts.Token));
    }

    /// <summary>Ask for a sync now (e.g. after an action or when the user presses F5).</summary>
    public void Poke() { Interlocked.Exchange(ref _pokes, 1); _wake.Release(); }

    /// <summary>Older emails still to list in a folder: -1 = not checked yet this session, 0 = complete.</summary>
    public int Remaining(long folderId) => _remaining.TryGetValue(folderId, out var n) ? n : -1;

    public int Backlog => _remaining.Values.Where(v => v > 0).Sum();

    /// <summary>The folder the user is looking at: its older emails are listed first.</summary>
    public void Prioritise(long folderId)
    {
        Interlocked.Exchange(ref _priorityFolder, folderId);
        if (Remaining(folderId) != 0) _wake.Release(); // a backfill round, not a full check
    }

    private DateTimeOffset? _lastSuccess;

    private void SetStatus(SyncState state, string msg = "")
    {
        if (state == SyncState.Idle) _lastSuccess = DateTimeOffset.Now;
        Status = new SyncStatus { State = state, Message = msg, LastSuccess = _lastSuccess, Backlog = Backlog };
        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>Progress within the current sync (keeps its state and message).</summary>
    private void SetProgress(string folder, int done, int total)
    {
        var cur = Status;
        if (cur.State is not (SyncState.Syncing or SyncState.Connecting) && !(cur.State == SyncState.Idle && Backlog > 0)) return;
        Status = new SyncStatus { State = cur.State, Message = cur.Message, Folder = folder, Done = done, Total = total, LastSuccess = _lastSuccess, Backlog = Backlog };
        StatusChanged?.Invoke(this, Status);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(15);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // A full check (new mail, flags, deletions in every folder) at start, on each poke and every poll interval.
                // In between, while older emails are still being listed, short "backfill" rounds run back to back
                // touching only the folders that still have some; the account stays "Idle" (drafts upload, ✓ time is right).
                var poked = Interlocked.Exchange(ref _pokes, 0) == 1;
                var full = !_initialDone || poked || DateTimeOffset.Now - _lastFullCheck >= PollInterval;
                if (full)
                {
                    SetStatus(_initialDone ? SyncState.Syncing : SyncState.Connecting, _initialDone ? "Checking for mail…" : "Connecting…");
                    await FlushPendingOpsAsync(ct);
                }
                await SyncAllAsync(full, ct);
                if (full) _lastFullCheck = DateTimeOffset.Now;
                _initialDone = true;
                backoff = TimeSpan.FromSeconds(15);
                var pending = _store.PendingOpCount(Account.Id);
                var backlog = Backlog;
                var windowLeft = _windowPending ? _windowLeft : 0;
                if (full || backlog == 0)
                    SetStatus(SyncState.Idle, pending > 0 ? $"{pending} change(s) waiting to reach the server"
                        : backlog > 0 ? "Getting older emails…"
                        : windowLeft > 0 ? $"Downloading emails to this PC… {windowLeft:N0} left"
                        : "Up to date");
                // Q40: older emails are listed and the download window filled back to back, not one batch per check.
                if (backlog > 0 || _windowPending)
                {
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(250), ct);
                    continue;
                }
                await _wake.WaitAsync(PollInterval, ct);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(0, ct); // coalesce pokes
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ReauthRequiredException ex)
            {
                Log.Warn($"[{Account.Email}] needs sign-in: {ex.Message}");
                SetStatus(SyncState.NeedsSignIn, ex.Message);
                try { await _wake.WaitAsync(TimeSpan.FromMinutes(30), ct); } catch (OperationCanceledException) { break; }
            }
            catch (Exception ex)
            {
                var offline = ImapLease.IsConnectionError(ex) || ex is TimeoutException || ex.InnerException is System.Net.Sockets.SocketException;
                Log.Error($"[{Account.Email}] sync failed", ex);
                SetStatus(offline ? SyncState.Offline : SyncState.Error, Connector.Friendly(ex));
                try { await _wake.WaitAsync(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 300));
            }
        }
    }

    // ───────────────────────── folders ─────────────────────────

    internal static FolderRole RoleOf(IMailFolder f, bool isInbox)
    {
        if (isInbox) return FolderRole.Inbox;
        var a = f.Attributes;
        if (a.HasFlag(FolderAttributes.Sent)) return FolderRole.Sent;
        if (a.HasFlag(FolderAttributes.Drafts)) return FolderRole.Drafts;
        if (a.HasFlag(FolderAttributes.Trash)) return FolderRole.Trash;
        if (a.HasFlag(FolderAttributes.Junk)) return FolderRole.Junk;
        if (a.HasFlag(FolderAttributes.Archive)) return FolderRole.Archive;
        if (a.HasFlag(FolderAttributes.All)) return FolderRole.All;
        if (a.HasFlag(FolderAttributes.Flagged)) return FolderRole.Flagged;
        if (a.HasFlag(FolderAttributes.Important)) return FolderRole.Important;
        return RoleByName(f.Name);
    }

    internal static FolderRole RoleByName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "sent" or "sent items" or "sent mail" or "sent messages" => FolderRole.Sent,
        "drafts" or "draft" => FolderRole.Drafts,
        "trash" or "deleted items" or "deleted messages" or "bin" => FolderRole.Trash,
        "junk" or "junk e-mail" or "junk email" or "spam" or "bulk mail" => FolderRole.Junk,
        "archive" or "archives" => FolderRole.Archive,
        _ => FolderRole.Other,
    };

    /// <summary>
    /// Every folder is mirrored (headers only), including Gmail's All Mail / Starred / Important: archived Gmail mail
    /// lives only in All Mail. The copies of one email in several folders are shown once (see MailStore.ListThreads).
    /// </summary>
    internal static bool ShouldMirror(FolderRole role, bool gmail) => true;

    private async Task<List<(MailFolder local, string path)>> SyncFolderListAsync(ImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var remote = await client.GetFoldersAsync(ns, StatusItems.None, false, ct);
        var inbox = client.Inbox;
        var gmail = client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(MailFolder, string)>();

        var all = remote.ToList();
        if (!all.Any(f => f.FullName.Equals(inbox.FullName, StringComparison.OrdinalIgnoreCase))) all.Insert(0, inbox);
        foreach (var f in all)
        {
            if (f.Attributes.HasFlag(FolderAttributes.NoSelect) || f.Attributes.HasFlag(FolderAttributes.NonExistent)) continue;
            var isInbox = f.FullName.Equals(inbox.FullName, StringComparison.OrdinalIgnoreCase);
            var role = RoleOf(f, isInbox);
            var local = new MailFolder
            {
                AccountId = Account.Id,
                Path = f.FullName,
                Name = isInbox ? "Inbox" : f.Name,
                Role = role,
                Delimiter = f.DirectorySeparator,
                Synced = ShouldMirror(role, gmail),
            };
            _store.UpsertFolder(local);
            seen.Add(f.FullName);
            result.Add((local, f.FullName));
        }
        bool changed = false;
        foreach (var old in _store.GetFolders(Account.Id).Where(f => !seen.Contains(f.Path)))
        {
            _store.DeleteFolder(old.Id);
            changed = true;
        }
        if (changed) Changed?.Invoke(new ChangeSet { AccountId = Account.Id, FoldersChanged = true });
        return result;
    }

    private async Task SyncAllAsync(bool full, CancellationToken ct)
    {
        await _sync.UseAsync(async client =>
        {
            var folders = full ? await SyncFolderListAsync(client, ct)
                : _store.GetFolders(Account.Id).Select(f => (local: f, path: f.Path)).ToList();
            var before = _store.GetFolders(Account.Id).ToDictionary(f => f.Id);
            // The folder being looked at first, then Inbox, Sent (for conversations), then the rest.
            // Older emails are listed in the same order, up to the round's budget.
            var priority = Interlocked.Read(ref _priorityFolder);
            var order = folders.Where(f => f.local.Synced && (full || Remaining(f.local.Id) != 0))
                .OrderBy(f => f.local.Id == priority ? -1 : f.local.Role switch { FolderRole.Inbox => 0, FolderRole.Sent => 1, FolderRole.Drafts => 2, FolderRole.Other => 3, _ => 4 })
                .ToList();
            // The first round only lists recent mail in every folder, so everything shows up quickly; older emails follow.
            _budget = _initialDone ? BackfillPerRound : 0;
            if (full)
                foreach (var gone in _remaining.Keys.Where(id => !folders.Any(o => o.local.Id == id)).ToList()) { _remaining.TryRemove(gone, out _); lock (_queue) _queue.Remove(gone); }
            var initial = !_initialDone && before.Values.All(f => f.LastSync == 0);
            var changes = new ChangeSet { AccountId = Account.Id, FoldersChanged = !_initialDone };
            foreach (var (local, path) in order)
            {
                ct.ThrowIfCancellationRequested();
                var stored = _store.GetFolder(local.Id) ?? local;
                SetProgress(stored.Name, 0, 0);
                try
                {
                    var f = await client.GetFolderAsync(path, ct);
                    var leftBefore = Remaining(stored.Id);
                    var (added, touched) = await SyncFolderAsync(client, f, stored, full, ct);
                    var leftAfter = Remaining(stored.Id);
                    // Also when the folder's "still loading" state changes, so an empty list can stop saying so.
                    if (touched || stored.LastSync == 0 || (leftBefore != leftAfter && (leftBefore <= 0 || leftAfter == 0))) changes.FolderIds.Add(stored.Id);
                    if (stored.Role == FolderRole.Inbox && stored.LastSync != 0 && !initial)
                    {
                        changes.NewInboxMessages.AddRange(added.Where(m => !m.IsSeen));
                        changes.NewInboxArrivals.AddRange(added);
                    }
                }
                catch (FolderNotFoundException) { _store.DeleteFolder(stored.Id); changes.FoldersChanged = true; }
                catch (Exception ex) when (!ImapLease.IsConnectionError(ex) && ex is not OperationCanceledException)
                {
                    Log.Error($"[{Account.Email}] folder {path} failed", ex);
                    // Don't spin on a folder that keeps failing: its older emails are retried at the next full check.
                    _remaining[stored.Id] = 0;
                    lock (_queue) _queue.Remove(stored.Id);
                }
                if (changes.FolderIds.Count > 0 && (stored.Role == FolderRole.Inbox || stored.Id == priority))
                {
                    // Show the inbox (first run) or the folder being looked at as soon as it is ready.
                    Changed?.Invoke(changes);
                    _lastChanged = DateTimeOffset.Now;
                    changes = new ChangeSet { AccountId = Account.Id };
                }
            }
            // Backfill-only rounds are frequent: updates for folders nobody is looking at are batched (about every 2 s).
            foreach (var id in changes.FolderIds) _deferred.Add(id);
            if (_deferred.Count > 0 || changes.FoldersChanged)
            {
                if (full || Backlog == 0 || DateTimeOffset.Now - _lastChanged >= TimeSpan.FromSeconds(2))
                {
                    var cs = new ChangeSet { AccountId = Account.Id, FoldersChanged = changes.FoldersChanged };
                    foreach (var id in _deferred) cs.FolderIds.Add(id);
                    cs.NewInboxMessages.AddRange(changes.NewInboxMessages);
                    cs.NewInboxArrivals.AddRange(changes.NewInboxArrivals);
                    _deferred.Clear();
                    Changed?.Invoke(cs);
                    _lastChanged = DateTimeOffset.Now;
                }
            }

            // Design DS1: download the emails of the account's window (newest first, inbox first) a batch per full check,
            // so they open instantly, work offline and are searchable. Older emails download when opened.
            // Q40: every round, not only on full checks, until the window is complete.
            await PrefetchWindowAsync(client, PrefetchPerRound, ct);
        }, ct);
    }

    /// <summary>Returns rows newly added in this folder, and whether anything in it changed.</summary>
    private async Task<(List<MessageRow> added, bool changed)> SyncFolderAsync(ImapClient client, IMailFolder f, MailFolder local, bool full, CancellationToken ct)
    {
        // Empty Trash / Spam waiting to reach the server (design TB1): don't list its emails again meanwhile.
        if (_store.GetPendingOps(Account.Id).Any(o => o.Kind == PendingOpKind.EmptyFolder && o.FolderId == local.Id)) return (new List<MessageRow>(), false);
        await f.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            if (local.UidValidity != 0 && local.UidValidity != f.UidValidity)
            {
                Log.Info($"[{Account.Email}] {local.Path}: UIDVALIDITY changed, resyncing");
                _store.WipeFolderMessages(local.Id);
                local.HighestModSeq = 0;
                local.UidNext = 0;
                lock (_tried) _tried.RemoveWhere(t => t.folder == local.Id);
                lock (_queue) _queue.Remove(local.Id);
                full = true;
            }
            var map = _store.GetUidMap(local.Id);
            var pendingRemovals = _store.PendingRemovals(Account.Id);
            var added = new List<MessageRow>();
            bool changed = false;
            if (!full)
            {
                // Backfill-only round: just list some more older emails from the queue built by the last full check.
                var backfilledOnly = await BackfillAsync(client, f, local, map, pendingRemovals, null, ct);
                return (added, backfilledOnly > 0);
            }
            // Everything at or above this UID is "new mail" (step 1); everything below is old mail listed by step 4.
            var uidCeiling = f.UidNext?.Id ?? uint.MaxValue;

            // 1. New messages
            // "New" = arrived since the last check (UID at or above the UIDNEXT we saw then). Anything older that
            // isn't listed yet is filled in by step 4 and never counts as new mail.
            IList<UniqueId> newUids;
            if (map.Count == 0 && local.UidNext == 0)
            {
                if (f.Count == 0) newUids = Array.Empty<UniqueId>();
                else
                {
                    var since = Account.SyncDays > 0 ? DateTime.Now.AddDays(-Math.Max(7, Account.SyncDays)) : new DateTime(1970, 1, 2);
                    var found = await f.SearchAsync(KitSearch.DeliveredAfter(since), ct);
                    var limit = local.Role == FolderRole.Inbox ? InitialInboxLimit : InitialFolderLimit;
                    newUids = found.OrderByDescending(u => u.Id).Take(limit).ToList();
                    if (newUids.Count == 0 && local.Role is FolderRole.Inbox or FolderRole.Sent)
                    {
                        // Quiet mailbox: still show the latest few.
                        var allUids = await f.SearchAsync(KitSearch.All, ct);
                        newUids = allUids.OrderByDescending(u => u.Id).Take(50).ToList();
                    }
                }
            }
            else
            {
                var floor = Math.Max(map.Count > 0 ? map.Keys.Max() + 1 : 1, local.UidNext);
                uidCeiling = (uint)Math.Clamp(floor, 1, uint.MaxValue);
                if (f.UidNext is { } next && next.Id <= floor) newUids = Array.Empty<UniqueId>();
                else
                {
                    var range = new UniqueIdRange(new UniqueId(f.UidValidity, (uint)Math.Clamp(floor, 1, uint.MaxValue)), UniqueId.MaxValue);
                    newUids = (await f.SearchAsync(KitSearch.Uids(range), ct)).Where(u => u.Id >= floor).ToList();
                }
            }
            newUids = newUids.Where(u => !pendingRemovals.Contains((local.Id, u.Id))).ToList();
            var fetchedSoFar = 0;
            foreach (var batch in newUids.OrderByDescending(u => u.Id).Chunk(150))
            {
                ct.ThrowIfCancellationRequested();
                var rows = await FetchRowsAsync(client, f, local, batch, ct);
                added.AddRange(_store.InsertMessages(rows));
                fetchedSoFar += batch.Length;
                SetProgress(local.Name, fetchedSoFar, newUids.Count);
            }
            if (added.Count > 0)
            {
                if (local.Role == FolderRole.Sent)
                {
                    var people = added.SelectMany(m => Composer.ParseAddresses(m.To + "," + m.Cc).Mailboxes).Select(mb => (mb.Address, mb.Name ?? "")).ToList();
                    _store.TouchContacts(people, DateTimeOffset.Now, sentTo: true);
                    ContactsLearned?.Invoke(people.Select(p => p.Address));
                }
                else
                    _store.TouchContacts(added.Where(m => m.Category == Category.People).Select(m => (m.FromAddress, m.FromName)), DateTimeOffset.Now);
            }

            // The whole list of UIDs on the server (for deletions and for older emails not listed yet).
            var serverAll = (await f.SearchAsync(KitSearch.All, ct)).ToList();

            // 2. Flag changes and 3. deletions on messages we already had
            if (map.Count > 0)
            {
                var changes = new List<(long uid, MFlags flags)>();
                bool condstore = f.HighestModSeq > 0 && local.HighestModSeq > 0 && client.Capabilities.HasFlag(ImapCapabilities.CondStore);
                if (condstore)
                {
                    if ((long)f.HighestModSeq != local.HighestModSeq)
                    {
                        var req = new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.Flags) { ChangedSince = (ulong)local.HighestModSeq };
                        foreach (var s in await f.FetchAsync(0, -1, req, ct))
                            if (map.TryGetValue(s.UniqueId.Id, out var cur) && s.Flags is { } fl)
                            {
                                var nf = Merge(cur.flags, fl);
                                if (nf != cur.flags) changes.Add((s.UniqueId.Id, nf));
                            }
                    }
                }
                else
                {
                    var known = map.Keys.Select(u => new UniqueId(f.UidValidity, (uint)u)).ToList();
                    foreach (var batch in known.Chunk(1000))
                        foreach (var s in await f.FetchAsync(batch, MessageSummaryItems.UniqueId | MessageSummaryItems.Flags, ct))
                            if (map.TryGetValue(s.UniqueId.Id, out var cur) && s.Flags is { } fl)
                            {
                                var nf = Merge(cur.flags, fl);
                                if (nf != cur.flags) changes.Add((s.UniqueId.Id, nf));
                            }
                }
                // Don't overwrite local changes that haven't reached the server yet.
                var pendingFlags = _store.GetPendingOps(Account.Id).Where(o => o.FolderId == local.Id).Select(o => o.Uid).ToHashSet();
                changes.RemoveAll(c => pendingFlags.Contains(c.uid));
                if (changes.Count > 0) _store.UpdateFlags(local.Id, changes);

                var serverUids = serverAll.Select(u => (long)u.Id).ToHashSet();
                var gone = map.Keys.Where(u => !serverUids.Contains(u)).ToList();
                if (gone.Count > 0) _store.DeleteUids(local.Id, gone);
                changed = changes.Count > 0 || gone.Count > 0;
            }

            // 4. Older emails: everything on the server (below the new-mail line) not listed here yet — the first sync
            //    only lists recent mail so the Inbox appears quickly. Headers only, newest first, within this round's budget.
            var have = map.Keys.ToHashSet();
            foreach (var u in newUids) have.Add(u.Id);
            List<UniqueId> missing;
            lock (_tried)
                missing = serverAll.Where(u => u.Id < uidCeiling && !have.Contains(u.Id) && !pendingRemovals.Contains((local.Id, u.Id)) && !_tried.Contains((local.Id, u.Id)))
                    .OrderByDescending(u => u.Id).ToList();
            var backfilled = await BackfillAsync(client, f, local, map, pendingRemovals, missing, ct);

            _store.UpdateFolderState(local.Id, f.UidValidity, f.UidNext?.Id ?? 0, (long)f.HighestModSeq);
            _store.RefreshFolderCounts(local.Id);
            return (added, changed || added.Count > 0 || backfilled > 0);
        }
        finally
        {
            try { await f.CloseAsync(false, ct); } catch { }
        }
    }

    /// <summary>
    /// Lists older emails from the folder's queue (headers only), newest first, within the round's budget.
    /// <paramref name="missing"/> replaces the queue when given (full check); otherwise the queue is drained.
    /// A batch the server refuses is skipped (its UIDs are not asked for again this session) rather than stalling the folder.
    /// </summary>
    private async Task<int> BackfillAsync(ImapClient client, IMailFolder f, MailFolder local, Dictionary<long, (long id, MFlags flags)> map,
        HashSet<(long, long)> pendingRemovals, List<UniqueId>? missing, CancellationToken ct)
    {
        List<UniqueId> queue;
        lock (_queue)
        {
            if (missing != null) _queue[local.Id] = missing;
            if (!_queue.TryGetValue(local.Id, out queue!)) { _remaining[local.Id] = 0; return 0; }
        }
        var take = queue.Take(Math.Max(0, _budget)).ToList();
        var backfilled = 0;
        var done = 0;
        foreach (var batch in take.Chunk(150))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var rows = await FetchRowsAsync(client, f, local, batch, ct);
                var inserted = _store.InsertMessages(rows);
                // A UID the server didn't return (expunged meanwhile) is not asked for again this session.
                var returned = rows.Select(r => r.Uid).ToHashSet();
                lock (_tried) foreach (var u in batch) if (!returned.Contains(u.Id)) _tried.Add((local.Id, u.Id));
                backfilled += inserted.Count;
                if (local.Role == FolderRole.Sent && inserted.Count > 0)
                {
                    var people = inserted.SelectMany(m => Composer.ParseAddresses(m.To + "," + m.Cc).Mailboxes).Select(mb => (mb.Address, mb.Name ?? "")).ToList();
                    _store.TouchContacts(people, inserted.Max(m => m.Date), sentTo: true);
                    ContactsLearned?.Invoke(people.Select(p => p.Address));
                }
            }
            catch (Exception ex) when (!ImapLease.IsConnectionError(ex) && ex is not OperationCanceledException)
            {
                Log.Warn($"[{Account.Email}] {local.Path}: {batch.Length} older emails skipped: {ex.Message}");
                lock (_tried) foreach (var u in batch) _tried.Add((local.Id, u.Id));
            }
            done += batch.Length;
            _budget -= batch.Length;
            lock (_queue) queue.RemoveRange(0, Math.Min(batch.Length, queue.Count));
            _remaining[local.Id] = queue.Count;
            SetProgress(local.Name, done, take.Count);
        }
        _remaining[local.Id] = queue.Count;
        if (queue.Count == 0) lock (_queue) _queue.Remove(local.Id);
        return backfilled;
    }

    internal static MFlags Merge(MFlags current, KitFlags server)
    {
        var f = current & ~(MFlags.Seen | MFlags.Flagged | MFlags.Answered | MFlags.Draft | MFlags.Deleted);
        if (server.HasFlag(KitFlags.Seen)) f |= MFlags.Seen;
        if (server.HasFlag(KitFlags.Flagged)) f |= MFlags.Flagged;
        if (server.HasFlag(KitFlags.Answered)) f |= MFlags.Answered;
        if (server.HasFlag(KitFlags.Draft)) f |= MFlags.Draft;
        if (server.HasFlag(KitFlags.Deleted)) f |= MFlags.Deleted;
        return f;
    }

    private async Task<List<MessageRow>> FetchRowsAsync(ImapClient client, IMailFolder f, MailFolder local, IList<UniqueId> uids, CancellationToken ct)
    {
        var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
                    | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure | MessageSummaryItems.References | MessageSummaryItems.PreviewText;
        var gmail = client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
        if (gmail) items |= MessageSummaryItems.GMailThreadId;
        var req = new FetchRequest(items) { Headers = new HeaderSet(ExtraHeaders) };
        var summaries = await f.FetchAsync(uids, req, ct);
        var rows = new List<MessageRow>();
        // Oldest first so replies can find their parents' thread keys within the same batch.
        _pendingBatchMerges.Clear();
        foreach (var s in summaries.OrderBy(s => s.UniqueId.Id))
            rows.Add(ToRow(s, local, gmail));
        // A later row in this batch may have merged conversations that earlier rows were keyed under.
        for (int pass = 0; pass < 3 && _pendingBatchMerges.Count > 0; pass++)
        {
            bool changed = false;
            foreach (var (keep, merged) in _pendingBatchMerges)
                foreach (var r in rows.Where(r => merged.Contains(r.ThreadKey))) { r.ThreadKey = keep; changed = true; }
            if (!changed) break;
        }
        return rows;
    }

    private readonly HashSet<long> _deferred = new();
    private readonly Dictionary<string, string> _batchKeys = new();
    /// <summary>Merges decided while building a batch; rows of the same batch still carrying an old key are fixed before insert.</summary>
    private readonly List<(string keep, List<string> merged)> _pendingBatchMerges = new();

    private MessageRow ToRow(IMessageSummary s, MailFolder local, bool gmail)
    {
        var env = s.Envelope ?? new Envelope();
        var from = env.From.Mailboxes.FirstOrDefault() ?? env.Sender.Mailboxes.FirstOrDefault();
        var fromAddr = from?.Address ?? "";
        var mid = Threading.NormalizeId(env.MessageId);
        var irt = Threading.NormalizeId(env.InReplyTo);
        var refs = s.References?.Select(Threading.NormalizeId).Where(r => r.Length > 0).ToList() ?? new List<string>();
        IReadOnlyList<string> Related(string id, IReadOnlyList<string> chain)
        {
            var keys = new List<string>();
            foreach (var x in chain.Append(id)) if (x.Length > 0 && _batchKeys.TryGetValue(x, out var k)) keys.Add(k);
            keys.AddRange(_store.RelatedThreadKeys(Account.Id, id, chain));
            return keys.Distinct().ToList();
        }
        var (key, merge) = Threading.Resolve(mid, irt, refs, gmail ? s.GMailThreadId : null, Related, $"{local.Id}:{s.UniqueId.Id}");
        if (merge.Count > 0)
        {
            _store.MergeThreads(Account.Id, key, merge);
            foreach (var k in _batchKeys.Where(kv => merge.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _batchKeys[k] = key;
            _pendingBatchMerges.Add((key, merge));
        }
        if (mid.Length > 0) { _batchKeys[mid] = key; if (_batchKeys.Count > 20000) _batchKeys.Clear(); }

        string H(string name) => s.Headers?[name] ?? "";
        var signals = new CategorySignals(
            fromAddr, from?.Name ?? "", H("List-Unsubscribe"), H("List-Id"), H("Precedence"), H("Auto-Submitted"), H("X-Mailer"),
            HasFeedbackId: H("Feedback-ID").Length > 0 || H("X-SG-EID").Length > 0,
            HasCampaignHeaders: H("X-Campaign").Length > 0 || H("X-CampaignID").Length > 0 || H("X-MC-User").Length > 0,
            FromKnownContact: _isKnownContact(fromAddr));
        var isOwn = fromAddr.Equals(Account.Email, StringComparison.OrdinalIgnoreCase);
        var category = local.Role is FolderRole.Sent or FolderRole.Drafts || isOwn
            ? Category.People
            : _senderOverride(fromAddr) ?? Categorizer.Classify(signals);

        var date = env.Date ?? s.InternalDate ?? DateTimeOffset.Now;
        if (s.InternalDate is { } internalDate && (date > DateTimeOffset.Now.AddDays(2) || date.Year < 1990)) date = internalDate;

        return new MessageRow
        {
            AccountId = Account.Id,
            FolderId = local.Id,
            Uid = s.UniqueId.Id,
            MessageId = mid,
            InReplyTo = irt,
            References = string.Join(" ", refs.Select(r => "<" + r + ">")),
            ThreadKey = key,
            FromName = from?.Name ?? "",
            FromAddress = fromAddr,
            To = string.Join(", ", env.To.Mailboxes.Select(m => m.ToString())),
            Cc = string.Join(", ", env.Cc.Mailboxes.Select(m => m.ToString())),
            ReplyTo = string.Join(", ", env.ReplyTo.Mailboxes.Select(m => m.ToString())),
            Subject = env.Subject ?? "",
            Preview = MimeText.Preview(s.PreviewText ?? ""),
            Date = date,
            SortDate = date,
            Flags = s.Flags is { } fl ? Merge(MFlags.None, fl) : MFlags.None,
            HasAttachments = s.Attachments?.Any() == true,
            Size = s.Size ?? 0,
            Category = category,
            ListUnsubscribe = H("List-Unsubscribe"),
        };
    }

    // ───────────────────────── bodies ─────────────────────────

    /// <summary>Emails downloaded per full check (design DS1); the rest of the window follows at the next checks.</summary>
    /// <summary>Emails downloaded ahead per round (Q40: rounds run back to back until the window is complete).</summary>
    public static int PrefetchPerRound { get; set; } = 200;
    /// <summary>Emails up to this size are downloaded whole, many per request (Q40); bigger ones in a text-only account
    /// get just their text and pictures.</summary>
    public const long WholeMessageMax = 512 * 1024;
    private const int StreamBatch = 25;
    private volatile bool _windowPending = true;
    private int _windowLeft;
    private readonly HashSet<long> _noBody = new();          // gone from the server or unreadable: not tried again this session
    private readonly System.Collections.Concurrent.ConcurrentQueue<long> _wanted = new();

    /// <summary>Q38: emails the reader will probably open next; downloaded before the rest of the window.</summary>
    public void WantBodies(IEnumerable<long> rowIds)
    {
        var any = false;
        foreach (var id in rowIds) { _wanted.Enqueue(id); any = true; }
        if (any) _wake.Release();   // a quick round, not a full check
    }

    /// <summary>Q40: the line under "Download emails" on the account card.</summary>
    public static string DescribeWindow(int onPc, int total, int syncDays)
    {
        var span = syncDays > 0 ? $" from the last {syncDays} days" : "";
        if (total == 0) return syncDays > 0 ? $"No emails{span} listed yet." : "No emails listed yet.";
        if (onPc >= total) return total == 1 ? $"The 1 email{span} is on this PC." : $"All {total:N0} emails{span} are on this PC.";
        return $"On this PC: {onPc:N0} of {total:N0} emails{span}. The rest are downloading.";
    }

    /// <summary>Q40: emails of the window on this PC / in the window.</summary>
    public (int OnPc, int Total) WindowProgress() => _store.WindowProgress(Account.Id, WindowStart);

    /// <summary>Start of the account's download window (design DS1), or null for everything.</summary>
    public DateTimeOffset? WindowStart => Account.SyncDays > 0 ? DateTimeOffset.Now.AddDays(-Account.SyncDays) : null;

    private async Task PrefetchWindowAsync(ImapClient client, int budget, CancellationToken ct)
    {
        _store.ShareBodiesWithCopies(Account.Id);   // Q39: copies in other folders need no download
        var folders = _store.GetFolders(Account.Id).ToDictionary(f => f.Id);

        // 1. What the reader asked for (the conversations next to the open one), wherever they are.
        var wanted = new List<MessageRow>();
        while (_wanted.TryDequeue(out var id))
            if (_store.GetMessage(id) is { BodyCached: false } r && !_noBody.Contains(r.Id) && wanted.All(w => w.Id != r.Id)) wanted.Add(r);
        foreach (var group in wanted.GroupBy(r => r.FolderId))
        {
            if (!folders.TryGetValue(group.Key, out var folder)) continue;
            try { await FetchBodiesAsync(client, folder, group.ToList(), ct); }
            catch (FolderNotFoundException) { }
        }

        // 2. The account's download window: Inbox first, then Sent, then the rest (newest first in each).
        var order = folders.Values
            .Where(f => f.Role is not (FolderRole.Trash or FolderRole.Junk or FolderRole.Drafts))
            .OrderBy(f => f.Role == FolderRole.Inbox ? 0 : f.Role == FolderRole.Sent ? 1 : 2)
            .ToList();
        var left = budget;
        foreach (var folder in order)
        {
            if (left <= 0) break;
            ct.ThrowIfCancellationRequested();
            var rows = _store.RowsWithoutBody(folder.Id, left + _noBody.Count, WindowStart)
                .Where(id => !_noBody.Contains(id)).Take(left)
                .Select(id => _store.GetMessage(id)).OfType<MessageRow>().ToList();
            if (rows.Count == 0) continue;
            left -= rows.Count;
            try { await FetchBodiesAsync(client, folder, rows, ct); }
            catch (FolderNotFoundException) { }
        }
        _windowPending = left <= 0;
        var (onPc, total) = WindowProgress();
        _windowLeft = Math.Max(0, total - onPc);
    }

    /// <summary>
    /// Downloads emails of one folder so they open from this PC. Small ones come whole, 25 per request (one round
    /// trip instead of one per email — Q40); bigger ones in a text-only account get their text and pictures only.
    /// </summary>
    private async Task FetchBodiesAsync(ImapClient client, MailFolder folder, List<MessageRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var f = await client.GetFolderAsync(folder.Path, ct);
        await f.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var whole = rows.Where(r => Account.DownloadAttachments ? r.Size < 5_000_000 : r.Size <= WholeMessageMax).ToList();
            foreach (var chunk in whole.Chunk(StreamBatch))
            {
                ct.ThrowIfCancellationRequested();
                var byUid = chunk.GroupBy(r => (uint)r.Uid).ToDictionary(g => g.Key, g => g.First());
                var done = new HashSet<long>();
                if (f is ImapFolder imap)
                {
                    await imap.GetStreamsAsync(chunk.Select(r => new UniqueId(f.UidValidity, (uint)r.Uid)).Distinct().ToList(), async (_, _, uid, stream, token) =>
                    {
                        if (!byUid.TryGetValue(uid.Id, out var row)) return;
                        try
                        {
                            var msg = await MimeMessage.LoadAsync(stream, token);
                            SaveBody(row, msg, writeFile: Account.DownloadAttachments);
                            done.Add(row.Id);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"[{Account.Email}] email {row.Id} unreadable: {ex.Message}"); }
                    }, ct);
                }
                foreach (var row in chunk.Where(r => !done.Contains(r.Id)))
                {
                    // Not in the batch answer (gone from the server, or not an IMAP folder): one by one, then give up.
                    try { SaveBody(row, await f.GetMessageAsync(new UniqueId(f.UidValidity, (uint)row.Uid), ct), writeFile: Account.DownloadAttachments); }
                    catch (Exception ex) when (ex is MessageNotFoundException or FormatException or ParseException) { _noBody.Add(row.Id); }
                }
            }
            foreach (var row in rows.Except(whole))
            {
                ct.ThrowIfCancellationRequested();
                try { await SaveTextOnlyAsync(f, new UniqueId(f.UidValidity, (uint)row.Uid), row, ct); }
                catch (Exception ex) when (ex is MessageNotFoundException or FormatException or ParseException) { _noBody.Add(row.Id); }
            }
        }
        finally { try { await f.CloseAsync(false, ct); } catch { } }
    }

    /// <summary>
    /// Design DS1, "attachments only when I open the email": downloads just the text and HTML of an email (and its
    /// invite, if any), using the server's description of its parts. Its attachments are listed with negative
    /// indices (<see cref="MimeText.PendingIndex"/>), which makes the reader fetch the whole email when it is opened.
    /// No copy goes into the message cache, as it is not the whole email.
    /// </summary>
    private async Task SaveTextOnlyAsync(IMailFolder f, UniqueId uid, MessageRow row, CancellationToken ct)
    {
        var s = (await f.FetchAsync(new[] { uid }, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, ct)).FirstOrDefault();
        if (s?.Body == null) return;
        async Task<string> TextOf(BodyPartText? part)
        {
            if (part == null) return "";
            return await f.GetBodyPartAsync(uid, part, ct) is TextPart tp ? tp.Text ?? "" : "";
        }
        var body = new MessageBody { Text = await TextOf(s.TextBody), Html = await TextOf(s.HtmlBody) };
        if (body.Html.Length == 0 && body.Text.Length > 0) body.Html = MimeText.TextToHtml(body.Text);
        if (body.Text.Length == 0 && body.Html.Length > 0) body.Text = MimeText.HtmlToText(body.Html);
        // The invite, also when it came as an .ics file (Q39: else every opening read the whole email for it).
        var cal = s.BodyParts.OfType<BodyPartBasic>().FirstOrDefault(p => p.ContentType.IsMimeType("text", "calendar")
            || p.ContentType.IsMimeType("application", "ics") || (p.FileName ?? "").EndsWith(".ics", StringComparison.OrdinalIgnoreCase));
        if (cal != null && await f.GetBodyPartAsync(uid, cal, ct) is MimePart cp)
        {
            if (cp is TextPart ctp) body.Calendar = ctp.Text ?? "";
            else if (cp.Content != null)
            {
                using var cms = new MemoryStream();
                cp.Content.DecodeTo(cms);
                body.Calendar = System.Text.Encoding.UTF8.GetString(cms.ToArray());
            }
        }
        body.Attachments = MimeText.PendingAttachments(s.BodyParts, s.TextBody, s.HtmlBody);
        // Design RL1: the pictures inside the email come too (they are part of what it shows), attachments don't.
        long total = 0;
        body.ImagesComplete = true;   // every picture looked at; too big ones are left out on purpose (Q39)
        foreach (var p in s.BodyParts.OfType<BodyPartBasic>())
        {
            var cid = p.ContentId?.Trim('<', '>') ?? "";
            if (cid.Length == 0 || p.IsAttachment || !p.ContentType.MediaType.Equals("image", StringComparison.OrdinalIgnoreCase)) continue;
            if (total + (long)p.Octets > MimeText.ImageCacheBytes) continue;
            if (await f.GetBodyPartAsync(uid, p, ct) is not MimePart part || part.Content == null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            total += ms.Length;
            body.Images[cid] = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
        }
        _store.SaveBody(row.Id, body);
    }

    public Func<string, long, string>? MimePathFor { get; set; }

    private void SaveBody(MessageRow row, MimeMessage msg, bool writeFile = true)
    {
        if (MimePathFor != null && writeFile)
        {
            var path = MimePathFor(Account.Id, row.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            msg.WriteTo(fs);
        }
        var body = MimeText.Extract(msg);
        body.Images = MimeText.InlineImages(msg, MimeText.ImageCacheBytes);   // design RL1: shown from this PC next time
        body.ImagesComplete = true;
        _store.SaveBody(row.Id, body);
    }

    /// <summary>
    /// Q39: gets an email ready to read. From the message file on this PC when there is one; otherwise from the server
    /// on the reading connection — whole when attachments come with emails (or it is small), else just its text and
    /// pictures (a 20 MB attachment isn't downloaded to show a few lines). Returns the saved body.
    /// </summary>
    public async Task<MessageBody?> FetchBodyAsync(MessageRow row, CancellationToken ct)
    {
        if (await LocalMimeAsync(row, ct) is { } local)
        {
            SaveBody(row, local, writeFile: false);
            return _store.GetBody(row.Id);
        }
        var folder = _store.GetFolder(row.FolderId);
        if (folder == null) return null;
        return await _ui.UseAsync(async client =>
        {
            var f = await client.GetFolderAsync(folder.Path, ct);
            await f.OpenAsync(FolderAccess.ReadOnly, ct);
            try
            {
                var uid = new UniqueId(f.UidValidity, (uint)row.Uid);
                if (Account.DownloadAttachments || row.Size <= WholeMessageMax) SaveBody(row, await f.GetMessageAsync(uid, ct));
                else await SaveTextOnlyAsync(f, uid, row, ct);
                return _store.GetBody(row.Id);
            }
            catch (MessageNotFoundException) { return null; }
            finally { try { await f.CloseAsync(false, ct); } catch { } }
        }, ct);
    }

    /// <summary>The email from its message file on this PC, or null (never the server).</summary>
    public async Task<MimeMessage?> LocalMimeAsync(MessageRow row, CancellationToken ct)
    {
        var path = MimePathFor?.Invoke(Account.Id, row.Id);
        if (path == null || !File.Exists(path)) return null;
        try { return await MimeMessage.LoadAsync(path, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn("cached message unreadable: " + ex.Message); return null; }
    }

    /// <summary>Downloads a message (interactive connection). Uses the message file on this PC when present;
    /// <paramref name="refreshBody"/> then also re-saves the stored text with the email's pictures (design RL1, for
    /// emails saved before pictures were kept), so the next opening needs neither.</summary>
    public async Task<MimeMessage?> GetMimeAsync(MessageRow row, CancellationToken ct, bool refreshBody = false)
    {
        var path = MimePathFor?.Invoke(Account.Id, row.Id);
        if (path != null && File.Exists(path))
        {
            try
            {
                var cached = await MimeMessage.LoadAsync(path, ct);
                if (refreshBody) SaveBody(row, cached, writeFile: false);
                return cached;
            }
            catch (Exception ex) { Log.Warn("cached message unreadable, refetching: " + ex.Message); }
        }
        var folder = _store.GetFolder(row.FolderId);
        if (folder == null) return null;
        return await _ui.UseAsync(async client =>
        {
            var f = await client.GetFolderAsync(folder.Path, ct);
            await f.OpenAsync(FolderAccess.ReadOnly, ct);
            try
            {
                var msg = await f.GetMessageAsync(new UniqueId(f.UidValidity, (uint)row.Uid), ct);
                SaveBody(row, msg);
                return msg;
            }
            catch (MessageNotFoundException) { return null; }
            finally { try { await f.CloseAsync(false, ct); } catch { } }
        }, ct);
    }

    // ───────────────────────── server changes ─────────────────────────

    /// <summary>Replays queued local changes (read/pin/move/delete) to the server.</summary>
    public async Task FlushPendingOpsAsync(CancellationToken ct)
    {
        var ops = _store.GetPendingOps(Account.Id);
        if (ops.Count == 0) return;
        var folders = _store.GetFolders(Account.Id).ToDictionary(f => f.Id);
        await _ui.UseAsync(async client =>
        {
            foreach (var group in ops.GroupBy(o => o.FolderId))
            {
                if (!folders.TryGetValue(group.Key, out var src)) { foreach (var o in group) _store.RemovePendingOp(o.Id); continue; }
                IMailFolder f;
                try { f = await client.GetFolderAsync(src.Path, ct); }
                catch (FolderNotFoundException) { foreach (var o in group) _store.RemovePendingOp(o.Id); continue; }
                await f.OpenAsync(FolderAccess.ReadWrite, ct);
                try
                {
                    foreach (var op in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (op.Kind == PendingOpKind.EmptyFolder)
                            {
                                // Empty Trash / Spam (design TB1): everything there, also what isn't listed on this PC yet.
                                await f.StoreAsync(UniqueIdRange.All, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
                                await f.ExpungeAsync(ct);
                                _store.RemovePendingOp(op.Id);
                                continue;
                            }
                            var uid = new UniqueId(f.UidValidity, (uint)op.Uid);
                            switch (op.Kind)
                            {
                                case PendingOpKind.SetSeen: await f.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, KitFlags.Seen) { Silent = true }, ct); break;
                                case PendingOpKind.ClearSeen: await f.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Remove, KitFlags.Seen) { Silent = true }, ct); break;
                                case PendingOpKind.SetFlagged: await f.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, KitFlags.Flagged) { Silent = true }, ct); break;
                                case PendingOpKind.ClearFlagged: await f.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Remove, KitFlags.Flagged) { Silent = true }, ct); break;
                                case PendingOpKind.Move:
                                    if (folders.TryGetValue(op.Arg, out var dst))
                                    {
                                        var target = await client.GetFolderAsync(dst.Path, ct);
                                        await f.MoveToAsync(uid, target, ct);
                                    }
                                    break;
                                case PendingOpKind.Delete:
                                    await f.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
                                    if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await f.ExpungeAsync(new[] { uid }, ct);
                                    else await f.ExpungeAsync(ct);
                                    break;
                            }
                            _store.RemovePendingOp(op.Id);
                        }
                        catch (Exception ex) when (ex is MessageNotFoundException || (ex is ImapCommandException ic && ic.Response == ImapCommandResponse.No))
                        {
                            Log.Warn($"[{Account.Email}] op {op.Kind} uid {op.Uid} dropped: {ex.Message}");
                            _store.RemovePendingOp(op.Id);
                        }
                        catch (Exception ex) when (!ImapLease.IsConnectionError(ex) && ex is not OperationCanceledException)
                        {
                            Log.Error($"[{Account.Email}] op {op.Kind} failed", ex);
                            _store.BumpPendingOp(op.Id);
                            if (op.Attempts >= 5) _store.RemovePendingOp(op.Id);
                        }
                    }
                }
                finally { try { await f.CloseAsync(false, ct); } catch { } }
            }
        }, ct);
    }

    /// <summary>Stores a copy of a sent message in the Sent folder (servers that don't do it themselves).</summary>
    public Task AppendToSentAsync(MimeMessage msg, CancellationToken ct) => AppendAsync(FolderRole.Sent, msg, KitFlags.Seen, ct);

    /// <summary>Appends a message to the folder with the given role. False when the account has no such folder.</summary>
    public async Task<bool> AppendAsync(FolderRole role, MimeMessage msg, KitFlags flags, CancellationToken ct)
    {
        var target = _store.GetFolders(Account.Id).FirstOrDefault(f => f.Role == role);
        if (target == null) return false;
        await _ui.UseAsync(async client =>
        {
            var f = await client.GetFolderAsync(target.Path, ct);
            await f.AppendAsync(new AppendRequest(msg, flags), ct);
        }, ct);
        return true;
    }

    /// <summary>
    /// Saves a draft in the Drafts folder, first removing earlier saves of the same message (same Message-ID),
    /// so repeated saves replace the draft instead of piling up copies.
    /// </summary>
    public async Task<bool> ReplaceDraftAsync(MimeMessage msg, CancellationToken ct)
    {
        var target = _store.GetFolders(Account.Id).FirstOrDefault(f => f.Role == FolderRole.Drafts);
        if (target == null) return false;
        await _ui.UseAsync(async client =>
        {
            var f = await client.GetFolderAsync(target.Path, ct);
            await RemoveByMessageIdAsync(client, f, msg.MessageId, ct);
            await f.AppendAsync(new AppendRequest(msg, KitFlags.Draft | KitFlags.Seen), ct);
        }, ct);
        return true;
    }

    /// <summary>After a message is sent: removes its saved drafts (same Message-ID) from the Drafts folder.</summary>
    public async Task DeleteDraftsByMessageIdAsync(string messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;
        var target = _store.GetFolders(Account.Id).FirstOrDefault(f => f.Role == FolderRole.Drafts);
        if (target == null) return;
        await _ui.UseAsync(async client =>
        {
            var f = await client.GetFolderAsync(target.Path, ct);
            await RemoveByMessageIdAsync(client, f, messageId, ct);
            return true;
        }, ct);
    }

    private static async Task RemoveByMessageIdAsync(ImapClient client, IMailFolder f, string? messageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;
        if (!f.IsOpen || f.Access != FolderAccess.ReadWrite) await f.OpenAsync(FolderAccess.ReadWrite, ct);
        var id = messageId.Trim().Trim('<', '>');
        var uids = await f.SearchAsync(KitSearch.HeaderContains("Message-ID", id), ct);
        if (uids.Count == 0) return;
        await f.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await f.ExpungeAsync(uids, ct);
        else await f.ExpungeAsync(ct);
    }

    /// <summary>Creates a folder (e.g. "Archive") at the top level of the personal namespace.</summary>
    public async Task<MailFolder?> EnsureFolderAsync(string name, FolderRole role, CancellationToken ct)
    {
        var existing = _store.GetFolders(Account.Id).FirstOrDefault(f => f.Role == role);
        if (existing != null) return existing;
        return await _ui.UseAsync(async client =>
        {
            var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
            var top = await client.GetFolderAsync(ns.Path, ct);
            IMailFolder created;
            try { created = await top.CreateAsync(name, true, ct) ?? throw new ImapCommandException(ImapCommandResponse.No, "create failed"); }
            catch (ImapCommandException) { created = await client.GetFolderAsync(string.IsNullOrEmpty(ns.Path) ? name : ns.Path + ns.DirectorySeparator + name, ct); }
            var local = new MailFolder { AccountId = Account.Id, Path = created.FullName, Name = created.Name, Role = role, Delimiter = created.DirectorySeparator };
            _store.UpsertFolder(local);
            return local;
        }, ct);
    }

    // ───────────────────────── IDLE ─────────────────────────

    private async Task IdleAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        var backoff = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            ImapClient? client = null;
            try
            {
                client = await _connector.OpenImapAsync(Account, ct);
                if (!client.Capabilities.HasFlag(ImapCapabilities.Idle)) { client.Dispose(); return; }
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                var changed = 0;
                inbox.CountChanged += (_, _) => Interlocked.Exchange(ref changed, 1);
                inbox.MessageExpunged += (_, _) => Interlocked.Exchange(ref changed, 1);
                inbox.MessageFlagsChanged += (_, _) => Interlocked.Exchange(ref changed, 1);
                backoff = TimeSpan.FromSeconds(30);
                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    using var done = new CancellationTokenSource(TimeSpan.FromMinutes(9));
                    using var link = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // End IDLE promptly when something changes so the sync loop can pick it up.
                    void OnCount(object? s, EventArgs e) { try { done.Cancel(); } catch { } }
                    inbox.CountChanged += OnCount;
                    try { await client.IdleAsync(done.Token, link.Token); }
                    finally { inbox.CountChanged -= OnCount; }
                    if (Interlocked.Exchange(ref changed, 0) == 1) Poke();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Info($"[{Account.Email}] IDLE ended: {ex.Message}");
            }
            finally
            {
                try { if (client?.IsConnected == true) await client.DisconnectAsync(true, CancellationToken.None); } catch { }
                client?.Dispose();
            }
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 600));
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _sync.Dispose();
        _ui.Dispose();
    }
}
