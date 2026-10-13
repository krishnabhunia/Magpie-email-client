using System.Text.Json;
using Magpie.Core.Models;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>Design B2: Google calendars and their events, kept on this PC so the calendar works offline.</summary>
public sealed partial class MailStore
{
    private static void MigrateCalendar(SqliteConnection c)
    {
        Exec(c, """
            CREATE TABLE IF NOT EXISTS cal_calendars(
              account_id TEXT NOT NULL, id TEXT NOT NULL, name TEXT NOT NULL DEFAULT '', color TEXT NOT NULL DEFAULT '#14606E',
              selected INTEGER NOT NULL DEFAULT 1, is_primary INTEGER NOT NULL DEFAULT 0, can_edit INTEGER NOT NULL DEFAULT 0,
              PRIMARY KEY(account_id, id));
            CREATE TABLE IF NOT EXISTS cal_events(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id TEXT NOT NULL, calendar_id TEXT NOT NULL, event_id TEXT NOT NULL DEFAULT '',
              title TEXT NOT NULL DEFAULT '', location TEXT NOT NULL DEFAULT '', description TEXT NOT NULL DEFAULT '',
              start INTEGER NOT NULL, end INTEGER NOT NULL, all_day INTEGER NOT NULL DEFAULT 0,
              status TEXT NOT NULL DEFAULT 'confirmed', my_answer INTEGER NOT NULL DEFAULT 0, i_organize INTEGER NOT NULL DEFAULT 1,
              organizer TEXT NOT NULL DEFAULT '', attendees TEXT NOT NULL DEFAULT '[]', meet TEXT NOT NULL DEFAULT '',
              recurrence TEXT NOT NULL DEFAULT '', recurring_id TEXT NOT NULL DEFAULT '', reminder INTEGER NOT NULL DEFAULT 10,
              updated INTEGER NOT NULL DEFAULT 0, pending INTEGER NOT NULL DEFAULT 0, add_meet INTEGER NOT NULL DEFAULT 0,
              reminded INTEGER NOT NULL DEFAULT 0);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_cal_event ON cal_events(account_id, calendar_id, event_id) WHERE event_id <> '';
            CREATE INDEX IF NOT EXISTS ix_cal_time ON cal_events(start, end);
            """);
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('cal_events') WHERE name='revision'")) == 0)
            Exec(c, "ALTER TABLE cal_events ADD COLUMN revision INTEGER NOT NULL DEFAULT 0");
        if (Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('cal_events') WHERE name='creation_id'")) == 0)
            Exec(c, "ALTER TABLE cal_events ADD COLUMN creation_id TEXT NOT NULL DEFAULT ''");
    }

    // ───────────────────────── calendars ─────────────────────────

    /// <summary>The account's calendars as Google lists them now; a calendar's tick (shown or not) is kept, gone ones leave with their events.</summary>
    public void SaveCalendars(string accountId, IReadOnlyList<CalendarInfo> calendars)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        foreach (var cal in calendars)
            Exec(c, """
                INSERT INTO cal_calendars(account_id,id,name,color,selected,is_primary,can_edit) VALUES($a,$i,$n,$c,$s,$p,$e)
                ON CONFLICT(account_id,id) DO UPDATE SET name=excluded.name, color=excluded.color, is_primary=excluded.is_primary, can_edit=excluded.can_edit
                """, ("$a", accountId), ("$i", cal.Id), ("$n", cal.Name), ("$c", cal.Color), ("$s", cal.Selected ? 1 : 0), ("$p", cal.Primary ? 1 : 0), ("$e", cal.CanEdit ? 1 : 0));
        var keep = calendars.Select(x => x.Id).ToHashSet();
        foreach (var gone in ReadCalendars(c, accountId).Where(x => !keep.Contains(x.Id)))
        {
            Exec(c, "DELETE FROM cal_calendars WHERE account_id=$a AND id=$i", ("$a", accountId), ("$i", gone.Id));
            Exec(c, "DELETE FROM cal_events WHERE account_id=$a AND calendar_id=$i", ("$a", accountId), ("$i", gone.Id));
        }
        tx.Commit();
    }

    public List<CalendarInfo> GetCalendars(string? accountId = null)
    {
        using var c = Open();
        return ReadCalendars(c, accountId);
    }

    private static List<CalendarInfo> ReadCalendars(SqliteConnection c, string? accountId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT account_id,id,name,color,selected,is_primary,can_edit FROM cal_calendars" +
                          (accountId == null ? "" : " WHERE account_id=$a") + " ORDER BY account_id, is_primary DESC, name";
        if (accountId != null) cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<CalendarInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new CalendarInfo
            {
                AccountId = r.GetString(0), Id = r.GetString(1), Name = r.GetString(2), Color = r.GetString(3),
                Selected = r.GetInt64(4) != 0, Primary = r.GetInt64(5) != 0, CanEdit = r.GetInt64(6) != 0,
            });
        return list;
    }

    public void SetCalendarSelected(string accountId, string calendarId, bool selected)
    {
        using var c = Open();
        Exec(c, "UPDATE cal_calendars SET selected=$s WHERE account_id=$a AND id=$i", ("$s", selected ? 1 : 0), ("$a", accountId), ("$i", calendarId));
    }

    // ───────────────────────── events ─────────────────────────

    private const string EventCols = "id,account_id,calendar_id,event_id,title,location,description,start,end,all_day,status,my_answer,i_organize,organizer,attendees,meet,recurrence,recurring_id,reminder,updated,pending,add_meet,revision,creation_id";

    private static CalendarEvent ReadCalEvent(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0), AccountId = r.GetString(1), CalendarId = r.GetString(2), EventId = r.GetString(3),
        Title = r.GetString(4), Location = r.GetString(5), Description = r.GetString(6),
        Start = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(7)).ToLocalTime(),
        End = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(8)).ToLocalTime(),
        AllDay = r.GetInt64(9) != 0, Status = r.GetString(10), MyAnswer = (EventAnswer)r.GetInt32(11), IAmOrganizer = r.GetInt64(12) != 0,
        Organizer = r.GetString(13), Attendees = JsonSerializer.Deserialize<List<EventAttendee>>(r.GetString(14)) ?? new(),
        MeetLink = r.GetString(15), Recurrence = r.GetString(16), RecurringEventId = r.GetString(17), ReminderMinutes = r.GetInt32(18),
        Updated = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(19)), Pending = (PendingEventOp)r.GetInt32(20), AddMeet = r.GetInt64(21) != 0,
        Revision = r.GetInt64(22), CreationId = r.GetString(23),
    };

    private static (string, object?)[] EventParams(CalendarEvent e) =>
    [
        ("$a", e.AccountId), ("$c", e.CalendarId), ("$e", e.EventId), ("$t", e.Title), ("$l", e.Location), ("$d", e.Description),
        ("$s", e.Start.ToUnixTimeMilliseconds()), ("$n", e.End.ToUnixTimeMilliseconds()), ("$ad", e.AllDay ? 1 : 0), ("$st", e.Status),
        ("$my", (int)e.MyAnswer), ("$io", e.IAmOrganizer ? 1 : 0), ("$o", e.Organizer), ("$at", JsonSerializer.Serialize(e.Attendees)),
        ("$m", e.MeetLink), ("$r", e.Recurrence), ("$ri", e.RecurringEventId), ("$rm", e.ReminderMinutes),
        ("$u", e.Updated.ToUnixTimeMilliseconds()), ("$p", (int)e.Pending), ("$am", e.AddMeet ? 1 : 0),
        ("$rv", e.Revision), ("$ci", e.CreationId),
    ];

    /// <summary>
    /// What Google says a calendar holds between <paramref name="from"/> and <paramref name="to"/>: events are added or
    /// updated, those no longer there are removed. Events changed on this PC and not sent yet are left alone.
    /// </summary>
    public void ReplaceEvents(string accountId, string calendarId, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<CalendarEvent> events)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        var local = new Dictionary<string, (long id, int pending)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT event_id,id,pending FROM cal_events WHERE account_id=$a AND calendar_id=$c AND event_id<>''";
            q.Parameters.AddWithValue("$a", accountId);
            q.Parameters.AddWithValue("$c", calendarId);
            using var r = q.ExecuteReader();
            while (r.Read()) local[r.GetString(0)] = (r.GetInt64(1), r.GetInt32(2));
        }
        var seen = new HashSet<string>();
        foreach (var e in events)
        {
            e.AccountId = accountId;
            e.CalendarId = calendarId;
            e.Pending = PendingEventOp.None;
            seen.Add(e.EventId);
            if (local.TryGetValue(e.EventId, out var l))
            {
                if (l.pending != 0) continue;   // our change wins until it has reached Google
                Exec(c, """
                    UPDATE cal_events SET title=$t,location=$l,description=$d,start=$s,end=$n,all_day=$ad,status=$st,my_answer=$my,i_organize=$io,
                      organizer=$o,attendees=$at,meet=$m,recurrence=$r,recurring_id=$ri,reminder=$rm,updated=$u,pending=$p,add_meet=$am
                    WHERE id=$id
                    """, [.. EventParams(e), ("$id", l.id)]);
                e.Id = l.id;
            }
            else
                e.Id = Convert.ToInt64(Scalar(c, $"""
                    INSERT INTO cal_events({EventCols[3..]}) VALUES($a,$c,$e,$t,$l,$d,$s,$n,$ad,$st,$my,$io,$o,$at,$m,$r,$ri,$rm,$u,$p,$am,$rv,$ci) RETURNING id
                    """, EventParams(e)));
        }
        // Gone from Google (deleted, or moved out of the window): only those that were in the window asked for.
        foreach (var (eventId, l) in local)
            if (!seen.Contains(eventId) && l.pending == 0)
                Exec(c, "DELETE FROM cal_events WHERE id=$id AND start < $to AND end > $from",
                    ("$id", l.id), ("$to", to.ToUnixTimeMilliseconds()), ("$from", from.ToUnixTimeMilliseconds()));
        tx.Commit();
    }

    /// <summary>Events of the ticked calendars that overlap [from, to), earliest first (cancelled and deleted ones left out).</summary>
    public List<CalendarEvent> EventsBetween(DateTimeOffset from, DateTimeOffset to)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {string.Join(",", EventCols.Split(',').Select(x => "e." + x))} FROM cal_events e
            JOIN cal_calendars k ON k.account_id=e.account_id AND k.id=e.calendar_id
            WHERE k.selected=1 AND e.start < $to AND e.end > $from AND e.status<>'cancelled' AND e.pending<>{(int)PendingEventOp.Delete}
            ORDER BY e.all_day DESC, e.start, e.end DESC, e.title
            """;
        cmd.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        var list = new List<CalendarEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadCalEvent(r));
        return list;
    }

    public CalendarEvent? GetEvent(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {EventCols} FROM cal_events WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadCalEvent(r) : null;
    }

    /// <summary>Saves an event made or changed on this PC (its <see cref="CalendarEvent.Pending"/> says what Google must be told). Returns its id.</summary>
    public long SaveLocalEvent(CalendarEvent e)
    {
        using var c = Open();
        if (e.Pending == PendingEventOp.Create && e.CreationId.Length == 0) e.CreationId = Guid.NewGuid().ToString("N");
        if (e.Id == 0)
        {
            e.Revision = 1;
            e.Id = Convert.ToInt64(Scalar(c, $"INSERT INTO cal_events({EventCols[3..]}) VALUES($a,$c,$e,$t,$l,$d,$s,$n,$ad,$st,$my,$io,$o,$at,$m,$r,$ri,$rm,$u,$p,$am,$rv,$ci) RETURNING id",
                EventParams(e)));
            return e.Id;
        }
        return WriteLocalEvent(c, e, null) ? e.Id : 0;
    }

    private static bool WriteLocalEvent(SqliteConnection c, CalendarEvent e, long? expectedRevision)
    {
        var revision = Scalar(c, """
            UPDATE cal_events SET account_id=$a,calendar_id=$c,event_id=$e,title=$t,location=$l,description=$d,start=$s,end=$n,all_day=$ad,status=$st,
              my_answer=$my,i_organize=$io,organizer=$o,attendees=$at,meet=$m,recurrence=$r,recurring_id=$ri,reminder=$rm,updated=$u,pending=$p,add_meet=$am,
              reminded=CASE WHEN start=$s THEN reminded ELSE 0 END, revision=revision+1,
              creation_id=CASE WHEN creation_id<>'' THEN creation_id ELSE $ci END
            WHERE id=$id AND ($expected IS NULL OR revision=$expected) RETURNING revision
            """, [.. EventParams(e), ("$id", e.Id), ("$expected", expectedRevision)]);
        if (revision == null || revision is DBNull) return false;
        e.Revision = Convert.ToInt64(revision);
        return true;
    }

    public void EnsureCreationId(CalendarEvent e)
    {
        using var c = Open();
        Exec(c, "UPDATE cal_events SET creation_id=$ci WHERE id=$id AND creation_id=''", ("$ci", Guid.NewGuid().ToString("N")), ("$id", e.Id));
        e.CreationId = Convert.ToString(Scalar(c, "SELECT creation_id FROM cal_events WHERE id=$id", ("$id", e.Id))) ?? "";
    }

    public bool CompleteEventUpload(CalendarEvent sent, CalendarEvent saved)
    {
        using var c = Open();
        saved.Id = sent.Id;
        saved.CreationId = sent.CreationId;
        saved.Pending = PendingEventOp.None;
        if (WriteLocalEvent(c, saved, sent.Revision)) return true;
        // Keep newer edits/deletions, while recording the server identity of a newly created event.
        Exec(c, """
            UPDATE cal_events SET event_id=$event, revision=revision+1,
              pending=CASE WHEN pending=1 THEN 2 ELSE pending END
            WHERE id=$id AND event_id='' AND $event<>''
            """, ("$event", saved.EventId), ("$id", sent.Id));
        return false;
    }

    public void DeleteEventIfUnchanged(CalendarEvent sent)
    {
        using var c = Open();
        Exec(c, "DELETE FROM cal_events WHERE id=$id AND revision=$rv", ("$id", sent.Id), ("$rv", sent.Revision));
    }

    public void DeleteCalendarsForAccount(string accountId)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, "DELETE FROM cal_events WHERE account_id=$a", ("$a", accountId));
        Exec(c, "DELETE FROM cal_calendars WHERE account_id=$a", ("$a", accountId));
        tx.Commit();
    }

    public void DeleteEventRow(long id)
    {
        using var c = Open();
        Exec(c, "DELETE FROM cal_events WHERE id=$id", ("$id", id));
    }

    /// <summary>Changes made on this PC that Google hasn't got yet, oldest first.</summary>
    public List<CalendarEvent> PendingEvents(string accountId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {EventCols} FROM cal_events WHERE account_id=$a AND pending<>0 ORDER BY id";
        cmd.Parameters.AddWithValue("$a", accountId);
        var list = new List<CalendarEvent>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadCalEvent(r));
        return list;
    }

    /// <summary>Events whose reminder is due at <paramref name="now"/> (not yet reminded for this start time); marks them reminded.</summary>
    public List<CalendarEvent> TakeDueEventReminders(DateTimeOffset now)
    {
        using var c = Open();
        var list = new List<CalendarEvent>();
        using var tx = c.BeginTransaction();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT {string.Join(",", EventCols.Split(',').Select(x => "e." + x))} FROM cal_events e
                JOIN cal_calendars k ON k.account_id=e.account_id AND k.id=e.calendar_id
                WHERE k.selected=1 AND e.reminder >= 0 AND e.all_day=0 AND e.status<>'cancelled' AND e.my_answer<>{(int)EventAnswer.Declined}
                  AND e.pending<>{(int)PendingEventOp.Delete} AND e.reminded<>e.start
                  AND e.start - e.reminder*60000 <= $now AND e.start >= $grace
                """;
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$grace", now.AddMinutes(-5).ToUnixTimeMilliseconds());
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadCalEvent(r));
        }
        foreach (var e in list) Exec(c, "UPDATE cal_events SET reminded=start WHERE id=$id", ("$id", e.Id));
        tx.Commit();
        return list;
    }
}
