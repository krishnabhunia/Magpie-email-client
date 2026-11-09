using Magpie.Core.Mail;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    private void EnsureDeliveryReceiptSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS outbox_delivery_receipts(
              outbox_id INTEGER PRIMARY KEY REFERENCES outbox(id) ON DELETE CASCADE,
              state INTEGER NOT NULL DEFAULT 0, server_response TEXT NOT NULL DEFAULT '');
            """);
    }

    /// <summary>Local send records for this conversation; never trust headers in mail received from someone else.</summary>
    public Dictionary<string, DeliveryReceiptResult> DeliveryReceiptsForThread(string accountId, string threadKey)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT o.message_id,d.state,d.server_response FROM outbox o
            JOIN outbox_delivery_receipts d ON d.outbox_id=o.id
            WHERE o.account_id=$a AND o.status=3 AND (o.thread_key=$t OR EXISTS (
              SELECT 1 FROM messages m WHERE m.account_id=o.account_id
              AND m.message_id=o.message_id AND m.thread_key=$t)) ORDER BY o.id
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$t", threadKey);
        var results = new Dictionary<string, DeliveryReceiptResult>(StringComparer.Ordinal);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var id = Threading.NormalizeId(r.GetString(0));
            if (id.Length == 0) continue;
            results[id] = new DeliveryReceiptResult((DeliveryReceiptState)r.GetInt32(1), DeliveryReceipts.SafeServerResponse(r.GetString(2)));
        }
        return results;
    }
}
