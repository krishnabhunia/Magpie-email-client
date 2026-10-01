using Magpie.Core.Models;
using Magpie.Core.Mail;
using System.Text.Json;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    public void DeferRules(long messageId, bool notify, IReadOnlyList<MailRule> rules)
    {
        using var c = Open();
        Exec(c, """
            INSERT INTO pending_rules(message_row,notify,rules) VALUES($id,$n,$r)
            ON CONFLICT(message_row) DO UPDATE SET notify=MAX(notify,excluded.notify),rules=excluded.rules
            """, ("$id", messageId), ("$n", notify ? 1 : 0), ("$r", JsonSerializer.Serialize(rules)));
    }

    public List<(MessageRow Row, bool Notify, List<MailRule> Rules)> DeferredRules(long? messageId = null, long afterMessageId = 0)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT message_row,notify,rules FROM pending_rules WHERE ($id IS NULL OR message_row=$id) ORDER BY CASE WHEN message_row>$after THEN 0 ELSE 1 END,message_row LIMIT 20";
        cmd.Parameters.AddWithValue("$id", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$after", afterMessageId);
        var rows = new List<(long Id, bool Notify, List<MailRule> Rules)>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetInt64(1) != 0,
                JsonSerializer.Deserialize<List<MailRule>>(reader.GetString(2)) ?? new()));
        return rows.Select(r => (Row: GetMessage(r.Id), r.Notify, r.Rules)).Where(r => r.Row != null)
            .Select(r => (r.Row!, r.Notify, r.Rules)).ToList();
    }

    public void CompleteDeferredRules(long messageId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM pending_rules WHERE message_row=$id", ("$id", messageId));
    }
}
