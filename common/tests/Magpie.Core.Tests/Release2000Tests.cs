using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>
/// 20.0.0 — design AL1 (option C): Trash says which emails Magpie's auto delete moved there and when (row label,
/// banner, per-email note), the Trash filter All / Auto-deleted / Deleted by you or elsewhere, and a restored
/// auto-deleted email is kept from its rule.
/// </summary>
public sealed class Release2000Tests
{
    private const string Shop = "offers@shop.test";

    private static MailEngine Engine(TempDir dir, out long inbox, out long receipts, out long trash)
    {
        var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        inbox = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        receipts = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Receipts", Name = "Receipts" });
        trash = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        return e;
    }

    private static (MailStore store, long inbox, long trash) Store(TempDir dir)
    {
        var (store, inbox, _) = Rows.NewStore(dir);
        var trash = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        return (store, inbox, trash);
    }

    private static void Sql(TempDir dir, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + dir.File("mail.db"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>A moment in this PC's own time zone (the texts are written in local time).</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    private static readonly (string, string) ShopRule = ("r1", "Rule: offers@shop.test after 30 days");

    // ───────────────────────── what is written when auto delete moves an email ─────────────────────────

    [Fact]
    public void Auto_delete_writes_when_from_where_and_under_which_rule_it_moved_an_email()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out _, out var receipts, out var trash);
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", receipts, "old", from: Shop, date: now.AddDays(-40), messageId: "old@shop"),
            Rows.Make("A", receipts, "new", from: Shop, date: now.AddDays(-2), messageId: "new@shop"),
        });
        var rule = new AutoDeleteRule { Pattern = Shop, Amount = 30 };
        e.SaveAutoDeleteRule(rule, startOnExisting: true);
        Assert.Equal(1, e.RunDueDeletes(now));

        var note = Assert.Single(e.Store.TrashNotes("A", new[] { "old@shop", "new@shop", "" })).Value;
        Assert.Equal(now.ToUnixTimeMilliseconds(), note.At.ToUnixTimeMilliseconds());
        Assert.Equal(rule.Id, note.RuleId);
        Assert.Equal("Rule: offers@shop.test after 30 days", note.RuleLine);
        Assert.Equal(receipts, note.FolderId);

        // Nothing is in Trash on this PC until the next check lists the moved copy there.
        Assert.Empty(e.Store.AutoDeletedThreads(new[] { trash }));
        Assert.Equal((0, 0), e.Store.TrashCounts(new[] { trash }));
        e.Store.InsertMessages(new[] { Rows.Make("A", trash, "old", from: Shop, date: now.AddDays(-40), messageId: "old@shop") });
        var (latest, count) = Assert.Single(e.Store.AutoDeletedThreads(new[] { trash })).Value;
        Assert.Equal((note, 1), (latest, count));
        Assert.Equal((1, 1), e.Store.TrashCounts(new[] { trash }));
        // Seeing the copy in Trash (the stamp that auto-empty counts from) leaves the note as it is.
        e.Store.StampTrash(trash, now.AddHours(1));
        Assert.Equal(note, e.Store.TrashNotes("A", new[] { "old@shop" })["old@shop"]);
    }

    [Fact]
    public void A_paused_rule_reads_without_paused_and_a_removed_rule_is_said_so()
    {
        var paused = new AutoDeleteRule { Pattern = "*@shop.test", Amount = 2, Unit = DeleteUnit.Months, Paused = true };
        Assert.Equal((paused.Id, "Rule: anyone at shop.test after 2 months"), AutoDeleted.RuleSnapshot(paused));
        Assert.Equal(("", ""), AutoDeleted.RuleSnapshot(null));

        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "o", from: Shop, date: now.AddDays(-40), messageId: "o@shop") });
        var rule = new AutoDeleteRule { Pattern = Shop, Amount = 30 };
        e.SaveAutoDeleteRule(rule, startOnExisting: true);
        e.RemoveAutoDeleteRule(rule.Id, clearTimers: false);   // "this email keeps its date"
        Assert.Equal(1, e.RunDueDeletes(now));
        var note = e.Store.TrashNotes("A", new[] { "o@shop" })["o@shop"];
        Assert.Equal(("", ""), (note.RuleId, note.RuleLine));
        Assert.Equal("Its rule was no longer there", AutoDeleted.RuleText(note));
    }

    [Fact]
    public void Deleting_a_senders_past_emails_at_once_is_noted_only_when_a_rule_did_it()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        var a = Rows.Make("A", inbox, "a", from: Shop, messageId: "a@shop");
        var b = Rows.Make("A", inbox, "b", from: Shop, messageId: "b@shop");
        e.Store.InsertMessages(new[] { a, b });
        var rows = e.Store.MessagesInFolder(inbox);
        var rule = new AutoDeleteRule { Pattern = Shop, Amount = 30 };

        Assert.Equal(1, e.TrashEmails(rows.Where(m => m.MessageId == "a@shop").ToList(), rule));
        Assert.Equal(1, e.TrashEmails(rows.Where(m => m.MessageId == "b@shop").ToList()));
        var note = Assert.Single(e.Store.TrashNotes("A", new[] { "a@shop", "b@shop" }));
        Assert.Equal("a@shop", note.Key);
        Assert.Equal((rule.Id, "Rule: offers@shop.test after 30 days", inbox), (note.Value.RuleId, note.Value.RuleLine, note.Value.FolderId));
        // Both still know where they were, for Restore.
        Assert.Equal(inbox, e.Store.TrashOrigin("A", "b@shop"));
    }

    [Fact]
    public void An_email_deleted_by_hand_has_no_note_and_loses_an_older_one()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Store(dir);
        var row = Rows.Make("A", inbox, "t", from: Shop, messageId: "t@shop");
        var at = Local(2026, 11, 21, 18, 24);

        store.RecordTrashed(new[] { row }, at, _ => ShopRule);
        Assert.Equal(new TrashNote(at, "r1", "Rule: offers@shop.test after 30 days", inbox), store.TrashNotes("A", new[] { "t@shop" })["t@shop"]);
        // Restored elsewhere, then deleted by hand here: it is no longer "auto-deleted".
        store.RecordTrashed(new[] { row }, at.AddDays(1));
        Assert.Empty(store.TrashNotes("A", new[] { "t@shop" }));
        Assert.Equal(inbox, store.TrashOrigin("A", "t@shop"));
        // Another account's email with the same Message-ID is its own.
        Assert.Empty(store.TrashNotes("B", new[] { "t@shop" }));
    }

    [Fact]
    public void An_email_without_a_message_id_cannot_be_noted()
    {
        using var dir = new TempDir();
        var (store, inbox, trash) = Store(dir);
        var row = Rows.Make("A", inbox, "t", from: Shop);
        row.MessageId = "";
        store.RecordTrashed(new[] { row }, DateTimeOffset.Now, _ => ShopRule);
        Assert.Empty(store.TrashNotes("A", new[] { "" }));

        var gone = Rows.Make("A", trash, "t", from: Shop);
        gone.MessageId = "";
        store.InsertMessages(new[] { gone });
        Assert.Empty(store.AutoDeletedThreads(new[] { trash }));
        Assert.Equal((1, 0), store.TrashCounts(new[] { trash }));
        Assert.False(store.IsKeptRestored("A", ""));
    }

    // ───────────────────────── the Trash list: label, counts, filter (option C) ─────────────────────────

    /// <summary>Trash with: one auto-deleted conversation, one of two emails where auto delete took one and the user the
    /// other, one deleted by hand, one deleted in another app (never seen leaving) and one without a Message-ID.</summary>
    private static (MailStore store, long trash, DateTimeOffset at) FilledTrash(TempDir dir)
    {
        var (store, inbox, trash) = Store(dir);
        var at = Local(2026, 11, 21, 18, 24);
        MessageRow In(long folder, string thread, string id) => Rows.Make("A", folder, thread, from: Shop, messageId: id, date: at.AddDays(-30));

        store.RecordTrashed(new[] { In(inbox, "auto", "auto@x") }, at, _ => ShopRule);
        store.RecordTrashed(new[] { In(inbox, "mixed", "m1@x") }, at.AddHours(-5), _ => ShopRule);
        store.RecordTrashed(new[] { In(inbox, "mixed", "m2@x") }, at.AddHours(2), _ => ("r2", "Rule: anyone at shop.test after 1 week"));
        store.RecordTrashed(new[] { In(inbox, "mixed", "m3@x") }, at.AddHours(3));
        store.RecordTrashed(new[] { In(inbox, "hand", "hand@x") }, at);
        var noId = In(trash, "noid", "x");
        noId.MessageId = "";
        store.InsertMessages(new[]
        {
            In(trash, "auto", "auto@x"), In(trash, "mixed", "m1@x"), In(trash, "mixed", "m2@x"), In(trash, "mixed", "m3@x"),
            In(trash, "hand", "hand@x"), In(trash, "elsewhere", "else@x"), noId,
            In(inbox, "inbox-only", "auto@x"),   // a copy of an auto-deleted email outside Trash is not a Trash row
        });
        return (store, trash, at);
    }

    [Fact]
    public void Each_conversation_in_Trash_knows_its_latest_auto_delete_and_how_many()
    {
        using var dir = new TempDir();
        var (store, trash, at) = FilledTrash(dir);
        var found = store.AutoDeletedThreads(new[] { trash });
        Assert.Equal(new[] { "auto", "mixed" }, found.Keys.Select(k => k.ThreadKey).OrderBy(k => k));
        Assert.Equal((at, "r1", 1), (found[("A", "auto")].Latest.At, found[("A", "auto")].Latest.RuleId, found[("A", "auto")].Count));
        // Two of the three emails were auto-deleted; the label shows the later one and its rule.
        Assert.Equal((at.AddHours(2), "r2", 2), (found[("A", "mixed")].Latest.At, found[("A", "mixed")].Latest.RuleId, found[("A", "mixed")].Count));
        Assert.Equal((5, 2), store.TrashCounts(new[] { trash }));
        Assert.Empty(store.AutoDeletedThreads(Array.Empty<long>()));
        Assert.Equal((0, 0), store.TrashCounts(Array.Empty<long>()));
    }

    [Fact]
    public void An_email_shown_by_its_copy_in_another_folder_still_finds_its_note_in_Trash()
    {
        using var dir = new TempDir();
        var (store, inbox, trash) = Store(dir);
        var all = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var at = Local(2026, 11, 21, 18, 24);
        MessageRow In(long folder, string thread, string id) => Rows.Make("A", folder, thread, from: Shop, messageId: id);
        store.RecordTrashed(new[] { In(inbox, "t", "both@x"), In(inbox, "back", "back@x") }, at, _ => ShopRule);
        store.RecordTrashed(new[] { In(inbox, "hand", "hand@x") }, at);
        store.InsertMessages(new[]
        {
            In(trash, "t", "both@x"), In(all, "t", "both@x"),   // not yet gone from All Mail on this PC
            In(inbox, "back", "back@x"),                         // put back in another app: no copy in Trash any more
            In(trash, "hand", "hand@x"),
        });
        // The conversation shows the All Mail copy (it comes before Trash), so the row's own folder says nothing.
        var shown = Assert.Single(store.GetThread("A", "t"));
        Assert.Equal(all, shown.FolderId);
        var notes = store.TrashNotesIn("A", new[] { "both@x", "back@x", "hand@x", "", "none@x" }, new[] { trash });
        Assert.Equal("both@x", Assert.Single(notes).Key);
        Assert.Equal(new TrashNote(at, "r1", "Rule: offers@shop.test after 30 days", inbox), notes["both@x"]);
        Assert.Empty(store.TrashNotesIn("A", new[] { "both@x" }, Array.Empty<long>()));
        Assert.Empty(store.TrashNotesIn("A", new[] { "both@x" }, new[] { inbox }));
        Assert.Empty(store.TrashNotesIn("B", new[] { "both@x" }, new[] { trash }));
        // The list agrees: the conversation in Trash is labelled.
        Assert.Equal("t", Assert.Single(store.AutoDeletedThreads(new[] { trash })).Key.ThreadKey);
    }

    [Fact]
    public void An_email_that_left_Trash_in_another_app_is_not_auto_deleted_when_it_comes_back()
    {
        using var dir = new TempDir();
        var (store, inbox, trash) = Store(dir);
        var at = Local(2026, 11, 21, 18, 24);
        MessageRow In(long folder, string thread, string id) => Rows.Make("A", folder, thread, from: Shop, messageId: id);
        store.RecordTrashed(new[] { In(inbox, "left", "left@x"), In(inbox, "stays", "stays@x"), In(inbox, "again", "again@x"), In(inbox, "queued", "queued@x") }, at, _ => ShopRule);
        store.RecordTrashed(new[] { In(inbox, "hand", "hand@x") }, at);
        var rows = store.InsertMessages(new[]
        {
            In(trash, "left", "left@x"), In(trash, "stays", "stays@x"), In(trash, "hand", "hand@x"),
            In(trash, "again", "again@x"), In(trash, "again", "again@x"),   // auto-deleted a second time: the new copy is listed already
            In(trash, "queued", "queued@x"),
        });
        long Uid(string id, int nth = 0) => rows.Where(r => r.MessageId == id).ElementAt(nth).Uid;
        // Auto delete took "queued" again; its move to Trash has not reached the server yet.
        store.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 9001, Kind = PendingOpKind.Move, Arg = trash, MessageId = "queued@x" });

        // The mail check finds these gone from Trash on the server.
        var gone = new[] { Uid("left@x"), Uid("hand@x"), Uid("again@x"), Uid("queued@x"), 424242 };
        Assert.Equal(1, store.EndAutoDeleted(trash, gone));
        store.DeleteUids(trash, gone);
        Assert.Equal(new[] { "again@x", "queued@x", "stays@x" },
            store.TrashNotes("A", new[] { "left@x", "stays@x", "again@x", "queued@x", "hand@x" }).Keys.OrderBy(k => k));
        Assert.Equal(inbox, store.TrashOrigin("A", "left@x"));   // Restore still knows where it was
        Assert.Equal(0, store.EndAutoDeleted(trash, Array.Empty<long>()));
        Assert.Equal(0, store.EndAutoDeleted(inbox, gone));

        // Deleted by hand in the other app later: it is listed in Trash again, and is not "auto-deleted".
        store.InsertMessages(new[] { In(trash, "left", "left@x") });
        store.StampTrash(trash, at.AddDays(2));
        Assert.DoesNotContain(store.AutoDeletedThreads(new[] { trash }).Keys, k => k.ThreadKey == "left");
        Assert.Equal(new[] { "left" }, store.ListThreads(new ListQuery { FolderIds = new[] { trash }, TrashReason = TrashReason.NotAutoDeleted }, DateTimeOffset.Now).Select(t => t.ThreadKey));
        Assert.Equal((3, 2), store.TrashCounts(new[] { trash }));
        Assert.Empty(store.TrashNotesIn("A", new[] { "left@x" }, new[] { trash }));
        // Auto delete taking it once more writes a new note.
        store.RecordTrashed(new[] { In(inbox, "left", "left@x") }, at.AddDays(3), _ => ShopRule);
        Assert.Equal(at.AddDays(3), store.TrashNotesIn("A", new[] { "left@x" }, new[] { trash })["left@x"].At);
    }

    [Fact]
    public void The_Trash_filter_splits_the_list_in_two_that_add_up_to_all()
    {
        using var dir = new TempDir();
        var (store, trash, _) = FilledTrash(dir);
        List<string> Threads(TrashReason reason) => store.ListThreads(new ListQuery { FolderIds = new[] { trash }, TrashReason = reason }, DateTimeOffset.Now)
            .Select(t => t.ThreadKey).OrderBy(k => k).ToList();

        Assert.Equal(new[] { "auto", "elsewhere", "hand", "mixed", "noid" }, Threads(TrashReason.Any));
        Assert.Equal(new[] { "auto", "mixed" }, Threads(TrashReason.AutoDeleted));
        Assert.Equal(new[] { "elsewhere", "hand", "noid" }, Threads(TrashReason.NotAutoDeleted));

        var (all, auto) = store.TrashCounts(new[] { trash });
        Assert.Equal(Threads(TrashReason.Any).Count, all);
        Assert.Equal(Threads(TrashReason.AutoDeleted).Count, auto);
        Assert.Equal(Threads(TrashReason.NotAutoDeleted).Count, all - auto);
    }

    [Fact]
    public void The_filter_words_carry_the_counts_and_it_shows_only_when_it_can_tell_two_kinds_apart()
    {
        Assert.Equal("All · 6", AutoDeleted.FilterLabel(TrashReason.Any, 6, 4));
        Assert.Equal("Auto-deleted · 4", AutoDeleted.FilterLabel(TrashReason.AutoDeleted, 6, 4));
        Assert.Equal("Deleted by you or elsewhere · 2", AutoDeleted.FilterLabel(TrashReason.NotAutoDeleted, 6, 4));
        Assert.Equal("All · 1,250", AutoDeleted.FilterLabel(TrashReason.Any, 1250, 0));
        Assert.Equal("Deleted by you or elsewhere · 0", AutoDeleted.FilterLabel(TrashReason.NotAutoDeleted, 3, 5));
        Assert.True(AutoDeleted.ShowFilter(1));
        Assert.False(AutoDeleted.ShowFilter(0));

        Assert.Equal("Everything in Trash", AutoDeleted.FilterTip(TrashReason.Any));
        Assert.Equal("Only the conversations that Magpie's auto delete moved to Trash", AutoDeleted.FilterTip(TrashReason.AutoDeleted));
        Assert.Equal("Only the conversations you deleted yourself, here or in another app", AutoDeleted.FilterTip(TrashReason.NotAutoDeleted));
        Assert.Equal("", AutoDeleted.EmptyText(TrashReason.Any));
        Assert.Equal("Auto delete has not moved anything here.", AutoDeleted.EmptyText(TrashReason.AutoDeleted));
        Assert.Equal("Everything here was moved by auto delete.", AutoDeleted.EmptyText(TrashReason.NotAutoDeleted));
    }

    // ───────────────────────── every word: row label, banner, note ─────────────────────────

    [Fact]
    public void The_label_banner_and_note_say_the_date_and_time_and_the_rule()
    {
        var at = Local(2026, 11, 21, 18, 24);
        var now = Local(2026, 11, 22, 9, 0);
        var note = new TrashNote(at, "r1", "Rule: offers@shop.test after 30 days", 7);

        Assert.Equal("Deleted by Magpie auto delete", AutoDeleted.Title);
        Assert.Equal("Auto-deleted · 21 Nov, 18:24", AutoDeleted.RowLabel(at, now));
        Assert.Equal("auto-deleted 21 Nov, 18:24", AutoDeleted.MessageNote(at, now));
        Assert.Equal("Saturday, 21 November 2026, 18:24", AutoDeleted.When(at));
        // Another year: the short date carries it. Early morning keeps its leading zero (24-hour clock).
        Assert.Equal("Auto-deleted · 21 Nov 2026, 18:24", AutoDeleted.RowLabel(at, Local(2027, 1, 2, 9, 0)));
        Assert.Equal("3 Jan, 06:05", AutoDeleted.ShortWhen(Local(2026, 1, 3, 6, 5), now));
        // Written in this PC's time, whatever zone the moment was kept in.
        Assert.Equal(AutoDeleted.RowLabel(at, now), AutoDeleted.RowLabel(at.ToUniversalTime(), now.ToUniversalTime()));

        Assert.Equal("Rule: offers@shop.test after 30 days · Arrived 22 Oct 2026 · Was in Inbox",
            AutoDeleted.Details(note, Local(2026, 10, 22, 8, 0), " Inbox "));
        Assert.Equal("Rule: offers@shop.test after 30 days", AutoDeleted.Details(note, null, ""));
        Assert.Equal("Its rule was no longer there · Was in Receipts", AutoDeleted.Details(note with { RuleLine = "" }, null, "Receipts"));

        Assert.Equal("", AutoDeleted.ManyLine(1));
        Assert.Equal("3 emails of this conversation were auto-deleted; the date above is the latest one.", AutoDeleted.ManyLine(3));
        Assert.Equal("Deleted by Magpie auto delete\nSaturday, 21 November 2026, 18:24\nRule: offers@shop.test after 30 days", AutoDeleted.Tip(note));
        Assert.EndsWith("\n2 emails of this conversation were auto-deleted; the date above is the latest one.", AutoDeleted.Tip(note, 2));

        Assert.Equal("Puts it back in Receipts and keeps it from auto delete", AutoDeleted.RestoreTip("Receipts"));
        Assert.Equal("Puts it back in the Inbox and keeps it from auto delete", AutoDeleted.RestoreTip(null));
    }

    [Fact]
    public void The_open_email_carries_its_auto_deleted_note_next_to_the_date()
    {
        var gone = Rows.Make("A", 1, "t", from: Shop, subject: "Sale");
        var byHand = Rows.Make("A", 1, "t", from: Shop, subject: "Sale");
        var now = DateTimeOffset.Now;
        var plain = new[] { new RenderMessage { Row = gone }, new RenderMessage { Row = byHand, Expanded = true } };
        var noted = new[]
        {
            new RenderMessage { Row = gone, DeletedNote = "auto-deleted 21 Nov, 18:24", DeletedTip = "Deleted by Magpie auto delete\nRule: <shop>" },
            new RenderMessage { Row = byHand, Expanded = true },
        };
        var page = HtmlRenderer.BuildConversation("Sale", noted, false, now).Html;
        Assert.Contains("<span class=\"dd weeks gone\" title=\"Deleted by Magpie auto delete\nRule: &lt;shop&gt;\">🗑 auto-deleted 21 Nov, 18:24</span>", page);
        Assert.Equal(1, page.Split("class=\"dd ").Length - 1);
        Assert.DoesNotContain("class=\"dd ", HtmlRenderer.BuildConversation("Sale", plain, false, now).Html);

        // The page is drawn again when the note appears or its words change.
        string Print(IReadOnlyList<RenderMessage> list) => ReaderPage.ContentFingerprint("Sale", list, false, false, 250, now);
        Assert.NotEqual(Print(plain), Print(noted));
        var later = new[] { new RenderMessage { Row = gone, DeletedNote = "auto-deleted 22 Nov, 07:00", DeletedTip = noted[0].DeletedTip }, noted[1] };
        Assert.NotEqual(Print(noted), Print(later));
        Assert.Equal(Print(noted), Print(noted.ToArray()));
    }

    // ───────────────────────── Restore keeps it ─────────────────────────

    [Fact]
    public void A_restored_auto_deleted_email_is_not_deleted_again_by_its_rule()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out var trash);
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", receipts, "old", from: Shop, date: now.AddDays(-40), messageId: "old@shop"),
            Rows.Make("A", inbox, "other", from: Shop, date: now.AddDays(-45), messageId: "other@shop"),
        });
        var rule = new AutoDeleteRule { Pattern = Shop, Amount = 30 };
        Assert.Equal(2, e.SaveAutoDeleteRule(rule, startOnExisting: true));
        Assert.Equal(2, e.RunDueDeletes(now));
        // The check lists both in Trash; the user takes one back.
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", trash, "old", from: Shop, date: now.AddDays(-40), messageId: "old@shop"),
            Rows.Make("A", trash, "other", from: Shop, date: now.AddDays(-45), messageId: "other@shop"),
        });
        foreach (var op in e.Store.GetPendingOps("A")) e.Store.RemovePendingOp(op.Id);
        Assert.False(e.Store.IsKeptRestored("A", "old@shop"));
        e.Restore("A", "old");

        var move = Assert.Single(e.Store.GetPendingOps("A"));
        Assert.Equal((PendingOpKind.Move, receipts), (move.Kind, move.Arg));            // back where it was
        Assert.Empty(e.Store.TrashNotes("A", new[] { "old@shop" }));                     // no longer "auto-deleted"
        Assert.True(e.Store.IsKeptRestored("A", "old@shop"));
        Assert.False(e.Store.IsKeptRestored("A", "other@shop"));
        Assert.Equal("other", Assert.Single(e.Store.AutoDeletedThreads(new[] { trash })).Key.ThreadKey);

        // The next check lists it in Receipts again, 40 days old under a 30-day rule: it gets no timer, by arrival or by the rule.
        var back = Rows.Make("A", receipts, "old", from: Shop, date: now.AddDays(-40), messageId: "old@shop");
        var fresh = Rows.Make("A", inbox, "fresh", from: Shop, date: now, messageId: "fresh@shop");
        e.Store.InsertMessages(new[] { back, fresh });
        var listed = e.Store.MessagesInFolder(receipts).Concat(e.Store.MessagesInFolder(inbox)).ToList();
        e.TagArrivals("A", listed);
        e.SaveAutoDeleteRule(rule, startOnExisting: true);
        Assert.Empty(e.Store.DeleteTimers("A", "old"));
        Assert.Single(e.Store.DeleteTimers("A", "fresh"));                             // other emails of the sender still get theirs
        Assert.Equal(0, e.RunDueDeletes(now.AddMinutes(1)));
        Assert.Single(e.Store.MessagesInFolder(receipts));
    }

    [Fact]
    public void Restoring_an_email_deleted_by_hand_does_not_keep_it_from_a_rule()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var trash);
        var now = DateTimeOffset.Now;
        var row = Rows.Make("A", inbox, "t", from: Shop, date: now.AddDays(-1), messageId: "t@shop");
        e.Store.RecordTrashed(new[] { row }, now);
        e.Store.InsertMessages(new[] { Rows.Make("A", trash, "t", from: Shop, date: now.AddDays(-1), messageId: "t@shop") });
        e.Restore("A", "t");
        Assert.False(e.Store.IsKeptRestored("A", "t@shop"));
        e.Store.InsertMessages(new[] { row });
        Assert.Equal(1, e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = Shop, Amount = 30 }, startOnExisting: true));
    }

    [Fact]
    public void Kept_after_restore_stops_every_timer_on_that_email_only()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Store(dir);
        store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "k", messageId: "k@x"), Rows.Make("A", inbox, "f", messageId: "f@x"), Rows.Make("B", inbox, "k2", messageId: "k@x"),
        });
        store.KeepRestored("A", new[] { "k@x", "k@x", "" }, DateTimeOffset.Now);
        store.KeepRestored("A", new[] { "k@x" }, DateTimeOffset.Now.AddMinutes(1));   // again: no error, still one entry
        var rows = store.MessagesInFolder(inbox);
        var at = DateTimeOffset.Now.AddDays(7);
        Assert.Equal(2, store.SetDeleteTimers(rows.Select(m => (m.Id, at)), "r1"));
        Assert.Empty(store.DeleteTimers("A", "k"));
        Assert.Single(store.DeleteTimers("A", "f"));
        Assert.Single(store.DeleteTimers("B", "k2"));
    }

    // ───────────────────────── a mail file from before 20.0.0 ─────────────────────────

    [Fact]
    public void An_older_mail_file_gets_the_reason_columns_and_reads_old_Trash_as_not_auto_deleted()
    {
        using var dir = new TempDir();
        var (store, inbox, trash) = Store(dir);
        var old = Rows.Make("A", inbox, "old", from: Shop, messageId: "old@x");
        store.RecordTrashed(new[] { old }, DateTimeOffset.Now.AddDays(-3));
        store.InsertMessages(new[] { Rows.Make("A", trash, "old", from: Shop, messageId: "old@x") });
        SqliteConnection.ClearAllPools();
        // A mail file written by 19.0.0: no reason, no rule, no list of restored emails.
        Sql(dir, "ALTER TABLE trash_from DROP COLUMN reason");
        Sql(dir, "ALTER TABLE trash_from DROP COLUMN rule_id");
        Sql(dir, "ALTER TABLE trash_from DROP COLUMN rule_line");
        Sql(dir, "DROP TABLE auto_delete_kept");
        SqliteConnection.ClearAllPools();

        var reopened = new MailStore(dir.File("mail.db"));
        Assert.Empty(reopened.TrashNotes("A", new[] { "old@x" }));
        Assert.Equal(inbox, reopened.TrashOrigin("A", "old@x"));                       // what it knew is kept
        Assert.Equal((1, 0), reopened.TrashCounts(new[] { trash }));
        Assert.Equal("old", Assert.Single(reopened.ListThreads(new ListQuery { FolderIds = new[] { trash }, TrashReason = TrashReason.NotAutoDeleted }, DateTimeOffset.Now)).ThreadKey);
        Assert.False(reopened.IsKeptRestored("A", "old@x"));

        // From then on auto deletes are noted, and a restored one is kept.
        var at = Local(2026, 11, 21, 18, 24);
        reopened.RecordTrashed(new[] { Rows.Make("A", inbox, "new", from: Shop, messageId: "new@x") }, at, _ => ShopRule);
        reopened.InsertMessages(new[] { Rows.Make("A", trash, "new", from: Shop, messageId: "new@x") });
        Assert.Equal(at, reopened.TrashNotes("A", new[] { "new@x" })["new@x"].At);
        Assert.Equal((2, 1), reopened.TrashCounts(new[] { trash }));
        reopened.KeepRestored("A", new[] { "new@x" }, at);
        Assert.True(reopened.IsKeptRestored("A", "new@x"));

        // Opening it once more changes nothing.
        SqliteConnection.ClearAllPools();
        var again = new MailStore(dir.File("mail.db"));
        Assert.Single(again.TrashNotes("A", new[] { "old@x", "new@x" }));
        Assert.True(again.IsKeptRestored("A", "new@x"));
    }
}
