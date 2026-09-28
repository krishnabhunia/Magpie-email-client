using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

/// <summary>1.1.2: sidebar width (H1), folder hover details (H2), row actions settings (H3).</summary>
public class Release112Tests
{
    [Theory]
    [InlineData(150, 264, true)]     // narrower than the minimum → icon rail, width kept for later
    [InlineData(199.9, 264, true)]
    [InlineData(200, 200, false)]
    [InlineData(333, 333, false)]
    [InlineData(900, 420, false)]    // clamped
    public void Sidebar_width_snaps_to_rail_below_the_minimum_and_clamps_above_the_maximum(double dragged, double width, bool rail)
    {
        var (w, r) = WindowPlacement.SnapSidebar(dragged);
        Assert.Equal(width, w);
        Assert.Equal(rail, r);
    }

    [Fact]
    public void Hover_and_row_action_settings_normalise_unknown_values_and_limits()
    {
        var a = new Appearance
        {
            FolderHover = new FolderHoverSettings { Lines = new() { "size", "bogus", "unread", "size" }, DelayMs = 42 },
            RowActions = new RowActionsSettings { Ids = new() { "delete", "nope", "archive", "delete", "snooze", "read", "pin", "remind", "tag" }, ConfirmDeleteOver = -3, BulkUndoSeconds = 99 },
        };
        a.Normalise();
        Assert.Equal(new[] { "unread", "size" }, a.FolderHover.Lines);   // display order, no repeats, no unknowns
        Assert.Equal(600, a.FolderHover.DelayMs);
        Assert.Equal(new[] { "delete", "archive", "snooze", "read", "pin" }, a.RowActions.Ids);   // user's order, at most five
        Assert.Equal(0, a.RowActions.ConfirmDeleteOver);
        Assert.Equal(30, a.RowActions.BulkUndoSeconds);

        // Settings saved by 1.1.1 have neither block: defaults appear, nothing throws.
        var old = System.Text.Json.JsonSerializer.Deserialize<Appearance>("""{"ButtonStyle":"IconOnly","FolderHover":null}""", AppSettings.Json)!;
        old.Normalise();
        Assert.True(old.FolderHover.Enabled);
        Assert.Equal(FolderHoverSettings.DefaultLines, old.FolderHover.Lines);
        Assert.Equal(RowActionsSettings.DefaultIds, old.RowActions.Ids);
        Assert.Equal(RowActionsMode.OnHover, old.RowActions.Mode);
    }

    [Fact]
    public void Folder_details_count_conversations_messages_size_and_dates()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var now = DateTimeOffset.Now;
        var rows = s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", date: now.AddMinutes(-30)),                                         // today, unread
            Rows.Make("A", inbox, "t1", date: now.AddDays(-5), flags: MessageFlags.Seen),                   // same conversation
            Rows.Make("A", inbox, "t2", from: "bob@y.com", date: now.AddDays(-12)),                          // oldest unread
            Rows.Make("A", inbox, "t3", from: "carol@y.com", date: now.AddDays(-40), flags: MessageFlags.Seen),
        });
        // sizes / attachments are not part of Rows.Make: set them directly
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dir.File("mail.db")))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE messages SET size=1000000, has_attach=CASE WHEN thread_key='t2' THEN 1 ELSE 0 END";
            cmd.ExecuteNonQuery();
        }
        var d = s.GetFolderDetails(new[] { inbox }, now);
        Assert.Equal(3, d.Total);
        Assert.Equal(2, d.Unread);
        Assert.Equal(1, d.Today);
        Assert.Equal(4, d.Messages);
        Assert.Equal(4_000_000, d.Size);
        Assert.Equal(1, d.WithAttachments);
        Assert.Equal("12 days", FolderDetails.Ago(d.OldestUnread!.Value, now));
        Assert.Equal("anita", d.LastSender);
        Assert.Equal("4 MB", FolderDetails.FormatSize(d.Size));
        Assert.Equal("2", d.Line("unread", now, x => "x"));
        Assert.Equal("1 new", d.Line("today", now, x => "x"));
        Assert.Equal("1", d.Line("attach", now, x => "x"));

        // A snoozed conversation leaves the numbers, and a tag filter narrows them.
        s.SetSnooze("A", "t2", new[] { inbox }, now.AddHours(2));
        Assert.Equal(2, s.GetFolderDetails(new[] { inbox }, now).Total);
        Assert.Equal(1, s.GetFolderDetails(new[] { inbox }, now, snoozedOnly: true).Total);
        foreach (var r in s.GetThreadCopies("A", "t3")) s.SetTags(r.Id, "Work");
        Assert.Equal(1, s.GetFolderDetails(new[] { inbox }, now, tag: "Work").Total);
        Assert.Equal("—", s.GetFolderDetails(new[] { inbox }, now, tag: "Nothing").Line("oldest", now, x => "x"));
    }

    [Fact]
    public void Ago_and_size_wording()
    {
        var now = DateTimeOffset.Now;
        Assert.Equal("just now", FolderDetails.Ago(now.AddSeconds(-20), now));
        Assert.Equal("5 min", FolderDetails.Ago(now.AddMinutes(-5), now));
        Assert.Equal("1 hour", FolderDetails.Ago(now.AddMinutes(-70), now));
        Assert.Equal("3 hours", FolderDetails.Ago(now.AddHours(-3), now));
        Assert.Equal("1 day", FolderDetails.Ago(now.AddHours(-30), now));
        Assert.Equal("2 months", FolderDetails.Ago(now.AddDays(-70), now));
        Assert.Equal("4 years", FolderDetails.Ago(now.AddDays(-365 * 4 - 10), now));
        Assert.Equal("612 kB", FolderDetails.FormatSize(612 * 1024));
        Assert.Equal("1.9 GB", FolderDetails.FormatSize((long)(1.9 * (1L << 30))));
        Assert.Equal("40 B", FolderDetails.FormatSize(40));
    }
}
