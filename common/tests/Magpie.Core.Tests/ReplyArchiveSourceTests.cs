using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Magpie.Core.Tests;

public class ReplyArchiveSourceTests
{
    private static MailEngine Engine(TempDir dir, Func<Account, MimeMessage, CancellationToken, Task>? send = null)
    {
        var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(), sendMessage: send);
        if (engine.AccountById("A") == null)
            engine.Config.Accounts.Add(new Account { Id = "A", Email = "original@example.test", Enabled = false });
        if (engine.AccountById("B") == null)
            engine.Config.Accounts.Add(new Account { Id = "B", Email = "sender@example.test", Enabled = false });
        return engine;
    }

    private static Draft Reply(string messageId = "reply-source@example.test") => new()
    {
        AccountId = "B", Mode = ComposeMode.Reply, MessageId = messageId, ThreadKey = "same-thread",
        ReplyArchiveSource = new ReplyArchiveSource("A", "same-thread"),
        To = "recipient@example.test", Subject = "Reply source", Html = "<p>Unsent reply</p>",
    };

    private static (long Inbox, long Archive) Folders(MailEngine engine, string account)
    {
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = account, Path = "INBOX", Role = FolderRole.Inbox });
        var archive = engine.Store.UpsertFolder(new MailFolder { AccountId = account, Path = "Archive", Role = FolderRole.Archive });
        engine.Store.InsertMessages(new[] { Rows.Make(account, inbox, "same-thread") });
        return (inbox, archive);
    }

    private static void Sql(string database, string sql, params (string Name, object Value)[] values)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(ComposeMode.Reply)]
    [InlineData(ComposeMode.ReplyAll)]
    public void Preparing_reply_captures_original_account_not_the_selected_sender(ComposeMode mode)
    {
        var original = Rows.Make("A", 1, "original-thread", messageId: "original@example.test");
        var draft = Composer.Prepare(mode, new Account { Id = "B", Email = "sender@example.test" }, original,
            null, new[] { "original@example.test", "sender@example.test" }, null);
        Assert.Equal("B", draft.AccountId);
        Assert.Equal(new ReplyArchiveSource("A", "original-thread"), draft.ReplyArchiveSource);
        var forward = Composer.Prepare(ComposeMode.Forward, new Account { Id = "B" }, original, null, Array.Empty<string>(), null);
        Assert.Null(forward.ReplyArchiveSource);
    }

    [Fact]
    public async Task Cross_account_send_archives_only_the_original_account_after_acceptance()
    {
        using var dir = new TempDir();
        string? sentFrom = null;
        using var engine = Engine(dir, (account, _, _) => { sentFrom = account.Id; return Task.CompletedTask; });
        var original = Folders(engine, "A");
        var selectedSender = Folders(engine, "B");
        var draft = Reply();
        var id = engine.QueueSend(draft, DateTimeOffset.Now.AddSeconds(-1), null, archiveAfterSend: true);
        Assert.Equal("B", engine.Store.GetOutbox().Single().AccountId);
        Assert.Equal(new ReplyArchiveSource("A", draft.ThreadKey), engine.Store.GetPostSendArchiveSource(id));
        Assert.Empty(engine.Store.PendingPostSendArchives());
        Assert.Equal(original.Inbox, engine.Store.GetThreadCopies("A", draft.ThreadKey).Single().FolderId);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal("B", sentFrom);
        Assert.Empty(engine.Store.GetThreadCopies("A", draft.ThreadKey));
        Assert.Contains(engine.Store.GetPendingOps("A"), op => op.Kind == PendingOpKind.Move && op.Arg == original.Archive);
        Assert.Equal(selectedSender.Inbox, engine.Store.GetThreadCopies("B", draft.ThreadKey).Single().FolderId);
        Assert.Empty(engine.Store.GetPendingOps("B"));
        Assert.Empty(engine.Store.PendingPostSendArchives());
    }

    [Fact]
    public void Local_save_reopen_restart_and_sender_switchback_keep_the_same_source()
    {
        using var dir = new TempDir();
        long local;
        string database;
        var draft = Reply();
        using (var engine = Engine(dir))
        {
            engine.Settings.Save(notify: false);
            local = engine.SaveLocalDraft(draft, pendingUpload: false);
            database = engine.Paths.Database;
            draft.AccountId = "A";
            Assert.Equal(new ReplyArchiveSource("A", draft.ThreadKey), engine.RestoreReplyArchiveSource(draft));
            engine.SaveLocalDraft(draft, pendingUpload: false);
            draft.AccountId = "B";
            engine.SaveLocalDraft(draft, pendingUpload: false);
        }
        using var reopenedEngine = Engine(dir);
        var reopened = reopenedEngine.OpenLocalDraft(local)!;
        Assert.Equal("B", reopened.AccountId);
        Assert.Equal(ComposeMode.EditDraft, reopened.Mode);
        Assert.Equal(new ReplyArchiveSource("A", draft.ThreadKey), reopened.ReplyArchiveSource);
        Assert.True(reopenedEngine.CanSendAndArchive(reopened));
        Assert.Equal(database, reopenedEngine.Paths.Database);
        // A server draft contains no private source headers; the same stable ID can rehydrate on this PC.
        using var mime = Composer.FromBytes(reopenedEngine.Store.GetLocalDraft(local)!.Mime);
        var fromServer = Composer.FromMime(mime, "B", draft.ThreadKey);
        Assert.Null(fromServer.ReplyArchiveSource);
        Assert.Equal(reopened.ReplyArchiveSource, reopenedEngine.RestoreReplyArchiveSource(fromServer));
        Assert.DoesNotContain(mime.Headers, header => header.Field.Contains("archive", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("ReplyArchiveSource", mime.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Draft_revision_roundtrip_retains_source_independently_of_sender()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var draft = Reply();
        var revision = engine.CaptureDraftRevision(draft);
        var restored = engine.Store.OpenDraftRevision("B", draft.MessageId, revision)!;
        Assert.Equal("B", restored.AccountId);
        Assert.Equal(draft.ReplyArchiveSource, restored.ReplyArchiveSource);
        Assert.Equal(draft.ThreadKey, restored.ThreadKey);
        Assert.True(engine.CanSendAndArchive(restored));
    }

    [Fact]
    public async Task Scheduled_and_undo_keep_inbox_and_recover_source_without_a_local_draft_or_context_cache()
    {
        using var dir = new TempDir();
        var sends = 0;
        using var engine = Engine(dir, (_, _, _) => { sends++; return Task.CompletedTask; });
        var original = Folders(engine, "A");
        var selectedSender = Folders(engine, "B");
        var draft = Reply();
        var local = engine.SaveLocalDraft(draft, pendingUpload: false);
        var id = engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null, archiveAfterSend: true);
        // ComposeViewModel removes its current local copy after queueing; QueueSend removes only stale siblings.
        engine.DeleteLocalDraft(local);
        Assert.Null(engine.Store.GetLocalDraft(local));
        Sql(engine.Paths.Database, "DELETE FROM reply_archive_sources");
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(0, sends);
        var recalled = engine.Recall(id)!;
        Assert.Equal("B", recalled.AccountId);
        Assert.Equal(new ReplyArchiveSource("A", draft.ThreadKey), recalled.ReplyArchiveSource);
        Assert.True(engine.CanSendAndArchive(recalled));
        Assert.Empty(engine.Store.PendingPostSendArchives());
        Assert.Equal(original.Inbox, engine.Store.GetThreadCopies("A", draft.ThreadKey).Single().FolderId);
        Assert.Equal(selectedSender.Inbox, engine.Store.GetThreadCopies("B", draft.ThreadKey).Single().FolderId);
        Assert.Equal(OutboxStatus.Cancelled, engine.Store.GetOutbox(includeDone: true).Single().Status);
        Assert.Null(engine.Recall(id));
    }

    [Fact]
    public async Task Pending_archive_survives_restart_without_context_cache_and_never_retargets_sender()
    {
        using var dir = new TempDir();
        long id;
        long archive;
        long senderInbox;
        using (var engine = Engine(dir))
        {
            engine.Settings.Save(notify: false);
            archive = Folders(engine, "A").Archive;
            senderInbox = Folders(engine, "B").Inbox;
            id = engine.QueueSend(Reply(), DateTimeOffset.Now.AddDays(1), null, archiveAfterSend: true);
            engine.Store.SetOutboxResult(id, OutboxStatus.Sent);
            Sql(engine.Paths.Database, "DELETE FROM reply_archive_sources");
        }
        using var restarted = Engine(dir, (_, _, _) => throw new InvalidOperationException("Already sent mail must not be sent twice"));
        Assert.Equal((id, "A", "same-thread"), restarted.Store.PendingPostSendArchives().Single());
        await restarted.ProcessOutboxAsync(CancellationToken.None);
        Assert.Empty(restarted.Store.GetThreadCopies("A", "same-thread"));
        Assert.Contains(restarted.Store.GetPendingOps("A"), op => op.Kind == PendingOpKind.Move && op.Arg == archive);
        Assert.Equal(senderInbox, restarted.Store.GetThreadCopies("B", "same-thread").Single().FolderId);
        Assert.Empty(restarted.Store.PendingPostSendArchives());
    }

    [Theory]
    [InlineData(ComposeMode.New)]
    [InlineData(ComposeMode.Forward)]
    [InlineData(ComposeMode.EditDraft)]
    public void Unknown_source_is_not_inferred_from_sender_thread_or_reply_headers(ComposeMode mode)
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var draft = Reply();
        draft.Mode = mode;
        draft.ReplyArchiveSource = null;
        draft.InReplyTo = "<original@example.test>";
        Assert.Null(engine.RestoreReplyArchiveSource(draft));
        Assert.False(engine.CanSendAndArchive(draft));
        Assert.Throws<InvalidOperationException>(() => engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null, true));
        Assert.Empty(engine.Store.GetOutbox());
        Assert.True(engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null) > 0);
    }

    [Fact]
    public void A_conflicting_cached_source_is_rejected_repeatedly_without_erasing_the_conflict()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var draft = Reply();
        engine.SaveLocalDraft(draft, pendingUpload: false);
        var conflict = new ReplyArchiveSource("B", draft.ThreadKey);
        draft.ReplyArchiveSource = conflict;
        Assert.False(engine.Store.RememberReplyArchiveSource(draft.MessageId, conflict));
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.Null(engine.RestoreReplyArchiveSource(draft));
            Assert.Equal(conflict, draft.ReplyArchiveSource);
            Assert.False(engine.CanSendAndArchive(draft));
            Assert.Throws<InvalidOperationException>(() => engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null, true));
        }
        Assert.Empty(engine.Store.GetOutbox());
        Assert.True(engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null) > 0);
        Assert.Equal(new ReplyArchiveSource("A", draft.ThreadKey), engine.Store.GetReplyArchiveSource(draft.MessageId));
    }

    [Fact]
    public void Changing_thread_or_removing_source_account_disables_archive_without_retargeting()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var draft = Reply();
        engine.SaveLocalDraft(draft, pendingUpload: false);
        draft.ThreadKey = "other-thread";
        Assert.False(engine.CanSendAndArchive(draft));
        Assert.Throws<InvalidOperationException>(() => engine.QueueSend(draft, DateTimeOffset.Now, null, true));
        draft.ThreadKey = "same-thread";
        engine.Config.Accounts.Remove(engine.AccountById("A")!);
        Assert.Null(engine.RestoreReplyArchiveSource(draft));
        Assert.False(engine.CanSendAndArchive(draft));
        Assert.Throws<InvalidOperationException>(() => engine.QueueSend(draft, DateTimeOffset.Now, null, true));
        Assert.Empty(engine.Store.GetOutbox());
    }

    [Fact]
    public async Task Removing_source_account_after_queue_does_not_archive_the_sending_account()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var senderInbox = Folders(engine, "B").Inbox;
        var id = engine.QueueSend(Reply(), DateTimeOffset.Now.AddDays(1), null, true);
        engine.Store.SetOutboxResult(id, OutboxStatus.Sent);
        engine.Config.Accounts.Remove(engine.AccountById("A")!);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(senderInbox, engine.Store.GetThreadCopies("B", "same-thread").Single().FolderId);
        Assert.Empty(engine.Store.GetPendingOps("B"));
        Assert.Empty(engine.Store.PendingPostSendArchives());
    }

    [Fact]
    public void Source_account_deletion_cleans_context_and_action_but_not_other_account_outgoing_mail()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        var draft = Reply();
        var id = engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null, true);
        engine.Store.DeleteAccount("A");
        Assert.Null(engine.Store.GetReplyArchiveSource(draft.MessageId));
        Assert.Null(engine.Store.GetPostSendArchiveSource(id));
        Assert.Equal("B", engine.Store.GetOutbox().Single().AccountId);
    }

    [Fact]
    public async Task Legacy_archive_actions_without_trusted_source_are_disabled_but_outgoing_mail_is_kept()
    {
        using var dir = new TempDir();
        long pending;
        long sent;
        long senderInbox;
        using (var engine = Engine(dir))
        {
            engine.Settings.Save(notify: false);
            senderInbox = Folders(engine, "B").Inbox;
            pending = engine.QueueSend(Reply("legacy-pending@example.test"), DateTimeOffset.Now.AddDays(1), null);
            sent = engine.QueueSend(Reply("legacy-sent@example.test"), DateTimeOffset.Now.AddDays(1), null);
            engine.Store.SetOutboxResult(sent, OutboxStatus.Sent);
            // Exactly the old schema: its account_id records From, not proven reply origin.
            Sql(engine.Paths.Database, "DROP TABLE post_send_archives; CREATE TABLE post_send_archives(outbox_id INTEGER PRIMARY KEY REFERENCES outbox(id) ON DELETE CASCADE,account_id TEXT NOT NULL,thread_key TEXT NOT NULL);"
                + " INSERT INTO post_send_archives VALUES($pending,'B','same-thread'),($sent,'B','same-thread');",
                ("$pending", pending), ("$sent", sent));
        }
        using var restarted = Engine(dir);
        Assert.Null(restarted.Store.GetPostSendArchiveSource(pending));
        Assert.Null(restarted.Store.GetPostSendArchiveSource(sent));
        Assert.Empty(restarted.Store.PendingPostSendArchives());
        Assert.Equal(OutboxStatus.Queued, restarted.Store.GetOutbox().Single().Status);
        Assert.Equal(2, restarted.Store.GetOutbox(includeDone: true).Count);
        await restarted.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(senderInbox, restarted.Store.GetThreadCopies("B", "same-thread").Single().FolderId);
        Assert.Empty(restarted.Store.GetPendingOps("B"));
    }

    [Fact]
    public void Reply_context_cache_is_bounded_expires_and_normalises_message_identity()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = engine.Paths.Database, Pooling = false }.ToString()))
        {
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = "INSERT INTO reply_archive_sources(message_id,account_id,thread_key,updated) VALUES($m,'A','same-thread',$u)";
            command.Parameters.AddWithValue("$m", "");
            command.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds());
            for (var i = 0; i < MailStore.MaxReplyArchiveSources + 3; i++)
            {
                command.Parameters["$m"].Value = "seed-" + i.ToString("D5") + "@example.test";
                command.ExecuteNonQuery();
            }
            command.Parameters["$m"].Value = "expired@example.test";
            command.Parameters["$u"].Value = DateTimeOffset.UtcNow.AddDays(-MailStore.ReplyArchiveSourceRetentionDays - 1).ToUnixTimeMilliseconds();
            command.ExecuteNonQuery();
            tx.Commit();
        }
        var source = new ReplyArchiveSource("A", "same-thread");
        Assert.True(engine.Store.RememberReplyArchiveSource("<NORMALISED@Example.Test>", source));
        Assert.Equal(source, engine.Store.GetReplyArchiveSource("normalised@example.test"));
        Assert.Null(engine.Store.GetReplyArchiveSource("expired@example.test"));
        using var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = engine.Paths.Database, Pooling = false }.ToString());
        check.Open();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM reply_archive_sources";
        Assert.Equal(MailStore.MaxReplyArchiveSources, Convert.ToInt32(count.ExecuteScalar()));
    }
}
