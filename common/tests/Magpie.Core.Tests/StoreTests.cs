using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

public class StoreTests
{
    [Fact]
    public void Threads_group_count_and_sort_newest_first()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var t0 = DateTimeOffset.Now.AddHours(-5);
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", date: t0, flags: MessageFlags.Seen),
            Rows.Make("A", inbox, "t1", date: t0.AddHours(2)),
            Rows.Make("A", inbox, "t2", from: "bob@y.com", date: t0.AddHours(1), flags: MessageFlags.Seen | MessageFlags.Flagged),
        });
        var list = s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now);
        Assert.Equal(2, list.Count);
        Assert.Equal("t1", list[0].ThreadKey);
        Assert.Equal(2, list[0].Count);
        Assert.Equal(1, list[0].UnreadCount);
        Assert.True(list[1].Flagged);
        Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox }, UnreadOnly = true }, DateTimeOffset.Now));
        Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox }, FlaggedOnly = true }, DateTimeOffset.Now));
        Assert.Equal(1, s.CountUnreadThreads(new[] { inbox }, DateTimeOffset.Now));
    }

    [Fact]
    public void Category_filter_uses_latest_message()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "n1", from: "news@shop.com", cat: Category.Newsletters),
            Rows.Make("A", inbox, "p1", from: "anita@x.com", cat: Category.People),
            Rows.Make("A", inbox, "x1", from: "noreply@bank.com", cat: Category.Notifications),
        });
        var now = DateTimeOffset.Now;
        Assert.Equal("p1", Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Category = Category.People }, now)).ThreadKey);
        Assert.Equal("n1", Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Category = Category.Newsletters }, now)).ThreadKey);
        Assert.Equal(1, s.CountUnreadThreads(new[] { inbox }, now, Category.Notifications));
    }

    [Fact]
    public void Snoozed_threads_hide_then_wake_to_top()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var old = DateTimeOffset.Now.AddDays(-3);
        s.InsertMessages(new[] { Rows.Make("A", inbox, "old", date: old), Rows.Make("A", inbox, "new", date: DateTimeOffset.Now.AddMinutes(-1)) });
        var until = DateTimeOffset.Now.AddHours(1);
        s.SetSnooze("A", "old", new[] { inbox }, until);
        var now = DateTimeOffset.Now;
        Assert.Equal("new", Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, now)).ThreadKey);
        Assert.Equal("old", Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Snoozed = true }, now)).ThreadKey);
        Assert.Equal(1, s.CountSnoozedThreads(now));
        Assert.Empty(s.WakeDueSnoozes(now));
        var later = until.AddSeconds(1);
        var woke = s.WakeDueSnoozes(later);
        Assert.Single(woke);
        var list = s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, later);
        Assert.Equal("old", list[0].ThreadKey); // jumped to the top
        Assert.Null(s.NextSnoozeDue());
    }

    [Fact]
    public void Thread_view_dedupes_label_copies_and_includes_sent()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var label = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "Work", Name = "Work", Role = FolderRole.Other });
        var t = DateTimeOffset.Now.AddHours(-2);
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t", date: t, messageId: "m1@x"),
            Rows.Make("A", label, "t", date: t, messageId: "m1@x"),
            Rows.Make("A", sent, "t", from: "me@test.local", date: t.AddHours(1), messageId: "m2@x"),
        });
        var thread = s.GetThread("A", "t");
        Assert.Equal(2, thread.Count);
        Assert.Equal(inbox, thread[0].FolderId);
        Assert.Equal("m2@x", thread[1].MessageId);
        Assert.Equal(3, s.GetThreadCopies("A", "t").Count);
    }

    [Fact]
    public void Fts_search_finds_subject_sender_and_body()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var rows = s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", from: "anita.rao@vendor.com", subject: "Vendor migration decision"),
            Rows.Make("A", inbox, "t2", from: "bob@y.com", subject: "Lunch"),
        });
        s.SaveBody(rows[1].Id, new MessageBody { Text = "Shall we try the new biryani place on Friday?", Html = "" });
        var now = DateTimeOffset.Now;
        List<ThreadRow> Find(string q) => s.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Search = SearchQuery.Parse(q) }, now);
        Assert.Equal("t1", Assert.Single(Find("migra")).ThreadKey);
        Assert.Equal("t2", Assert.Single(Find("biryani")).ThreadKey);
        Assert.Equal("t1", Assert.Single(Find("from:anita")).ThreadKey);
        Assert.Equal("t1", Assert.Single(Find("anita.rao@vendor.com")).ThreadKey);
        Assert.Empty(Find("\"new vendor\""));
        Assert.Equal("t2", Assert.Single(Find("\"biryani place\"")).ThreadKey);
        Assert.Equal(2, Find("is:unread").Count);
    }

    [Fact]
    public void Uid_map_flags_and_deletes()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var r = s.InsertMessages(new[] { Rows.Make("A", inbox, "t1"), Rows.Make("A", inbox, "t2") });
        Assert.Equal(r.Max(x => x.Uid), s.MaxUid(inbox));
        // duplicate insert is ignored
        Assert.Empty(s.InsertMessages(new[] { new MessageRow { AccountId = "A", FolderId = inbox, Uid = r[0].Uid, ThreadKey = "t1", Date = DateTimeOffset.Now } }));
        s.UpdateFlags(inbox, new[] { (r[0].Uid, MessageFlags.Seen) });
        Assert.Equal(MessageFlags.Seen, s.GetUidMap(inbox)[r[0].Uid].flags);
        s.DeleteUids(inbox, new[] { r[1].Uid });
        Assert.Single(s.GetUidMap(inbox));
        s.RefreshFolderCounts(inbox);
        var f = s.GetFolder(inbox)!;
        Assert.Equal(1, f.Total);
        Assert.Equal(0, f.Unread);
    }

    [Fact]
    public void Outbox_claim_cancel_and_recover()
    {
        using var dir = new TempDir();
        var (s, _, _) = Rows.NewStore(dir);
        var id = s.AddOutbox(new OutboxItem { AccountId = "A", Mime = new byte[] { 1, 2 }, SendAt = DateTimeOffset.Now, Subject = "s" });
        Assert.True(s.TryClaimOutbox(id));
        Assert.False(s.TryClaimOutbox(id));      // already sending
        Assert.False(s.CancelOutbox(id));        // too late to undo
        s.RecoverStuckOutbox();                  // crash while sending → retry later
        Assert.Equal(OutboxStatus.Failed, s.GetOutbox().Single().Status);
        Assert.True(s.CancelOutbox(id));
        Assert.Empty(s.GetOutbox());
        Assert.Single(s.GetOutbox(includeDone: true));
    }

    [Fact]
    public void Reminder_reply_detection_ignores_my_messages()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var after = DateTimeOffset.Now.AddHours(-1);
        s.InsertMessages(new[] { Rows.Make("A", sent, "t", from: "me@test.local", date: after.AddMinutes(1)) });
        Assert.False(s.HasReplyAfter("A", "t", after, new[] { "me@test.local" }));
        s.InsertMessages(new[] { Rows.Make("A", inbox, "t", from: "anita@x.com", date: after.AddMinutes(30)) });
        Assert.True(s.HasReplyAfter("A", "t", after, new[] { "ME@test.local" }));
        var id = s.AddReminder(new Reminder { AccountId = "A", ThreadKey = "t", After = after, Due = DateTimeOffset.Now });
        s.AddReminder(new Reminder { AccountId = "A", ThreadKey = "t", After = after, Due = DateTimeOffset.Now }); // replaces the first
        Assert.Single(s.GetReminders(ReminderState.Waiting));
        Assert.Equal(ReminderState.Done, s.GetReminders().Single(r => r.Id == id).State);
    }

    [Fact]
    public void Contacts_autocomplete_and_known_senders_promotion()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        s.TouchContacts(new[] { ("Anita.Rao@vendor.com", "Anita Rao"), ("news@shop.com", "") }, DateTimeOffset.Now);
        s.TouchContacts(new[] { ("anita.rao@vendor.com", "") }, DateTimeOffset.Now, sentTo: true);
        var hits = s.SearchContacts("rao");
        Assert.Equal("anita.rao@vendor.com", Assert.Single(hits).Address);
        Assert.Equal("Anita Rao", hits[0].Name);
        Assert.Contains("anita.rao@vendor.com", s.KnownContacts());
        Assert.DoesNotContain("news@shop.com", s.KnownContacts());
        s.InsertMessages(new[] { Rows.Make("A", inbox, "t", from: "anita.rao@vendor.com", cat: Category.Notifications) });
        Assert.Equal(1, s.PromoteKnownSenders(Array.Empty<string>()));
        Assert.Equal(Category.People, s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now)[0].Latest.Category);
    }

    [Fact]
    public void Pending_ops_roundtrip()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        s.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 7, Kind = PendingOpKind.Move, Arg = 3 });
        s.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 8, Kind = PendingOpKind.SetSeen });
        Assert.Equal(2, s.PendingOpCount("A"));
        Assert.Contains((inbox, 7L), s.PendingRemovals("A"));
        Assert.DoesNotContain((inbox, 8L), s.PendingRemovals("A"));
        var op = s.GetPendingOps("A")[0];
        s.BumpPendingOp(op.Id);
        Assert.Equal(1, s.GetPendingOps("A")[0].Attempts);
        s.RemovePendingOp(op.Id);
        Assert.Equal(1, s.PendingOpCount("A"));
    }

    [Fact]
    public void Delete_account_removes_everything()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        s.InsertMessages(new[] { Rows.Make("A", inbox, "t") });
        s.DeleteAccount("A");
        Assert.Empty(s.GetFolders("A"));
        Assert.Empty(s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now));
    }
}

public class ThreadMergeTests
{
    [Fact]
    public void Message_linking_two_conversations_merges_them()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        // Inbox synced first: a1 (root) and a2 (reply to our me1, which isn't stored yet) look unrelated.
        var a1 = Rows.Make("A", inbox, "r:a1@v", messageId: "a1@v");
        var a2 = Rows.Make("A", inbox, "r:me1@t", messageId: "a2@v");
        a2.InReplyTo = "me1@t";
        s.InsertMessages(new[] { a1, a2 });
        Assert.Equal(2, s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now).Count);

        // Then Sent: me1 replies to a1, and a2 replied to me1 → one conversation.
        var (key, merge) = Magpie.Core.Mail.Threading.Resolve("<me1@t>", "<a1@v>", new List<string> { "a1@v" }, null,
            (mid, chain) => s.RelatedThreadKeys("A", mid, chain), "x");
        Assert.Equal("r:a1@v", key);
        Assert.Equal(new[] { "r:me1@t" }, merge);
        s.MergeThreads("A", key, merge);
        var me1 = Rows.Make("A", sent, key, from: "me@test.local", messageId: "me1@t");
        s.InsertMessages(new[] { me1 });
        var threads = s.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now);
        Assert.Equal(2, Assert.Single(threads).Count);
        Assert.Equal(3, s.GetThread("A", key).Count);
    }
}
