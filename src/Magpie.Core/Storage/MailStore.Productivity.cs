using Magpie.Core.Models;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    // Also run when a database came from a newer parallel branch: never downgrade user_version.
    private void EnsureProductivitySchema()
    {
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS inbox_views(
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                query TEXT NOT NULL,
                account_id TEXT NULL,
                inbox_only INTEGER NOT NULL DEFAULT 1);
            """);
    }

    public IReadOnlyList<InboxView> GetInboxViews()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,name,query,account_id,inbox_only FROM inbox_views ORDER BY id";
        using var r = cmd.ExecuteReader();
        var result = new List<InboxView>();
        while (r.Read())
            result.Add(new InboxView(r.GetInt64(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) != 0));
        return result;
    }

    public long SaveInboxView(string name, string query, string? accountId, bool inboxOnly, long? id = null)
    {
        name = name.Trim();
        query = query.Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Use a view name of 1–80 characters.");
        if (query.Length > 2048 || SearchQuery.Parse(query).IsEmpty)
            throw new ArgumentException("Enter a search query of 1–2048 characters.");
        accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId.Trim();
        using var c = Open();
        using var tx = c.BeginTransaction();
        if (id == null && Convert.ToInt64(Scalar(c, "SELECT count(*) FROM inbox_views")) >= 32)
            throw new ArgumentException("Keep up to 32 saved views. Remove a view before adding another.");
        long result;
        if (id is { } existing)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE inbox_views SET name=$n,query=$q,account_id=$a,inbox_only=$b WHERE id=$id RETURNING id";
            cmd.Parameters.AddWithValue("$id", existing);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$a", (object?)accountId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$b", inboxOnly ? 1 : 0);
            result = cmd.ExecuteScalar() is long value ? value : throw new ArgumentException("This saved view no longer exists.");
        }
        else
            result = Convert.ToInt64(Scalar(c,
                "INSERT INTO inbox_views(name,query,account_id,inbox_only) VALUES($n,$q,$a,$b) RETURNING id",
                ("$n", name), ("$q", query), ("$a", (object?)accountId ?? DBNull.Value), ("$b", inboxOnly ? 1 : 0)));
        tx.Commit();
        return result;
    }

    public void DeleteInboxView(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM inbox_views WHERE id=$id", ("$id", id));
    }

    /// <summary>Resolve replies before the deadline and claim due reminders once. No network calls.</summary>
    public (List<Reminder> Due, int Completed) AdvanceReminders(DateTimeOffset now, IReadOnlyCollection<string> myAddresses)
    {
        var due = new List<Reminder>();
        var completed = 0;
        foreach (var reminder in GetReminders(ReminderState.Waiting))
        {
            var answered = !reminder.Always && HasReplyAfter(reminder.AccountId, reminder.ThreadKey, reminder.After, myAddresses);
            if (!answered && reminder.Due > now) continue;
            var state = answered ? ReminderState.Done : ReminderState.Due;
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE reminders SET state=$state WHERE id=$id AND state=0";
            cmd.Parameters.AddWithValue("$state", (int)state);
            cmd.Parameters.AddWithValue("$id", reminder.Id);
            if (cmd.ExecuteNonQuery() != 1) continue; // dismissed/replaced/claimed by another worker
            reminder.State = state;
            if (answered) completed++; else due.Add(reminder);
        }
        return (due, completed);
    }
}
