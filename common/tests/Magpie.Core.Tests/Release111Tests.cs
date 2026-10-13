using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

/// <summary>1.1.1: every folder is listed in full (headers only) and an email in several folders shows once.</summary>
public class Release111Tests
{
    [Fact]
    public void Every_folder_is_mirrored_including_Gmail_All_Mail_Starred_Important()
    {
        foreach (var role in Enum.GetValues<FolderRole>())
        {
            Assert.True(AccountSync.ShouldMirror(role, gmail: true));
            Assert.True(AccountSync.ShouldMirror(role, gmail: false));
        }
    }

    [Fact]
    public void Email_in_several_folders_counts_once_and_prefers_the_inbox_copy()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var all = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var label = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "1_Forever/Godrej Hill Retreat", Name = "Godrej Hill Retreat", Role = FolderRole.Other });
        var t0 = DateTimeOffset.Now.AddDays(-400);
        s.InsertMessages(new[]
        {
            // One conversation: an incoming email (Inbox + label + All Mail copies) and our reply (Sent + All Mail copies).
            Rows.Make("A", all, "t1", date: t0, messageId: "in1@x"),
            Rows.Make("A", inbox, "t1", date: t0, messageId: "in1@x"),
            Rows.Make("A", label, "t1", date: t0, messageId: "in1@x"),
            Rows.Make("A", all, "t1", from: "me@test.local", date: t0.AddHours(1), flags: MessageFlags.Seen, messageId: "re1@x"),
            Rows.Make("A", sent, "t1", from: "me@test.local", date: t0.AddHours(1), flags: MessageFlags.Seen, messageId: "re1@x"),
            // Archived: only in All Mail.
            Rows.Make("A", all, "t2", from: "bob@y.com", date: t0.AddDays(1), messageId: "arch@x"),
        });
        var now = DateTimeOffset.Now;

        var everywhere = s.ListThreads(new ListQuery { FolderIds = new[] { inbox, sent, all, label } }, now);
        Assert.Equal(2, everywhere.Count);
        var t1 = everywhere.Single(t => t.ThreadKey == "t1");
        Assert.Equal(2, t1.Count);          // 2 emails, not 5 copies
        Assert.Equal(1, t1.UnreadCount);
        Assert.Equal(sent, t1.Latest.FolderId); // Sent copy preferred over All Mail

        var inboxAndAll = s.ListThreads(new ListQuery { FolderIds = new[] { inbox, all } }, now);
        Assert.Equal(2, inboxAndAll.Single(t => t.ThreadKey == "t1").Count);

        // Single folders are unchanged: All Mail shows everything, the label shows its one email.
        Assert.Equal(2, s.ListThreads(new ListQuery { FolderIds = new[] { all } }, now).Count);
        Assert.Equal(1, Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { label } }, now)).Count);

        // The reader shows each email once too.
        Assert.Equal(new[] { "in1@x", "re1@x" }, s.GetThread("A", "t1").Select(m => m.MessageId));
    }

    [Fact]
    public void Snoozed_inbox_copy_hides_the_conversation_in_multi_folder_views()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var all = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", messageId: "a@x", flags: MessageFlags.Flagged),
            Rows.Make("A", all, "t1", messageId: "a@x", flags: MessageFlags.Flagged),
        });
        s.SetSnooze("A", "t1", new[] { inbox }, DateTimeOffset.Now.AddHours(2));
        Assert.Empty(s.ListThreads(new ListQuery { FolderIds = new[] { inbox, all }, FlaggedOnly = true }, DateTimeOffset.Now));
    }

    [Fact]
    public void Body_search_finds_an_email_whose_body_was_cached_on_its_All_Mail_copy()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var all = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var ids = s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", subject: "Invoice", messageId: "b@x", preview: ""),
            Rows.Make("A", all, "t1", subject: "Invoice", messageId: "b@x", preview: ""),
        });
        var allCopy = ids.Single(m => m.FolderId == all);
        s.SaveBody(allCopy.Id, new MessageBody { Text = "disburse the amount mentioned" });
        var hit = Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox, all }, Search = SearchQuery.Parse("disburse") }, DateTimeOffset.Now));
        Assert.Equal(1, hit.Count);
    }

    [Fact]
    public void Search_across_folders_finds_each_email_once()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var all = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", subject: "Demand note level 2", messageId: "d@x"),
            Rows.Make("A", all, "t1", subject: "Demand note level 2", messageId: "d@x"),
        });
        var hit = Assert.Single(s.ListThreads(new ListQuery { FolderIds = new[] { inbox, all }, Search = SearchQuery.Parse("subject:demand") }, DateTimeOffset.Now));
        Assert.Equal(1, hit.Count);
        Assert.Equal(inbox, hit.Latest.FolderId);
    }
}
