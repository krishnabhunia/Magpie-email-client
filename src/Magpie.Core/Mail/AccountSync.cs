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
}

public sealed class ChangeSet
{
    public string AccountId { get; init; } = "";
    public HashSet<long> FolderIds { get; } = new();
    public List<MessageRow> NewInboxMessages { get; } = new();
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
    public void Poke() => _wake.Release();

    private void SetStatus(SyncState state, string msg = "")
    {
        Status = new SyncStatus { State = state, Message = msg };
        StatusChanged?.Invoke(this, Status);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(15);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetStatus(_initialDone ? SyncState.Syncing : SyncState.Connecting, _initialDone ? "Checking for mail…" : "Connecting…");
                await FlushPendingOpsAsync(ct);
                await SyncAllAsync(ct);
                _initialDone = true;
                backoff = TimeSpan.FromSeconds(15);
                var pending = _store.PendingOpCount(Account.Id);
                SetStatus(SyncState.Idle, pending > 0 ? $"{pending} change(s) waiting to reach the server" : "Up to date");
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

    /// <summary>Gmail's virtual folders duplicate every message; we list them but don't mirror them.</summary>
    internal static bool ShouldMirror(FolderRole role, bool gmail) =>
        !(gmail && role is FolderRole.All or FolderRole.Important or FolderRole.Flagged);

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

    private async Task SyncAllAsync(CancellationToken ct)
    {
        await _sync.UseAsync(async client =>
        {
            var folders = await SyncFolderListAsync(client, ct);
            var before = _store.GetFolders(Account.Id).ToDictionary(f => f.Id);
            // Inbox first, then Sent (for conversations), then the rest.
            var order = folders.Where(f => f.local.Synced)
                .OrderBy(f => f.local.Role switch { FolderRole.Inbox => 0, FolderRole.Sent => 1, FolderRole.Drafts => 2, FolderRole.Other => 3, _ => 4 })
                .ToList();
            var initial = !_initialDone && before.Values.All(f => f.LastSync == 0);
            var changes = new ChangeSet { AccountId = Account.Id, FoldersChanged = !_initialDone };
            foreach (var (local, path) in order)
            {
                ct.ThrowIfCancellationRequested();
                var stored = _store.GetFolder(local.Id) ?? local;
                try
                {
                    var f = await client.GetFolderAsync(path, ct);
                    var (added, touched) = await SyncFolderAsync(client, f, stored, ct);
                    if (touched || stored.LastSync == 0) changes.FolderIds.Add(stored.Id);
                    if (stored.Role == FolderRole.Inbox && stored.LastSync != 0 && !initial)
                        changes.NewInboxMessages.AddRange(added.Where(m => !m.IsSeen));
                }
                catch (FolderNotFoundException) { _store.DeleteFolder(stored.Id); changes.FoldersChanged = true; }
                catch (Exception ex) when (!ImapLease.IsConnectionError(ex) && ex is not OperationCanceledException)
                {
                    Log.Error($"[{Account.Email}] folder {path} failed", ex);
                }
                if (stored.Role == FolderRole.Inbox && changes.FolderIds.Count > 0)
                {
                    // Show the inbox as soon as it is ready on first run.
                    Changed?.Invoke(changes);
                    changes = new ChangeSet { AccountId = Account.Id };
                }
            }
            if (changes.FolderIds.Count > 0 || changes.FoldersChanged) Changed?.Invoke(changes);

            // Prefetch bodies of the newest inbox messages so they open instantly (and become searchable).
            var inboxFolder = _store.GetFolders(Account.Id).FirstOrDefault(f => f.Role == FolderRole.Inbox);
            if (inboxFolder != null) await PrefetchBodiesAsync(client, inboxFolder, 40, ct);
        }, ct);
    }

    /// <summary>Returns rows newly added in this folder, and whether anything in it changed.</summary>
    private async Task<(List<MessageRow> added, bool changed)> SyncFolderAsync(ImapClient client, IMailFolder f, MailFolder local, CancellationToken ct)
    {
        await f.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            if (local.UidValidity != 0 && local.UidValidity != f.UidValidity)
            {
                Log.Info($"[{Account.Email}] {local.Path}: UIDVALIDITY changed, resyncing");
                _store.WipeFolderMessages(local.Id);
                local.HighestModSeq = 0;
            }
            var map = _store.GetUidMap(local.Id);
            var pendingRemovals = _store.PendingRemovals(Account.Id);
            var added = new List<MessageRow>();
            bool changed = false;

            // 1. New messages
            IList<UniqueId> newUids;
            if (map.Count == 0)
            {
                if (f.Count == 0) newUids = Array.Empty<UniqueId>();
                else
                {
                    var since = DateTime.Now.AddDays(-Math.Max(7, Account.SyncDays));
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
                var max = map.Keys.Max();
                if (f.UidNext is { } next && next.Id <= max + 1) newUids = Array.Empty<UniqueId>();
                else
                {
                    var range = new UniqueIdRange(new UniqueId(f.UidValidity, (uint)Math.Min(max + 1, uint.MaxValue)), UniqueId.MaxValue);
                    newUids = (await f.SearchAsync(KitSearch.Uids(range), ct)).Where(u => u.Id > max).ToList();
                }
            }
            newUids = newUids.Where(u => !pendingRemovals.Contains((local.Id, u.Id))).ToList();
            foreach (var batch in newUids.OrderByDescending(u => u.Id).Chunk(150))
            {
                ct.ThrowIfCancellationRequested();
                var rows = await FetchRowsAsync(client, f, local, batch, ct);
                added.AddRange(_store.InsertMessages(rows));
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

                var serverUids = (await f.SearchAsync(KitSearch.All, ct)).Select(u => (long)u.Id).ToHashSet();
                var gone = map.Keys.Where(u => !serverUids.Contains(u)).ToList();
                if (gone.Count > 0) _store.DeleteUids(local.Id, gone);
                changed = changes.Count > 0 || gone.Count > 0;
            }

            _store.UpdateFolderState(local.Id, f.UidValidity, f.UidNext?.Id ?? 0, (long)f.HighestModSeq);
            _store.RefreshFolderCounts(local.Id);
            return (added, changed || added.Count > 0);
        }
        finally
        {
            try { await f.CloseAsync(false, ct); } catch { }
        }
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

    private async Task PrefetchBodiesAsync(ImapClient client, MailFolder folder, int count, CancellationToken ct)
    {
        var rows = _store.RowsWithoutBody(folder.Id, count);
        if (rows.Count == 0) return;
        var f = await client.GetFolderAsync(folder.Path, ct);
        await f.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            foreach (var id in rows)
            {
                ct.ThrowIfCancellationRequested();
                var row = _store.GetMessage(id);
                if (row == null) continue;
                try
                {
                    var msg = await f.GetMessageAsync(new UniqueId(f.UidValidity, (uint)row.Uid), ct);
                    SaveBody(row, msg);
                }
                catch (MessageNotFoundException) { }
            }
        }
        finally { try { await f.CloseAsync(false, ct); } catch { } }
    }

    public Func<string, long, string>? MimePathFor { get; set; }

    private void SaveBody(MessageRow row, MimeMessage msg)
    {
        if (MimePathFor != null)
        {
            var path = MimePathFor(Account.Id, row.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var fs = File.Create(path);
            msg.WriteTo(fs);
        }
        var body = MimeText.Extract(msg);
        _store.SaveBody(row.Id, body);
    }

    /// <summary>Downloads a message (interactive connection). Uses the local cache when present.</summary>
    public async Task<MimeMessage?> GetMimeAsync(MessageRow row, CancellationToken ct)
    {
        var path = MimePathFor?.Invoke(Account.Id, row.Id);
        if (path != null && File.Exists(path))
        {
            try { return await MimeMessage.LoadAsync(path, ct); }
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
                        var uid = new UniqueId(f.UidValidity, (uint)op.Uid);
                        try
                        {
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
