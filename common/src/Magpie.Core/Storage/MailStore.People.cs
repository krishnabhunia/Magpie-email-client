using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>Design HM1: what the hover card says about a person.</summary>
public sealed partial class MailStore
{
    /// <summary>How many emails this address sent (a copy in two folders counts once) and when the last one came.</summary>
    public (int Count, DateTimeOffset? Last) AddressStats(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return (0, null);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(DISTINCT CASE WHEN message_id <> '' THEN message_id ELSE CAST(id AS TEXT) END), MAX(date)
            FROM messages WHERE from_addr = $a COLLATE NOCASE
            """;
        cmd.Parameters.AddWithValue("$a", address.Trim());
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, null);
        var count = r.GetInt32(0);
        return (count, r.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)));
    }
}
