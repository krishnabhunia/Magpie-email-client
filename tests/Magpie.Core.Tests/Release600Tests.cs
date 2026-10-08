using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Updates;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>5.0.0: delete three ways (design DX1) and the words on the title-bar Update button (design UB1).</summary>
public class Release600Tests
{
    private static MailEngine Engine(TempDir dir, out long inbox, out long receipts, out long allMail, out long trash, out long sent)
    {
        var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        inbox = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        receipts = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Receipts", Name = "Receipts" });
        allMail = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        trash = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        sent = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        return e;
    }

    // ───────────── DX1-A: delete for good ─────────────

    [Fact]
    public async Task Delete_forever_takes_every_copy_of_each_email_and_skips_Trash()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out var allMail, out var trash, out _);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t", messageId: "m1@x"),
            Rows.Make("A", allMail, "t", messageId: "m1@x"),                                  // Gmail's All Mail copy
            Rows.Make("A", inbox, "t", messageId: "m2@x", flags: MessageFlags.Flagged),      // pinned: still goes, the user chose it
            Rows.Make("A", receipts, "t", messageId: "m3@x"),                                // same conversation, not in the view
            Rows.Make("A", inbox, "other", messageId: "o1@x"),
        });
        e.Store.RecordTrashed(e.Store.MessagesInFolder(inbox).Where(m => m.MessageId == "m1@x"), DateTimeOffset.Now);

        Assert.Equal(2, e.CountDeleteForever("A", new[] { "t" }, new[] { inbox }));          // m1 (two copies) + m2
        Assert.Equal(2, await e.DeleteForeverAsync("A", new[] { "t" }, new[] { inbox }));

        Assert.Equal("m3@x", Assert.Single(e.Store.GetThreadCopies("A", "t")).MessageId);     // only the Receipts one is left
        Assert.Single(e.Store.MessagesInFolder(inbox));                                        // "other"
        Assert.Empty(e.Store.MessagesInFolder(allMail));
        var ops = e.Store.GetPendingOps("A");
        Assert.Equal(3, ops.Count);
        Assert.All(ops, o => Assert.Equal(PendingOpKind.Delete, o.Kind));                       // \Deleted + expunge, never a move to Trash
        Assert.Equal(new[] { inbox, inbox, allMail }, ops.Select(o => o.FolderId).OrderBy(f => f));
        Assert.Empty(e.Store.MessagesInFolder(trash));
        Assert.Null(e.Store.TrashOrigin("A", "m1@x"));                                          // nothing to restore
        Assert.Equal(0, await e.DeleteForeverAsync("A", new[] { "t" }, new[] { inbox }));
    }

    [Fact]
    public void A_rule_that_deletes_trashes_every_copy_of_the_email()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var allMail, out var trash, out _);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "p", from: "promo@shop.com", messageId: "p1@x"),
            Rows.Make("A", allMail, "p", from: "promo@shop.com", messageId: "p1@x"),
            Rows.Make("A", inbox, "k", from: "boss@work.com", messageId: "k1@x"),
        });
        e.Config.Rules.Add(new MailRule { Name = "Promos", Conditions = { new() { Field = RuleField.From, Op = RuleOp.Is, Value = "promo@shop.com" } }, Actions = { new() { Kind = RuleActionKind.Delete } } });
        var arrivals = e.Store.MessagesInFolder(inbox);
        var quiet = e.RunRules("A", arrivals);

        Assert.Contains(arrivals.Single(m => m.MessageId == "p1@x").Id, quiet);
        Assert.Empty(e.Store.MessagesInFolder(allMail));                                        // the All Mail copy went too
        Assert.Equal("k1@x", Assert.Single(e.Store.MessagesInFolder(inbox)).MessageId);
        Assert.Equal(2, e.Store.GetPendingOps("A").Count(o => o.Kind == PendingOpKind.Move && o.Arg == trash));
        Assert.Equal(inbox, e.Store.TrashOrigin("A", "p1@x"));                                   // Restore knows the main copy's folder
    }

    // ───────────── DX1-B1: one dialog, past and future ─────────────

    private static void SeedSender(MailEngine e, long inbox, long receipts, long trash, long sent, DateTimeOffset now)
    {
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "o1", from: "anita@xyz.com", date: now.AddDays(-30), messageId: "o1@x"),
            Rows.Make("A", receipts, "o2", from: "anita@xyz.com", date: now.AddDays(-400), messageId: "o2@x"),  // outside the Inbox
            Rows.Make("A", inbox, "n1", from: "anita@xyz.com", date: now.AddDays(-2), messageId: "n1@x"),
            Rows.Make("A", receipts, "n2", from: "anita@xyz.com", date: now.AddDays(-1), messageId: "n2@x"),
            Rows.Make("A", inbox, "pin", from: "anita@xyz.com", date: now.AddDays(-50), flags: MessageFlags.Flagged, messageId: "pin@x"),
            Rows.Make("A", trash, "tr", from: "anita@xyz.com", date: now.AddDays(-50), messageId: "tr@x"),
            Rows.Make("A", sent, "se", from: "anita@xyz.com", date: now.AddDays(-50), messageId: "se@x"),
            Rows.Make("A", inbox, "d1", from: "ravi@xyz.com", date: now.AddDays(-9), messageId: "d1@x"),
        });
    }

    [Fact]
    public void Past_summary_counts_all_older_and_oldest_in_every_folder_but_Trash_Spam_Sent_and_Drafts()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out _, out var trash, out var sent);
        var now = DateTimeOffset.Now;
        SeedSender(e, inbox, receipts, trash, sent, now);

        var s = e.PastSummary("anita@xyz.com", "", AutoDelete.KeepSince(false, 7, DeleteUnit.Days, now));
        Assert.Equal((4, 2, 2), (s.Total, s.Older, s.Kept));
        Assert.Equal(now.AddDays(-400).ToUnixTimeMilliseconds(), s.Oldest!.Value.ToUnixTimeMilliseconds());
        Assert.Equal((4, 4), (e.PastSummary("anita@xyz.com", "", null).Total, e.PastSummary("anita@xyz.com", "", null).Older));   // Nothing = all
        Assert.Equal(5, e.PastSummary("*@xyz.com", "", null).Total);
        Assert.Equal(0, e.PastSummary("anita@xyz.com", "B", null).Total);                       // another account
        Assert.Equal(4, e.PastFrom("anita@xyz.com", null, "A").Count);
        Assert.Equal(0, e.PastSummary("not an address", "", null).Total);
    }

    [Fact]
    public void Existing_mail_for_a_rule_covers_every_folder_the_past_delete_covers()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out _, out var trash, out var sent);
        var now = DateTimeOffset.Now;
        SeedSender(e, inbox, receipts, trash, sent, now);
        var rows = e.ExistingFor(new AutoDeleteRule { Pattern = "anita@xyz.com" });
        Assert.Equal(new[] { "n1@x", "n2@x", "o1@x", "o2@x" }, rows.Select(m => m.MessageId).OrderBy(x => x));   // Receipts in; pinned, Trash, Sent out
        Assert.Empty(e.ExistingFor(new AutoDeleteRule { Pattern = "anita@xyz.com", AccountId = "B" }));
    }

    [Fact]
    public void Past_and_future_in_one_run_lists_the_old_ones_and_times_the_kept_ones_outside_the_Inbox_too()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out _, out var trash, out var sent);
        var now = DateTimeOffset.Now;
        SeedSender(e, inbox, receipts, trash, sent, now);

        var r = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "Anita@XYZ.com", Past = true, Future = true, Amount = 7, Unit = DeleteUnit.Days }, now);
        Assert.Equal(new[] { "o1@x", "o2@x" }, r.Past.Rows.Select(m => m.MessageId).OrderBy(x => x));
        Assert.Equal(2, r.Past.Count);
        Assert.NotNull(r.Rule);
        Assert.True(r.RuleIsNew);
        Assert.Equal(("anita@xyz.com", 7, DeleteUnit.Days, false), (r.Rule!.Pattern, r.Rule.Amount, r.Rule.Unit, r.Rule.Otp));
        Assert.Equal(2, r.TimersSet);                                                          // n1 (Inbox) and n2 (Receipts)
        Assert.Single(e.AutoDeleteRules());
        var n2 = Assert.Single(e.Store.DeleteTimers("A", "n2"));
        Assert.Equal(now.AddDays(-1).AddDays(7).ToUnixTimeMilliseconds(), n2.At.ToUnixTimeMilliseconds());   // counted from arrival
        Assert.Empty(e.Store.DeleteTimers("A", "o1"));                                          // the old ones are for the past delete, not a timer
        Assert.Empty(e.Store.DeleteTimers("A", "pin"));
        Assert.Equal(0, e.RunDueDeletes(now));                                                  // nothing goes behind the undo wait's back
        Assert.Equal(8, e.Store.MessagesInFolder(inbox).Count + e.Store.MessagesInFolder(receipts).Count + e.Store.MessagesInFolder(trash).Count + e.Store.MessagesInFolder(sent).Count);

        // The caller trashes the past rows after its undo wait.
        e.TrashEmails(r.Past.Rows);
        Assert.Equal(2, e.Store.GetPendingOps("A").Count(o => o.Kind == PendingOpKind.Move && o.Arg == trash));
        Assert.DoesNotContain(e.Store.MessagesInFolder(receipts), m => m.MessageId == "o2@x");

        // A second run for the same sender changes the one rule; it is not new, so an undo would not remove it.
        var again = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "anita@xyz.com", Future = true, Amount = 3, Unit = DeleteUnit.Days }, now);
        Assert.False(again.RuleIsNew);
        Assert.Equal(r.Rule.Id, again.Rule!.Id);
        Assert.Equal(3, Assert.Single(e.AutoDeleteRules()).Amount);
    }

    [Fact]
    public void Future_only_times_the_emails_not_yet_past_the_time_and_leaves_the_rest_unless_PAST_is_on()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out _, out var trash, out var sent);
        var now = DateTimeOffset.Now;
        SeedSender(e, inbox, receipts, trash, sent, now);

        var r = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "anita@xyz.com", Future = true, Amount = 7, Unit = DeleteUnit.Days, StartOnExisting = true }, now);
        Assert.Equal(0, r.Past.Count);
        Assert.Equal(2, r.TimersSet);
        Assert.Empty(e.Store.DeleteTimers("A", "o1"));                                          // 30 days old: left alone, PAST was off
        Assert.Equal(0, e.RunDueDeletes(now));

        e.RemoveAutoDeleteRule(r.Rule!.Id, clearTimers: true);
        var off = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "anita@xyz.com", Future = true, Amount = 7, Unit = DeleteUnit.Days, StartOnExisting = false }, now);
        Assert.Equal(0, off.TimersSet);                                                         // the setting is off: new mail only
        Assert.Empty(e.Store.DeleteTimers("A", "n1"));
    }

    [Fact]
    public void Nothing_deletes_every_past_email_and_makes_no_rule_and_OTP_counts_24_hours()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out _, out var trash, out var sent);
        var now = DateTimeOffset.Now;
        SeedSender(e, inbox, receipts, trash, sent, now);

        var all = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "*@xyz.com", Past = true, Future = true, KeepNothing = true }, now);
        Assert.Equal(5, all.Past.Count);                                                        // the domain: Anita's 4 + Ravi's 1
        Assert.Null(all.Rule);
        Assert.Empty(e.AutoDeleteRules());

        var otp = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "anita@xyz.com", Future = true, Otp = true, AccountId = "A" }, now);
        Assert.True(otp.Rule!.Otp);
        Assert.Equal("A", otp.Rule.AccountId);
        Assert.Equal(0, otp.TimersSet);                                                         // everything here is older than 24 h
        var cut = AutoDelete.KeepSince(true, 1, DeleteUnit.Days, now.AddHours(-1));          // 25 h ago: n2 (24 h) is kept
        Assert.Equal((3, 1), (e.PastSummary("anita@xyz.com", "A", cut).Older, e.PastSummary("anita@xyz.com", "A", cut).Kept));

        Assert.Throws<ArgumentException>(() => e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "nonsense", Past = true }, now));
    }

    [Fact]
    public void Dialog_words_follow_the_ticks_and_the_counts()
    {
        Assert.Equal("Delete 212 now and keep deleting", AutoDelete.ButtonText(true, true, 212));
        Assert.Equal("Delete 212 now", AutoDelete.ButtonText(true, false, 212));
        Assert.Equal("Create rule", AutoDelete.ButtonText(false, true, 0));
        Assert.Equal("Save rule", AutoDelete.ButtonText(false, true, 0, editing: true));
        Assert.Equal("", AutoDelete.ButtonText(false, false, 5));
        Assert.Equal("", AutoDelete.ButtonText(true, false, 0));                                // nothing older than the kept period
        Assert.Equal("Create rule", AutoDelete.ButtonText(true, true, 0));

        var oldest = new DateTimeOffset(2024, 3, 12, 9, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal("delete the 212 older than 1 week now (oldest 12 Mar 2024). Keeps 28 from the last week, pinned ones, Sent and Drafts.",
            AutoDelete.PastLine(new AutoDelete.PastSummary(240, 212, oldest), false, "1 week"));
        Assert.Equal("delete all 240 now (oldest 12 Mar 2024). Keeps pinned ones, Sent and Drafts.", AutoDelete.PastLine(new AutoDelete.PastSummary(240, 240, oldest), true, "1 week"));
        Assert.Equal("none older than 1 month here now. Keeps 3 from the last month, pinned ones, Sent and Drafts.", AutoDelete.PastLine(new AutoDelete.PastSummary(3, 0, null), false, "1 month"));
        Assert.Equal("delete the 1 older than 3 days now. Keeps 0 from the last 3 days, pinned ones, Sent and Drafts.", AutoDelete.PastLine(new AutoDelete.PastSummary(1, 1, null), false, "3 days"));
        Assert.Equal("delete all 2 now. Keeps pinned ones, Sent and Drafts.", AutoDelete.PastLine(new AutoDelete.PastSummary(2, 2, null), true, "1 week"));
        Assert.Equal("delete each one 1 week after it arrives (an auto-delete rule you can pause or remove in Settings → Rules).", AutoDelete.FutureLine("1 week"));

        Assert.Equal(new[] { "1 day", "3 days", "1 week", "2 weeks", "1 month", "3 months", "6 months", "1 year" }, AutoDelete.KeepChoices.Select(c => AutoDelete.KeepLabel(false, c.Amount, c.Unit)));
        Assert.Equal("24 hours", AutoDelete.KeepLabel(true, 7, DeleteUnit.Days));
        Assert.Equal("2 years", AutoDelete.KeepLabel(false, 2, DeleteUnit.Years));
        var now = new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(5.5));
        Assert.Equal(now.AddDays(-7), AutoDelete.KeepSince(false, 7, DeleteUnit.Days, now));
        Assert.Equal(now.AddMonths(-3), AutoDelete.KeepSince(false, 3, DeleteUnit.Months, now));
        Assert.Equal(now.AddHours(-24), AutoDelete.KeepSince(true, 7, DeleteUnit.Days, now));
        Assert.StartsWith("Next email from *@xyz.com arriving today ", AutoDelete.PreviewLine("*@xyz.com", new AutoDeleteRule { Amount = 7 }, now));
        Assert.EndsWith(" and carry the tag", AutoDelete.PreviewLine("*@xyz.com", new AutoDeleteRule { Amount = 7 }, now));

        var rule = new AutoDeleteRule { Pattern = "*@xyz.com", Amount = 7 };
        var empty = AutoDelete.PastEmails.Of(Array.Empty<MessageRow>());
        Assert.Equal("Emails from anyone at xyz.com will be deleted 1 week after they arrive · 28 already here timed", AutoDelete.ToastText("*@xyz.com", new AutoDelete.DeleteFromResult(empty, rule, true, 28)));
        var some = AutoDelete.PastEmails.Of(new[] { Rows.Make("A", 1, "x", messageId: "1@x"), Rows.Make("A", 1, "y", messageId: "2@x") });
        Assert.Equal("Deleting 2 emails from *@xyz.com · new ones go 1 week after they arrive", AutoDelete.ToastText("*@xyz.com", new AutoDelete.DeleteFromResult(some, rule, true, 0)));
        Assert.Equal("Deleting 2 emails from *@xyz.com", AutoDelete.ToastText("*@xyz.com", new AutoDelete.DeleteFromResult(some, null, false, 0)));
        var one = AutoDelete.PastEmails.Of(new[] { Rows.Make("A", 1, "x", messageId: "1@x") });
        Assert.Equal("Deleting 1 email from anita@xyz.com", AutoDelete.ToastText("anita@xyz.com", new AutoDelete.DeleteFromResult(one, null, false, 0)));
        Assert.Contains(HoverMenus.ForAddress("Anita Rao", "anita@xyz.com", isMe: false, hasCalendar: false, picturesTrusted: true), o => o.Id == "E10" && o.Label == "Delete emails from Anita…");
    }

    [Fact]
    public async Task Delete_forever_leaves_the_same_MessageId_in_another_account()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _, out _, out _);
        var inboxB = e.Store.UpsertFolder(new MailFolder { AccountId = "B", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t", messageId: "same@x"),
            Rows.Make("B", inboxB, "t", messageId: "same@x"),     // the same email received in another account
        });
        Assert.Equal(1, await e.DeleteForeverAsync("A", new[] { "t" }, new[] { inbox }));
        Assert.Empty(e.Store.MessagesInFolder(inbox));
        Assert.Single(e.Store.MessagesInFolder(inboxB));
        Assert.Empty(e.Store.GetPendingOps("B"));
    }

    [Fact]
    public void A_rule_that_deletes_leaves_Sent_and_Drafts_copies()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var allMail, out var trash, out var sent);
        var drafts = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Drafts", Name = "Drafts", Role = FolderRole.Drafts });
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "n", from: "me@test.local", messageId: "note@x"),      // a note to myself
            Rows.Make("A", sent, "n", from: "me@test.local", messageId: "note@x"),
            Rows.Make("A", drafts, "n", from: "me@test.local", messageId: "note@x"),
            Rows.Make("A", allMail, "n", from: "me@test.local", messageId: "note@x"),
        });
        e.Config.Rules.Add(new MailRule { Name = "Notes", Conditions = { new() { Field = RuleField.From, Op = RuleOp.Is, Value = "me@test.local" } }, Actions = { new() { Kind = RuleActionKind.Delete } } });
        e.RunRules("A", e.Store.MessagesInFolder(inbox));
        Assert.Empty(e.Store.MessagesInFolder(inbox));
        Assert.Empty(e.Store.MessagesInFolder(allMail));
        Assert.Single(e.Store.MessagesInFolder(sent));
        Assert.Single(e.Store.MessagesInFolder(drafts));
        Assert.Equal(2, e.Store.GetPendingOps("A").Count(o => o.Kind == PendingOpKind.Move && o.Arg == trash));
    }

    [Fact]
    public void Editing_a_rule_into_an_existing_sender_merges_to_one_rule()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _, out _, out _);
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "r", from: "ravi@xyz.com", date: now.AddDays(-1), messageId: "r@x") });
        var anita = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "anita@xyz.com", Future = true, Amount = 7 }, now).Rule!;
        var ravi = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "ravi@xyz.com", Future = true, Amount = 30 }, now).Rule!;
        Assert.Equal(ravi.Id, Assert.Single(e.Store.DeleteTimers("A", "r")).Rule);
        Assert.Equal(2, e.AutoDeleteRules().Count);

        // Change Anita's rule to Ravi's address: one rule is left, with the edited time, and Ravi's timers follow it.
        var merged = e.ApplyDeleteFrom(new AutoDelete.DeleteFromRequest { Pattern = "ravi@xyz.com", Future = true, Amount = 3, RuleId = anita.Id }, now);
        Assert.False(merged.RuleIsNew);
        var only = Assert.Single(e.AutoDeleteRules());
        Assert.Equal(("ravi@xyz.com", 3), (only.Pattern, only.Amount));
        Assert.Equal(only.Id, merged.Rule!.Id);
        Assert.Equal(only.Id, Assert.Single(e.Store.DeleteTimers("A", "r")).Rule);
    }

    // ───────────── UB1: the Update button's words ─────────────

    [Theory]
    [InlineData("en-US")] [InlineData("en-IN")] [InlineData("de-DE")] [InlineData("")]
    public void Update_button_words_are_the_same_in_every_culture(string culture)
    {
        var before = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture);
        try
        {
            var c = culture.Length == 0 ? System.Globalization.CultureInfo.InvariantCulture : System.Globalization.CultureInfo.GetCultureInfo(culture);
            System.Globalization.CultureInfo.CurrentCulture = c;
            System.Globalization.CultureInfo.CurrentUICulture = c;
            Assert.Equal("Downloading · 62 %", UpdateText.ButtonText("downloading", "4.1.0", 0.62));
            Assert.Equal("Downloading · 0 %", UpdateText.ButtonText("downloading", "4.1.0", 0));
            Assert.Equal("Update 4.1.0", UpdateText.ButtonText("available", "4.1.0", 0.5));
            Assert.Equal("checked 2 h ago", UpdateText.CheckedAgo(DateTimeOffset.Now.AddHours(-2), DateTimeOffset.Now));
        }
        finally
        {
            (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture) = before;
        }
    }


    [Fact]
    public void Update_button_words_follow_the_state_and_say_when_it_last_checked()
    {
        var now = new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(5.5));
        Assert.Equal("not checked yet", UpdateText.CheckedAgo(null, now));
        Assert.Equal("checked just now", UpdateText.CheckedAgo(now.AddSeconds(-20), now));
        Assert.Equal("checked 5 min ago", UpdateText.CheckedAgo(now.AddMinutes(-5), now));
        Assert.Equal("checked 2 h ago", UpdateText.CheckedAgo(now.AddHours(-2), now));
        Assert.Equal("checked yesterday", UpdateText.CheckedAgo(now.AddHours(-40), now));
        Assert.Equal("checked 3 days ago", UpdateText.CheckedAgo(now.AddDays(-3), now));

        Assert.Equal("Update", UpdateText.ButtonText("idle", "", 0));
        Assert.Equal("Update", UpdateText.ButtonText("uptodate", "", 0));
        Assert.Equal("Update", UpdateText.ButtonText("error", "", 0));
        Assert.Equal("Checking…", UpdateText.ButtonText("checking", "", 0));
        Assert.Equal("Update 4.1.0", UpdateText.ButtonText("available", "4.1.0", 0));
        Assert.Equal("Downloading · 62 %", UpdateText.ButtonText("downloading", "4.1.0", 0.62));
        Assert.Equal("100 %", UpdateText.Percent(1.2));
        Assert.Equal("Restart to update", UpdateText.ButtonText("ready", "4.1.0", 1));

        Assert.Equal("Magpie 4.0.2 is up to date · checked 2 h ago · click to check now", UpdateText.ButtonTip("uptodate", "4.0.2", "", now.AddHours(-2), now));
        Assert.Equal("Magpie 4.0.2 is up to date · not checked yet · click to check now", UpdateText.ButtonTip("idle", "4.0.2", "", null, now));
        Assert.StartsWith("Magpie 4.1.0 is available", UpdateText.ButtonTip("available", "4.0.2", "4.1.0", now, now));
        Assert.StartsWith("Magpie 4.1.0 is ready", UpdateText.ButtonTip("ready", "4.0.2", "4.1.0", now, now));
        Assert.Contains("try again", UpdateText.ButtonTip("error", "4.0.2", "", now, now));
    }
}
