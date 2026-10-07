using System.Text.Json;
using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>What the message list is showing.</summary>
public sealed class ListQuery
{
    /// <summary>Folders to include. Empty = none.</summary>
    public IReadOnlyCollection<long> FolderIds { get; init; } = Array.Empty<long>();
    public Category? Category { get; init; }
    public bool UnreadOnly { get; init; }
    public bool FlaggedOnly { get; init; }
    /// <summary>True: only snoozed conversations. False: hide snoozed ones (normal inbox).</summary>
    public bool Snoozed { get; init; }
    /// <summary>The Set aside pile (design B7).</summary>
    public bool SetAside { get; init; }
    /// <summary>Only conversations with an auto-delete timer before this time ("Deleting soon", design AD4).</summary>
    public DateTimeOffset? DeletingBefore { get; init; }
    public string? Tag { get; init; }
    public SearchQuery? Search { get; init; }
    public int Limit { get; init; } = 300;
    public int Offset { get; init; }
}

/// <summary>
/// Local mail store: SQLite (WAL) with an FTS5 index over subject, sender, recipients and body text.
/// Every method opens its own pooled connection, so it is safe to call from the UI and sync threads.
/// </summary>
public sealed partial class MailStore
{
    private readonly string _cs;
    /// <summary>Bump when tables are added; every statement in Migrate is idempotent (IF NOT EXISTS).</summary>
    public const int SchemaVersion = 9;

    public MailStore(string dbPath)
    {
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        Migrate();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=8000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        var ver = Convert.ToInt32(Scalar(c, "PRAGMA user_version;"));
        if (ver >= SchemaVersion) return;
        using var tx = c.BeginTransaction();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS folders(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id TEXT NOT NULL, path TEXT NOT NULL, name TEXT NOT NULL,
              role INTEGER NOT NULL DEFAULT 0, delimiter TEXT NOT NULL DEFAULT '/',
              uidvalidity INTEGER NOT NULL DEFAULT 0, uidnext INTEGER NOT NULL DEFAULT 0,
              modseq INTEGER NOT NULL DEFAULT 0, unread INTEGER NOT NULL DEFAULT 0, total INTEGER NOT NULL DEFAULT 0,
              last_sync INTEGER NOT NULL DEFAULT 0, synced INTEGER NOT NULL DEFAULT 1,
              UNIQUE(account_id, path));
            CREATE TABLE IF NOT EXISTS messages(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id TEXT NOT NULL, folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
              uid INTEGER NOT NULL, message_id TEXT NOT NULL DEFAULT '', in_reply_to TEXT NOT NULL DEFAULT '',
              refs TEXT NOT NULL DEFAULT '', thread_key TEXT NOT NULL,
              from_name TEXT NOT NULL DEFAULT '', from_addr TEXT NOT NULL DEFAULT '',
              to_list TEXT NOT NULL DEFAULT '', cc_list TEXT NOT NULL DEFAULT '', reply_to TEXT NOT NULL DEFAULT '',
              subject TEXT NOT NULL DEFAULT '', preview TEXT NOT NULL DEFAULT '',
              date INTEGER NOT NULL, sort_date INTEGER NOT NULL, flags INTEGER NOT NULL DEFAULT 0,
              has_attach INTEGER NOT NULL DEFAULT 0, size INTEGER NOT NULL DEFAULT 0,
              category INTEGER NOT NULL DEFAULT 0, list_unsub TEXT NOT NULL DEFAULT '',
              snooze_until INTEGER NULL, tags TEXT NOT NULL DEFAULT '', body_cached INTEGER NOT NULL DEFAULT 0,
              UNIQUE(folder_id, uid));
            CREATE INDEX IF NOT EXISTS ix_msg_thread ON messages(account_id, thread_key);
            CREATE INDEX IF NOT EXISTS ix_msg_folder_sort ON messages(folder_id, sort_date DESC);
            CREATE INDEX IF NOT EXISTS ix_msg_msgid ON messages(account_id, message_id);
            CREATE INDEX IF NOT EXISTS ix_msg_irt ON messages(account_id, in_reply_to);
            CREATE INDEX IF NOT EXISTS ix_msg_snooze ON messages(snooze_until) WHERE snooze_until IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_msg_counts ON messages(folder_id, account_id, thread_key, flags, snooze_until);
            CREATE TABLE IF NOT EXISTS bodies(
              message_row INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
              html TEXT NOT NULL DEFAULT '', text TEXT NOT NULL DEFAULT '', attachments TEXT NOT NULL DEFAULT '[]');
            CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(subject, sender, recipients, body, tokenize='unicode61 remove_diacritics 2');
            CREATE TABLE IF NOT EXISTS pending_ops(
              id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL, folder_id INTEGER NOT NULL,
              uid INTEGER NOT NULL, kind INTEGER NOT NULL, arg INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
              created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS outbox(
              id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL, mime BLOB NOT NULL,
              send_at INTEGER NOT NULL, status INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0,
              last_error TEXT NOT NULL DEFAULT '', subject TEXT NOT NULL DEFAULT '', to_text TEXT NOT NULL DEFAULT '',
              message_id TEXT NOT NULL DEFAULT '', thread_key TEXT NOT NULL DEFAULT '', remind_at INTEGER NULL,
              created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS reminders(
              id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL, thread_key TEXT NOT NULL,
              subject TEXT NOT NULL DEFAULT '', after INTEGER NOT NULL, due INTEGER NOT NULL,
              state INTEGER NOT NULL DEFAULT 0, always INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS contacts(
              addr TEXT PRIMARY KEY, name TEXT NOT NULL DEFAULT '', count INTEGER NOT NULL DEFAULT 0, last INTEGER NOT NULL DEFAULT 0,
              sent INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS summaries(
              account_id TEXT NOT NULL, thread_key TEXT NOT NULL, digest TEXT NOT NULL, text TEXT NOT NULL, created INTEGER NOT NULL,
              PRIMARY KEY(account_id, thread_key));
            CREATE TABLE IF NOT EXISTS local_drafts(
              id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL, mime BLOB NOT NULL,
              subject TEXT NOT NULL DEFAULT '', to_text TEXT NOT NULL DEFAULT '', preview TEXT NOT NULL DEFAULT '',
              thread_key TEXT NOT NULL DEFAULT '', source_draft_row INTEGER NULL,
              pending_upload INTEGER NOT NULL DEFAULT 0, updated INTEGER NOT NULL, message_id TEXT NOT NULL DEFAULT '');
            """);
        // v2 → v3: local drafts remember their Message-ID (stale copies are dropped once the message is sent or saved).
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('local_drafts') WHERE name='message_id'")) == 0)
            Exec(c, "ALTER TABLE local_drafts ADD COLUMN message_id TEXT NOT NULL DEFAULT ''");
        // v5 (1.2.0): a body keeps its invite (text/calendar part, design B3).
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('bodies') WHERE name='calendar'")) == 0)
            Exec(c, "ALTER TABLE bodies ADD COLUMN calendar TEXT NOT NULL DEFAULT ''");
        // v6 (2.2.0, design RL1): a body keeps the pictures inside the email, so opening it needs nothing else.
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('bodies') WHERE name='images'")) == 0)
            Exec(c, "ALTER TABLE bodies ADD COLUMN images TEXT NOT NULL DEFAULT ''");
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('bodies') WHERE name='images_done'")) == 0)
            Exec(c, "ALTER TABLE bodies ADD COLUMN images_done INTEGER NOT NULL DEFAULT 0");
        // v4 → v5 (1.2.0): auto-delete / OTP delete (designs AD1–AD4). delete_rule is the rule's id, or "-" for "Keep this one".
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name='delete_at'")) == 0)
        {
            Exec(c, "ALTER TABLE messages ADD COLUMN delete_at INTEGER NULL");
            Exec(c, "ALTER TABLE messages ADD COLUMN delete_rule TEXT NOT NULL DEFAULT ''");
        }
        Exec(c, """
            CREATE INDEX IF NOT EXISTS ix_msg_delete ON messages(delete_at) WHERE delete_at IS NOT NULL;
            CREATE TABLE IF NOT EXISTS events(
              account_id TEXT NOT NULL, uid TEXT NOT NULL, summary TEXT NOT NULL DEFAULT '', start INTEGER NOT NULL, end INTEGER NOT NULL,
              all_day INTEGER NOT NULL DEFAULT 0, location TEXT NOT NULL DEFAULT '', organizer TEXT NOT NULL DEFAULT '',
              answer TEXT NOT NULL DEFAULT '', sequence INTEGER NOT NULL DEFAULT 0, cancelled INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(account_id, uid));
            CREATE TABLE IF NOT EXISTS auto_delete_rules(
              id TEXT PRIMARY KEY, pattern TEXT NOT NULL, account_id TEXT NOT NULL DEFAULT '', otp INTEGER NOT NULL DEFAULT 0,
              amount INTEGER NOT NULL DEFAULT 7, unit INTEGER NOT NULL DEFAULT 0, paused INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL);
            """);
        MigrateCalendar(c);   // design B2 (MailStore.Calendar.cs)
        MigrateTrash(c);      // design TB1 (MailStore.Trash.cs)
        Exec(c, $"PRAGMA user_version={SchemaVersion};");
        tx.Commit();
    }

    // ───────────────────────── folders ─────────────────────────

    public long UpsertFolder(MailFolder f)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO folders(account_id,path,name,role,delimiter,synced) VALUES($a,$p,$n,$r,$d,$s)
            ON CONFLICT(account_id,path) DO UPDATE SET name=excluded.name, role=excluded.role, delimiter=excluded.delimiter, synced=excluded.synced
            RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$a", f.AccountId);
        cmd.Parameters.AddWithValue("$p", f.Path);
        cmd.Parameters.AddWithValue("$n", f.Name);
        cmd.Parameters.AddWithValue("$r", (int)f.Role);
        cmd.Parameters.AddWithValue("$d", f.Delimiter == '\0' ? "" : f.Delimiter.ToString());
        cmd.Parameters.AddWithValue("$s", f.Synced ? 1 : 0);
        f.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return f.Id;
    }

    public List<MailFolder> GetFolders(string? accountId = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,account_id,path,name,role,delimiter,uidvalidity,uidnext,modseq,unread,total,last_sync,synced FROM folders"
                          + (accountId == null ? "" : " WHERE account_id=$a") + " ORDER BY account_id, role=0, role, path COLLATE NOCASE";
        if (accountId != null) cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<MailFolder>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var d = r.GetString(5);
            list.Add(new MailFolder
            {
                Id = r.GetInt64(0), AccountId = r.GetString(1), Path = r.GetString(2), Name = r.GetString(3),
                Role = (FolderRole)r.GetInt32(4), Delimiter = d.Length > 0 ? d[0] : '\0',
                UidValidity = r.GetInt64(6), UidNext = r.GetInt64(7), HighestModSeq = r.GetInt64(8),
                Unread = r.GetInt32(9), Total = r.GetInt32(10), LastSync = r.GetInt64(11), Synced = r.GetInt32(12) != 0,
            });
        }
        return list;
    }

    public MailFolder? GetFolder(long id) => GetFolders().FirstOrDefault(f => f.Id == id);

    public void UpdateFolderState(long folderId, long uidValidity, long uidNext, long modSeq)
    {
        using var c = Open();
        Exec(c, "UPDATE folders SET uidvalidity=$v, uidnext=$n, modseq=$m, last_sync=$t WHERE id=$id",
            ("$v", uidValidity), ("$n", uidNext), ("$m", modSeq), ("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("$id", folderId));
    }

    public void RefreshFolderCounts(long folderId)
    {
        using var c = Open();
        Exec(c, """
            UPDATE folders SET
              total=(SELECT COUNT(*) FROM messages WHERE folder_id=$id),
              unread=(SELECT COUNT(*) FROM messages WHERE folder_id=$id AND (flags & 1)=0)
            WHERE id=$id
            """, ("$id", folderId));
    }

    public void DeleteFolder(long folderId)
    {
        using var c = Open();
        DeleteFtsForFolder(c, folderId);
        Exec(c, "DELETE FROM folders WHERE id=$id", ("$id", folderId));
    }

    public void DeleteAccount(string accountId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE account_id=$a)", ("$a", accountId));
        foreach (var t in new[] { "messages", "folders", "pending_ops", "outbox", "reminders", "summaries" })
            Exec(c, $"DELETE FROM {t} WHERE account_id=$a", ("$a", accountId));
        tx.Commit();
    }

    // ───────────────────────── messages (sync side) ─────────────────────────

    public long MaxUid(long folderId)
    {
        using var c = Open();
        return Convert.ToInt64(Scalar(c, "SELECT IFNULL(MAX(uid),0) FROM messages WHERE folder_id=$f", ("$f", folderId)));
    }

    public Dictionary<long, (long id, MessageFlags flags)> GetUidMap(long folderId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT uid,id,flags FROM messages WHERE folder_id=$f";
        cmd.Parameters.AddWithValue("$f", folderId);
        var map = new Dictionary<long, (long, MessageFlags)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = (r.GetInt64(1), (MessageFlags)r.GetInt32(2));
        return map;
    }

    /// <summary>Thread key of any known message in the account with one of these Message-IDs.</summary>
    public string? FindThreadKey(string accountId, IEnumerable<string> messageIds)
    {
        var ids = messageIds.Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(40).ToList();
        if (ids.Count == 0) return null;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = ids.Select((id, i) => { cmd.Parameters.AddWithValue("$m" + i, id); return "$m" + i; });
        cmd.CommandText = $"SELECT thread_key FROM messages WHERE account_id=$a AND message_id IN ({string.Join(',', ps)}) LIMIT 1";
        cmd.Parameters.AddWithValue("$a", accountId);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// Thread keys of stored messages that are <paramref name="messageId"/> itself, one of <paramref name="chain"/>
    /// (its ancestors), or a direct reply to it. Oldest conversation first.
    /// </summary>
    public List<string> RelatedThreadKeys(string accountId, string messageId, IReadOnlyList<string> chain)
    {
        var ids = chain.Append(messageId).Where(s => !string.IsNullOrEmpty(s)).Distinct().Take(60).ToList();
        if (ids.Count == 0) return new();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = ids.Select((id, i) => { cmd.Parameters.AddWithValue("$m" + i, id); return "$m" + i; }).ToList();
        var irt = messageId.Length > 0 ? " OR in_reply_to=$mid" : "";
        cmd.CommandText = "SELECT thread_key, MIN(date) d FROM messages WHERE account_id=$a AND (message_id IN (" + string.Join(",", ps) + ")" + irt + ") GROUP BY thread_key ORDER BY d";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$mid", messageId);
        var list = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    /// <summary>Re-keys whole conversations into <paramref name="keep"/> (and everything that refers to them).</summary>
    public void MergeThreads(string accountId, string keep, IReadOnlyCollection<string> others)
    {
        if (others.Count == 0) return;
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var o in others.Where(o => o != keep))
        {
            foreach (var t in new[] { "messages", "reminders", "outbox" })
                Exec(c, "UPDATE " + t + " SET thread_key=$k WHERE account_id=$a AND thread_key=$o", ("$k", keep), ("$a", accountId), ("$o", o));
            Exec(c, "DELETE FROM summaries WHERE account_id=$a AND thread_key IN ($o,$k)", ("$a", accountId), ("$o", o), ("$k", keep));
        }
        tx.Commit();
    }

    /// <summary>Inserts new messages (ignores duplicates by folder+uid). Returns the rows actually added with ids set.</summary>
    public List<MessageRow> InsertMessages(IEnumerable<MessageRow> rows)
    {
        var added = new List<MessageRow>();
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO messages(account_id,folder_id,uid,message_id,in_reply_to,refs,thread_key,from_name,from_addr,
              to_list,cc_list,reply_to,subject,preview,date,sort_date,flags,has_attach,size,category,list_unsub,tags)
            VALUES($a,$f,$u,$mid,$irt,$refs,$tk,$fn,$fa,$to,$cc,$rt,$s,$pv,$d,$sd,$fl,$ha,$sz,$cat,$lu,$tags)
            RETURNING id;
            """;
        var p = new Dictionary<string, SqliteParameter>();
        foreach (var n in new[] { "$a", "$f", "$u", "$mid", "$irt", "$refs", "$tk", "$fn", "$fa", "$to", "$cc", "$rt", "$s", "$pv", "$d", "$sd", "$fl", "$ha", "$sz", "$cat", "$lu", "$tags" })
            p[n] = cmd.Parameters.Add(n, SqliteType.Text);
        using var fts = c.CreateCommand();
        fts.CommandText = "INSERT INTO messages_fts(rowid,subject,sender,recipients,body) VALUES($id,$s,$f,$r,$b)";
        var fid = fts.Parameters.Add("$id", SqliteType.Integer);
        var fs = fts.Parameters.Add("$s", SqliteType.Text);
        var ff = fts.Parameters.Add("$f", SqliteType.Text);
        var fr = fts.Parameters.Add("$r", SqliteType.Text);
        var fb = fts.Parameters.Add("$b", SqliteType.Text);
        foreach (var m in rows)
        {
            p["$a"].Value = m.AccountId; p["$f"].Value = m.FolderId; p["$u"].Value = m.Uid;
            p["$mid"].Value = m.MessageId; p["$irt"].Value = m.InReplyTo; p["$refs"].Value = m.References;
            p["$tk"].Value = m.ThreadKey; p["$fn"].Value = m.FromName; p["$fa"].Value = m.FromAddress;
            p["$to"].Value = m.To; p["$cc"].Value = m.Cc; p["$rt"].Value = m.ReplyTo; p["$s"].Value = m.Subject;
            p["$pv"].Value = m.Preview; p["$d"].Value = m.Date.ToUnixTimeMilliseconds();
            p["$sd"].Value = (m.SortDate == default ? m.Date : m.SortDate).ToUnixTimeMilliseconds();
            p["$fl"].Value = (int)m.Flags; p["$ha"].Value = m.HasAttachments ? 1 : 0; p["$sz"].Value = m.Size;
            p["$cat"].Value = (int)m.Category; p["$lu"].Value = m.ListUnsubscribe; p["$tags"].Value = m.Tags;
            var res = cmd.ExecuteScalar();
            if (res == null || res is DBNull) continue;
            m.Id = Convert.ToInt64(res);
            fid.Value = m.Id; fs.Value = m.Subject; ff.Value = $"{m.FromName} {m.FromAddress}";
            fr.Value = $"{m.To} {m.Cc}"; fb.Value = m.Preview;
            fts.ExecuteNonQuery();
            added.Add(m);
        }
        tx.Commit();
        return added;
    }

    public void UpdateFlags(long folderId, IEnumerable<(long uid, MessageFlags flags)> changes)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET flags=$fl WHERE folder_id=$f AND uid=$u";
        var pf = cmd.Parameters.Add("$fl", SqliteType.Integer);
        cmd.Parameters.AddWithValue("$f", folderId);
        var pu = cmd.Parameters.Add("$u", SqliteType.Integer);
        foreach (var (uid, flags) in changes) { pf.Value = (int)flags; pu.Value = uid; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void DeleteUids(long folderId, IEnumerable<long> uids)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var del = c.CreateCommand();
        del.CommandText = "DELETE FROM messages_fts WHERE rowid=(SELECT id FROM messages WHERE folder_id=$f AND uid=$u); DELETE FROM messages WHERE folder_id=$f AND uid=$u;";
        del.Parameters.AddWithValue("$f", folderId);
        var pu = del.Parameters.Add("$u", SqliteType.Integer);
        foreach (var u in uids) { pu.Value = u; del.ExecuteNonQuery(); }
        tx.Commit();
    }

    public void WipeFolderMessages(long folderId)
    {
        using var c = Open();
        DeleteFtsForFolder(c, folderId);
        Exec(c, "DELETE FROM messages WHERE folder_id=$f", ("$f", folderId));
    }

    private static void DeleteFtsForFolder(SqliteConnection c, long folderId) =>
        Exec(c, "DELETE FROM messages_fts WHERE rowid IN (SELECT id FROM messages WHERE folder_id=$f)", ("$f", folderId));

    // ───────────────────────── messages (UI side) ─────────────────────────

    private const string MsgCols = "m.id,m.account_id,m.folder_id,m.uid,m.message_id,m.in_reply_to,m.refs,m.thread_key,m.from_name,m.from_addr,m.to_list,m.cc_list,m.reply_to,m.subject,m.preview,m.date,m.sort_date,m.flags,m.has_attach,m.size,m.category,m.list_unsub,m.snooze_until,m.tags,m.body_cached";

    private static MessageRow ReadMsg(SqliteDataReader r, int o = 0) => new()
    {
        Id = r.GetInt64(o), AccountId = r.GetString(o + 1), FolderId = r.GetInt64(o + 2), Uid = r.GetInt64(o + 3),
        MessageId = r.GetString(o + 4), InReplyTo = r.GetString(o + 5), References = r.GetString(o + 6), ThreadKey = r.GetString(o + 7),
        FromName = r.GetString(o + 8), FromAddress = r.GetString(o + 9), To = r.GetString(o + 10), Cc = r.GetString(o + 11),
        ReplyTo = r.GetString(o + 12), Subject = r.GetString(o + 13), Preview = r.GetString(o + 14),
        Date = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(o + 15)), SortDate = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(o + 16)),
        Flags = (MessageFlags)r.GetInt32(o + 17), HasAttachments = r.GetInt32(o + 18) != 0, Size = r.GetInt64(o + 19),
        Category = (Category)r.GetInt32(o + 20), ListUnsubscribe = r.GetString(o + 21),
        SnoozeUntil = r.IsDBNull(o + 22) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(o + 22)),
        Tags = r.GetString(o + 23), BodyCached = r.GetInt32(o + 24) != 0,
    };

    public MessageRow? GetMessage(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadMsg(r) : null;
    }

    /// <summary>Conversations for the message list, newest first.</summary>
    public List<ThreadRow> ListThreads(ListQuery q, DateTimeOffset now)
    {
        if (q.FolderIds.Count == 0) return new();
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        var fps = q.FolderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$fo" + i, id); return "$fo" + i; });
        var inFolders = $"m.folder_id IN ({string.Join(',', fps)})";
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$aside", AsideMs);
        cmd.Parameters.AddWithValue("$gate", GateMs);
        var snooze = q.SetAside ? "m.snooze_until = $aside"
            : q.Snoozed ? "m.snooze_until IS NOT NULL AND m.snooze_until > $now AND m.snooze_until < $gate"
            : "(m.snooze_until IS NULL OR m.snooze_until <= $now)";
        // Tag and search match any copy of an email (a body may be cached on one copy only); the snooze filter applies
        // to the copy that represents it, so a snoozed Inbox email stays hidden in Pinned / tag views.
        var match = new List<string> { inFolders };
        if (q.Tag != null) { match.Add("(',' || m.tags || ',') LIKE $tag"); cmd.Parameters.AddWithValue("$tag", $"%,{q.Tag},%"); }
        if (q.Search != null && !q.Search.IsEmpty) match.Add(q.Search.ToSql(cmd, "m"));
        // "Deleting soon" lists everything with a timer, snoozed / set aside / waiting at the door included.
        if (q.DeletingBefore == null) where.Add(snooze);

        if (q.DeletingBefore is { } db)
        {
            where.Add("m.delete_at IS NOT NULL AND m.delete_at <= $delb AND (m.flags & 2)=0 AND m.delete_rule NOT IN (SELECT id FROM auto_delete_rules WHERE paused=1)");
            cmd.Parameters.AddWithValue("$delb", db.ToUnixTimeMilliseconds());
        }

        var having = new List<string>();
        if (q.UnreadOnly) having.Add("unread > 0");
        if (q.FlaggedOnly) having.Add("flagged > 0");
        var outer = new List<string> { "rn=1" };
        if (q.Category is { } cat) { outer.Add("category=$cat"); cmd.Parameters.AddWithValue("$cat", (int)cat); }
        if (having.Count > 0) outer.AddRange(having);

        // Views over several folders (Pinned, tags, search everywhere, Gmail's All Mail next to its labels) can hold
        // the same email twice; each email is counted once, preferring its Inbox / Sent / label copy.
        var source = q.FolderIds.Count > 1
            ? $"""
              (SELECT * FROM (
                 SELECT m.*, ROW_NUMBER() OVER (
                   PARTITION BY m.account_id, CASE WHEN m.message_id='' THEN 'id:' || m.id ELSE m.message_id END
                   ORDER BY {CopyRankSql("fo.role")}, m.id) AS dn
                 FROM messages m JOIN folders fo ON fo.id=m.folder_id WHERE {string.Join(" AND ", match)}
               ) WHERE dn=1) m WHERE {string.Join(" AND ", where)}
              """
            : $"messages m WHERE {string.Join(" AND ", match)} AND {string.Join(" AND ", where)}";
        cmd.CommandText = $"""
            WITH f AS (
              SELECT {MsgCols},
                ROW_NUMBER() OVER (PARTITION BY m.account_id, m.thread_key ORDER BY m.sort_date DESC, m.id DESC) AS rn,
                COUNT(*) OVER (PARTITION BY m.account_id, m.thread_key) AS cnt,
                SUM(CASE WHEN (m.flags & 1)=0 THEN 1 ELSE 0 END) OVER (PARTITION BY m.account_id, m.thread_key) AS unread,
                MAX(CASE WHEN (m.flags & 2)<>0 THEN 1 ELSE 0 END) OVER (PARTITION BY m.account_id, m.thread_key) AS flagged,
                MAX(m.has_attach) OVER (PARTITION BY m.account_id, m.thread_key) AS anyatt,
                GROUP_CONCAT(CASE WHEN m.from_name<>'' THEN m.from_name ELSE m.from_addr END, '|') OVER (PARTITION BY m.account_id, m.thread_key) AS people,
                MIN(CASE WHEN (m.flags & 2)=0 THEN m.delete_at END) OVER (PARTITION BY m.account_id, m.thread_key) AS del_at,
                FIRST_VALUE(CASE WHEN (m.flags & 2)=0 AND m.delete_at IS NOT NULL THEN m.delete_rule ELSE '' END) OVER (
                  PARTITION BY m.account_id, m.thread_key ORDER BY (CASE WHEN (m.flags & 2)=0 AND m.delete_at IS NOT NULL THEN 0 ELSE 1 END), m.delete_at) AS del_rule
              FROM {source}
            )
            SELECT * FROM f WHERE {string.Join(" AND ", outer)}
            ORDER BY sort_date DESC LIMIT $lim OFFSET $off
            """;
        cmd.Parameters.AddWithValue("$lim", q.Limit);
        cmd.Parameters.AddWithValue("$off", q.Offset);
        var list = new List<ThreadRow>();
        using var r = cmd.ExecuteReader();
        var delAt = r.GetOrdinal("del_at");
        var delRule = r.GetOrdinal("del_rule");
        while (r.Read())
        {
            var m = ReadMsg(r);
            var people = r.IsDBNull(30) ? "" : r.GetString(30);
            list.Add(new ThreadRow
            {
                ThreadKey = m.ThreadKey, AccountId = m.AccountId, Latest = m,
                Count = r.GetInt32(26), UnreadCount = r.GetInt32(27), Flagged = r.GetInt32(28) != 0, HasAttachments = r.GetInt32(29) != 0,
                Participants = string.Join(", ", people.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(FirstName).Distinct().Take(4)),
                SnoozeUntil = m.SnoozeUntil,
                DeleteAt = r.IsDBNull(delAt) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(delAt)),
                DeleteRule = r.IsDBNull(delRule) ? "" : r.GetString(delRule),
            });
        }
        return list;
    }

    private static string FirstName(string s)
    {
        s = s.Trim().Trim('"');
        if (s.Contains('@')) return s.Split('@')[0];
        var sp = s.IndexOf(' ');
        return sp > 0 ? s[..sp] : s;
    }

    /// <summary>
    /// All messages of a conversation across folders (Inbox, Sent, labels…), oldest first.
    /// Copies of the same Message-ID in several folders (Gmail labels) appear once.
    /// </summary>
    public List<MessageRow> GetThread(string accountId, string threadKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {MsgCols}, f.role FROM messages m JOIN folders f ON f.id=m.folder_id
            WHERE m.account_id=$a AND m.thread_key=$t ORDER BY m.date, m.id
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", threadKey);
        var rows = new List<(MessageRow m, FolderRole role)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) rows.Add((ReadMsg(r), (FolderRole)r.GetInt32(25)));
        return Dedupe(rows);
    }

    /// <summary>Which copy of an email represents it when it sits in several folders (lower = preferred).</summary>
    internal static int CopyRank(FolderRole r) => r switch
    {
        FolderRole.Inbox => 0, FolderRole.Sent => 1, FolderRole.Drafts => 2, FolderRole.Other => 3, FolderRole.Archive => 4,
        FolderRole.Flagged => 5, FolderRole.Important => 6, FolderRole.All => 7, _ => 8,
    };

    private static string CopyRankSql(string col) =>
        "CASE " + string.Join(" ", Enum.GetValues<FolderRole>().Select(r => $"WHEN {col}={(int)r} THEN {CopyRank(r)}")) + " ELSE 9 END";

    internal static List<MessageRow> Dedupe(List<(MessageRow m, FolderRole role)> rows)
    {
        static int Rank(FolderRole r) => CopyRank(r);
        var result = new List<MessageRow>();
        foreach (var g in rows.GroupBy(x => string.IsNullOrEmpty(x.m.MessageId) ? "#" + x.m.Id : x.m.MessageId))
            result.Add(g.OrderBy(x => Rank(x.role)).First().m);
        return result.OrderBy(m => m.Date).ThenBy(m => m.Id).ToList();
    }

    /// <summary>All local copies (every folder) of the given conversation — used for actions like archive/delete.</summary>
    public List<MessageRow> GetThreadCopies(string accountId, string threadKey, long? onlyFolder = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.account_id=$a AND m.thread_key=$t" + (onlyFolder is null ? "" : " AND m.folder_id=$f");
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", threadKey);
        if (onlyFolder is { } f) cmd.Parameters.AddWithValue("$f", f);
        var list = new List<MessageRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    /// <summary>Messages in these folders, newest first (snoozed ones left out) — rules run over them on request (design B5).</summary>
    public List<MessageRow> GetMessagesIn(IReadOnlyCollection<long> folderIds, int limit = 5000)
    {
        var list = new List<MessageRow>();
        if (folderIds.Count == 0) return list;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.folder_id IN ({string.Join(",", folderIds.Select(f => f.ToString(System.Globalization.CultureInfo.InvariantCulture)))}) " +
                          "AND m.snooze_until IS NULL ORDER BY m.sort_date DESC LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    public void SetLocalFlags(long rowId, MessageFlags flags)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET flags=$f WHERE id=$id", ("$f", (int)flags), ("$id", rowId));
    }

    public void DeleteRow(long rowId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM messages_fts WHERE rowid=$id; DELETE FROM messages WHERE id=$id;", ("$id", rowId));
    }

    public void SetTags(long rowId, string tags)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET tags=$t WHERE id=$id", ("$t", tags), ("$id", rowId));
    }

    public void SetCategory(string accountId, string fromAddress, Category cat)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET category=$c WHERE account_id=$a AND from_addr=$f", ("$c", (int)cat), ("$a", accountId), ("$f", fromAddress));
    }

    // ───────────────────────── snooze ─────────────────────────

    public void SetSnooze(string accountId, string threadKey, IEnumerable<long> folderIds, DateTimeOffset? until)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var f in folderIds)
            Exec(c, "UPDATE messages SET snooze_until=$u WHERE account_id=$a AND thread_key=$t AND folder_id=$f",
                ("$u", until?.ToUnixTimeMilliseconds()), ("$a", accountId), ("$t", threadKey), ("$f", f));
        tx.Commit();
    }

    /// <summary>Wakes snoozed conversations whose time has come: they jump to the top of the inbox. Returns the woken rows.</summary>
    public List<MessageRow> WakeDueSnoozes(DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var woke = new List<MessageRow>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.snooze_until IS NOT NULL AND m.snooze_until <= $now";
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            using var r = cmd.ExecuteReader();
            while (r.Read()) woke.Add(ReadMsg(r));
        }
        Exec(c, "UPDATE messages SET snooze_until=NULL, sort_date=$now WHERE snooze_until IS NOT NULL AND snooze_until <= $now",
            ("$now", now.ToUnixTimeMilliseconds()));
        tx.Commit();
        return woke;
    }

    public DateTimeOffset? NextSnoozeDue()
    {
        using var c = Open();
        var v = Scalar(c, "SELECT MIN(snooze_until) FROM messages WHERE snooze_until IS NOT NULL");
        return v is null or DBNull ? null : DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(v));
    }

    public void BumpThread(string accountId, string threadKey, DateTimeOffset when)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET sort_date=$w WHERE account_id=$a AND thread_key=$t AND id=(SELECT id FROM messages WHERE account_id=$a AND thread_key=$t ORDER BY sort_date DESC LIMIT 1)",
            ("$w", when.ToUnixTimeMilliseconds()), ("$a", accountId), ("$t", threadKey));
    }

    // ───────────────────────── bodies ─────────────────────────

    public MessageBody? GetBody(long rowId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT html,text,attachments,calendar,images,images_done FROM bodies WHERE message_row=$id";
        cmd.Parameters.AddWithValue("$id", rowId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var images = r.GetString(4);
        return new MessageBody
        {
            Html = r.GetString(0), Text = r.GetString(1),
            Attachments = JsonSerializer.Deserialize<List<AttachmentInfo>>(r.GetString(2)) ?? new(),
            Calendar = r.GetString(3),
            Images = images.Length > 0 && JsonSerializer.Deserialize<Dictionary<string, string>>(images) is { } map
                ? new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase),
            ImagesComplete = r.GetInt64(5) != 0,
        };
    }

    public void SaveBody(long rowId, MessageBody body, string? subject = null, string? sender = null, string? recipients = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var attachments = JsonSerializer.Serialize(body.Attachments);
        var images = body.Images.Count > 0 ? JsonSerializer.Serialize(body.Images) : "";
        var ftsText = body.Text.Length > 200_000 ? body.Text[..200_000] : body.Text;
        // Q39: the same email in other folders (Gmail keeps it in Inbox and All Mail) gets this body too, instead of
        // being downloaded again. Copies that already have one keep theirs.
        var targets = new List<long> { rowId };
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT s.id FROM messages m JOIN messages s ON s.account_id=m.account_id AND s.message_id=m.message_id AND s.id<>m.id
                WHERE m.id=$id AND m.message_id<>'' AND s.body_cached=0
                """;
            q.Parameters.AddWithValue("$id", rowId);
            using var r = q.ExecuteReader();
            while (r.Read()) targets.Add(r.GetInt64(0));
        }
        foreach (var id in targets)
        {
            Exec(c, "INSERT OR REPLACE INTO bodies(message_row,html,text,attachments,calendar,images,images_done) VALUES($id,$h,$t,$a,$c,$i,$d)",
                ("$id", id), ("$h", body.Html), ("$t", body.Text), ("$a", attachments), ("$c", body.Calendar ?? ""), ("$i", images), ("$d", body.ImagesComplete ? 1 : 0));
            Exec(c, "UPDATE messages SET body_cached=1 WHERE id=$id", ("$id", id));
            Exec(c, "UPDATE messages_fts SET body=$b WHERE rowid=$id", ("$b", ftsText), ("$id", id));
        }
        tx.Commit();
    }

    /// <summary>Q39: gives every copy of an email that has no body yet the body of a copy that has one (emails saved
    /// before copies shared their body). Returns how many copies got one.</summary>
    public int ShareBodiesWithCopies(string accountId)
    {
        using var c = Open();
        var pairs = new List<(long to, long from)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT m.id, MIN(s.id) FROM messages m JOIN messages s ON s.account_id=m.account_id AND s.message_id=m.message_id AND s.id<>m.id AND s.body_cached=1
                WHERE m.account_id=$a AND m.body_cached=0 AND m.message_id<>'' GROUP BY m.id
                """;
            q.Parameters.AddWithValue("$a", accountId);
            using var r = q.ExecuteReader();
            while (r.Read()) pairs.Add((r.GetInt64(0), r.GetInt64(1)));
        }
        if (pairs.Count == 0) return 0;
        using var tx = c.BeginTransaction();
        foreach (var (to, from) in pairs)
        {
            Exec(c, """
                INSERT OR REPLACE INTO bodies(message_row,html,text,attachments,calendar,images,images_done)
                SELECT $to,html,text,attachments,calendar,images,images_done FROM bodies WHERE message_row=$from
                """, ("$to", to), ("$from", from));
            Exec(c, "UPDATE messages SET body_cached=1 WHERE id=$to", ("$to", to));
            Exec(c, "UPDATE messages_fts SET body=(SELECT substr(text,1,200000) FROM bodies WHERE message_row=$to) WHERE rowid=$to", ("$to", to));
        }
        tx.Commit();
        return pairs.Count;
    }

    /// <summary>Q40: how many emails of the account's download window are on this PC (each email once, however many
    /// folders it is in; Trash, Spam and Drafts left out, as they aren't downloaded ahead).</summary>
    public (int OnPc, int Total) WindowProgress(string accountId, DateTimeOffset? since)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(done),0) FROM (
              SELECT MAX(m.body_cached) done FROM messages m JOIN folders f ON f.id=m.folder_id
              WHERE m.account_id=$a AND m.sort_date >= $s AND f.role NOT IN ({(int)FolderRole.Trash},{(int)FolderRole.Junk},{(int)FolderRole.Drafts})
              GROUP BY CASE WHEN m.message_id='' THEN 'id:' || m.id ELSE m.message_id END)
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$s", since?.ToUnixTimeMilliseconds() ?? long.MinValue);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (Convert.ToInt32(r.GetInt64(1)), Convert.ToInt32(r.GetInt64(0)));
    }

    /// <summary>True when any email of the conversation still has to be downloaded (design RL1: then the reader shows
    /// "Loading…"; otherwise it renders straight from this PC).</summary>
    public bool HasUncachedBody(string accountId, string threadKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Q39: an email counts as here when any of its copies (Inbox, All Mail…) is.
        cmd.CommandText = """
            SELECT COUNT(*) FROM messages m WHERE m.account_id=$a AND m.thread_key=$t AND m.body_cached=0
              AND NOT (m.message_id<>'' AND EXISTS (SELECT 1 FROM messages s WHERE s.account_id=m.account_id AND s.message_id=m.message_id AND s.body_cached=1))
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", threadKey);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>Newest emails in a folder with no downloaded text yet; only those since <paramref name="since"/> when given
    /// (design DS1: the account's download window).</summary>
    public List<long> RowsWithoutBody(long folderId, int limit, DateTimeOffset? since = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Q40: every size (big emails get their text only); copies of an email that is here already are left out
        // (ShareBodiesWithCopies gives them its body).
        cmd.CommandText = """
            SELECT m.id FROM messages m WHERE m.folder_id=$f AND m.body_cached=0 AND m.sort_date >= $s
              AND NOT (m.message_id<>'' AND EXISTS (SELECT 1 FROM messages s WHERE s.account_id=m.account_id AND s.message_id=m.message_id AND s.body_cached=1))
            ORDER BY m.sort_date DESC LIMIT $l
            """;
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$s", since?.ToUnixTimeMilliseconds() ?? long.MinValue);
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    // ───────────────────────── pending server operations ─────────────────────────

    public void AddPendingOp(PendingOp op)
    {
        using var c = Open();
        Exec(c, "INSERT INTO pending_ops(account_id,folder_id,uid,kind,arg,created) VALUES($a,$f,$u,$k,$g,$c)",
            ("$a", op.AccountId), ("$f", op.FolderId), ("$u", op.Uid), ("$k", (int)op.Kind), ("$g", op.Arg), ("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public List<PendingOp> GetPendingOps(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,account_id,folder_id,uid,kind,arg,attempts FROM pending_ops WHERE account_id=$a ORDER BY id";
        cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<PendingOp>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PendingOp { Id = r.GetInt64(0), AccountId = r.GetString(1), FolderId = r.GetInt64(2), Uid = r.GetInt64(3), Kind = (PendingOpKind)r.GetInt32(4), Arg = r.GetInt64(5), Attempts = r.GetInt32(6) });
        return list;
    }

    public int PendingOpCount(string accountId)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pending_ops WHERE account_id=$a", ("$a", accountId)));
    }

    public void RemovePendingOp(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM pending_ops WHERE id=$id", ("$id", id));
    }

    public void BumpPendingOp(long id)
    {
        using var c = Open();
        Exec(c, "UPDATE pending_ops SET attempts=attempts+1 WHERE id=$id", ("$id", id));
    }

    /// <summary>(folder, uid) pairs with a queued Move/Delete — sync must not resurrect them.</summary>
    public HashSet<(long folder, long uid)> PendingRemovals(string accountId)
    {
        return GetPendingOps(accountId).Where(o => o.Kind is PendingOpKind.Move or PendingOpKind.Delete)
            .Select(o => (o.FolderId, o.Uid)).ToHashSet();
    }

    // ───────────────────────── local drafts (design F1) ─────────────────────────

    /// <summary>Inserts (id 0) or updates a draft kept on this PC. Returns its id.</summary>
    public long SaveLocalDraft(LocalDraft d, bool insertIfMissing = true)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (d.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO local_drafts(account_id,mime,subject,to_text,preview,thread_key,source_draft_row,pending_upload,updated,message_id)
                VALUES($a,$m,$s,$t,$p,$k,$src,$pu,$u,$mid) RETURNING id
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE local_drafts SET account_id=$a, mime=$m, subject=$s, to_text=$t, preview=$p, thread_key=$k,
                  source_draft_row=$src, pending_upload=$pu, updated=$u, message_id=$mid WHERE id=$id RETURNING id
                """;
            cmd.Parameters.AddWithValue("$id", d.Id);
        }
        cmd.Parameters.AddWithValue("$a", d.AccountId);
        cmd.Parameters.AddWithValue("$m", d.Mime);
        cmd.Parameters.AddWithValue("$s", d.Subject);
        cmd.Parameters.AddWithValue("$t", d.ToText);
        cmd.Parameters.AddWithValue("$p", d.Preview);
        cmd.Parameters.AddWithValue("$k", d.ThreadKey);
        cmd.Parameters.AddWithValue("$src", (object?)d.SourceDraftRow ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pu", d.PendingUpload ? 1 : 0);
        // Strictly increasing, so two saves within the same millisecond still count as a change.
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_stampGate) { if (stamp <= _lastStamp) stamp = _lastStamp + 1; _lastStamp = stamp; }
        cmd.Parameters.AddWithValue("$u", stamp);
        cmd.Parameters.AddWithValue("$mid", d.MessageId ?? "");
        var id = cmd.ExecuteScalar();
        if (id == null || id is DBNull)
        {
            // The row was deleted meanwhile (sent, discarded, uploaded). An explicit save stores it again;
            // an autosave (insertIfMissing = false) does not bring it back.
            if (!insertIfMissing) return 0;
            d.Id = 0;
            return SaveLocalDraft(d);
        }
        d.Id = Convert.ToInt64(id);
        d.Updated = DateTimeOffset.FromUnixTimeMilliseconds(stamp);
        return d.Id;
    }

    private readonly object _stampGate = new();
    private long _lastStamp;

    public List<LocalDraft> GetLocalDrafts(bool pendingOnly = false)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,account_id,mime,subject,to_text,preview,thread_key,source_draft_row,pending_upload,updated,message_id FROM local_drafts"
            + (pendingOnly ? " WHERE pending_upload=1" : "") + " ORDER BY updated DESC";
        var list = new List<LocalDraft>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new LocalDraft
            {
                Id = r.GetInt64(0), AccountId = r.GetString(1), Mime = (byte[])r.GetValue(2), Subject = r.GetString(3),
                ToText = r.GetString(4), Preview = r.GetString(5), ThreadKey = r.GetString(6),
                SourceDraftRow = r.IsDBNull(7) ? null : r.GetInt64(7), PendingUpload = r.GetInt32(8) != 0,
                Updated = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(9)), MessageId = r.GetString(10),
            });
        return list;
    }

    public LocalDraft? GetLocalDraft(long id) => GetLocalDrafts().FirstOrDefault(d => d.Id == id);

    /// <summary>Local drafts that are new messages (not newer copies of a server draft, which is counted already).</summary>
    public int CountLocalDrafts()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM local_drafts WHERE source_draft_row IS NULL"));
    }

    public void DeleteLocalDraft(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM local_drafts WHERE id=$id", ("$id", id));
    }

    /// <summary>Deletes the row only if it was not saved again since <paramref name="updated"/>. True if deleted.</summary>
    public bool DeleteLocalDraftIfUnchanged(long id, DateTimeOffset updated)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM local_drafts WHERE id=$id AND updated=$u";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$u", updated.ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery() > 0;
    }

    public void DeleteLocalDraftsForAccount(string accountId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM local_drafts WHERE account_id=$a", ("$a", accountId));
    }

    // ───────────────────────── outbox ─────────────────────────

    public long AddOutbox(OutboxItem o)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO outbox(account_id,mime,send_at,status,subject,to_text,message_id,thread_key,remind_at,created)
            VALUES($a,$m,$s,0,$su,$to,$mid,$tk,$ra,$c) RETURNING id
            """;
        cmd.Parameters.AddWithValue("$a", o.AccountId);
        cmd.Parameters.AddWithValue("$m", o.Mime);
        cmd.Parameters.AddWithValue("$s", o.SendAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$su", o.Subject);
        cmd.Parameters.AddWithValue("$to", o.ToText);
        cmd.Parameters.AddWithValue("$mid", o.MessageId);
        cmd.Parameters.AddWithValue("$tk", o.ThreadKey);
        cmd.Parameters.AddWithValue("$ra", (object?)o.RemindAt?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        o.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return o.Id;
    }

    /// <param name="withMime">false = leave the message itself out (status bar, counts: cheap to read often).</param>
    public List<OutboxItem> GetOutbox(bool includeDone = false, bool withMime = true)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,account_id," + (withMime ? "mime" : "x''") + ",send_at,status,attempts,last_error,subject,to_text,message_id,thread_key,remind_at,created FROM outbox"
            + (includeDone ? "" : " WHERE status IN (0,1,2)") + " ORDER BY send_at";
        var list = new List<OutboxItem>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new OutboxItem
            {
                Id = r.GetInt64(0), AccountId = r.GetString(1), Mime = (byte[])r.GetValue(2),
                SendAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), Status = (OutboxStatus)r.GetInt32(4),
                Attempts = r.GetInt32(5), LastError = r.GetString(6), Subject = r.GetString(7), ToText = r.GetString(8),
                MessageId = r.GetString(9), ThreadKey = r.GetString(10),
                RemindAt = r.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
                Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(12)),
            });
        return list;
    }

    /// <summary>Atomically claims a queued item for sending. False when it was cancelled or already taken.</summary>
    public bool TryClaimOutbox(long id)
    {
        using var c = Open();
        return Exec(c, "UPDATE outbox SET status=1 WHERE id=$id AND status IN (0,2)", ("$id", id)) == 1;
    }

    public bool CancelOutbox(long id)
    {
        using var c = Open();
        return Exec(c, "UPDATE outbox SET status=4 WHERE id=$id AND status IN (0,2)", ("$id", id)) == 1;
    }

    public void SetOutboxResult(long id, OutboxStatus status, string error = "", DateTimeOffset? retryAt = null)
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET status=$s, last_error=$e, attempts=attempts+1, send_at=COALESCE($r, send_at) WHERE id=$id",
            ("$s", (int)status), ("$e", error), ("$r", retryAt?.ToUnixTimeMilliseconds()), ("$id", id));
    }

    public void RescheduleOutbox(long id, DateTimeOffset sendAt)
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET send_at=$s, status=0 WHERE id=$id AND status IN (0,2)", ("$s", sendAt.ToUnixTimeMilliseconds()), ("$id", id));
    }

    /// <summary>On start-up: an item left "sending" by a crash goes back to the queue.</summary>
    public void RecoverStuckOutbox()
    {
        using var c = Open();
        Exec(c, "UPDATE outbox SET status=2, last_error='Interrupted — will retry' WHERE status=1");
    }

    // ───────────────────────── reminders ─────────────────────────

    public long AddReminder(Reminder rem)
    {
        using var c = Open();
        Exec(c, "UPDATE reminders SET state=2 WHERE account_id=$a AND thread_key=$t AND state=0", ("$a", rem.AccountId), ("$t", rem.ThreadKey));
        rem.Id = Convert.ToInt64(Scalar(c, "INSERT INTO reminders(account_id,thread_key,subject,after,due,state,always) VALUES($a,$t,$s,$af,$d,0,$al) RETURNING id",
            ("$a", rem.AccountId), ("$t", rem.ThreadKey), ("$s", rem.Subject), ("$af", rem.After.ToUnixTimeMilliseconds()),
            ("$d", rem.Due.ToUnixTimeMilliseconds()), ("$al", rem.Always ? 1 : 0)));
        return rem.Id;
    }

    public List<Reminder> GetReminders(ReminderState? state = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,account_id,thread_key,subject,after,due,state,always FROM reminders" + (state is null ? "" : " WHERE state=$s") + " ORDER BY due";
        if (state is { } s) cmd.Parameters.AddWithValue("$s", (int)s);
        var list = new List<Reminder>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Reminder
            {
                Id = r.GetInt64(0), AccountId = r.GetString(1), ThreadKey = r.GetString(2), Subject = r.GetString(3),
                After = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), Due = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
                State = (ReminderState)r.GetInt32(6), Always = r.GetInt32(7) != 0,
            });
        return list;
    }

    public void SetReminderState(long id, ReminderState state)
    {
        using var c = Open();
        Exec(c, "UPDATE reminders SET state=$s WHERE id=$id", ("$s", (int)state), ("$id", id));
    }

    /// <summary>True when someone other than <paramref name="myAddresses"/> wrote in the thread after <paramref name="after"/>.</summary>
    public bool HasReplyAfter(string accountId, string threadKey, DateTimeOffset after, IReadOnlyCollection<string> myAddresses)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = myAddresses.Select((a, i) => { cmd.Parameters.AddWithValue("$me" + i, a.ToLowerInvariant()); return "$me" + i; }).ToList();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE account_id=$a AND thread_key=$t AND date > $d"
                          + (ps.Count > 0 ? $" AND lower(from_addr) NOT IN ({string.Join(',', ps)})" : "");
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", threadKey);
        cmd.Parameters.AddWithValue("$d", after.ToUnixTimeMilliseconds());
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // ───────────────────────── contacts ─────────────────────────

    /// <summary>Records people seen in mail. <paramref name="sentTo"/>: we wrote to them (they become "known contacts" for the smart inbox).</summary>
    public void TouchContacts(IEnumerable<(string addr, string name)> people, DateTimeOffset when, bool sentTo = false)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO contacts(addr,name,count,last,sent) VALUES($a,$n,1,$l,$s)
            ON CONFLICT(addr) DO UPDATE SET count=count+1, last=MAX(last,$l), sent=MAX(sent,$s), name=CASE WHEN $n<>'' THEN $n ELSE name END
            """;
        cmd.Parameters.AddWithValue("$s", sentTo ? 1 : 0);
        var pa = cmd.Parameters.Add("$a", SqliteType.Text);
        var pn = cmd.Parameters.Add("$n", SqliteType.Text);
        cmd.Parameters.AddWithValue("$l", when.ToUnixTimeMilliseconds());
        foreach (var (addr, name) in people)
        {
            if (string.IsNullOrWhiteSpace(addr) || !addr.Contains('@')) continue;
            pa.Value = addr.Trim().ToLowerInvariant();
            pn.Value = name?.Trim().Trim('"') ?? "";
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Moves mail from people we have written to into People, except senders the user categorised. Returns rows changed.</summary>
    public int PromoteKnownSenders(IEnumerable<string> userOverrides)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = userOverrides.Select((a, i) => { cmd.Parameters.AddWithValue("$o" + i, a.ToLowerInvariant()); return "$o" + i; }).ToList();
        cmd.CommandText = "UPDATE messages SET category=0 WHERE category<>0 AND lower(from_addr) IN (SELECT addr FROM contacts WHERE sent=1)"
                          + (ps.Count > 0 ? $" AND lower(from_addr) NOT IN ({string.Join(',', ps)})" : "");
        return cmd.ExecuteNonQuery();
    }

    public HashSet<string> KnownContacts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT addr FROM contacts WHERE sent=1";
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var r = cmd.ExecuteReader();
        while (r.Read()) set.Add(r.GetString(0));
        return set;
    }

    public List<Contact> SearchContacts(string prefix, int limit = 8)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT addr,name,count,last FROM contacts WHERE addr LIKE $p ESCAPE '\\' OR name LIKE $p ESCAPE '\\' OR name LIKE $w ESCAPE '\\' ORDER BY sent DESC, count DESC, last DESC LIMIT $l";
        var esc = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$p", esc + "%");
        cmd.Parameters.AddWithValue("$w", "% " + esc + "%");
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<Contact>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Contact { Address = r.GetString(0), Name = r.GetString(1), Count = r.GetInt32(2), Last = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)) });
        return list;
    }

    // ───────────────────────── AI summary cache ─────────────────────────

    public string? GetSummary(string accountId, string threadKey, string digest)
    {
        using var c = Open();
        return Scalar(c, "SELECT text FROM summaries WHERE account_id=$a AND thread_key=$t AND digest=$d", ("$a", accountId), ("$t", threadKey), ("$d", digest)) as string;
    }

    public void SaveSummary(string accountId, string threadKey, string digest, string text)
    {
        using var c = Open();
        Exec(c, "INSERT OR REPLACE INTO summaries(account_id,thread_key,digest,text,created) VALUES($a,$t,$d,$x,$c)",
            ("$a", accountId), ("$t", threadKey), ("$d", digest), ("$x", text), ("$c", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public void ClearSummary(string accountId, string threadKey)
    {
        using var c = Open();
        Exec(c, "DELETE FROM summaries WHERE account_id=$a AND thread_key=$t", ("$a", accountId), ("$t", threadKey));
    }

    // ───────────────────────── counts ─────────────────────────

    public int CountUnreadThreads(IReadOnlyCollection<long> folderIds, DateTimeOffset now, Category? cat = null)
    {
        if (folderIds.Count == 0) return 0;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        cmd.CommandText = $"""
            SELECT COUNT(*) FROM (
              SELECT account_id, thread_key FROM messages
              WHERE folder_id IN ({string.Join(',', ps)}) AND (flags & 1)=0 AND (snooze_until IS NULL OR snooze_until <= $now)
              {(cat is null ? "" : "AND category=$c")}
              GROUP BY account_id, thread_key)
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        if (cat is { } cc) cmd.Parameters.AddWithValue("$c", (int)cc);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Unread and total conversations in these folders (design C2). A conversation counts once, and is unread
    /// if any of its messages is. Snoozed conversations are left out unless <paramref name="includeSnoozed"/>.
    /// </summary>
    public (int Unread, int Total) CountThreads(IReadOnlyCollection<long> folderIds, DateTimeOffset now, Category? cat = null,
        string? tag = null, bool flaggedOnly = false, bool includeSnoozed = false)
    {
        if (folderIds.Count == 0) return (0, 0);
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        var where = new List<string> { $"folder_id IN ({string.Join(',', ps)})" };
        if (!includeSnoozed) { where.Add("(snooze_until IS NULL OR snooze_until <= $now)"); cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds()); }
        if (cat is { } cc) { where.Add("category=$c"); cmd.Parameters.AddWithValue("$c", (int)cc); }
        if (tag != null) { where.Add("(',' || tags || ',') LIKE $tag"); cmd.Parameters.AddWithValue("$tag", $"%,{tag},%"); }
        if (flaggedOnly) where.Add("(flags & 2)<>0");
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(u), 0) FROM (
              SELECT MAX(CASE WHEN (flags & 1)=0 THEN 1 ELSE 0 END) AS u FROM messages
              WHERE {string.Join(" AND ", where)}
              GROUP BY account_id, thread_key)
            """;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (Convert.ToInt32(r.GetValue(1)), Convert.ToInt32(r.GetValue(0))) : (0, 0);
    }

    /// <summary>
    /// Everything the folder hover card can show (design H2), in one pass over the given folders (or a tag).
    /// Conversations are counted like the sidebar; messages, size and attachments count every email.
    /// </summary>
    public Mail.FolderDetails GetFolderDetails(IReadOnlyCollection<long> folderIds, DateTimeOffset now, string? tag = null, bool flaggedOnly = false, bool snoozedOnly = false, bool setAsideOnly = false)
    {
        var d = new Mail.FolderDetails();
        if (folderIds.Count == 0) return d;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        var where = new List<string> { $"folder_id IN ({string.Join(',', ps)})" };
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$aside", AsideMs);
        cmd.Parameters.AddWithValue("$gate", GateMs);
        where.Add(setAsideOnly ? "snooze_until = $aside"
            : snoozedOnly ? "(snooze_until IS NOT NULL AND snooze_until > $now AND snooze_until < $gate)"
            : "(snooze_until IS NULL OR snooze_until <= $now)");
        if (tag != null) { where.Add("(',' || tags || ',') LIKE $tag"); cmd.Parameters.AddWithValue("$tag", $"%,{tag},%"); }
        if (flaggedOnly) where.Add("(flags & 2)<>0");
        cmd.Parameters.AddWithValue("$day", new DateTimeOffset(now.LocalDateTime.Date, now.Offset).ToUnixTimeMilliseconds());
        cmd.CommandText = $"""
            WITH m AS (SELECT * FROM messages WHERE {string.Join(" AND ", where)}),
            t AS (SELECT MAX(CASE WHEN (flags & 1)=0 THEN 1 ELSE 0 END) AS u, MAX(date) AS d FROM m GROUP BY account_id, thread_key)
            SELECT
              (SELECT COUNT(*) FROM t), (SELECT COALESCE(SUM(u),0) FROM t), (SELECT COUNT(*) FROM t WHERE d >= $day),
              (SELECT MIN(date) FROM m WHERE (flags & 1)=0),
              (SELECT MAX(date) FROM m),
              (SELECT CASE WHEN from_name<>'' THEN from_name ELSE from_addr END FROM m ORDER BY date DESC, id DESC LIMIT 1),
              (SELECT COUNT(*) FROM m), (SELECT COALESCE(SUM(size),0) FROM m), (SELECT COUNT(*) FROM m WHERE has_attach=1)
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return d;
        d.Total = Convert.ToInt32(r.GetValue(0));
        d.Unread = Convert.ToInt32(r.GetValue(1));
        d.Today = Convert.ToInt32(r.GetValue(2));
        d.OldestUnread = r.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3));
        d.LastReceived = r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4));
        d.LastSender = r.IsDBNull(5) ? "" : r.GetString(5);
        d.Messages = Convert.ToInt32(r.GetValue(6));
        d.Size = Convert.ToInt64(r.GetValue(7));
        d.WithAttachments = Convert.ToInt32(r.GetValue(8));
        return d;
    }

    /// <summary>Unread and total conversations per folder, in one pass (snoozed ones left out).</summary>
    public Dictionary<long, (int Unread, int Total)> CountThreadsByFolder(DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT folder_id, COUNT(*), SUM(u) FROM (
              SELECT folder_id, MAX(CASE WHEN (flags & 1)=0 THEN 1 ELSE 0 END) AS u FROM messages
              WHERE snooze_until IS NULL OR snooze_until <= $now
              GROUP BY folder_id, account_id, thread_key)
            GROUP BY folder_id
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        var map = new Dictionary<long, (int, int)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetInt64(0)] = (Convert.ToInt32(r.GetValue(2)), Convert.ToInt32(r.GetValue(1)));
        return map;
    }

    /// <summary>Unread and total conversations per tag (one pass; a conversation counts once per tag).</summary>
    public Dictionary<string, (int Unread, int Total)> CountThreadsByTag(IReadOnlyCollection<long> folderIds, DateTimeOffset now)
    {
        var map = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        if (folderIds.Count == 0) return map;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        cmd.CommandText = $"""
            SELECT GROUP_CONCAT(tags, ','), MAX(CASE WHEN (flags & 1)=0 THEN 1 ELSE 0 END) FROM messages
            WHERE tags <> '' AND folder_id IN ({string.Join(',', ps)}) AND (snooze_until IS NULL OR snooze_until <= $now)
            GROUP BY account_id, thread_key
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var unread = Convert.ToInt32(r.GetValue(1));
            foreach (var tag in r.GetString(0).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var (u, t) = map.TryGetValue(tag, out var x) ? x : (0, 0);
                map[tag] = (u + unread, t + 1);
            }
        }
        return map;
    }

    /// <summary>Unread and total conversations per smart-inbox category, by each conversation's latest message
    /// (the same rule the list uses).</summary>
    public Dictionary<Category, (int Unread, int Total)> CountThreadsByCategory(IReadOnlyCollection<long> folderIds, DateTimeOffset now)
    {
        var map = new Dictionary<Category, (int, int)>();
        if (folderIds.Count == 0) return map;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        cmd.CommandText = $"""
            SELECT category, 0, u FROM (
              SELECT category,
                     MAX(CASE WHEN (flags & 1)=0 THEN 1 ELSE 0 END) OVER (PARTITION BY account_id, thread_key) AS u,
                     ROW_NUMBER() OVER (PARTITION BY account_id, thread_key ORDER BY sort_date DESC, id DESC) AS rn
              FROM messages
              WHERE folder_id IN ({string.Join(',', ps)}) AND (snooze_until IS NULL OR snooze_until <= $now))
            WHERE rn = 1
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var cat = (Category)r.GetInt32(0);
            var (u, t) = map.TryGetValue(cat, out var x) ? x : (0, 0);
            map[cat] = (u + Convert.ToInt32(r.GetValue(2)), t + 1);
        }
        return map;
    }

    public int CountSnoozedThreads(DateTimeOffset now)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(DISTINCT account_id || '|' || thread_key) FROM messages WHERE snooze_until > $n AND snooze_until < $g",
            ("$n", now.ToUnixTimeMilliseconds()), ("$g", GateMs)));
    }

    public int CountSetAsideThreads()
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(DISTINCT account_id || '|' || thread_key) FROM messages WHERE snooze_until = $a", ("$a", AsideMs)));
    }

    /// <summary>Set aside is a snooze that never wakes (see <see cref="MessageRow.SetAsideMark"/>).</summary>
    private static readonly long AsideMs = MessageRow.SetAsideMark.ToUnixTimeMilliseconds();
    private static readonly long GateMs = MessageRow.GateMark.ToUnixTimeMilliseconds();

    // ───────────────────────── events from invites (design B3) ─────────────────────────

    public sealed record LocalEvent(string AccountId, string Uid, string Summary, DateTimeOffset Start, DateTimeOffset End, bool AllDay,
        string Location, string Organizer, string Answer, int Sequence, bool Cancelled);

    public LocalEvent? GetEvent(string accountId, string uid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT account_id,uid,summary,start,end,all_day,location,organizer,answer,sequence,cancelled FROM events WHERE account_id=$a AND uid=$u";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$u", uid);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadEvent(r) : null;
    }

    private static LocalEvent ReadEvent(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), r.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), r.GetInt32(5) != 0,
        r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt32(9), r.GetInt32(10) != 0);

    public void SaveEvent(LocalEvent e)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO events(account_id,uid,summary,start,end,all_day,location,organizer,answer,sequence,cancelled)
            VALUES($a,$u,$s,$st,$en,$ad,$l,$o,$an,$sq,$cx)
            ON CONFLICT(account_id,uid) DO UPDATE SET summary=$s, start=$st, end=$en, all_day=$ad, location=$l, organizer=$o, answer=$an, sequence=$sq, cancelled=$cx
            """, ("$a", e.AccountId), ("$u", e.Uid), ("$s", e.Summary), ("$st", e.Start.ToUnixTimeMilliseconds()), ("$en", e.End.ToUnixTimeMilliseconds()),
            ("$ad", e.AllDay ? 1 : 0), ("$l", e.Location), ("$o", e.Organizer), ("$an", e.Answer), ("$sq", e.Sequence), ("$cx", e.Cancelled ? 1 : 0));
    }

    /// <summary>Events you said yes or maybe to that overlap this time (the invite card's clash line).</summary>
    public List<LocalEvent> EventsOverlapping(DateTimeOffset start, DateTimeOffset end, string exceptUid)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT account_id,uid,summary,start,end,all_day,location,organizer,answer,sequence,cancelled FROM events
            WHERE cancelled=0 AND answer IN ('ACCEPTED','TENTATIVE') AND uid<>$x AND start < $e AND end > $s AND all_day=0 ORDER BY start
            """;
        cmd.Parameters.AddWithValue("$s", start.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$e", end.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$x", exceptUid);
        var list = new List<LocalEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadEvent(r));
        return list;
    }

    // ───────────────────────── auto-delete (designs AD1–AD4) ─────────────────────────

    public List<Mail.AutoDeleteRule> GetAutoDeleteRules()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,pattern,account_id,otp,amount,unit,paused,created FROM auto_delete_rules ORDER BY created";
        var list = new List<Mail.AutoDeleteRule>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Mail.AutoDeleteRule
            {
                Id = r.GetString(0), Pattern = r.GetString(1), AccountId = r.GetString(2), Otp = r.GetInt32(3) != 0, Amount = r.GetInt32(4),
                Unit = (Mail.DeleteUnit)r.GetInt32(5), Paused = r.GetInt32(6) != 0, Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)),
            });
        return list;
    }

    public void SaveAutoDeleteRule(Mail.AutoDeleteRule rule)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO auto_delete_rules(id,pattern,account_id,otp,amount,unit,paused,created) VALUES($id,$p,$a,$o,$n,$u,$z,$c)
            ON CONFLICT(id) DO UPDATE SET pattern=$p, account_id=$a, otp=$o, amount=$n, unit=$u, paused=$z
            """, ("$id", rule.Id), ("$p", rule.Pattern), ("$a", rule.AccountId), ("$o", rule.Otp ? 1 : 0), ("$n", rule.Amount), ("$u", (int)rule.Unit),
            ("$z", rule.Paused ? 1 : 0), ("$c", rule.Created.ToUnixTimeMilliseconds()));
    }

    /// <summary>Emails timed by one rule now belong to another (two rules for one sender become one).</summary>
    public void MoveDeleteTimers(string fromRuleId, string toRuleId)
    {
        using var c = Open();
        Exec(c, "UPDATE messages SET delete_rule=$to WHERE delete_rule=$from", ("$to", toRuleId), ("$from", fromRuleId));
    }

    /// <summary>Removes a rule; <paramref name="clearTimers"/> also takes its timers off the emails that carry them.</summary>
    public void DeleteAutoDeleteRule(string id, bool clearTimers)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM auto_delete_rules WHERE id=$id", ("$id", id));
        if (clearTimers) Exec(c, "UPDATE messages SET delete_at=NULL, delete_rule='' WHERE delete_rule=$id", ("$id", id));
        tx.Commit();
    }

    /// <summary>Puts a timer on each row (keeps an earlier one already there). Rows kept by hand ("-") are left alone.</summary>
    public int SetDeleteTimers(IEnumerable<(long RowId, DateTimeOffset At)> rows, string ruleId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var n = 0;
        foreach (var (id, at) in rows)
            n += Exec(c, "UPDATE messages SET delete_at=$t, delete_rule=$r WHERE id=$id AND delete_rule<>'-' AND (delete_at IS NULL OR delete_at > $t)",
                ("$t", at.ToUnixTimeMilliseconds()), ("$r", ruleId), ("$id", id));
        tx.Commit();
        return n;
    }

    /// <summary>"Keep this one" / "Keep all": the timer comes off and never comes back on these rows.</summary>
    public void KeepRows(IEnumerable<long> rowIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in rowIds) Exec(c, "UPDATE messages SET delete_at=NULL, delete_rule='-' WHERE id=$id AND delete_at IS NOT NULL", ("$id", id));
        tx.Commit();
    }

    /// <summary>Rows whose time has come: not pinned, and their rule isn't paused.</summary>
    public List<MessageRow> DueDeletes(DateTimeOffset now)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {MsgCols} FROM messages m LEFT JOIN auto_delete_rules r ON r.id=m.delete_rule
            WHERE m.delete_at IS NOT NULL AND m.delete_at <= $now AND (m.flags & 2)=0 AND COALESCE(r.paused,0)=0
            """;
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        var list = new List<MessageRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    /// <summary>Per rule: emails waiting and the next delete (the AD4 table).</summary>
    public Dictionary<string, (int Waiting, DateTimeOffset? Next)> AutoDeleteStats()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT delete_rule, COUNT(*), MIN(delete_at) FROM messages WHERE delete_at IS NOT NULL AND (flags & 2)=0 GROUP BY delete_rule";
        var map = new Dictionary<string, (int, DateTimeOffset?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) map[r.GetString(0)] = (r.GetInt32(1), r.IsDBNull(2) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)));
        return map;
    }

    /// <summary>Conversations with a timer before <paramref name="until"/> ("Deleting soon").</summary>
    public int CountDeletingSoon(DateTimeOffset until)
    {
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(DISTINCT account_id || '|' || thread_key) FROM messages WHERE delete_at IS NOT NULL AND delete_at <= $u AND (flags & 2)=0 AND delete_rule NOT IN (SELECT id FROM auto_delete_rules WHERE paused=1)",
            ("$u", until.ToUnixTimeMilliseconds())));
    }

    /// <summary>Rows of a conversation that carry a timer (reader bar, Keep this one).</summary>
    public List<(long Id, DateTimeOffset At, string Rule)> DeleteTimers(string accountId, string threadKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, delete_at, delete_rule FROM messages WHERE account_id=$a AND thread_key=$t AND delete_at IS NOT NULL AND (flags & 2)=0 ORDER BY delete_at";
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", threadKey);
        var list = new List<(long, DateTimeOffset, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2)));
        return list;
    }

    /// <summary>Rows with a timer before <paramref name="until"/> ("Keep all" in Deleting soon).</summary>
    public List<long> RowsDeletingBefore(DateTimeOffset until)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM messages WHERE delete_at IS NOT NULL AND delete_at <= $u AND (flags & 2)=0 AND delete_rule NOT IN (SELECT id FROM auto_delete_rules WHERE paused=1)";
        cmd.Parameters.AddWithValue("$u", until.ToUnixTimeMilliseconds());
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Messages in these folders from an address or "*@domain" (existing mail for a new auto-delete rule).</summary>
    public List<MessageRow> MessagesFrom(IReadOnlyCollection<long> folderIds, string pattern)
    {
        var list = new List<MessageRow>();
        if (folderIds.Count == 0) return list;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ps = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        var p = pattern.Trim().ToLowerInvariant();
        string match;
        if (p.StartsWith("*@", StringComparison.Ordinal))
        {
            match = "lower(m.from_addr) LIKE $p ESCAPE '\\'";
            cmd.Parameters.AddWithValue("$p", "%" + p[1..].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
        }
        else { match = "lower(m.from_addr)=$p"; cmd.Parameters.AddWithValue("$p", p); }
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.folder_id IN ({string.Join(',', ps)}) AND {match}";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    // ───────────────────────── Gatekeeper (design B7) ─────────────────────────

    /// <summary>Keeps these rows out of the Inbox until their sender is allowed (true) or lets them in (false, on top).</summary>
    public void SetAtGate(IEnumerable<long> rowIds, bool atGate, DateTimeOffset now)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in rowIds)
            Exec(c, atGate ? "UPDATE messages SET snooze_until=$g WHERE id=$id" : "UPDATE messages SET snooze_until=NULL, sort_date=MAX(sort_date,$now) WHERE id=$id AND snooze_until=$g",
                ("$g", GateMs), ("$id", id), ("$now", now.ToUnixTimeMilliseconds()));
        tx.Commit();
    }

    /// <summary>Senders waiting at the door: address, name, first subject, how many emails, newest first.</summary>
    public List<Mail.GateSender> GateSenders()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT lower(from_addr), MAX(from_name), COUNT(*), MAX(date),
                   (SELECT subject FROM messages x WHERE lower(x.from_addr)=lower(m.from_addr) AND x.snooze_until=$g ORDER BY x.date LIMIT 1)
            FROM messages m WHERE snooze_until=$g GROUP BY lower(from_addr) ORDER BY MAX(date) DESC
            """;
        cmd.Parameters.AddWithValue("$g", GateMs);
        var list = new List<Mail.GateSender>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Mail.GateSender(r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1), r.IsDBNull(4) ? "" : r.GetString(4), r.GetInt32(2),
                DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3))));
        return list;
    }

    public List<MessageRow> GateMessages(string? fromAddress = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.snooze_until=$g" + (fromAddress == null ? "" : " AND lower(m.from_addr)=lower($a)");
        cmd.Parameters.AddWithValue("$g", GateMs);
        if (fromAddress != null) cmd.Parameters.AddWithValue("$a", fromAddress);
        var list = new List<MessageRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    /// <summary>
    /// True when we already have mail from this address — not counting these rows, other copies of the same emails
    /// (Gmail's All Mail), mail still waiting at the door (or its copies), or mail in Spam / Trash.
    /// </summary>
    public bool HasMailFrom(string address, IReadOnlyCollection<long> exceptIds, IReadOnlyCollection<string> exceptMessageIds)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ex = exceptIds.Count == 0 ? "" : $" AND m.id NOT IN ({string.Join(",", exceptIds.Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)))})";
        var mids = exceptMessageIds.Where(m => m.Length > 0).Distinct().Select((m, i) => { cmd.Parameters.AddWithValue("$m" + i, m); return "$m" + i; }).ToList();
        if (mids.Count > 0) ex += $" AND m.message_id NOT IN ({string.Join(",", mids)})";
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM messages m JOIN folders f ON f.id=m.folder_id WHERE lower(m.from_addr)=lower($a)"
            + " AND (m.snooze_until IS NULL OR m.snooze_until<>$g) AND f.role NOT IN ($junk,$trash)"
            + " AND (m.message_id='' OR m.message_id NOT IN (SELECT message_id FROM messages WHERE snooze_until=$g AND message_id<>''))"
            + ex + ")";
        cmd.Parameters.AddWithValue("$junk", (int)FolderRole.Junk);
        cmd.Parameters.AddWithValue("$trash", (int)FolderRole.Trash);
        cmd.Parameters.AddWithValue("$a", address);
        cmd.Parameters.AddWithValue("$g", GateMs);
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    // ───────────────────────── helpers ─────────────────────────

    private static int Exec(SqliteConnection c, string sql, params (string name, object? value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection c, string sql, params (string name, object? value)[] ps)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }
}
