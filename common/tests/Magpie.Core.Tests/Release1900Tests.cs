using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Tests;

/// <summary>
/// 19.0.0 — design RC1 B (every To / Cc / Bcc address of an email: Bcc kept, the line under the sender, the Details
/// card) and design PE1 (every email with one address: the card's numbers, the three "emails with" lines, the list).
/// </summary>
public sealed class Release1900Tests
{
    private const string Me = "me@test.local";
    private const string Rajesh = "rajesh@buildwell.example";
    private static readonly string[] Mine = { Me };

    private static void Sql(TempDir dir, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + dir.File("mail.db"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static MessageRow Row(long folder, string thread, string from, string to, string cc = "", string bcc = "", string subject = "Hello",
        int daysAgo = 0, string messageId = "", MessageFlags flags = MessageFlags.Seen)
    {
        var r = Rows.Make("A", folder, thread, from: from, subject: subject, date: new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero).AddDays(-daysAgo), flags: flags, messageId: messageId);
        r.To = to; r.Cc = cc; r.Bcc = bcc;
        return r;
    }

    /// <summary>One mailbox: 2 from Rajesh (one also in All Mail), 3 sent to him (To, Cc, Bcc only), 1 where he is copied in,
    /// plus a draft, a Trash email, a look-alike address and an unrelated email.</summary>
    private static (MailStore store, long inbox, long sent, long all, long trash) Mailbox(TempDir dir)
    {
        var (store, inbox, sent) = Rows.NewStore(dir);
        var all = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var trash = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        var drafts = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Drafts", Name = "Drafts", Role = FolderRole.Drafts });
        var label = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Flat/Handover", Name = "Handover", Role = FolderRole.Other });
        store.InsertMessages(new[]
        {
            Row(inbox, "t1", Rajesh, Me, subject: "Re: Handover schedule", daysAgo: 2, messageId: "r1@x"),
            Row(all, "t1", Rajesh, Me, subject: "Re: Handover schedule", daysAgo: 2, messageId: "r1@x"),          // the same email, Gmail's All Mail copy
            Row(label, "t2", Rajesh, Me, subject: "Site visit on Saturday", daysAgo: 11, messageId: "r2@x"),
            Row(sent, "t1", Me, $"secretarial@buildwell.example, Rajesh Mehta <{Rajesh}>, Anita Rao <anita@buildwell.example>", cc: "legal@buildwell.example", subject: "Handover schedule", daysAgo: 0, messageId: "s1@x"),
            Row(sent, "t3", Me, "Anita Rao <anita@buildwell.example>", cc: $"Rajesh Mehta <{Rajesh}>", subject: "Snag list", daysAgo: 7, messageId: "s2@x"),
            Row(sent, "t4", Me, "Anita Rao <anita@buildwell.example>", bcc: Rajesh, subject: "Payment receipt for October", daysAgo: 16, messageId: "s3@x"),
            Row(all, "t4", Me, "Anita Rao <anita@buildwell.example>", subject: "Payment receipt for October", daysAgo: 16, messageId: "s3@x"),   // All Mail copy: Bcc not known
            Row(inbox, "t5", "anita@buildwell.example", Me, cc: $"Rajesh Mehta <{Rajesh}>", subject: "Minutes of the review call", daysAgo: 23, messageId: "c1@x", flags: MessageFlags.None),
            Row(drafts, "t6", Me, Rajesh, subject: "Not sent yet", daysAgo: 1, messageId: "d1@x"),
            Row(trash, "t7", Rajesh, Me, subject: "Thrown away", daysAgo: 30, messageId: "x1@x"),
            Row(inbox, "t8", "xrajesh@buildwell.example", Me, cc: "joe.rajesh@buildwell.example.org", subject: "Look-alike", daysAgo: 3, messageId: "l1@x"),
            Row(inbox, "t9", "news@paper.example", Me, subject: "Daily digest", daysAgo: 1, messageId: "n1@x"),
        });
        // The copied-in email is snoozed: person lists still show it.
        Sql(dir, $"UPDATE messages SET snooze_until={DateTimeOffset.Now.AddDays(3).ToUnixTimeMilliseconds()} WHERE message_id='c1@x'");
        return (store, inbox, sent, all, trash);
    }

    // ───────────────────────── RC1: Bcc is kept ─────────────────────────

    [Fact]
    public void An_older_mail_file_gets_the_bcc_column_and_reads_as_no_bcc()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        store.InsertMessages(new[] { Row(inbox, "t", Rajesh, Me) });
        SqliteConnection.ClearAllPools();
        Sql(dir, "ALTER TABLE messages DROP COLUMN bcc_list");   // a mail file written before 19.0.0
        SqliteConnection.ClearAllPools();

        var reopened = new MailStore(dir.File("mail.db"));
        var row = Assert.Single(reopened.GetThread("A", "t"));
        Assert.Equal("", row.Bcc);
        Assert.Equal("", reopened.GetMessage(row.Id)!.Bcc);
        // New emails are written with their Bcc from then on.
        reopened.InsertMessages(new[] { Row(inbox, "t2", Me, "anita@x.in", bcc: Rajesh) });
        Assert.Equal(Rajesh, Assert.Single(reopened.GetThread("A", "t2")).Bcc);
    }

    [Fact]
    public void A_sent_email_keeps_its_bcc_and_the_all_mail_copy_shows_it_too()
    {
        using var dir = new TempDir();
        var (store, _, _, _, _) = Mailbox(dir);
        var email = Assert.Single(store.GetThread("A", "t4"));
        Assert.Equal(Rajesh, email.Bcc);
        Assert.Equal(Rajesh, store.GetMessage(email.Id)!.Bcc);
    }

    [Fact]
    public void Bcc_lines_of_emails_listed_earlier_are_read_once()
    {
        using var dir = new TempDir();
        var (store, inbox, sent) = Rows.NewStore(dir);
        var mineInSent = Row(sent, "a", Me, "anita@x.in");
        var otherInSent = Row(sent, "b", "alias@test.local", "anita@x.in");
        var mineElsewhere = Row(inbox, "c", Me, "anita@x.in");
        var received = Row(inbox, "d", Rajesh, Me);
        store.InsertMessages(new[] { mineInSent, otherInSent, mineElsewhere, received });
        Sql(dir, "UPDATE messages SET bcc_list=NULL");   // as listed by an older Magpie

        // Everything in Sent; elsewhere only what you sent (a received email never carries Bcc).
        Assert.Equal(new[] { otherInSent.Uid, mineInSent.Uid }, store.BccUnread(sent, sentFolder: true, Me, 10));
        Assert.Equal(new[] { mineElsewhere.Uid }, store.BccUnread(inbox, sentFolder: false, Me, 10));
        Assert.Single(store.BccUnread(sent, sentFolder: true, Me, 1));

        Assert.Equal(1, store.SetBcc(sent, new[] { (mineInSent.Uid, $"Rajesh Mehta <{Rajesh}>"), (otherInSent.Uid, "") }));
        Assert.Empty(store.BccUnread(sent, sentFolder: true, Me, 10));                      // "" = read, none: not asked again
        Assert.Equal($"Rajesh Mehta <{Rajesh}>", Assert.Single(store.GetThread("A", "a")).Bcc);
        Assert.Equal(0, store.SetBcc(sent, new[] { (mineInSent.Uid, "someone@else.example") }));   // never overwritten
        Assert.Equal($"Rajesh Mehta <{Rajesh}>", Assert.Single(store.GetThread("A", "a")).Bcc);

        // The Bcc person is found by words, by to: / with: and as a contact page search.
        var folders = new[] { inbox, sent };
        List<ThreadRow> Find(SearchQuery q) => store.ListThreads(new ListQuery { FolderIds = folders, Search = q }, DateTimeOffset.Now);
        Assert.Equal("a", Assert.Single(Find(SearchQuery.Parse("mehta"))).ThreadKey);
        Assert.Equal("a", Assert.Single(Find(SearchQuery.Parse("to:" + Rajesh))).ThreadKey);
        Assert.Contains(Find(SearchQuery.Parse("with:" + Rajesh)), t => t.ThreadKey == "a");
        Assert.Contains(Find(SearchQuery.WithMailbox(Rajesh)), t => t.ThreadKey == "a");
    }

    [Fact]
    public void The_bcc_line_is_read_from_the_servers_envelope()
    {
        var env = new MailKit.Envelope();
        Assert.Equal("", AccountSync.BccLine(null));
        Assert.Equal("", AccountSync.BccLine(env));
        env.Bcc.Add(new MimeKit.MailboxAddress("Rajesh Mehta", Rajesh));
        env.Bcc.Add(MimeKit.MailboxAddress.Parse("my.records@example.com"));
        Assert.Equal($"\"Rajesh Mehta\" <{Rajesh}>, my.records@example.com", AccountSync.BccLine(env));
    }

    [Fact]
    public void An_email_the_server_refused_to_return_keeps_its_bcc_unread()
    {
        // 10 came back with a Bcc, 11 without one, 12 is gone from the server, 13 was refused (IMAP NO).
        var returned = new Dictionary<long, string> { [10] = Rajesh, [11] = "" };
        var lines = AccountSync.BccLines(new long[] { 10, 11, 12, 13 }, returned, new HashSet<long> { 13 });
        Assert.Equal(new[] { (10L, Rajesh), (11L, ""), (12L, "") }, lines);

        using var dir = new TempDir();
        var (store, _, sent) = Rows.NewStore(dir);
        var refused = Row(sent, "a", Me, "anita@x.in");
        var gone = Row(sent, "b", Me, "anita@x.in");
        store.InsertMessages(new[] { refused, gone });
        Sql(dir, "UPDATE messages SET bcc_list=NULL");
        store.SetBcc(sent, AccountSync.BccLines(new[] { refused.Uid, gone.Uid }, new Dictionary<long, string>(), new HashSet<long> { refused.Uid }));
        Assert.Equal(new[] { refused.Uid }, store.BccUnread(sent, sentFolder: true, Me, 10));   // asked again at the next start
    }

    // ───────────────────────── RC1 B: the line under the sender and the Details card ─────────────────────────

    private static MessageRow Thirteen()
    {
        var r = Row(1, "t", Me, "secretarial@buildwell.example, Rajesh Mehta <rajesh@buildwell.example>, Anita Rao <anita@buildwell.example>, "
            + "Sandeep Kulkarni <sandeep@buildwell.example>, Farah Khan <farah@buildwell.example>, customer.care@buildwell.example, "
            + "Gaurav Shah <gaurav@buildwell.example>, Priya Nair <priya@buildwell.example>",
            cc: "legal@buildwell.example, Meera Iyer <meera@buildwell.example>, Vikram Joshi <vikram@buildwell.example>, crm.pune@buildwell.example",
            bcc: "my.records@example.com", subject: "Handover schedule — follow-up");
        r.Id = 7;
        r.FromName = "Krishna Bhunia";
        return r;
    }

    [Fact]
    public void The_line_under_the_sender_shows_whole_names_then_counts()
    {
        var line = HtmlRenderer.RecipientLine(Thirteen());
        Assert.Contains(">secretarial@buildwell.example</span>, ", line);
        Assert.Contains(">Rajesh Mehta</span>, ", line);
        Assert.Contains(">Anita Rao</span>, <span class=\"to-n\">+5 more</span>", line);
        Assert.DoesNotContain("Sandeep", line);
        Assert.EndsWith(" · cc <span class=\"to-n\">4</span> · bcc <span class=\"to-n\">1</span>", line);

        // Few recipients: everyone by name, nothing counted.
        var few = Row(1, "t", Me, "Anita Rao <anita@x.in>", cc: "Rajesh Mehta <rajesh@x.in>", bcc: "boss@x.in");
        var shown = HtmlRenderer.RecipientLine(few);
        Assert.StartsWith("to <span class=\"hm-a\"", shown);
        Assert.Contains(" · cc <span class=\"hm-a\"", shown);
        Assert.Contains(">boss@x.in</span>", shown);
        Assert.DoesNotContain("to-n", shown);

        // Three names is the limit across To, Cc and Bcc together.
        var mixed = Row(1, "t", Me, "a@x.in, b@x.in", cc: "c@x.in, d@x.in");
        Assert.EndsWith(" · cc <span class=\"to-n\">2</span>", HtmlRenderer.RecipientLine(mixed));
        Assert.Equal("no recipients listed", HtmlRenderer.RecipientLine(Row(1, "t", Me, "")));
    }

    [Fact]
    public void The_details_card_lists_every_address_with_the_date_subject_and_folder()
    {
        var row = Thirteen();
        var card = HtmlRenderer.DetailsCard(new RenderMessage { Row = row, KeptIn = "Sent · me@test.local" });
        Assert.StartsWith("<div class=\"rd\" id=\"rd7\" hidden data-n=\"13\"", card);
        foreach (var label in new[] { ">From<", ">To · 8<", ">Cc · 4<", ">Bcc · 1<", ">Date<", ">Subject<", ">Kept in<" }) Assert.Contains(label, card);
        foreach (var address in (row.To + "," + row.Cc + "," + row.Bcc).Split(','))
            Assert.Contains(MimeKit.MailboxAddress.Parse(address.Trim()).Address, card);
        Assert.Contains("Sandeep Kulkarni <span class=\"rd-a\">sandeep@buildwell.example</span>", card);
        Assert.Contains("Friday, 13 November 2026", card);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Handover schedule — follow-up"), card);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("Sent · me@test.local"), card);
        Assert.Contains("<span>Copy all 13 addresses</span></button>", card);

        // No folder known (Magpie for Mac) and no Bcc: those rows are left out.
        var plain = HtmlRenderer.DetailsCard(new RenderMessage { Row = Row(1, "t", Rajesh, Me) });
        Assert.DoesNotContain("Kept in", plain);
        Assert.DoesNotContain(">Bcc", plain);
        Assert.DoesNotContain(">Cc", plain);
        // One address: its own row button copies it, so there is no "Copy all".
        Assert.DoesNotContain("Copy all", plain);
        Assert.Contains("class=\"rd-c\" data-t=\"me@test.local\"", plain);
        // A different reply address is shown; the sender's own is not repeated.
        var reply = Row(1, "t", Rajesh, Me);
        reply.ReplyTo = $"{Rajesh}, Help Desk <help@buildwell.example>";
        Assert.Contains("Help Desk <span class=\"rd-a\">help@buildwell.example</span>", HtmlRenderer.DetailsCard(new RenderMessage { Row = reply }));
    }

    // ───────────────────────── CP1 A: copy one address, one group, or all ─────────────────────────

    /// <summary>The text a copy button carries, as the page reads it back (data-t, lines written as &amp;#10;).</summary>
    private static string CopyText(string card, string cssClass, int nth = 0)
    {
        var marker = $"class=\"{cssClass}\" data-t=\"";
        var at = -1;
        for (int i = 0; i <= nth; i++) { at = card.IndexOf(marker, at + 1, StringComparison.Ordinal); Assert.True(at >= 0, $"no {cssClass} button #{nth}"); }
        var start = at + marker.Length;
        return System.Net.WebUtility.HtmlDecode(card[start..card.IndexOf('"', start)]);
    }

    [Fact]
    public void Copy_all_pastes_one_address_per_line_under_To_CC_and_BCC()
    {
        var card = HtmlRenderer.DetailsCard(new RenderMessage { Row = Thirteen(), KeptIn = "Sent · me@test.local" });
        var expected = string.Join("\n",
            "To",
            "secretarial@buildwell.example", "rajesh@buildwell.example", "anita@buildwell.example", "sandeep@buildwell.example",
            "farah@buildwell.example", "customer.care@buildwell.example", "gaurav@buildwell.example", "priya@buildwell.example",
            "",
            "CC",
            "legal@buildwell.example", "meera@buildwell.example", "vikram@buildwell.example", "crm.pune@buildwell.example",
            "",
            "BCC",
            "my.records@example.com");
        Assert.Equal(expected, CopyText(card, "rd-all"));
        Assert.Contains("data-s=\"Copied 13 addresses\"", card);
        // On Windows every line end becomes CR LF, so Notepad shows one address per line.
        var pasted = AddressCopy.ForClipboard(expected);
        Assert.Equal(18, pasted.Split("\r\n").Length);
        Assert.StartsWith("To\r\nsecretarial@buildwell.example\r\n", pasted);
        Assert.Contains("priya@buildwell.example\r\n\r\nCC\r\nlegal@buildwell.example", pasted);
        Assert.EndsWith("\r\n\r\nBCC\r\nmy.records@example.com", pasted);
        Assert.DoesNotContain("\n", pasted.Replace("\r\n", ""));
        Assert.Equal("a\r\nb\r\nc\r\nd", AddressCopy.ForClipboard("a\nb\r\nc\rd"));   // never doubled
        Assert.Equal("", AddressCopy.ForClipboard(null));
    }

    [Fact]
    public void Copy_all_leaves_out_a_group_without_addresses_and_From()
    {
        // To and Bcc only: no CC heading, and the sender is not in the text.
        var row = Row(3, "t", Me, $"{Rajesh}, anita@buildwell.example");
        row.Bcc = "my.records@example.com";
        var text = CopyText(HtmlRenderer.DetailsCard(new RenderMessage { Row = row }), "rd-all");
        Assert.Equal("To\nrajesh@buildwell.example\nanita@buildwell.example\n\nBCC\nmy.records@example.com", text);
        Assert.DoesNotContain("me@test.local", text);
        Assert.Equal("CC\nc@x.in", AddressCopy.All(Array.Empty<string>(), new[] { "c@x.in" }, Array.Empty<string>()));
        Assert.Equal("", AddressCopy.All(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void Every_address_has_its_own_copy_button()
    {
        var row = Thirteen();
        var card = HtmlRenderer.DetailsCard(new RenderMessage { Row = row });
        // From + 8 To + 4 Cc + 1 Bcc.
        Assert.Equal(14, card.Split("class=\"rd-c\"").Length - 1);
        Assert.Equal(row.FromAddress, CopyText(card, "rd-c"));
        Assert.Equal("secretarial@buildwell.example", CopyText(card, "rd-c", 1));
        Assert.Equal("my.records@example.com", CopyText(card, "rd-c", 13));
        Assert.Contains("data-s=\"Copied anita@buildwell.example\" title=\"Copy anita@buildwell.example\" aria-label=\"Copy anita@buildwell.example\" onclick=\"rdCopy(event,7,this)\"", card);
        // Only the address is copied, never the name.
        Assert.DoesNotContain("data-t=\"Anita", card);
        // A different reply address can be copied too.
        var reply = Row(1, "t", Rajesh, Me);
        reply.ReplyTo = "Help Desk <help@buildwell.example>";
        Assert.Contains("class=\"rd-c\" data-t=\"help@buildwell.example\"", HtmlRenderer.DetailsCard(new RenderMessage { Row = reply }));
    }

    [Fact]
    public void A_group_with_two_or_more_addresses_has_a_Copy_button_without_a_heading()
    {
        var card = HtmlRenderer.DetailsCard(new RenderMessage { Row = Thirteen() });
        // To (8) and Cc (4) have one; Bcc (1) does not — its row button does the same.
        Assert.Equal(2, card.Split("class=\"rd-g\"").Length - 1);
        Assert.Contains("<span class=\"w-c\">Copy To</span><span class=\"w-k\">Copied</span>", card);
        Assert.Contains("<span class=\"w-c\">Copy Cc</span>", card);
        Assert.DoesNotContain("Copy Bcc", card);
        Assert.Equal("legal@buildwell.example\nmeera@buildwell.example\nvikram@buildwell.example\ncrm.pune@buildwell.example", CopyText(card, "rd-g", 1));
        Assert.Equal(8, CopyText(card, "rd-g").Split('\n').Length);
        Assert.Contains("data-s=\"Copied 4 Cc addresses\"", card);
        Assert.Contains("data-s=\"Copied 8 To addresses\"", card);
        // Lines are written as &#10; so nothing between Magpie and the page can fold them.
        Assert.Contains("legal@buildwell.example&#10;meera@buildwell.example", card);
        Assert.Equal("Copied 1 Bcc address", AddressCopy.SaidGroup("Bcc", 1));
    }

    [Fact]
    public void The_same_address_twice_in_a_group_is_copied_once()
    {
        var row = Row(4, "t", Me, "a@x.in, A@X.IN, b@x.in", cc: "a@x.in, c@x.in");
        var card = HtmlRenderer.DetailsCard(new RenderMessage { Row = row });
        // In To once; it stays in CC, because it was sent there as well.
        Assert.Equal("To\na@x.in\nb@x.in\n\nCC\na@x.in\nc@x.in", CopyText(card, "rd-all"));
        Assert.Contains("<span>Copy all 4 addresses</span>", card);
    }

    [Fact]
    public void The_reading_page_has_the_details_button_and_no_tooltip_over_the_address_card()
    {
        var list = new List<RenderMessage> { new() { Row = Thirteen(), Expanded = true, IsMine = true, Body = new MessageBody { Text = "Dear team," }, KeptIn = "Sent · me@test.local" } };
        var now = new DateTimeOffset(2026, 11, 13, 22, 0, 0, TimeSpan.Zero);
        var windows = HtmlRenderer.BuildConversation("Handover schedule", list, false, now, addressTips: false).Html;
        Assert.Contains("class=\"to-b\" aria-expanded=\"false\" aria-controls=\"rd7\"", windows);
        Assert.Contains("onclick=\"rdToggle(event,7)\">Details</button>", windows);
        Assert.Contains("function rdToggle(", windows);
        Assert.Contains("function rdCopy(ev,id,b)", windows);
        Assert.Contains("function rdSaid(id,t,ok)", windows);
        Assert.Contains("id=\"rd7\" hidden", windows);
        Assert.DoesNotContain("title=\"rajesh@buildwell.example\"", windows);   // the browser's tooltip covered the card
        Assert.Contains("title=\"rajesh@buildwell.example\"", HtmlRenderer.BuildConversation("Handover schedule", list, false, now).Html);
        // The page a reader keeps is rebuilt when a Bcc line arrives later.
        string Print(string bcc)
        {
            var row = Thirteen();
            row.Bcc = bcc;
            return ReaderPage.ContentFingerprint("s", new[] { new RenderMessage { Row = row } }, false, false, 600, now);
        }
        Assert.NotEqual(Print(""), Print("my.records@example.com"));
        // The shell both apps load keeps working with the card's script (ids are looked up in the open page).
        Assert.Contains("readerFind('rd'+id)", ReaderShell.Build(false));
    }

    // ───────────────────────── PE1: the numbers and the list ─────────────────────────

    [Fact]
    public void The_card_counts_each_email_once_by_the_part_the_address_plays()
    {
        using var dir = new TempDir();
        var (store, _, _, _, _) = Mailbox(dir);
        var stats = store.GetPersonStats(Rajesh, Mine);
        Assert.Equal(new PersonStats(6, 2, 3, 1, new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero)), stats);
        Assert.Equal(6, stats.Of(PersonKind.All));
        Assert.Equal(3, stats.Of(PersonKind.YouSent));
        // Trash and Spam only when asked for; drafts never.
        Assert.Equal(new PersonStats(7, 3, 3, 1, stats.Last), store.GetPersonStats(Rajesh, Mine, includeBin: true));
        // Upper case and spaces don't matter; a look-alike address is another person; nobody = nothing.
        Assert.Equal(stats, store.GetPersonStats("  RAJESH@BuildWell.example ", Mine));
        Assert.Equal(1, store.GetPersonStats("xrajesh@buildwell.example", Mine).All);
        Assert.Equal(PersonStats.None, store.GetPersonStats("nobody@nowhere.example", Mine));
        Assert.Equal(PersonStats.None, store.GetPersonStats(" ", Mine));
        // Without your own addresses, what you sent can only be "copied in".
        Assert.Equal(new PersonStats(6, 2, 0, 4, stats.Last), store.GetPersonStats(Rajesh, Array.Empty<string>()));
    }

    [Fact]
    public void The_list_has_one_row_per_email_newest_first_and_matches_the_numbers()
    {
        using var dir = new TempDir();
        var (store, _, _, _, _) = Mailbox(dir);
        var stats = store.GetPersonStats(Rajesh, Mine);
        var all = store.ListPersonEmails(Rajesh, PersonKind.All, Mine);
        Assert.Equal(new[] { "s1@x", "r1@x", "s2@x", "r2@x", "s3@x", "c1@x" }, all.Select(e => e.Row.MessageId));
        foreach (var kind in PersonView.Kinds) Assert.Equal(stats.Of(kind), store.ListPersonEmails(Rajesh, kind, Mine).Count);
        Assert.All(store.ListPersonEmails(Rajesh, PersonKind.From, Mine), e => Assert.Equal(PersonKind.From, e.Kind));
        Assert.Equal(new[] { "s1@x", "s2@x", "s3@x" }, store.ListPersonEmails(Rajesh, PersonKind.YouSent, Mine).Select(e => e.Row.MessageId));

        // Each email as its Inbox / Sent / label copy rather than All Mail, with the Bcc the Sent copy knows.
        var byId = all.ToDictionary(e => e.Row.MessageId);
        Assert.Equal(FolderRole.Inbox, byId["r1@x"].FolderRole);
        Assert.Equal("Flat/Handover", byId["r2@x"].FolderPath);
        Assert.Equal(FolderRole.Sent, byId["s3@x"].FolderRole);
        Assert.Equal(Rajesh, byId["s3@x"].Row.Bcc);
        Assert.Equal(PersonKind.CopiedIn, byId["c1@x"].Kind);
        Assert.NotNull(byId["c1@x"].Row.SnoozeUntil);                 // snoozed and unread emails are listed too
        Assert.False(byId["c1@x"].Row.IsSeen);

        Assert.Equal(7, store.ListPersonEmails(Rajesh, PersonKind.All, Mine, includeBin: true).Count);
        Assert.Equal(2, store.ListPersonEmails(Rajesh, PersonKind.All, Mine, limit: 2).Count);
        Assert.Empty(store.ListPersonEmails("", PersonKind.All, Mine));
    }

    [Fact]
    public void The_same_emails_can_be_listed_as_conversations()
    {
        using var dir = new TempDir();
        var (store, _, _, _, _) = Mailbox(dir);
        var folders = store.GetFolders().Where(f => f.Role is not (FolderRole.Trash or FolderRole.Junk or FolderRole.Drafts)).Select(f => f.Id).ToList();
        List<string> Threads(PersonKind kind, bool anySnooze = true) => store.ListThreads(new ListQuery
        {
            FolderIds = folders, Search = SearchQuery.ForPerson(Rajesh, kind, Mine), AnySnooze = anySnooze,
        }, DateTimeOffset.Now).Select(t => t.ThreadKey).ToList();

        Assert.Equal(new[] { "t1", "t3", "t2", "t4", "t5" }, Threads(PersonKind.All));
        Assert.Equal(new[] { "t1", "t2" }, Threads(PersonKind.From));
        Assert.Equal(new[] { "t1", "t3", "t4" }, Threads(PersonKind.YouSent));
        Assert.Equal(new[] { "t5" }, Threads(PersonKind.CopiedIn));
        Assert.DoesNotContain("t5", Threads(PersonKind.All, anySnooze: false));   // the usual lists still hide a snoozed conversation
        Assert.False(SearchQuery.ForPerson(Rajesh, PersonKind.All, Mine).IsEmpty);
    }

    [Fact]
    public void The_address_card_puts_the_three_email_lines_first_with_their_numbers()
    {
        static string[] Ids(IEnumerable<HoverOption> o) => o.Select(x => x.Id).ToArray();
        var stats = new PersonStats(1051, 12, 35, 4, DateTimeOffset.Now);
        var card = HoverMenus.ForAddress("Rajesh Mehta", Rajesh, isMe: false, hasCalendar: true, picturesTrusted: false, hasContacts: true, stats: stats);
        Assert.Equal(new[] { "E5", "E4", "E12", "-", "E1", "E2", "E3", "-", "E6", "E7", "E8", "E9", "E10", "E11" }, Ids(card));
        Assert.Equal(("All emails with Rajesh", "1,051"), (card[0].Label, card[0].Count));
        Assert.Equal(("Emails from Rajesh", "12"), (card[1].Label, card[1].Count));
        Assert.Equal(("Emails you sent to Rajesh", "35"), (card[2].Label, card[2].Count));
        Assert.True(card[3].IsSeparator);
        Assert.Null(card[4].Count);
        // Numbers not known yet: the lines are there without them.
        Assert.All(HoverMenus.ForAddress("Rajesh Mehta", Rajesh, false, false, true).Take(3), o => Assert.Null(o.Count));
        // Your own address keeps its five lines.
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5" }, Ids(HoverMenus.ForAddress("Me", Me, isMe: true, hasCalendar: true, picturesTrusted: false, stats: stats)));

        Assert.Equal(PersonKind.All, HoverMenus.PersonKindOf("E5"));
        Assert.Equal(PersonKind.From, HoverMenus.PersonKindOf("E4"));
        Assert.Equal(PersonKind.YouSent, HoverMenus.PersonKindOf("E12"));
        Assert.Equal(new[] { ("1,051", "all"), ("12", "received"), ("35", "you sent"), ("4", "copied in") }, HoverMenus.Tiles(stats));
        Assert.Empty(HoverMenus.Tiles(PersonStats.None));
        Assert.Empty(HoverMenus.Tiles(null));
    }

    [Fact]
    public void The_list_words_say_who_sent_what_to_whom()
    {
        using var dir = new TempDir();
        var (store, _, _, _, _) = Mailbox(dir);
        var rows = store.ListPersonEmails(Rajesh, PersonKind.All, Mine).ToDictionary(e => e.Row.MessageId);
        string Line(string id) => PersonView.RowLine(rows[id], "Rajesh Mehta", Rajesh);
        Assert.Equal("From rajesh", Line("r1@x"));                                  // the sender's own name on that email
        Assert.Equal("To Rajesh Mehta +3", Line("s1@x"));
        Assert.Equal("To Anita Rao · cc Rajesh Mehta", Line("s2@x"));
        Assert.Equal("To Anita Rao · bcc Rajesh Mehta", Line("s3@x"));
        Assert.Equal("From anita · cc Rajesh Mehta", Line("c1@x"));
        Assert.True(PersonView.OnlyBcc(rows["s3@x"], Rajesh));
        Assert.False(PersonView.OnlyBcc(rows["s2@x"], Rajesh));
        var two = new PersonEmail { Kind = PersonKind.YouSent, Row = Row(1, "t", Me, $"{Rajesh}, Anita Rao <anita@x.in>") };
        Assert.Equal("To Rajesh Mehta, Anita Rao", PersonView.RowLine(two, "Rajesh Mehta", Rajesh));
        var one = new PersonEmail { Kind = PersonKind.YouSent, Row = Row(1, "t", Me, Rajesh) };
        Assert.Equal($"To {Rajesh}", PersonView.RowLine(one, "", Rajesh));

        var stats = store.GetPersonStats(Rajesh, Mine);
        Assert.Equal("Emails with Rajesh Mehta", PersonView.Title("Rajesh Mehta", Rajesh));
        Assert.Equal("Emails with " + Rajesh, PersonView.Title("", Rajesh));
        Assert.Equal(Rajesh + " · every folder, both accounts", PersonView.SubTitle("Rajesh Mehta", Rajesh, 2));
        Assert.Equal("every folder", PersonView.SubTitle("", Rajesh, 1));
        Assert.Equal(Rajesh + " · every folder, all 3 accounts", PersonView.SubTitle("Rajesh Mehta", Rajesh, 3));
        Assert.Equal(new[] { "All 6", "From Rajesh 2", "You sent 3", "Copied in 1" }, PersonView.Kinds.Select(k => PersonView.FilterLabel(k, "Rajesh Mehta", Rajesh, stats)));
        Assert.Equal("6 emails · 2 received · 3 you sent · 1 copied in", PersonView.StatsLine(stats));
        Assert.Equal("1 email · 1 received", PersonView.StatsLine(new PersonStats(1, 1, 0, 0, null)));
        Assert.Equal("No emails on this PC yet", PersonView.StatsLine(PersonStats.None));
        Assert.Equal("Last email on 13 Nov", PersonView.LastLine(stats, new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Last email on 13 Nov 2026", PersonView.LastLine(stats, new DateTimeOffset(2027, 1, 2, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("", PersonView.LastLine(PersonStats.None, DateTimeOffset.Now));

        Assert.Equal("Inbox", PersonView.FolderLabel(FolderRole.Inbox, "INBOX"));
        Assert.Equal("Spam", PersonView.FolderLabel(FolderRole.Junk, "[Gmail]/Spam"));
        Assert.Equal("Flat/Handover", PersonView.FolderLabel(FolderRole.Other, "Flat/Handover"));
        Assert.Equal("Receipts", PersonView.FolderLabel(FolderRole.Other, "[Gmail]/Receipts"));
        Assert.Equal("6 emails on this PC. Trash and Spam are left out.", PersonView.FootLine(6, false, false, false));
        Assert.Equal("400+ conversations on this PC. Trash and Spam are included.", PersonView.FootLine(400, true, true, true));
        Assert.Equal("1 email on this PC. Trash and Spam are left out.", PersonView.FootLine(1, false, false, false));
        Assert.Equal("No emails you sent to Rajesh Mehta on this PC.", PersonView.EmptyText(PersonKind.YouSent, "Rajesh Mehta", Rajesh));
        Assert.Equal("Nothing more on the server.", PersonView.ServerResult(0, ""));
        Assert.Equal("1 more email found on the server.", PersonView.ServerResult(1, ""));
        Assert.Equal("12 more emails found on the server. Could not ask a@b.c: offline", PersonView.ServerResult(12, "a@b.c: offline"));
        Assert.Equal("Could not ask a@b.c: offline", PersonView.ServerResult(0, "a@b.c: offline"));
    }

    [Fact]
    public void Search_the_server_too_asks_only_folders_that_still_have_older_emails()
    {
        MailFolder F(long id, FolderRole role, long uidNext = 50, bool synced = true) => new() { Id = id, AccountId = "A", Path = role + "/" + id, Name = role.ToString(), Role = role, UidNext = uidNext, Synced = synced };
        var left = new Dictionary<long, int> { [1] = 120, [2] = 0, [3] = 40, [4] = 9, [5] = 9, [6] = 9, [7] = 9, [8] = 9 };
        int Remaining(long id) => left.TryGetValue(id, out var n) ? n : -1;

        // Plain IMAP: every folder still listing, but never Trash, Spam, Drafts, one not mirrored or one never checked.
        var imap = new[] { F(1, FolderRole.Inbox), F(2, FolderRole.Sent), F(3, FolderRole.Other), F(4, FolderRole.Trash), F(5, FolderRole.Junk), F(6, FolderRole.Drafts), F(7, FolderRole.Other, synced: false), F(8, FolderRole.Other, uidNext: 0), F(9, FolderRole.Archive) };
        Assert.Equal(new long[] { 1, 3, 9 }, AccountSync.AddressSearchFolders(imap, Remaining).Select(f => f.Id));
        // Gmail keeps every email in All Mail: its labels are not asked one by one.
        var gmail = new[] { F(1, FolderRole.Inbox), F(3, FolderRole.Other), F(10, FolderRole.All), F(11, FolderRole.Sent), F(12, FolderRole.Other) };
        Assert.Equal(new long[] { 1, 10, 11 }, AccountSync.AddressSearchFolders(gmail, Remaining).Select(f => f.Id));
        // Everything listed: nothing to ask.
        Assert.Empty(AccountSync.AddressSearchFolders(new[] { F(2, FolderRole.Sent) }, Remaining));
    }

    [Fact]
    public void A_folder_the_server_would_not_search_is_reported_not_called_nothing_more()
    {
        Assert.Equal("", AccountSync.SearchProblem(Array.Empty<(string, string)>()));
        Assert.Equal("Inbox could not be searched (SEARCH not allowed now)", AccountSync.SearchProblem(new[] { ("Inbox", "SEARCH not allowed now") }));
        Assert.Equal("4 folders could not be searched (Inbox, Sent, Flat, …: busy)",
            AccountSync.SearchProblem(new[] { ("Inbox", "busy"), ("Sent", "busy"), ("Flat", "busy"), ("Old", "other") }));
        // Under the list: never "Nothing more on the server" when part of the search failed.
        var line = PersonView.ServerResult(0, "me@x.in: Inbox could not be searched (busy).");
        Assert.Equal("Could not ask me@x.in: Inbox could not be searched (busy).", line);
        Assert.StartsWith("2 more emails found on the server. Could not ask ", PersonView.ServerResult(2, "me@x.in: Inbox could not be searched (busy)."));
    }

    [Fact]
    public void A_page_built_again_shows_the_opened_email_again_but_a_page_shown_again_stays_put()
    {
        var shell = ReaderShell.Build(false);
        Assert.Contains("page.focused!==p.focus", shell);
        Assert.Contains("page.focused=p.focus", shell);
        var page = new ReaderPage("k", "f", "<html><head><style></style></head><body><div id=\"m7\"></div></body></html>", 0, Focus: 7);
        Assert.Contains("\"focus\":7", ReaderShell.PresentScript(page, 1, false, 600));
        Assert.Contains("\"focus\":0", ReaderShell.PresentScript(page with { Focus = 0 }, 2, true, 600));
    }
}
