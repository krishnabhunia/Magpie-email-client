using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    private void EnsureSecureOutboxSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS outbox_secure_state(
              outbox_id INTEGER PRIMARY KEY REFERENCES outbox(id) ON DELETE CASCADE,
              protected_recall BLOB NOT NULL);
            DELETE FROM outbox_secure_state WHERE outbox_id NOT IN (SELECT id FROM outbox WHERE status IN (0,1,2));
            """);
    }

    private static void SaveSecureOutboxState(SqliteConnection c, SqliteTransaction tx, OutboxItem item)
    {
        if (item.ProtectedSecurityRecall == null) return;
        using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO outbox_secure_state(outbox_id,protected_recall) VALUES($id,$p)";
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$p", item.ProtectedSecurityRecall);
        command.ExecuteNonQuery();
    }

    public byte[]? SecureOutboxState(long outboxId)
    {
        using var c = Open();
        using var command = c.CreateCommand();
        command.CommandText = "SELECT protected_recall FROM outbox_secure_state WHERE outbox_id=$id";
        command.Parameters.AddWithValue("$id", outboxId);
        return command.ExecuteScalar() as byte[];
    }

    public void ClearSecureOutboxState(long outboxId)
    {
        using var c = Open();
        using var command = c.CreateCommand();
        command.CommandText = "DELETE FROM outbox_secure_state WHERE outbox_id=$id";
        command.Parameters.AddWithValue("$id", outboxId);
        command.ExecuteNonQuery();
    }
}
