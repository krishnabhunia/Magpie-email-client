using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>
/// Design AL1: an email that Magpie's auto delete moved to Trash — when, from which folder, and the rule as it read
/// at that moment (the rule may be changed or removed later; the note keeps what was true then).
/// </summary>
public sealed record TrashNote(DateTimeOffset At, string RuleId, string RuleLine, long FolderId);

/// <summary>Design AL1 (option C): which conversations of Trash a list shows.</summary>
public enum TrashReason { Any = 0, AutoDeleted = 1, NotAutoDeleted = 2 }

/// <summary>Design TB1: where a conversation was before it went to Trash (Restore), and since when it is there (auto-empty).</summary>
public sealed partial class MailStore
{
    /// <summary>The reason written for an email that auto delete moved to Trash (design AL1); "" = by hand or elsewhere.</summary>
    public const string AutoDeleted = "auto";

    private static void MigrateTrash(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS trash_from(
              account_id TEXT NOT NULL, message_id TEXT NOT NULL, folder_id INTEGER NOT NULL DEFAULT 0, at INTEGER NOT NULL,
              PRIMARY KEY(account_id, message_id));
            """);
        EnsureTrashReason(c);
    }

    /// <summary>
    /// Design AL1: why an email went to Trash and under which rule. Checked on every start, not tied to a schema
    /// number (as with <c>messages.bcc_list</c>), so an update needs no new compatibility entry.
    /// </summary>
    private static void EnsureTrashReason(SqliteConnection c)
    {
        // Emails the user took back out of Trash after auto delete put them there: they get no timer again.
        Exec(c, """
            CREATE TABLE IF NOT EXISTS auto_delete_kept(
              account_id TEXT NOT NULL, message_id TEXT NOT NULL, at INTEGER NOT NULL,
              PRIMARY KEY(account_id, message_id));
            """);
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='trash_from'")) == 0) return;
        foreach (var column in new[] { "reason", "rule_id", "rule_line" })
            if (Convert.ToInt32(Scalar(c, $"SELECT COUNT(*) FROM pragma_table_info('trash_from') WHERE name='{column}'")) == 0)
                Exec(c, $"ALTER TABLE trash_from ADD COLUMN {column} TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// These emails are going to Trash now, out of their folder (Restore puts them back there).
    /// <paramref name="autoRule"/> (design AL1): auto delete is moving them — it gives each email's rule id and the
    /// rule in words, ("", "") when the rule is gone. Without it the email was deleted by hand, which also clears an
    /// older auto-delete note of the same email.
    /// </summary>
    public void RecordTrashed(IEnumerable<MessageRow> rows, DateTimeOffset at, Func<MessageRow, (string RuleId, string RuleLine)>? autoRule = null)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var m in rows.Where(m => m.MessageId.Length > 0))
        {
            var (ruleId, ruleLine) = autoRule?.Invoke(m) ?? ("", "");
            Exec(c, "INSERT OR REPLACE INTO trash_from(account_id,message_id,folder_id,at,reason,rule_id,rule_line) VALUES($a,$m,$f,$t,$r,$ri,$rl)",
                ("$a", m.AccountId), ("$m", m.MessageId), ("$f", m.FolderId), ("$t", at.ToUnixTimeMilliseconds()),
                ("$r", autoRule == null ? "" : AutoDeleted), ("$ri", ruleId), ("$rl", ruleLine));
        }
        tx.Commit();
    }

    /// <summary>
    /// Design AL1: these emails were restored from Trash after auto delete had put them there. No rule puts a timer on
    /// them again (<see cref="SetDeleteTimers"/>) — without this the restored copy, older than its rule allows, would
    /// be due at once and go straight back to Trash.
    /// </summary>
    public void KeepRestored(string accountId, IEnumerable<string> messageIds, DateTimeOffset at)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in messageIds.Where(i => !string.IsNullOrEmpty(i)).Distinct())
            Exec(c, "INSERT OR REPLACE INTO auto_delete_kept(account_id,message_id,at) VALUES($a,$m,$t)",
                ("$a", accountId), ("$m", id), ("$t", at.ToUnixTimeMilliseconds()));
        tx.Commit();
    }

    /// <summary>True when this email was restored after an auto delete and is therefore kept from every rule.</summary>
    public bool IsKeptRestored(string accountId, string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return false;
        using var c = Open();
        return Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM auto_delete_kept WHERE account_id=$a AND message_id=$m", ("$a", accountId), ("$m", messageId))) > 0;
    }

    /// <summary>Design AL1: the auto-delete note of each of these emails (by Message-ID); others are not in the result.</summary>
    public Dictionary<string, TrashNote> TrashNotes(string accountId, IEnumerable<string> messageIds)
    {
        var notes = new Dictionary<string, TrashNote>(StringComparer.Ordinal);
        using var c = Open();
        foreach (var id in messageIds.Where(i => !string.IsNullOrEmpty(i)).Distinct())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT at, rule_id, rule_line, folder_id FROM trash_from WHERE account_id=$a AND message_id=$m AND reason=$r";
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$m", id);
            cmd.Parameters.AddWithValue("$r", AutoDeleted);
            using var r = cmd.ExecuteReader();
            if (r.Read()) notes[id] = new TrashNote(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), r.GetString(1), r.GetString(2), r.GetInt64(3));
        }
        return notes;
    }

    /// <summary>
    /// Design AL1: per conversation in these (Trash) folders, its latest auto-deleted email's note and how many of its
    /// emails auto delete put there. Key = (account id, conversation key).
    /// </summary>
    public Dictionary<(string AccountId, string ThreadKey), (TrashNote Latest, int Count)> AutoDeletedThreads(IReadOnlyCollection<long> folderIds)
    {
        var found = new Dictionary<(string, string), (TrashNote, int)>();
        if (folderIds.Count == 0) return found;
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ids = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        cmd.CommandText = $"""
            SELECT m.account_id, m.thread_key, t.at, t.rule_id, t.rule_line, t.folder_id
            FROM messages m JOIN trash_from t ON t.account_id=m.account_id AND t.message_id=m.message_id
            WHERE m.folder_id IN ({string.Join(',', ids)}) AND m.message_id<>'' AND t.reason=$r
            ORDER BY t.at DESC
            """;
        cmd.Parameters.AddWithValue("$r", AutoDeleted);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = (r.GetString(0), r.GetString(1));
            if (found.TryGetValue(key, out var have)) found[key] = (have.Item1, have.Item2 + 1);
            else found[key] = (new TrashNote(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2)), r.GetString(3), r.GetString(4), r.GetInt64(5)), 1);
        }
        return found;
    }

    /// <summary>Design AL1 (option C): conversations in these (Trash) folders — all of them, and those auto delete put there.</summary>
    public (int All, int AutoDeleted) TrashCounts(IReadOnlyCollection<long> folderIds)
    {
        if (folderIds.Count == 0) return (0, 0);
        using var c = Open();
        using var cmd = c.CreateCommand();
        var ids = folderIds.Select((id, i) => { cmd.Parameters.AddWithValue("$f" + i, id); return "$f" + i; });
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(auto), 0) FROM (
              SELECT MAX(CASE WHEN t.reason=$r THEN 1 ELSE 0 END) AS auto
              FROM messages m LEFT JOIN trash_from t ON t.account_id=m.account_id AND t.message_id=m.message_id AND m.message_id<>''
              WHERE m.folder_id IN ({string.Join(',', ids)})
              GROUP BY m.account_id, m.thread_key)
            """;
        cmd.Parameters.AddWithValue("$r", AutoDeleted);
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt32(0), r.GetInt32(1)) : (0, 0);
    }

    /// <summary>
    /// The list filter of design AL1 (option C) for the message alias <paramref name="m"/>: a conversation counts as
    /// auto-deleted when any of its emails in that folder was, so "all" is always the two other choices together.
    /// </summary>
    internal static string TrashReasonSql(TrashReason reason, string m)
    {
        if (reason == TrashReason.Any) return "1=1";
        var any = $"EXISTS (SELECT 1 FROM messages tx JOIN trash_from tr ON tr.account_id=tx.account_id AND tr.message_id=tx.message_id " +
                  $"WHERE tx.account_id={m}.account_id AND tx.thread_key={m}.thread_key AND tx.folder_id={m}.folder_id AND tx.message_id<>'' AND tr.reason='{AutoDeleted}')";
        return reason == TrashReason.AutoDeleted ? any : "NOT " + any;
    }

    /// <summary>The folder an email was in before Trash, when Magpie moved it there (0 / null = not known).</summary>
    public long? TrashOrigin(string accountId, string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return null;
        using var c = Open();
        var v = Scalar(c, "SELECT folder_id FROM trash_from WHERE account_id=$a AND message_id=$m", ("$a", accountId), ("$m", messageId));
        return v is null or DBNull || Convert.ToInt64(v) == 0 ? null : Convert.ToInt64(v);
    }

    public void ForgetTrashed(string accountId, IEnumerable<string> messageIds)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var id in messageIds.Where(i => i.Length > 0))
            Exec(c, "DELETE FROM trash_from WHERE account_id=$a AND message_id=$m", ("$a", accountId), ("$m", id));
        tx.Commit();
    }

    /// <summary>Emails in Trash that Magpie hasn't seen there before get today's date (put there elsewhere, e.g. on the phone).</summary>
    public void StampTrash(long trashFolderId, DateTimeOffset now)
    {
        using var c = Open();
        Exec(c, """
            INSERT OR IGNORE INTO trash_from(account_id,message_id,folder_id,at)
            SELECT account_id, message_id, 0, $t FROM messages WHERE folder_id=$f AND message_id<>''
            """, ("$f", trashFolderId), ("$t", now.ToUnixTimeMilliseconds()));
    }

    /// <summary>Emails that have been in Trash since before <paramref name="before"/>.</summary>
    public List<MessageRow> TrashedBefore(long trashFolderId, DateTimeOffset before)
    {
        var list = new List<MessageRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m JOIN trash_from t ON t.account_id=m.account_id AND t.message_id=m.message_id " +
                          "WHERE m.folder_id=$f AND t.at < $b";
        cmd.Parameters.AddWithValue("$f", trashFolderId);
        cmd.Parameters.AddWithValue("$b", before.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    /// <summary>Every local copy of one email (the same Message-ID in any folder of the account — Gmail labels, All Mail).
    /// A row without a Message-ID is only itself (design DX1).</summary>
    public List<MessageRow> CopiesOf(MessageRow m)
    {
        var list = new List<MessageRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        if (m.MessageId.Length == 0)
        {
            cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.id=$id";
            cmd.Parameters.AddWithValue("$id", m.Id);
        }
        else
        {
            cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.account_id=$a AND m.message_id=$m";
            cmd.Parameters.AddWithValue("$a", m.AccountId);
            cmd.Parameters.AddWithValue("$m", m.MessageId);
        }
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }

    /// <summary>Every email of a folder (no limit, snoozed ones too).</summary>
    public List<MessageRow> MessagesInFolder(long folderId)
    {
        var list = new List<MessageRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {MsgCols} FROM messages m WHERE m.folder_id=$f";
        cmd.Parameters.AddWithValue("$f", folderId);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadMsg(r));
        return list;
    }
}
