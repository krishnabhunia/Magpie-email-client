using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>Design TB1: where a conversation was before it went to Trash (Restore), and since when it is there (auto-empty).</summary>
public sealed partial class MailStore
{
    private static void MigrateTrash(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS trash_from(
              account_id TEXT NOT NULL, message_id TEXT NOT NULL, folder_id INTEGER NOT NULL DEFAULT 0, at INTEGER NOT NULL,
              PRIMARY KEY(account_id, message_id));
            """);
    }

    /// <summary>These emails are going to Trash now, out of their folder (Restore puts them back there).</summary>
    public void RecordTrashed(IEnumerable<MessageRow> rows, DateTimeOffset at)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var m in rows.Where(m => m.MessageId.Length > 0))
            Exec(c, "INSERT OR REPLACE INTO trash_from(account_id,message_id,folder_id,at) VALUES($a,$m,$f,$t)",
                ("$a", m.AccountId), ("$m", m.MessageId), ("$f", m.FolderId), ("$t", at.ToUnixTimeMilliseconds()));
        tx.Commit();
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
