using Magpie.Core.Mail;
using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    public const int MaxReplyArchiveSources = 5000;
    public const int ReplyArchiveSourceRetentionDays = 180;

    private void EnsureReplyArchiveSourceSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS reply_archive_sources(
              message_id TEXT PRIMARY KEY, account_id TEXT NOT NULL, thread_key TEXT NOT NULL,
              updated INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_reply_archive_source_updated ON reply_archive_sources(updated);
            """);
        PruneReplyArchiveSources(c, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Keep the selected source locally, never in delivered MIME. A Message-ID cannot be silently retargeted.
    /// The bounded cache supports server-draft reopen on this PC; expired/evicted context disables Send & Archive.
    /// Queued actions keep their own durable source in post_send_archives and do not depend on this cache.
    /// </summary>
    public bool RememberReplyArchiveSource(string messageId, ReplyArchiveSource source)
    {
        messageId = Threading.NormalizeId(messageId);
        if (!ValidReplyArchiveMessageId(messageId) || !source.IsValid) return false;
        using var c = Open();
        using var tx = c.BeginTransaction();
        var now = DateTimeOffset.UtcNow;
        // Never prune a conflicting old entry before testing it: expiry is not permission to retarget a Message-ID.
        var changed = Exec(c, """
            INSERT INTO reply_archive_sources(message_id,account_id,thread_key,updated) VALUES($m,$a,$t,$u)
            ON CONFLICT(message_id) DO UPDATE SET updated=$u
              WHERE account_id=$a AND thread_key=$t
            """, ("$m", messageId), ("$a", source.AccountId), ("$t", source.ThreadKey), ("$u", now.ToUnixTimeMilliseconds()));
        PruneReplyArchiveSources(c, now);
        tx.Commit();
        return changed == 1;
    }

    public ReplyArchiveSource? GetReplyArchiveSource(string messageId)
    {
        messageId = Threading.NormalizeId(messageId);
        if (!ValidReplyArchiveMessageId(messageId)) return null;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT account_id,thread_key FROM reply_archive_sources WHERE message_id=$m AND updated >= $cutoff";
        cmd.Parameters.AddWithValue("$m", messageId);
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-ReplyArchiveSourceRetentionDays).ToUnixTimeMilliseconds());
        using var row = cmd.ExecuteReader();
        if (!row.Read()) return null;
        var source = new ReplyArchiveSource(row.GetString(0), row.GetString(1));
        return source.IsValid ? source : null;
    }

    private static bool ValidReplyArchiveMessageId(string messageId) => !string.IsNullOrWhiteSpace(messageId)
        && messageId.Length <= 1024 && !messageId.Any(char.IsControl);

    private static void PruneReplyArchiveSources(SqliteConnection c, DateTimeOffset now)
    {
        Exec(c, "DELETE FROM reply_archive_sources WHERE updated < $cutoff",
            ("$cutoff", now.AddDays(-ReplyArchiveSourceRetentionDays).ToUnixTimeMilliseconds()));
        Exec(c, """
            DELETE FROM reply_archive_sources WHERE message_id IN
              (SELECT message_id FROM reply_archive_sources ORDER BY updated DESC,message_id DESC LIMIT -1 OFFSET $max)
            """, ("$max", MaxReplyArchiveSources));
    }
}
