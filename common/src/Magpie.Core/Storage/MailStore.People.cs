using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>Design PE1: how many emails there are with one address, by the part it plays.</summary>
public sealed record PersonStats(int All, int From, int YouSent, int CopiedIn, DateTimeOffset? Last)
{
    public static readonly PersonStats None = new(0, 0, 0, 0, null);
    public int Of(PersonKind kind) => kind switch { PersonKind.From => From, PersonKind.YouSent => YouSent, PersonKind.CopiedIn => CopiedIn, _ => All };
}

/// <summary>Design PE1: one email of the "emails with one address" list.</summary>
public sealed class PersonEmail
{
    public MessageRow Row { get; init; } = new();
    /// <summary>From, YouSent or CopiedIn (never All).</summary>
    public PersonKind Kind { get; init; }
    public FolderRole FolderRole { get; init; }
    public string FolderPath { get; init; } = "";
}

/// <summary>Designs HM1 / PE1 / RC1: what Magpie knows about a person's emails, and the Bcc line of sent emails.</summary>
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

    /// <summary>Design RC1: added on every start when missing (not tied to the schema number). NULL = not read yet.</summary>
    private static void EnsureBccColumn(SqliteConnection c)
    {
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name='bcc_list'")) == 0)
            Exec(c, "ALTER TABLE messages ADD COLUMN bcc_list TEXT NULL");
    }

    /// <summary>Folders the person views look in: everything mirrored except Drafts, and Trash / Spam unless asked for.</summary>
    private static string PersonFolders(bool includeBin) =>
        $"fo.synced=1 AND fo.role<>{(int)FolderRole.Drafts}" + (includeBin ? "" : $" AND fo.role NOT IN ({(int)FolderRole.Trash},{(int)FolderRole.Junk})");

    private const string EmailKey = "CASE WHEN m.message_id='' THEN 'id:' || m.id ELSE m.message_id END";

    /// <summary>
    /// Design PE1: the numbers on the address card and the list's filters. An email kept in several folders counts
    /// once; snoozed and read emails count like any other. <paramref name="mine"/> = your own accounts' addresses.
    /// </summary>
    public PersonStats GetPersonStats(string address, IEnumerable<string> mine, bool includeBin = false)
    {
        if (string.IsNullOrWhiteSpace(address)) return PersonStats.None;
        var person = SearchQuery.ForPerson(address, PersonKind.All, mine).Person!;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT COUNT(*), COALESCE(SUM(k=1),0), COALESCE(SUM(k=2),0), COALESCE(SUM(k=3),0), MAX(d) FROM (
              SELECT MIN({person.KindSql(cmd, "m")}) AS k, MAX(m.date) AS d
              FROM messages m JOIN folders fo ON fo.id=m.folder_id
              WHERE {PersonFolders(includeBin)} AND {person.ToSql(cmd, "m")}
              GROUP BY m.account_id, {EmailKey})
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.GetInt32(0) == 0) return PersonStats.None;
        return new PersonStats(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3),
            r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)));
    }

    /// <summary>
    /// Design PE1: one row per email with this address, newest first. Each email appears once, as its Inbox / Sent /
    /// label copy rather than Gmail's All Mail; its Bcc comes from whichever copy knows it.
    /// </summary>
    public List<PersonEmail> ListPersonEmails(string address, PersonKind kind, IEnumerable<string> mine, bool includeBin = false, int limit = 400)
    {
        var list = new List<PersonEmail>();
        if (string.IsNullOrWhiteSpace(address)) return list;
        var person = SearchQuery.ForPerson(address, PersonKind.All, mine).Person!;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {MsgCols}, m.bcc_any, m.k, m.fo_role, m.fo_path FROM (
              SELECT m.*, fo.role AS fo_role, fo.path AS fo_path, {person.KindSql(cmd, "m")} AS k,
                MAX(COALESCE(m.bcc_list,'')) OVER (PARTITION BY m.account_id, {EmailKey}) AS bcc_any,
                ROW_NUMBER() OVER (PARTITION BY m.account_id, {EmailKey} ORDER BY {CopyRankSql("fo.role")}, m.id) AS dn
              FROM messages m JOIN folders fo ON fo.id=m.folder_id
              WHERE {PersonFolders(includeBin)} AND {person.ToSql(cmd, "m")}
            ) m WHERE m.dn=1 {(kind == PersonKind.All ? "" : "AND m.k=" + (int)kind)}
            ORDER BY m.sort_date DESC, m.id DESC LIMIT $lim
            """;
        cmd.Parameters.AddWithValue("$lim", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = ReadMsg(r);
            row.Bcc = r.GetString(25);
            list.Add(new PersonEmail { Row = row, Kind = (PersonKind)r.GetInt32(26), FolderRole = (FolderRole)r.GetInt32(27), FolderPath = r.GetString(28) });
        }
        return list;
    }

    // ───────────────────────── Bcc of sent emails (design RC1) ─────────────────────────

    /// <summary>
    /// UIDs in this folder whose Bcc was never read, newest first: every email of a Sent folder, elsewhere only the
    /// ones you sent (<paramref name="myAddress"/>) — a received email never carries a Bcc line.
    /// </summary>
    public List<long> BccUnread(long folderId, bool sentFolder, string myAddress, int limit)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT uid FROM messages WHERE folder_id=$f AND bcc_list IS NULL"
            + (sentFolder ? "" : " AND from_addr=$me COLLATE NOCASE") + " ORDER BY uid DESC LIMIT $lim";
        cmd.Parameters.AddWithValue("$f", folderId);
        cmd.Parameters.AddWithValue("$me", myAddress);
        cmd.Parameters.AddWithValue("$lim", limit);
        var list = new List<long>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetInt64(0));
        return list;
    }

    /// <summary>Stores the Bcc lines read from the server ("" = none) and makes those people findable by words too.</summary>
    public int SetBcc(long folderId, IEnumerable<(long uid, string bcc)> lines)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE messages SET bcc_list=$b WHERE folder_id=$f AND uid=$u AND bcc_list IS NULL RETURNING id";
        cmd.Parameters.AddWithValue("$f", folderId);
        var pu = cmd.Parameters.Add("$u", SqliteType.Integer);
        var pb = cmd.Parameters.Add("$b", SqliteType.Text);
        using var fts = c.CreateCommand();
        fts.CommandText = "UPDATE messages_fts SET recipients = recipients || ' ' || $b WHERE rowid=$id";
        var fb = fts.Parameters.Add("$b", SqliteType.Text);
        var fid = fts.Parameters.Add("$id", SqliteType.Integer);
        var found = 0;
        foreach (var (uid, bcc) in lines)
        {
            pu.Value = uid; pb.Value = bcc;
            var id = cmd.ExecuteScalar();
            if (id == null || id is DBNull || bcc.Length == 0) continue;
            fb.Value = bcc; fid.Value = Convert.ToInt64(id);
            fts.ExecuteNonQuery();
            found++;
        }
        tx.Commit();
        return found;
    }
}
