using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>4.0.0: Trash and Spam (design TB1), deleting past emails (DP1), tick boxes and Select ▾ (SL1).</summary>
public class Release400Tests
{
    private static MailEngine Engine(TempDir dir, out long inbox, out long receipts, out long trash, out long junk)
    {
        var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        inbox = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        receipts = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Receipts", Name = "Receipts" });
        trash = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        junk = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Spam", Name = "Spam", Role = FolderRole.Junk });
        return e;
    }

    // ───────────── TB1 ─────────────

    [Fact]
    public void Empty_Trash_deletes_everything_there_and_asks_the_server_once()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var trash, out _);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", trash, "t1"), Rows.Make("A", trash, "t2"), Rows.Make("A", trash, "t3"), Rows.Make("A", inbox, "keep"),
        });
        Assert.Equal(3, e.EmptyFolder("A", FolderRole.Trash));
        Assert.Empty(e.Store.MessagesInFolder(trash));
        Assert.Single(e.Store.MessagesInFolder(inbox));
        var op = Assert.Single(e.Store.GetPendingOps("A"));
        Assert.Equal((PendingOpKind.EmptyFolder, trash), (op.Kind, op.FolderId));
        Assert.Throws<ArgumentException>(() => e.EmptyFolder("A", FolderRole.Inbox));
    }

    [Fact]
    public async Task Restore_puts_a_conversation_back_where_it_was_or_in_the_Inbox()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out var trash, out _);
        var bill = Rows.Make("A", receipts, "bill", messageId: "bill@x");
        e.Store.InsertMessages(new[] { bill });
        await e.TrashAsync("A", "bill", new[] { receipts });
        // The sync then lists it in Trash; another one was put there elsewhere (unknown origin).
        e.Store.InsertMessages(new[] { Rows.Make("A", trash, "bill", messageId: "bill@x"), Rows.Make("A", trash, "other", messageId: "other@x") });
        foreach (var op in e.Store.GetPendingOps("A")) e.Store.RemovePendingOp(op.Id);

        e.Restore("A", "bill");
        e.Restore("A", "other");
        var moves = e.Store.GetPendingOps("A").Where(o => o.Kind == PendingOpKind.Move).Select(o => o.Arg).ToList();
        Assert.Equal(new[] { receipts, inbox }, moves);
        Assert.Empty(e.Store.MessagesInFolder(trash));
    }

    [Fact]
    public void Not_spam_moves_to_the_Inbox_and_lets_the_sender_through()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _, out var junk);
        e.Store.InsertMessages(new[] { Rows.Make("A", junk, "s1", from: "Friend@Real.com") });
        e.NotSpam("A", "s1");
        var op = Assert.Single(e.Store.GetPendingOps("A"));
        Assert.Equal((PendingOpKind.Move, inbox), (op.Kind, op.Arg));
        Assert.Contains("friend@real.com", e.Config.Gatekeeper.Allowed);
    }

    [Fact]
    public void Trash_empties_itself_after_the_chosen_days_counted_from_arrival_in_Trash()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out _, out _, out var trash, out _);
        e.Store.InsertMessages(new[] { Rows.Make("A", trash, "old", date: DateTimeOffset.Now.AddYears(-3), messageId: "old@x") });
        var now = DateTimeOffset.Now;
        Assert.Equal(0, e.AutoEmptyTrash(now));                         // never, by default
        e.Config.EmptyTrashAfterDays = 7;
        Assert.Equal(0, e.AutoEmptyTrash(now));                         // first seen in Trash now: an old email isn't deleted at once
        Assert.Equal(0, e.AutoEmptyTrash(now.AddDays(6)));
        Assert.Equal(1, e.AutoEmptyTrash(now.AddDays(8)));
        Assert.Empty(e.Store.MessagesInFolder(trash));
        Assert.Equal(PendingOpKind.Delete, Assert.Single(e.Store.GetPendingOps("A")).Kind);
    }

    [Theory]
    [InlineData(0, 0)] [InlineData(-5, 0)] [InlineData(3, 7)] [InlineData(7, 7)] [InlineData(20, 30)] [InlineData(90, 30)]
    public void Empty_Trash_setting_is_never_7_or_30_days(int saved, int expected)
    {
        var s = new AppSettings { EmptyTrashAfterDays = saved };
        SettingsStore.Normalise(s);
        Assert.Equal(expected, s.EmptyTrashAfterDays);
        Assert.True(new AppSettings().AutoDeleteIncludePast);      // design DP1 (D7): on by default
    }

    // ───────────── DP1 ─────────────

    [Fact]
    public void Emails_already_here_from_a_sender_count_once_skip_pinned_trash_and_sent()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out var trash, out _);
        var sent = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        var old = DateTimeOffset.Now.AddYears(-2);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "a1", from: "anita@vendorco.in", date: old, messageId: "a1@x"),
            Rows.Make("A", receipts, "a1", from: "anita@vendorco.in", date: old, messageId: "a1@x"),   // a copy (label)
            Rows.Make("A", inbox, "a2", from: "Anita@VendorCo.in", messageId: "a2@x"),
            Rows.Make("A", inbox, "a3", from: "anita@vendorco.in", flags: MessageFlags.Flagged, messageId: "a3@x"),
            Rows.Make("A", trash, "a4", from: "anita@vendorco.in", messageId: "a4@x"),
            Rows.Make("A", sent, "a5", from: "anita@vendorco.in", messageId: "a5@x"),
            Rows.Make("A", inbox, "b1", from: "ravi@vendorco.in", messageId: "b1@x"),
        });
        var p = e.PastFrom("anita@vendorco.in");
        Assert.Equal(2, p.Count);
        Assert.Equal(3, p.Rows.Count);
        Assert.Equal(old.ToUnixTimeMilliseconds(), p.Oldest!.Value.ToUnixTimeMilliseconds());
        Assert.Equal(3, e.PastFrom("*@vendorco.in").Count);
        Assert.Equal(1, e.PastFrom("anita@vendorco.in", DateTimeOffset.Now.AddYears(-1)).Count);
        Assert.Equal(0, e.PastFrom("not an address").Count);

        e.TrashEmails(p.Rows);
        var ops = e.Store.GetPendingOps("A");
        Assert.Equal(3, ops.Count(o => o.Kind == PendingOpKind.Move && o.Arg == trash));
        Assert.Contains(e.Store.TrashOrigin("A", "a2@x"), new long?[] { inbox });                           // Restore knows where it was
        Assert.Single(e.Store.MessagesInFolder(inbox), m => m.FromAddress.StartsWith("anita", StringComparison.OrdinalIgnoreCase));   // the pinned one
    }

    [Fact]
    public void Confirmation_says_the_oldest_and_newest()
    {
        var rows = new[]
        {
            Rows.Make("A", 1, "x", date: new DateTimeOffset(2024, 3, 3, 12, 0, 0, TimeSpan.FromHours(5.5)), messageId: "1@x"),
            Rows.Make("A", 1, "y", date: new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(5.5)), messageId: "2@x"),
        };
        var p = AutoDelete.PastEmails.Of(rows);
        Assert.Equal(2, p.Count);
        Assert.Equal("Oldest: 3 March 2024 · Newest: 30 September 2026", AutoDelete.DatesLine(p));
        Assert.Equal("", AutoDelete.DatesLine(AutoDelete.PastEmails.Of(Array.Empty<MessageRow>())));
        Assert.Equal(new[] { "1 week", "1 month", "3 months", "6 months", "1 year", "2 years" }, AutoDelete.OlderThan.Select(o => o.Label));
    }

    [Fact]
    public void A_new_rule_counts_existing_emails_from_their_arrival_so_old_ones_go_at_once()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var trash, out _);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "o1", from: "promo@shop.in", date: DateTimeOffset.Now.AddDays(-30)),
            Rows.Make("A", inbox, "o2", from: "promo@shop.in", date: DateTimeOffset.Now.AddDays(-1)),
        });
        Assert.Equal(2, e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "promo@shop.in", Amount = 7 }, startOnExisting: true));
        Assert.Equal(1, e.RunDueDeletes(DateTimeOffset.Now));                    // the 30-day-old one
        Assert.Equal("o2", Assert.Single(e.Store.MessagesInFolder(inbox)).ThreadKey);
    }

    [Fact]
    public void One_auto_delete_rule_per_sender_a_second_one_changes_the_first()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _, out _);
        e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "p1", from: "alert@magicbricks.com") });
        var first = new AutoDeleteRule { Pattern = "alert@magicbricks.com", Amount = 7 };
        e.SaveAutoDeleteRule(first, startOnExisting: true);
        e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "ALERT@magicbricks.com", Amount = 1, Unit = DeleteUnit.Months }, startOnExisting: false);
        var rule = Assert.Single(e.AutoDeleteRules());
        Assert.Equal((first.Id, 1, DeleteUnit.Months), (rule.Id, rule.Amount, rule.Unit));
        e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "*@magicbricks.com", Amount = 3 }, false);   // the domain is another rule
        Assert.Equal(2, e.AutoDeleteRules().Count);

        // Rules saved twice by an older version are merged at start; their emails keep a timer.
        e.Store.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "alert@magicbricks.com", Amount = 14, Created = DateTimeOffset.Now.AddMinutes(1) });
        Assert.Equal(3, e.AutoDeleteRules().Count);
        Assert.Equal(1, e.MergeDuplicateAutoDeleteRules());
        Assert.Equal(2, e.AutoDeleteRules().Count);
        Assert.Contains(e.AutoDeleteRules(), r => r.Pattern == "alert@magicbricks.com" && r.Amount == 14);
        Assert.Single(e.Store.DeleteTimers("A", "p1"));
    }

    // ───────────── SL1 ─────────────

    private static ThreadRow T(string from = "a@x.in", int unread = 0, bool pinned = false, bool att = false, int daysAgo = 0,
        Category cat = Category.People, string tags = "") => new()
    {
        AccountId = "A", ThreadKey = Guid.NewGuid().ToString("N"), UnreadCount = unread, Flagged = pinned, HasAttachments = att,
        Latest = new MessageRow { FromAddress = from, Date = DateTimeOffset.Now.AddDays(-daysAgo), Category = cat, Tags = tags },
    };

    [Fact]
    public void Select_by_filter_ticks_the_matching_conversations()
    {
        Assert.True(Selection.Ticks(T(), SelectFilter.All, null, false));
        Assert.False(Selection.Ticks(T(), SelectFilter.None, null, true));
        Assert.False(Selection.Ticks(T(), SelectFilter.Invert, null, true));
        Assert.True(Selection.Ticks(T(unread: 2), SelectFilter.Unread, null, false));
        Assert.False(Selection.Ticks(T(unread: 2), SelectFilter.Read, null, false));
        Assert.True(Selection.Ticks(T(pinned: true), SelectFilter.Pinned, null, false));
        Assert.True(Selection.Ticks(T(att: true), SelectFilter.WithAttachments, null, false));
        Assert.True(Selection.Ticks(T(from: "Anita@X.in"), SelectFilter.SameSender, "anita@x.in", false));
        Assert.False(Selection.Ticks(T(from: "ravi@x.in"), SelectFilter.SameSender, "anita@x.in", false));
        var month = Selection.OlderThan.Single(o => o.Label == "1 month").Cutoff(DateTimeOffset.Now);
        Assert.True(Selection.Ticks(T(daysAgo: 40), SelectFilter.OlderThan, month, false));
        Assert.False(Selection.Ticks(T(daysAgo: 3), SelectFilter.OlderThan, month, false));
        Assert.True(Selection.Ticks(T(cat: Category.Newsletters), SelectFilter.Category, Category.Newsletters, false));
        Assert.True(Selection.Ticks(T(tags: "Work, Waiting"), SelectFilter.Tag, "waiting", false));
        Assert.False(Selection.Ticks(T(tags: "Work"), SelectFilter.Tag, "Waiting", false));
    }

    [Fact]
    public void The_box_above_the_list_shows_none_some_or_all()
    {
        Assert.False(Selection.HeaderState(0, 10));
        Assert.Null(Selection.HeaderState(3, 10));
        Assert.True(Selection.HeaderState(10, 10));
        Assert.Equal("All 400 on screen are selected.", Selection.AllOnScreenLine(400, "Inbox"));
    }

    [Fact]
    public void Schema_9_keeps_where_trashed_emails_came_from()
    {
        Assert.Equal(9, MailStore.SchemaVersion);
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var m = Rows.Make("A", inbox, "t", messageId: "m@x");
        s.RecordTrashed(new[] { m }, DateTimeOffset.Now);
        Assert.Equal(inbox, s.TrashOrigin("A", "m@x"));
        s.ForgetTrashed("A", new[] { "m@x" });
        Assert.Null(s.TrashOrigin("A", "m@x"));
    }
}
