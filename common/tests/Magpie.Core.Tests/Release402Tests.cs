using Magpie.Core.Caching;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

public class Release402Tests
{
    [Fact]
    public async Task A_saved_body_opens_after_restart_without_an_imap_connection_or_mime_read()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Role = FolderRole.Inbox });
        var row = engine.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t") }).Single();
        engine.Store.SaveBody(row.Id, new MessageBody { Html = "<p>Offline</p>", Text = "Offline", ImagesComplete = true });
        var reopened = new MailStore(engine.Paths.Database);
        using var sync = new AccountSync(new Account { Id = "A", ImapHost = "invalid.test" }, reopened, engine.Connector, _ => false, _ => null);
        var mimeReads = 0;
        sync.MimePathFor = (_, _) => { mimeReads++; throw new InvalidOperationException("Must use saved text first"); };
        Assert.Equal("Offline", (await sync.FetchBodyAsync(row, CancellationToken.None))!.Text);
        Assert.Equal("Offline", (await sync.FetchBodyAsync(row, CancellationToken.None))!.Text);
        Assert.Equal(0, mimeReads);
        Assert.True(reopened.TryGetBodyFromMemory(row.Id, out _));
    }

    [Fact]
    public async Task Hot_body_reads_do_not_wait_for_the_disk_writer()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t") }).Single();
        store.SaveBody(row.Id, new MessageBody { Text = "hot" });
        var saved = store.GetBody(row.Id)!;
        var writerGate = typeof(MailStore).GetField("_bodyGate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(store)!;
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() =>
        {
            lock (writerGate) { started.SetResult(); release.Wait(TimeSpan.FromSeconds(10)); }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Task.Run(() =>
            {
                Assert.True(store.TryGetBodyFromMemory(row.Id, out var hot));
                Assert.Equal("hot", hot!.Text);
                Assert.NotNull(store.BodyFingerprint(row.Id, saved));
                Assert.Equal("hot", store.GetBody(row.Id)!.Text);
            }).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); await writer; }
    }

    [Fact]
    public void Memory_snapshots_are_independent_and_a_body_save_invalidates_the_old_content_and_digest()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t") }).Single();
        store.SaveBody(row.Id, new MessageBody { Html = "<p>one</p>", Images = { ["logo"] = "data:old" }, Attachments = { new() { FileName = "old.pdf" } } });
        var first = store.GetBody(row.Id)!;
        var digest = store.BodyFingerprint(row.Id, first);
        Assert.NotNull(digest);
        first.Html = "<p>two</p>";
        first.Images["logo"] = "data:new";
        first.Attachments[0].FileName = "changed.pdf";
        Assert.Null(store.BodyFingerprint(row.Id, first));
        var unchanged = store.GetBody(row.Id)!;
        Assert.Equal("<p>one</p>", unchanged.Html);
        Assert.Equal("old.pdf", unchanged.Attachments[0].FileName);
        store.SaveBody(row.Id, first);
        Assert.False(store.TryGetBodyFromMemory(row.Id, out _));
        var changed = store.GetBody(row.Id)!;
        Assert.Equal("<p>two</p>", changed.Html);
        Assert.NotEqual(digest, store.BodyFingerprint(row.Id, changed));
        Assert.Null(store.BodyFingerprint(row.Id, unchanged));
    }

    [Theory]
    [InlineData("row")]
    [InlineData("uids")]
    [InlineData("wipe")]
    [InlineData("folder")]
    [InlineData("account")]
    public void Deleted_mail_is_removed_from_disk_and_memory(string operation)
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t") }).Single();
        store.SaveBody(row.Id, new MessageBody { Text = "cached" });
        Assert.NotNull(store.GetBody(row.Id));
        switch (operation)
        {
            case "row": store.DeleteRow(row.Id); break;
            case "uids": store.DeleteUids(inbox, new[] { row.Uid }); break;
            case "wipe": store.WipeFolderMessages(inbox); break;
            case "folder": store.DeleteFolder(inbox); break;
            case "account": store.DeleteAccount("A"); break;
        }
        Assert.False(store.TryGetBodyFromMemory(row.Id, out _));
        Assert.Null(store.GetBody(row.Id));
    }

    [Fact]
    public void Cache_evicts_by_recency_and_bytes_and_does_not_keep_oversized_entries()
    {
        var cache = new LruCache<string, string>(3, 10);
        cache.Set("a", "a", 4); cache.Set("b", "b", 4);
        Assert.True(cache.TryGet("a", out _));
        cache.Set("c", "c", 4);
        Assert.False(cache.TryGet("b", out _));
        cache.Set("large", "large", 11);
        Assert.False(cache.TryGet("large", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.Equal(8, cache.Bytes);
        cache.Clear(); Assert.Equal(0, cache.Count); Assert.Equal(0, cache.Bytes);
    }

    [Fact]
    public async Task Cancelling_one_reader_keeps_the_shared_download_for_other_readers()
    {
        var shared = new SharedWork<long, string>();
        var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var selection = new CancellationTokenSource();
        var downloads = 0;
        Task<string> Download() { Interlocked.Increment(ref downloads); return finished.Task; }
        var first = shared.RunAsync(1, Download, selection.Token);
        var second = shared.RunAsync(1, Download, CancellationToken.None);
        selection.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        finished.SetResult("saved to disk");
        Assert.Equal("saved to disk", await second);
        Assert.Equal(1, downloads);
    }

    [Fact]
    public async Task A_failed_shared_operation_can_be_retried()
    {
        var shared = new SharedWork<int, int>();
        await Assert.ThrowsAsync<IOException>(() => shared.RunAsync(1, () => Task.FromException<int>(new IOException()), CancellationToken.None));
        Assert.Equal(42, await shared.RunAsync(1, () => Task.FromResult(42), CancellationToken.None));
    }

    [Fact]
    public void Large_local_pictures_are_retained_in_memory_without_changing_the_saved_disk_body()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t") }).Single();
        store.SaveBody(row.Id, new MessageBody { Html = "<img src=\"cid:photo\">", ImagesComplete = true,
            Attachments = { new() { ContentId = "photo", ContentType = "image/png", Inline = true } } });
        var original = store.GetBody(row.Id)!;
        Assert.True(store.RetainInlineImages(row.Id, original, new() { ["photo"] = "data:local-picture" }));
        Assert.Equal("data:local-picture", store.GetBody(row.Id)!.Images["photo"]);
        Assert.Empty(new MailStore(dir.File("mail.db")).GetBody(row.Id)!.Images);
        store.SaveBody(row.Id, new MessageBody { Html = "<p>new content</p>" });
        Assert.False(store.RetainInlineImages(row.Id, original, new() { ["photo"] = "data:stale" }));
        Assert.Empty(store.GetBody(row.Id)!.Images);
    }

    [Fact]
    public void Oversized_local_pictures_have_no_retained_digest_and_can_be_recovered_again()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t") }).Single();
        store.SaveBody(row.Id, new MessageBody { Html = "<img src=\"cid:photo\">", ImagesComplete = true });
        var pictures = new Dictionary<string, string> { ["photo"] = "data:image/png;base64," + new string('A', 33 * 1024 * 1024) };
        for (var selection = 0; selection < 2; selection++)
        {
            var presentation = store.GetBody(row.Id)!;
            Assert.True(store.RetainInlineImages(row.Id, presentation, pictures));
            presentation.Images = pictures;
            Assert.Null(store.BodyFingerprint(row.Id, presentation)); // Cannot suppress recovery on the next open.
            Assert.False(store.TryGetBodyFromMemory(row.Id, out _));
            Assert.Same(pictures["photo"], presentation.Images["photo"]);
            Assert.Empty(new MailStore(dir.File("mail.db")).GetBody(row.Id)!.Images);
        }
    }

    [Fact]
    public void Prepared_page_fingerprints_cover_content_images_permissions_and_theme()
    {
        var body = new MessageBody { Html = "<p>one</p>", Text = "one", Images = { ["logo"] = "data:one" } };
        var message = new RenderMessage { Row = new MessageRow { Id = 1 }, Body = body, InlineImages = body.Images };
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000_000);
        string Digest(bool allow = false, bool dark = false) => ReaderPage.ContentFingerprint("Subject", new[] { message }, allow, dark, 600, now);
        var original = Digest();
        body.Html = "<p>two</p>"; Assert.NotEqual(original, Digest());
        original = Digest(); body.Images["logo"] = "data:two"; Assert.NotEqual(original, Digest());
        Assert.NotEqual(Digest(), Digest(allow: true)); Assert.NotEqual(Digest(), Digest(dark: true));
    }

    [Fact]
    public void Marking_mail_read_does_not_invalidate_its_prepared_content()
    {
        var row = new MessageRow { Id = 1, Flags = MessageFlags.None };
        var body = new MessageBody { Html = "<p>Saved content</p>" };
        var now = DateTimeOffset.Now;
        var before = ReaderPage.ContentFingerprint("Subject", new[] { new RenderMessage { Row = row, Body = body, Expanded = true } }, false, false, 600, now);
        row.Flags = MessageFlags.Seen;
        var after = ReaderPage.ContentFingerprint("Subject", new[] { new RenderMessage { Row = row, Body = body, Expanded = false } }, false, false, 600, now);
        Assert.Equal(before, after); // Read badges update in place; the user's expanded/collapsed DOM is retained.
    }

    [Fact]
    public void A_prepared_page_keeps_sandboxing_and_separates_only_the_outer_document()
    {
        var result = HtmlRenderer.BuildConversation("Subject", new[] { new RenderMessage
        {
            Row = new MessageRow { Id = 1 }, Body = new MessageBody { Html = "<p>Hello</p><script>attack()</script>" }, Expanded = true,
        } }, false, DateTimeOffset.Now);
        var page = new ReaderPage("A/thread", "digest", result.Html, result.BlockedImages);
        Assert.Contains("<iframe", page.Body);
        Assert.Contains("sandbox=\"allow-same-origin allow-popups allow-popups-to-escape-sandbox\"", page.Body);
        Assert.DoesNotContain("attack()", page.Body);
        Assert.DoesNotContain("allow-scripts", page.Body);
        Assert.DoesNotContain("<script>", page.Body);
        Assert.Contains(".msg", page.Style);
    }

    [Fact]
    public void Ninety_day_bodies_survive_restart_and_are_not_queued_for_download_again()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var since = DateTimeOffset.Now.AddDays(-90);
        var rows = store.InsertMessages(new[] { Rows.Make("A", inbox, "recent", date: since.AddDays(1)), Rows.Make("A", inbox, "older", date: since.AddDays(-1)) });
        store.SaveBody(rows[0].Id, new MessageBody { Text = "recent body", ImagesComplete = true });
        var reopened = new MailStore(dir.File("mail.db"));
        Assert.Equal((1, 1), reopened.WindowProgress("A", since));
        Assert.Empty(reopened.RowsWithoutBody(inbox, 100, since));
        Assert.Equal("recent body", reopened.GetBody(rows[0].Id)!.Text);
    }
}
