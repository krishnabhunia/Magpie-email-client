using Magpie.Core.Mail;
using Magpie.Core.Models;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>2.3.0: emails open from this PC without the server (Q39), the download window is filled and shown (Q40),
/// the reader switches at once (Q38).</summary>
public class Release230Tests
{
    private static AttachmentInfo Inline(string cid, string type = "image/png") =>
        new() { Index = 3, FileName = "x", ContentType = type, Inline = true, ContentId = cid };

    // ───────────── Q39: when an email must be read again ─────────────

    [Fact]
    public void Only_a_missing_picture_the_email_shows_needs_the_email_again()
    {
        var shown = new MessageBody { Html = "<img src=\"cid:logo\">", Attachments = { Inline("logo") } };
        Assert.True(MimeText.NeedsDownload(shown));                                    // shown, not kept
        shown.Images["LOGO"] = "data:image/png;base64,AA==";
        Assert.False(MimeText.NeedsDownload(shown));                                   // kept (content-ids ignore case)

        var calendar = new MessageBody { Html = "<p>Invite</p>", Attachments = { Inline("cal", "text/calendar") } };
        Assert.False(MimeText.NeedsDownload(calendar));                                // not a picture: used to reload every time
        var unused = new MessageBody { Html = "<p>No pictures here</p>", Attachments = { Inline("sig") } };
        Assert.False(MimeText.NeedsDownload(unused));                                  // a picture the email doesn't show

        var tooBig = new MessageBody { Html = "<img src=\"cid:photo\">", Attachments = { Inline("photo") }, ImagesComplete = true };
        Assert.False(MimeText.NeedsDownload(tooBig));                                  // looked at already: too big to keep
        Assert.Equal(new[] { "photo" }, MimeText.MissingPictures(tooBig));             // (comes from the file on this PC if there is one)
    }

    [Fact]
    public void Pictures_state_is_kept_and_content_ids_ignore_case()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t1") }).Single();
        store.SaveBody(row.Id, new MessageBody { Html = "<img src=\"cid:Logo\">", Images = { ["Logo"] = "data:x" }, ImagesComplete = true });
        var back = store.GetBody(row.Id)!;
        Assert.True(back.ImagesComplete);
        Assert.True(back.Images.ContainsKey("logo"));
    }

    // ───────────── Q39: one download per email, however many folders it is in ─────────────

    [Fact]
    public void A_downloaded_email_gives_its_body_to_its_copies_in_other_folders()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var all = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var rows = store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", messageId: "m1@x"), Rows.Make("A", all, "t1", messageId: "m1@x") });
        Assert.True(store.HasUncachedBody("A", "t1"));
        store.SaveBody(rows[0].Id, new MessageBody { Html = "<p>hi</p>", Text = "hi" });
        Assert.Equal("hi", store.GetBody(rows[1].Id)!.Text);                            // the All Mail copy isn't downloaded again
        Assert.False(store.HasUncachedBody("A", "t1"));
        Assert.Empty(store.RowsWithoutBody(all, 10));
    }

    [Fact]
    public void Copies_saved_before_2_3_0_get_the_body_of_a_copy_that_has_one()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var all = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var a = store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", messageId: "m1@x") }).Single();
        store.SaveBody(a.Id, new MessageBody { Html = "<p>hi</p>", Text = "hi", ImagesComplete = true });
        var b = store.InsertMessages(new[] { Rows.Make("A", all, "t1", messageId: "m1@x") }).Single();   // listed later
        Assert.Null(store.GetBody(b.Id));
        Assert.Equal(1, store.ShareBodiesWithCopies("A"));
        Assert.True(store.GetBody(b.Id)!.ImagesComplete);
        Assert.Equal(0, store.ShareBodiesWithCopies("A"));
    }

    // ───────────── Q40: the download window ─────────────

    [Fact]
    public void Window_counts_each_email_once_and_leaves_out_trash_and_older_mail()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var all = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "All", Name = "All Mail", Role = FolderRole.All });
        var trash = store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        var since = DateTimeOffset.Now.AddDays(-90);
        var rows = store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", messageId: "m1@x"), Rows.Make("A", all, "t1", messageId: "m1@x"),   // one email, two folders
            Rows.Make("A", inbox, "t2", messageId: "m2@x"),
            Rows.Make("A", trash, "t3", messageId: "m3@x"),                                                // not downloaded ahead
            Rows.Make("A", inbox, "t4", messageId: "m4@x", date: DateTimeOffset.Now.AddDays(-200)),        // older than the window
        });
        Assert.Equal((0, 2), store.WindowProgress("A", since));
        store.SaveBody(rows[0].Id, new MessageBody { Text = "x" });
        Assert.Equal((1, 2), store.WindowProgress("A", since));
        Assert.Equal((1, 3), store.WindowProgress("A", null));                          // "everything"
    }

    [Fact]
    public void Big_emails_are_in_the_window_too()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var big = Rows.Make("A", inbox, "t1");
        big.Size = 25_000_000;   // a big attachment: its text is still downloaded ahead
        store.InsertMessages(new[] { big });
        Assert.Single(store.RowsWithoutBody(inbox, 10, DateTimeOffset.Now.AddDays(-90)));
    }

    [Fact]
    public void The_account_card_says_how_much_is_on_this_pc()
    {
        Assert.Equal("All 1,234 emails from the last 90 days are on this PC.", AccountSync.DescribeWindow(1234, 1234, 90));
        Assert.Equal("On this PC: 1,200 of 1,234 emails from the last 90 days. The rest are downloading.", AccountSync.DescribeWindow(1200, 1234, 90));
        Assert.Equal("All 5 emails are on this PC.", AccountSync.DescribeWindow(5, 5, 0));
        Assert.Equal("No emails from the last 30 days listed yet.", AccountSync.DescribeWindow(0, 0, 30));
    }

    // ───────────── Q38: the pane switches at once ─────────────

    [Fact]
    public void Switching_to_an_email_on_this_pc_shows_its_header_without_loading()
    {
        var here = HtmlRenderer.LoadingBody("Lunch", "Anita", "Wed 30 Sep, 10:42", downloading: false);
        Assert.Contains("Lunch", here);
        Assert.Contains("Anita", here);
        Assert.DoesNotContain("Loading", here);
        Assert.Contains("Loading", HtmlRenderer.LoadingBody("Lunch", "Anita", "Wed 30 Sep, 10:42"));
    }
}
