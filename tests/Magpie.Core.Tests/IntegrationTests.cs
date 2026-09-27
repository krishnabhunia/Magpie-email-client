using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using MimeKit;
using KitFlags = MailKit.MessageFlags;
using MailFolder = Magpie.Core.Models.MailFolder;
using SearchQuery = MailKit.Search.SearchQuery;

namespace Magpie.Core.Tests;

/// <summary>
/// End-to-end tests against a real IMAP server (Dovecot on 127.0.0.1:1143) and an SMTP server that
/// delivers into it (127.0.0.1:1025). They are skipped (pass trivially) when those servers are not running.
/// Set up with build/test-servers/start.sh.
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests
{
    private const string Host = "127.0.0.1";
    private const string UsersFile = "/opt/imaptest/users";

    private static bool ServersUp()
    {
        foreach (var port in new[] { 1143, 1025 })
        {
            try { using var c = new TcpClient(); c.Connect(Host, port); }
            catch { return false; }
        }
        return File.Exists(UsersFile);
    }

    private static string NewUser()
    {
        var u = $"u{Guid.NewGuid():N}"[..12] + "@test.local";
        File.AppendAllText(UsersFile, $"{u}:{{PLAIN}}secret\n");
        return u;
    }

    private static Account AccountFor(string email) => new()
    {
        Email = email, DisplayName = "Tester", UserName = email, Kind = AccountKind.Imap, Auth = AuthMethod.Password,
        ImapHost = Host, ImapPort = 1143, ImapSecurity = TlsMode.None,
        SmtpHost = Host, SmtpPort = 1025, SmtpSecurity = TlsMode.None, ServerSavesSent = false,
    };

    private static async Task<ImapClient> Raw(string user)
    {
        var c = new ImapClient();
        await c.ConnectAsync(Host, 1143, MailKit.Security.SecureSocketOptions.None);
        await c.AuthenticateAsync(user, "secret");
        return c;
    }

    private static MimeMessage Msg(string from, string to, string subject, string text, string? id = null, string? inReplyTo = null, DateTimeOffset? date = null, Action<MimeMessage>? extra = null)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.Date = date ?? DateTimeOffset.Now;
        m.MessageId = id ?? MimeKit.Utils.MimeUtils.GenerateMessageId("x.test");
        if (inReplyTo != null) { m.InReplyTo = inReplyTo; m.References.Add(inReplyTo); }
        m.Body = new TextPart("plain") { Text = text };
        extra?.Invoke(m);
        return m;
    }

    private static async Task Append(string user, string folder, MimeMessage m, KitFlags flags = KitFlags.None)
    {
        using var c = await Raw(user);
        var f = folder == "INBOX" ? c.Inbox : await c.GetFolderAsync(folder);
        await f.AppendAsync(new AppendRequest(m, flags));
        await c.DisconnectAsync(true);
    }

    private static async Task<T> WaitFor<T>(Func<T?> probe, string what, int seconds = 25) where T : class
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var v = probe();
            if (v != null) return v;
            await Task.Delay(200);
        }
        throw new TimeoutException("Timed out waiting for: " + what);
    }

    private static async Task WaitUntil(Func<bool> probe, string what, int seconds = 25) =>
        await WaitFor(() => probe() ? "ok" : null, what, seconds);

    private static async Task WaitUntilAsync(Func<Task<bool>> probe, string what, int seconds = 25)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await probe()) return;
            await Task.Delay(300);
        }
        throw new TimeoutException("Timed out waiting for: " + what);
    }

    private static MailEngine NewEngine(TempDir dir) => new(new AppPaths(dir.Path), new FakeProtector());

    private static List<ThreadRow> Inbox(MailEngine e, Category? cat = null) =>
        e.Store.ListThreads(new ListQuery { FolderIds = e.FolderIds(FolderRole.Inbox), Category = cat }, DateTimeOffset.Now);

    [Fact]
    public async Task Sync_threads_categories_and_server_roundtrips()
    {
        if (!ServersUp()) return;
        var me = NewUser();
        var first = Msg("anita@vendor.test", me, "Vendor migration", "Can we decide by Friday?", "a1@vendor.test", date: DateTimeOffset.Now.AddHours(-3));
        await Append(me, "INBOX", first);
        await Append(me, "Sent", Msg(me, "anita@vendor.test", "Re: Vendor migration", "Friday works.", "me1@test.local", "a1@vendor.test", DateTimeOffset.Now.AddHours(-2)), KitFlags.Seen);
        await Append(me, "INBOX", Msg("anita@vendor.test", me, "Re: Vendor migration", "Great, booked.", "a2@vendor.test", "me1@test.local", DateTimeOffset.Now.AddHours(-1)));
        await Append(me, "INBOX", Msg("news@shop.test", me, "Weekly deals", "Sale!", extra: m => m.Headers.Add("List-Unsubscribe", "<mailto:u@shop.test>")));
        await Append(me, "INBOX", Msg("no-reply@bank.test", me, "OTP 123456", "Your code"));

        using var dir = new TempDir();
        using var e = NewEngine(dir);
        Assert.Null(await e.Connector.TestAsync(AccountFor(me), "secret", CancellationToken.None));
        Assert.Contains("password", await e.Connector.TestAsync(AccountFor(me), "wrong", CancellationToken.None) ?? "", StringComparison.OrdinalIgnoreCase);

        e.Start();
        var acc = AccountFor(me);
        e.AddAccount(acc, "secret", null);

        await WaitUntil(() => Inbox(e).Count == 3, "3 inbox conversations");
        var folders = e.Folders(acc.Id);
        Assert.Contains(folders, f => f.Role == FolderRole.Sent);
        Assert.Contains(folders, f => f.Role == FolderRole.Archive);
        Assert.Contains(folders, f => f.Role == FolderRole.Trash);

        // Conversation = both inbox messages + our Sent reply, oldest first
        var vendor = Inbox(e).Single(t => t.Latest.Subject.Contains("Vendor"));
        Assert.Equal(2, vendor.Count);
        await WaitUntil(() => e.Store.GetThread(acc.Id, vendor.ThreadKey).Count == 3, "sent reply joins the conversation");
        var thread = e.Store.GetThread(acc.Id, vendor.ThreadKey);
        Assert.Equal(new[] { "a1@vendor.test", "me1@test.local", "a2@vendor.test" }, thread.Select(m => m.MessageId));

        // Smart inbox (anita is a known contact because we wrote to her)
        await WaitUntil(() => Inbox(e, Category.People).Count == 1, "People bucket");
        Assert.Equal("Weekly deals", Inbox(e, Category.Newsletters).Single().Latest.Subject);
        Assert.Equal("OTP 123456", Inbox(e, Category.Notifications).Single().Latest.Subject);

        // Opening downloads the body and caches it
        var (body, mime) = await e.LoadAsync(thread[0], true, CancellationToken.None);
        Assert.Contains("decide by Friday", body!.Text);
        Assert.NotNull(mime);
        Assert.True(File.Exists(e.Paths.MimePath(acc.Id, thread[0].Id)));

        // Mark read → \Seen on the server
        e.SetRead(acc.Id, vendor.ThreadKey, true);
        await WaitUntilAsync(async () =>
        {
            using var c = await Raw(me);
            await c.Inbox.OpenAsync(FolderAccess.ReadOnly);
            return (await c.Inbox.SearchAsync(SearchQuery.NotSeen.And(SearchQuery.SubjectContains("Vendor")))).Count == 0;
        }, "server \\Seen");

        // Pin → \Flagged on the newest message
        e.SetPinned(acc.Id, vendor.ThreadKey, true);
        await WaitUntilAsync(async () =>
        {
            using var c = await Raw(me);
            await c.Inbox.OpenAsync(FolderAccess.ReadOnly);
            return (await c.Inbox.SearchAsync(SearchQuery.Flagged)).Count == 1;
        }, "server \\Flagged");
        Assert.True(Inbox(e).Single(t => t.ThreadKey == vendor.ThreadKey).Flagged);

        // Flag change made on another device arrives via CONDSTORE
        using (var c = await Raw(me))
        {
            await c.Inbox.OpenAsync(FolderAccess.ReadWrite);
            var otp = (await c.Inbox.SearchAsync(SearchQuery.SubjectContains("OTP"))).Single();
            await c.Inbox.StoreAsync(otp, new StoreFlagsRequest(StoreAction.Add, KitFlags.Seen) { Silent = true });
        }
        e.SyncNow(acc.Id);
        await WaitUntil(() => Inbox(e).Single(t => t.Latest.Subject == "OTP 123456").UnreadCount == 0, "remote read state");

        // Archive → moves to Archive on the server, gone from the local inbox
        var news = Inbox(e).Single(t => t.Latest.Subject == "Weekly deals");
        await e.ArchiveAsync(acc.Id, news.ThreadKey, e.FolderIds(FolderRole.Inbox, acc.Id));
        Assert.DoesNotContain(Inbox(e), t => t.ThreadKey == news.ThreadKey);
        await WaitUntilAsync(async () =>
        {
            using var c = await Raw(me);
            var arch = await c.GetFolderAsync("Archive");
            await arch.OpenAsync(FolderAccess.ReadOnly);
            return arch.Count == 1;
        }, "server archive");
        var archiveId = e.FolderIds(FolderRole.Archive, acc.Id).Single();
        e.SyncNow(acc.Id);
        await WaitUntil(() => e.Store.GetUidMap(archiveId).Count == 1, "archive folder synced");

        // Trash
        var otpThread = Inbox(e).Single(t => t.Latest.Subject == "OTP 123456");
        await e.TrashAsync(acc.Id, otpThread.ThreadKey, e.FolderIds(FolderRole.Inbox, acc.Id));
        await WaitUntilAsync(async () =>
        {
            using var c = await Raw(me);
            var trash = await c.GetFolderAsync("Trash");
            await trash.OpenAsync(FolderAccess.ReadOnly);
            return trash.Count == 1;
        }, "server trash");

        // Deleted on another device → disappears locally
        using (var c = await Raw(me))
        {
            await c.Inbox.OpenAsync(FolderAccess.ReadWrite);
            var uids = await c.Inbox.SearchAsync(SearchQuery.SubjectContains("booked").Or(SearchQuery.BodyContains("booked")));
            await c.Inbox.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true });
            await c.Inbox.ExpungeAsync();
        }
        e.SyncNow(acc.Id);
        await WaitUntil(() => Inbox(e).Single(t => t.ThreadKey == vendor.ThreadKey).Count == 1, "remote delete");
    }

    [Fact]
    public async Task New_mail_push_send_undo_send_later_and_sent_copy()
    {
        if (!ServersUp()) return;
        var me = NewUser();
        var friend = NewUser();
        await Append(me, "INBOX", Msg("anita@vendor.test", me, "Hello", "first"));

        using var dir = new TempDir();
        using var e = NewEngine(dir);
        var newMail = new List<MessageRow>();
        e.NewMail += rows => { lock (newMail) newMail.AddRange(rows); };
        var sentEvents = new List<OutboxItem>();
        e.Sent += o => { lock (sentEvents) sentEvents.Add(o); };
        e.Start();
        var acc = AccountFor(me);
        e.AddAccount(acc, "secret", null);
        await WaitUntil(() => Inbox(e).Count == 1, "initial sync");
        await WaitUntil(() => e.StatusOf(acc.Id)?.State == SyncState.Idle, "idle");

        // A new message arrives → IDLE push → NewMail event (no manual refresh)
        await Task.Delay(21_000); // IDLE loop starts 20 s after launch
        await Append(me, "INBOX", Msg("bob@y.test", me, "Pushed", "via idle"));
        await WaitUntil(() => { lock (newMail) return newMail.Any(m => m.Subject == "Pushed"); }, "IDLE push new-mail event", 30);

        // Send now (the undo window is the send time)
        var d = new Draft { AccountId = acc.Id, To = friend, Subject = "From Magpie", Html = "<p>Hi from the test</p>" };
        var id = e.QueueSend(d, DateTimeOffset.Now, null);
        await WaitUntil(() => { lock (sentEvents) return sentEvents.Any(s => s.Id == id); }, "sent");
        await WaitUntilAsync(async () =>
        {
            using var c = await Raw(friend);
            await c.Inbox.OpenAsync(FolderAccess.ReadOnly);
            return (await c.Inbox.SearchAsync(SearchQuery.SubjectContains("From Magpie"))).Count == 1;
        }, "delivered to recipient");
        // Copy saved in Sent (server doesn't do it itself)
        await WaitUntil(() => e.Store.ListThreads(new ListQuery { FolderIds = e.FolderIds(FolderRole.Sent, acc.Id) }, DateTimeOffset.Now)
            .Any(t => t.Latest.Subject == "From Magpie"), "Sent copy synced");
        Assert.Contains(friend, e.Store.KnownContacts());

        // Undo send: recalled before its time → never delivered, draft comes back
        var undoId = e.QueueSend(new Draft { AccountId = acc.Id, To = friend, Subject = "Oops", Html = "<p>undo me</p>" }, DateTimeOffset.Now.AddSeconds(8), null);
        var back = e.Recall(undoId);
        Assert.NotNull(back);
        Assert.Equal("Oops", back!.Subject);
        Assert.Contains("undo me", back.Html);

        // Send later: queued now, goes out at its time
        var laterId = e.QueueSend(new Draft { AccountId = acc.Id, To = friend, Subject = "Scheduled", Html = "<p>later</p>" }, DateTimeOffset.Now.AddSeconds(4), DateTimeOffset.Now.AddSeconds(5));
        Assert.Contains(e.Outbox(), o => o.Id == laterId && o.Status == OutboxStatus.Queued);
        await WaitUntil(() => { lock (sentEvents) return sentEvents.Any(s => s.Id == laterId); }, "scheduled send", 30);
        Assert.Single(e.WaitingReminders()); // "remind me if no reply" armed after sending

        using (var c = await Raw(friend))
        {
            await c.Inbox.OpenAsync(FolderAccess.ReadOnly);
            Assert.Empty(await c.Inbox.SearchAsync(SearchQuery.SubjectContains("Oops")));
        }
    }

    [Fact]
    public async Task Snooze_wakes_and_follow_up_reminder_fires()
    {
        if (!ServersUp()) return;
        var me = NewUser();
        await Append(me, "INBOX", Msg("anita@vendor.test", me, "Snooze me", "later please"), KitFlags.Seen);
        await Append(me, "INBOX", Msg("bob@y.test", me, "Remind me", "no reply yet"), KitFlags.Seen);

        using var dir = new TempDir();
        using var e = NewEngine(dir);
        var woke = new List<MessageRow>();
        e.SnoozeWoke += r => { lock (woke) woke.AddRange(r); };
        var due = new List<Reminder>();
        e.ReminderDue += r => { lock (due) due.Add(r); };
        e.Start();
        var acc = AccountFor(me);
        e.AddAccount(acc, "secret", null);
        await WaitUntil(() => Inbox(e).Count == 2, "sync");

        var snooze = Inbox(e).Single(t => t.Latest.Subject == "Snooze me");
        e.Snooze(acc.Id, snooze.ThreadKey, DateTimeOffset.Now.AddSeconds(3));
        Assert.Single(Inbox(e));
        Assert.Single(e.Store.ListThreads(new ListQuery { FolderIds = e.FolderIds(FolderRole.Inbox), Snoozed = true }, DateTimeOffset.Now));
        await WaitUntil(() => { lock (woke) return woke.Count > 0; }, "snooze woke", 15);
        var back = Inbox(e);
        Assert.Equal(2, back.Count);
        Assert.Equal("Snooze me", back[0].Latest.Subject); // on top
        Assert.Equal(1, back[0].UnreadCount);              // and unread again

        var remind = Inbox(e).Single(t => t.Latest.Subject == "Remind me");
        e.RemindMe(acc.Id, remind.ThreadKey, "Remind me", DateTimeOffset.Now.AddSeconds(-1), always: false);
        await WaitUntil(() => { lock (due) return due.Count > 0; }, "reminder due", 45);
        Assert.Single(e.DueReminders());
    }
}
