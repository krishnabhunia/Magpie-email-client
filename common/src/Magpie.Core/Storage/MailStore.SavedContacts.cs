using System.Text.Json;
using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>
/// Design B4: the Google contacts of each account, kept on this PC (table <c>saved_contacts</c>). Changes made here are
/// saved at once with a pending mark and sent to Google by <c>ContactsService</c>. The older <c>contacts</c> table (people
/// seen in mail) stays as it is: those are the "Recent" people.
/// </summary>
public sealed partial class MailStore
{
    /// <summary>Created on every start (not tied to the schema number), like the productivity tables.</summary>
    private void EnsureSavedContactsSchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS saved_contacts(
              id INTEGER PRIMARY KEY AUTOINCREMENT, account_id TEXT NOT NULL, resource TEXT NOT NULL DEFAULT '', etag TEXT NOT NULL DEFAULT '',
              given TEXT NOT NULL DEFAULT '', family TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '',
              emails TEXT NOT NULL DEFAULT '[]', phones TEXT NOT NULL DEFAULT '[]', company TEXT NOT NULL DEFAULT '', title TEXT NOT NULL DEFAULT '',
              notes TEXT NOT NULL DEFAULT '', groups TEXT NOT NULL DEFAULT '[]', pending INTEGER NOT NULL DEFAULT 0, updated INTEGER NOT NULL DEFAULT 0,
              sources TEXT NOT NULL DEFAULT '[]', raw TEXT NOT NULL DEFAULT '{}');
            CREATE INDEX IF NOT EXISTS ix_saved_contacts_account ON saved_contacts(account_id, resource);
            """);
        // A table made by an earlier test build lacks the newer columns: add them (presence check, like the productivity tables).
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(saved_contacts)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns.Add(r.GetString(1));
        }
        if (!columns.Contains("sources")) Exec(c, "ALTER TABLE saved_contacts ADD COLUMN sources TEXT NOT NULL DEFAULT '[]'");
        if (!columns.Contains("raw")) Exec(c, "ALTER TABLE saved_contacts ADD COLUMN raw TEXT NOT NULL DEFAULT '{}'");
    }

    private const string ContactColumns = "id,account_id,resource,etag,given,family,name,emails,phones,company,title,notes,groups,pending,updated,sources,raw";

    private static SavedContact ReadContact(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), AccountId = r.GetString(1), Resource = r.GetString(2), Etag = r.GetString(3),
        GivenName = r.GetString(4), FamilyName = r.GetString(5), Name = r.GetString(6),
        Emails = JsonList(r.GetString(7)), Phones = JsonList(r.GetString(8)), Company = r.GetString(9), Title = r.GetString(10),
        Notes = r.GetString(11), Groups = JsonList(r.GetString(12)), Pending = (PendingContactOp)r.GetInt32(13),
        Updated = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(14)), Sources = r.GetString(15), Raw = r.GetString(16),
    };

    private static List<string> JsonList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    /// <summary>Every saved contact (all accounts), contacts being deleted left out, by name.</summary>
    public List<SavedContact> GetSavedContacts(string? accountId = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ContactColumns} FROM saved_contacts WHERE pending<>3" + (accountId == null ? "" : " AND account_id=$a") + " ORDER BY name COLLATE NOCASE, id";
        if (accountId != null) cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<SavedContact>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadContact(r));
        return list;
    }

    public SavedContact? GetSavedContact(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ContactColumns} FROM saved_contacts WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadContact(r) : null;
    }

    public List<SavedContact> PendingContacts(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ContactColumns} FROM saved_contacts WHERE account_id=$a AND pending<>0 ORDER BY id";
        cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<SavedContact>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadContact(r));
        return list;
    }

    /// <summary>
    /// What Google has now replaces this account's contacts, except the ones changed on this PC and not sent yet
    /// (they win until they have reached Google). A contact already here keeps its row id (the page's selection and an
    /// open edit window refer to it); contacts gone from Google are removed, new ones added.
    /// </summary>
    public void ReplaceSavedContacts(string accountId, IReadOnlyList<SavedContact> fromGoogle)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var pending = new HashSet<string>(StringComparer.Ordinal);
        var known = new Dictionary<string, long>(StringComparer.Ordinal);   // resource → row id of this account's rows
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, resource, pending FROM saved_contacts WHERE account_id=$a AND resource<>''";
            cmd.Parameters.AddWithValue("$a", accountId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                known[r.GetString(1)] = r.GetInt64(0);
                if (r.GetInt32(2) != 0) pending.Add(r.GetString(1));
            }
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in fromGoogle)
        {
            if (p.Resource.Length == 0 || !seen.Add(p.Resource) || pending.Contains(p.Resource)) continue;
            p.AccountId = accountId;
            p.Pending = PendingContactOp.None;
            if (known.TryGetValue(p.Resource, out var id))
            {
                p.Id = id;
                using var cmd = c.CreateCommand();
                cmd.CommandText = UpdateSql;
                Bind(cmd, p);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            else InsertContact(c, p);
        }
        // Gone from Google (and not changed here): rows with a Google id that the list no longer has.
        foreach (var (resource, id) in known)
            if (!seen.Contains(resource) && !pending.Contains(resource))
                Exec(c, "DELETE FROM saved_contacts WHERE id=$id", ("$id", id));
        // A new contact Google refused (no id, nothing pending any more) is not kept as a ghost.
        Exec(c, "DELETE FROM saved_contacts WHERE account_id=$a AND resource='' AND pending=0", ("$a", accountId));
        tx.Commit();
    }

    private const string UpdateSql = """
        UPDATE saved_contacts SET account_id=$a,resource=$r,etag=$e,given=$g,family=$f,name=$n,emails=$em,phones=$ph,company=$co,
          title=$ti,notes=$no,groups=$gr,pending=$p,updated=$u,sources=$s,raw=$raw WHERE id=$id
        """;

    private static void InsertContact(SqliteConnection c, SavedContact p)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO saved_contacts(account_id,resource,etag,given,family,name,emails,phones,company,title,notes,groups,pending,updated,sources,raw)
            VALUES($a,$r,$e,$g,$f,$n,$em,$ph,$co,$ti,$no,$gr,$p,$u,$s,$raw); SELECT last_insert_rowid();
            """;
        Bind(cmd, p);
        p.Id = Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void Bind(SqliteCommand cmd, SavedContact p)
    {
        cmd.Parameters.AddWithValue("$a", p.AccountId);
        cmd.Parameters.AddWithValue("$r", p.Resource);
        cmd.Parameters.AddWithValue("$e", p.Etag);
        cmd.Parameters.AddWithValue("$g", p.GivenName);
        cmd.Parameters.AddWithValue("$f", p.FamilyName);
        cmd.Parameters.AddWithValue("$n", p.Name);
        cmd.Parameters.AddWithValue("$em", JsonSerializer.Serialize(p.Emails));
        cmd.Parameters.AddWithValue("$ph", JsonSerializer.Serialize(p.Phones));
        cmd.Parameters.AddWithValue("$co", p.Company);
        cmd.Parameters.AddWithValue("$ti", p.Title);
        cmd.Parameters.AddWithValue("$no", p.Notes);
        cmd.Parameters.AddWithValue("$gr", JsonSerializer.Serialize(p.Groups));
        cmd.Parameters.AddWithValue("$p", (int)p.Pending);
        cmd.Parameters.AddWithValue("$u", p.Updated.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$s", p.Sources.Length > 0 ? p.Sources : "[]");
        cmd.Parameters.AddWithValue("$raw", p.Raw.Length > 0 ? p.Raw : "{}");
    }

    /// <summary>Saves a contact changed on this PC (new when Id is 0), keeping its pending mark. A row that is gone meanwhile is added again.</summary>
    public void SaveLocalContact(SavedContact p)
    {
        using var c = Open();
        if (p.Id != 0)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = UpdateSql;
            Bind(cmd, p);
            cmd.Parameters.AddWithValue("$id", p.Id);
            if (cmd.ExecuteNonQuery() > 0) return;
        }
        InsertContact(c, p);
    }

    /// <summary>
    /// A pending change reached Google: Google's copy replaces it, unless it was changed again meanwhile (then the newer
    /// change keeps its pending mark — a deletion stays a deletion — and only Google's id and version are taken).
    /// Returns false when the row was removed here in the meantime (the caller then removes it from Google too).
    /// </summary>
    public bool CompleteContactUpload(SavedContact sent, SavedContact fromGoogle)
    {
        using var c = Open();
        var row = Scalar(c, "SELECT updated FROM saved_contacts WHERE id=$id", ("$id", sent.Id));
        if (row == null) return false;
        if (Convert.ToInt64(row) != sent.Updated.ToUnixTimeMilliseconds())
        {
            Exec(c, "UPDATE saved_contacts SET resource=$r, etag=$e, sources=$s, pending=CASE WHEN pending=3 THEN 3 ELSE 2 END WHERE id=$id",
                ("$r", fromGoogle.Resource), ("$e", fromGoogle.Etag), ("$s", fromGoogle.Sources.Length > 0 ? fromGoogle.Sources : "[]"), ("$id", sent.Id));
            return true;
        }
        fromGoogle.Id = sent.Id;
        fromGoogle.AccountId = sent.AccountId;
        fromGoogle.Pending = PendingContactOp.None;
        fromGoogle.Updated = sent.Updated;
        using var cmd = c.CreateCommand();
        cmd.CommandText = UpdateSql;
        Bind(cmd, fromGoogle);
        cmd.Parameters.AddWithValue("$id", sent.Id);
        cmd.ExecuteNonQuery();
        return true;
    }

    /// <summary>Google refused the change: forget the pending mark, unless the contact was changed again here since it was sent.</summary>
    public void ClearPendingIfUnchanged(SavedContact sent)
    {
        using var c = Open();
        Exec(c, "UPDATE saved_contacts SET pending=0 WHERE id=$id AND updated=$u", ("$id", sent.Id), ("$u", sent.Updated.ToUnixTimeMilliseconds()));
    }

    public void DeleteSavedContactRow(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM saved_contacts WHERE id=$id", ("$id", id));
    }

    public void DeleteSavedContactsForAccount(string accountId)
    {
        using var c = Open();
        Exec(c, "DELETE FROM saved_contacts WHERE account_id=$a", ("$a", accountId));
    }

    /// <summary>People seen in mail (the older <c>contacts</c> table): address → (name, how often, last, written to).</summary>
    public List<(string Address, string Name, int Count, DateTimeOffset Last, bool Sent)> MailPeople(int limit = 5000)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT addr,name,count,last,sent FROM contacts ORDER BY sent DESC, count DESC, last DESC LIMIT $l";
        cmd.Parameters.AddWithValue("$l", limit);
        var list = new List<(string, string, int, DateTimeOffset, bool)>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.GetString(1), r.GetInt32(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), r.GetInt32(4) != 0));
        return list;
    }
}
