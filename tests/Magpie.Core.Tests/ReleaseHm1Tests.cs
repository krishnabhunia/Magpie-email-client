using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>Design HM1: hover cards on addresses, the subject and attachments in the reading pane.</summary>
public class ReleaseHm1Tests
{
    private static string[] Ids(IEnumerable<HoverOption> o) => o.Select(x => x.Id).ToArray();

    [Fact]
    public void Your_own_address_offers_only_copy_write_and_search()
    {
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5" }, Ids(HoverMenus.ForAddress("Me", "me@gmail.com", isMe: true, hasCalendar: true, picturesTrusted: false)));
    }

    [Fact]
    public void Someone_elses_address_offers_everything_that_can_work_now()
    {
        var all = HoverMenus.ForAddress("Anita Rao", "anita@vendorco.in", isMe: false, hasCalendar: true, picturesTrusted: false);
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5", "E7", "E8", "E9", "E10", "E11" }, Ids(all));
        Assert.Contains(all, o => o.Label == "Emails from Anita");
        Assert.Contains(all, o => o.Label == "New event with Anita");
        Assert.Contains(all, o => o.Label == "Always show pictures from Anita");

        // No calendar → no E7; pictures already shown → no E8; Contacts page → E6.
        var some = HoverMenus.ForAddress("Anita Rao", "anita@vendorco.in", false, hasCalendar: false, picturesTrusted: true, hasContacts: true);
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E9", "E10", "E11" }, Ids(some));
    }

    [Theory]
    [InlineData("Anita Rao", "anita@vendorco.in", "Anita")]
    [InlineData("\"Rao, Anita\"", "anita@vendorco.in", "Anita")]
    [InlineData("", "anita@vendorco.in", "anita")]
    [InlineData("anita@vendorco.in", "anita@vendorco.in", "anita")]
    public void First_names_come_from_the_name_or_the_address(string name, string address, string expected) =>
        Assert.Equal(expected, HoverMenus.FirstName(name, address));

    [Fact]
    public void Name_and_address_is_ready_to_paste()
    {
        Assert.Equal("Anita Rao <anita@vendorco.in>", HoverMenus.NameAndAddress("Anita Rao", "anita@vendorco.in"));
        Assert.Equal("\"Rao, Anita\" <anita@vendorco.in>", HoverMenus.NameAndAddress("Rao, Anita", "anita@vendorco.in"));
        Assert.Equal("anita@vendorco.in", HoverMenus.NameAndAddress("", "anita@vendorco.in"));
    }

    [Fact]
    public void Subject_card_shows_calendar_and_ai_lines_only_when_they_work()
    {
        Assert.Equal(new[] { "S1", "S2", "S3", "S4", "S5", "S6", "S8" }, Ids(HoverMenus.ForSubject(hasCalendar: false, aiSummary: false)));
        Assert.Equal(new[] { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9" }, Ids(HoverMenus.ForSubject(true, true)));
        Assert.Equal("Vendor migration", HoverMenus.CleanSubject("Re: Fwd: Vendor migration"));
        Assert.Equal("3 emails in this conversation · Inbox", HoverMenus.SubjectLine(3, "Inbox"));
    }

    [Fact]
    public void Attachment_card_fits_the_file()
    {
        var pdf = HoverMenus.ForAttachment("Vendor-contract-v3.pdf", "Anita Rao", "anita@vendorco.in", fromMe: false, attachmentCount: 2, saved: false);
        Assert.Equal(new[] { "A1", "A5", "A2", "A3", "A4", "A6", "A8", "A9" }, Ids(pdf));
        Assert.Contains(pdf, o => o.Label == "Save all 2 attachments…" );
        Assert.Contains(pdf, o => o.Label == "Files from Anita");

        // A zip can't be previewed; one file → no Save all; saved → Show in folder; my own file → no "Files from".
        var zip = HoverMenus.ForAttachment("logs.zip", "Me", "me@gmail.com", fromMe: true, attachmentCount: 1, saved: true);
        Assert.Equal(new[] { "A1", "A2", "A4", "A6", "A7", "A9" }, Ids(zip));
    }

    [Fact]
    public void Card_lines_read_plainly()
    {
        Assert.Equal("PDF document · 2.4 MB · on this PC", HoverMenus.AttachmentLine("a.pdf", (long)(2.4 * 1024 * 1024), true));
        Assert.Equal("JPG picture · 820 KB · downloads when opened", HoverMenus.AttachmentLine("site-photo.JPG", 820 * 1024, false));
        Assert.Equal("XYZ file", HoverMenus.KindName("x.xyz"));
        Assert.Equal("PDF", HoverMenus.Badge("a.pdf"));
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal("anita@vendorco.in · 34 emails, last on 30 Sep", HoverMenus.AddressLine("anita@vendorco.in", 34, now, now));
        Assert.Equal("anita@vendorco.in · 1 email, last on 3 Jan 2025", HoverMenus.AddressLine("anita@vendorco.in", 1, new DateTimeOffset(2025, 1, 3, 12, 0, 0, TimeSpan.FromHours(5.5)), now));
        Assert.Equal("anita@vendorco.in", HoverMenus.AddressLine("anita@vendorco.in", 0, null, now));
    }

    [Fact]
    public void Search_options_type_the_right_search()
    {
        Assert.Equal("from:anita@vendorco.in", HoverMenus.SearchFor("E4", "anita@vendorco.in"));
        Assert.Equal("with:anita@vendorco.in", HoverMenus.SearchFor("E5", "anita@vendorco.in"));
        Assert.Equal("subject:\"Vendor migration\"", HoverMenus.SearchFor("S2", "Vendor migration"));
        Assert.Equal("from:anita@vendorco.in has:attachment", HoverMenus.SearchFor("A8", "anita@vendorco.in"));
        Assert.Equal("file:\"Vendor contract v3.pdf\"", HoverMenus.SearchFor("A9", "Vendor contract v3.pdf"));

        var q = SearchQuery.Parse("with:anita file:\"Vendor contract\"");
        Assert.Equal(new[] { "anita" }, q.With);
        Assert.Equal(new[] { "Vendor contract" }, q.File);
        Assert.False(q.IsEmpty);
    }

    [Fact]
    public void With_finds_the_person_as_sender_or_in_cc_and_file_finds_attachment_names()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var fromAnita = Rows.Make("A", inbox, "t1", from: "anita@vendorco.in", subject: "Contract");
        var ccAnita = Rows.Make("A", sent, "t2", from: "me@test.local", subject: "Minutes");
        ccAnita.Cc = "Anita Rao <anita@vendorco.in>";
        var other = Rows.Make("A", inbox, "t3", from: "ravi@x.in", subject: "Lunch");
        s.InsertMessages(new[] { fromAnita, ccAnita, other });
        s.SaveBody(fromAnita.Id, new MessageBody { Text = "see file", Attachments = { new AttachmentInfo { Index = 1, FileName = "Vendor-contract-v3.pdf", Size = 1000 } } });

        List<string> Find(string text) => s.ListThreads(new ListQuery { FolderIds = new[] { inbox, sent }, Search = SearchQuery.Parse(text) }, DateTimeOffset.Now)
            .Select(t => t.ThreadKey).OrderBy(k => k).ToList();

        Assert.Equal(new[] { "t1", "t2" }, Find("with:anita@vendorco.in"));
        Assert.Equal(new[] { "t1" }, Find("from:anita@vendorco.in"));
        Assert.Equal(new[] { "t1" }, Find("file:contract-v3"));
        Assert.Empty(Find("file:invoice"));
    }

    [Fact]
    public void Address_stats_count_a_copy_in_two_folders_once()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var all = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var d1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", from: "anita@vendorco.in", date: d1, messageId: "m1@x"),
            Rows.Make("A", all, "t1", from: "anita@vendorco.in", date: d1, messageId: "m1@x"),
            Rows.Make("A", inbox, "t2", from: "Anita@VendorCo.in", date: d2, messageId: "m2@x"),
            Rows.Make("A", inbox, "t3", from: "ravi@x.in", date: d2, messageId: "m3@x"),
        });
        var (count, last) = s.AddressStats("anita@vendorco.in");
        Assert.Equal(2, count);
        Assert.Equal(d2, last);
        Assert.Equal((0, (DateTimeOffset?)null), s.AddressStats("nobody@x.in"));
    }

    [Fact]
    public void Reading_page_marks_people_and_files_for_the_cards()
    {
        var row = Rows.Make("A", 1, "t1", from: "anita@vendorco.in", subject: "Contract");
        row.FromName = "Anita Rao";
        row.To = "\"Me\" <me@test.local>, sandeep@x.in";
        row.Cc = "Ravi <ravi@x.in>";
        row.Id = 7;
        var body = new MessageBody { Html = "<p>hi</p>", Attachments = { new AttachmentInfo { Index = 2, FileName = "a&b.pdf", Size = 10 } } };
        var html = HtmlRenderer.BuildConversation("Contract", new[] { new RenderMessage { Row = row, Body = body, Expanded = true } }, false, DateTimeOffset.Now, hoverDelayMs: 5000).Html;

        Assert.Contains("data-a=\"anita@vendorco.in\" data-n=\"Anita Rao\" data-m=\"7\"", html);
        Assert.Contains("data-a=\"sandeep@x.in\"", html);
        Assert.Contains(">Ravi</span>", html);
        Assert.Contains("class=\"att hm-f\"", html);
        Assert.Contains("data-f=\"a&amp;b.pdf\"", html);
        Assert.Contains("var HM_DELAY=1000;", html);        // clamped to the setting's range
        Assert.Contains("id=\"hm\"", html);
    }
}
