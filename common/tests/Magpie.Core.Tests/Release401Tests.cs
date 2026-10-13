using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpie.Core.Ai;
using Magpie.Core.Auth;
using Magpie.Core.Calendar;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using Magpie.Core.Updates;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Magpie.Core.Tests;

public class Release401Tests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepted_send_does_not_retry_after_cleanup_or_cancellation(bool cancel)
    {
        using var dir = new TempDir();
        var sends = 0;
        using var cts = new CancellationTokenSource();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(),
            sendMessage: (_, _, _) => { sends++; return Task.CompletedTask; });
        engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        var failures = 0;
        engine.SendFailed += (_, _) => failures++;
        engine.Sent += _ =>
        {
            if (cancel) { cts.Cancel(); throw new OperationCanceledException(cts.Token); }
            throw new IOException("draft cleanup failed");
        };
        var id = engine.QueueSend(new Draft { AccountId = "A", To = "you@test.local", Subject = "Once", Html = "<p>Hello</p>" }, DateTimeOffset.Now.AddMinutes(-1), null);
        await engine.ProcessOutboxAsync(cts.Token);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(1, sends);
        Assert.Equal(0, failures);
        Assert.Equal(OutboxStatus.Sent, engine.Store.GetOutbox(includeDone: true).Single(x => x.Id == id).Status);
    }

    [Theory]
    [InlineData(42u, true)]
    [InlineData(43u, false)]
    [InlineData(0u, false)]
    public void Offline_actions_remember_their_mailbox_generation(uint generation, bool safe)
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        store.UpdateFolderState(inbox, generation, 2, 0);
        store.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 1, Kind = PendingOpKind.Delete });
        var op = Assert.Single(store.GetPendingOps("A"));
        Assert.Equal(generation, op.UidValidity);
        Assert.Equal(safe, AccountSync.CanReplay(op, 42));
    }

    private static CalendarEvent Event() => new()
    {
        AccountId = "A", CalendarId = "c", Title = "Original", Pending = PendingEventOp.Create,
        Start = DateTimeOffset.Now.AddHours(1), End = DateTimeOffset.Now.AddHours(2), AddMeet = true,
    };

    [Theory]
    [InlineData("messages", true)]
    [InlineData("messages", false)]
    [InlineData("mail.db-wal", false)]
    [InlineData("mail.db-shm", false)]
    [InlineData("mail.db", true)]
    public void Mail_move_requires_explicit_replacement_of_reserved_destination_entries(string entry, bool directory)
    {
        using var dir = new TempDir();
        var source = dir.File("source"); var target = dir.File("target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "mail.db"), "source");
        var reserved = Path.Combine(target, entry);
        if (directory) Directory.CreateDirectory(reserved);
        var sentinel = directory ? Path.Combine(reserved, "unrelated.txt") : reserved;
        File.WriteAllText(sentinel, "keep me");
        Assert.Throws<InvalidOperationException>(() => MailLocation.Move(source, target, false));
        Assert.Equal("keep me", File.ReadAllText(sentinel));
        Assert.Equal("source", File.ReadAllText(Path.Combine(source, "mail.db")));
        Assert.Single(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public async Task Targeted_header_rules_preserve_deferred_arrival_actions_and_notification()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Role = FolderRole.Inbox });
        var row = Assert.Single(engine.Store.InsertMessages(new[] { Rows.Make("A", inbox, "arrival") }));
        var bodyRule = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Body, Value = "body match" } }, Actions = { new RuleAction { Kind = RuleActionKind.Pin } } };
        var headerRule = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Subject, Value = "Hello" } }, Actions = { new RuleAction { Kind = RuleActionKind.Tag, Target = "manual" } } };
        var announcements = new List<MessageRow>();
        engine.NewMail += rows => announcements.AddRange(rows);
        engine.RunRules("A", new[] { row }, new[] { bodyRule }, new HashSet<long> { row.Id });
        engine.RunRules("A", new[] { row }, new[] { headerRule });
        Assert.True(Assert.Single(engine.Store.DeferredRules(row.Id)).Notify);
        engine.Store.SaveBody(row.Id, new MessageBody { Text = "body match" });
        await engine.ProcessDeferredRulesAsync(CancellationToken.None);
        await engine.ProcessDeferredRulesAsync(CancellationToken.None);
        Assert.True(engine.Store.GetMessage(row.Id)!.IsFlagged);
        Assert.Contains("manual", engine.Store.GetMessage(row.Id)!.Tags);
        Assert.Equal(row.Id, Assert.Single(announcements).Id);
        Assert.Empty(engine.Store.DeferredRules(row.Id));
    }

    [Fact]
    public async Task Another_body_rule_preserves_the_arrival_snapshot_and_executes_both_requests()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Role = FolderRole.Inbox });
        var row = Assert.Single(engine.Store.InsertMessages(new[] { Rows.Make("A", inbox, "arrival") }));
        var original = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Body, Value = "original" } }, Actions = { new RuleAction { Kind = RuleActionKind.MarkRead } } };
        var manual = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Body, Value = "manual" } }, Actions = { new RuleAction { Kind = RuleActionKind.Pin } } };
        engine.RunRules("A", new[] { row }, new[] { original }, new HashSet<long> { row.Id });
        original.Conditions[0].Value = "changed after deferring";
        engine.RunRules("A", new[] { row }, new[] { manual });
        var queued = Assert.Single(engine.Store.DeferredRules(row.Id));
        Assert.Equal(new[] { original.Id, manual.Id }, queued.Rules.Select(r => r.Id));
        Assert.True(queued.Notify);
        engine.Store.SaveBody(row.Id, new MessageBody { Text = "original manual" });
        await engine.ProcessDeferredRulesAsync(CancellationToken.None);
        Assert.True(engine.Store.GetMessage(row.Id)!.IsSeen);
        Assert.True(engine.Store.GetMessage(row.Id)!.IsFlagged);
    }

    [Fact]
    public async Task Shutdown_after_acceptance_keeps_follow_up_and_removes_local_draft()
    {
        using var dir = new TempDir();
        using var cts = new CancellationTokenSource();
        var sends = 0;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(),
            sendMessage: (_, _, _) => { sends++; cts.Cancel(); return Task.CompletedTask; });
        var account = new Account { Id = "A", Email = "me@test.local", Enabled = false, ServerSavesSent = true };
        engine.Config.Accounts.Add(account);
        engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Drafts", Role = FolderRole.Drafts });
        var sync = new AccountSync(account, engine.Store, engine.Connector, _ => false, _ => null);
        var syncs = (System.Collections.Concurrent.ConcurrentDictionary<string, AccountSync>)typeof(MailEngine)
            .GetField("_syncs", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
        syncs.TryAdd("A", sync);
        var due = DateTimeOffset.Now.AddDays(1);
        var id = engine.QueueSend(new Draft { AccountId = "A", To = "you@test.local", Subject = "Once", Html = "<p>Hello</p>" }, DateTimeOffset.Now.AddMinutes(-1), due);
        var queued = Assert.Single(engine.Store.GetOutbox());
        engine.Store.SaveLocalDraft(new LocalDraft { AccountId = "A", MessageId = queued.MessageId, Subject = "Stale local copy" });
        await engine.ProcessOutboxAsync(cts.Token);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(1, sends);
        Assert.Empty(engine.Store.GetLocalDrafts());
        var reminder = Assert.Single(engine.Store.GetReminders());
        Assert.Equal(queued.ThreadKey, reminder.ThreadKey);
        Assert.Equal(due.ToUnixTimeMilliseconds(), reminder.Due.ToUnixTimeMilliseconds());
        Assert.Equal(OutboxStatus.Sent, engine.Store.GetOutbox(includeDone: true).Single(x => x.Id == id).Status);
    }

    [Fact]
    public void An_incomplete_unpublished_journal_does_not_block_startup()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.Settings, "{\"QuickReplies\":[\"Original\"]}");
        File.WriteAllText(dir.File(SettingsBackup.RestoreJournal + ".next"), "[true,");
        Assert.False(SettingsBackup.ApplyPending(paths, new FakeProtector()));
        Assert.Contains("Original", File.ReadAllText(paths.Settings));
        Assert.False(File.Exists(dir.File(SettingsBackup.RestoreJournal)));
    }

    [Fact]
    public void A_journal_write_failure_keeps_all_original_files_and_allows_retry()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path); var protector = new FakeProtector();
        File.WriteAllText(paths.Settings, "{\"QuickReplies\":[\"Original\"]}");
        new SecretVault(paths.Secrets, protector).Set("old", "old-key");
        SettingsBackup.StagePending(paths, new SettingsBackup.Contents { SettingsJson = "{\"QuickReplies\":[\"Restored\"]}" }, protector);
        using (var locked = new FileStream(dir.File(SettingsBackup.RestoreJournal + ".next"), FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(SettingsBackup.ApplyPending(paths, protector));
            Assert.Contains("Original", File.ReadAllText(paths.Settings));
            Assert.Equal("old-key", new SecretVault(paths.Secrets, protector).Get("old"));
            Assert.False(File.Exists(dir.File(SettingsBackup.RestoreJournal)));
            Assert.True(File.Exists(dir.File(SettingsBackup.PendingFile)));
        }
        Assert.True(SettingsBackup.ApplyPending(paths, protector));
        Assert.Contains("Restored", File.ReadAllText(paths.Settings));
    }

    [Theory]
    [InlineData(0u, true)]
    [InlineData(42u, true)]
    [InlineData(43u, false)]
    public void Empty_folder_replay_handles_its_zero_uid_and_validates_known_generation(uint generation, bool safe)
    {
        Assert.Equal(safe, AccountSync.CanReplay(new PendingOp { Kind = PendingOpKind.EmptyFolder, UidValidity = generation }, 42));
        Assert.False(AccountSync.CanReplay(new PendingOp { Kind = PendingOpKind.Delete, UidValidity = 42 }, 42));
    }

    [Fact]
    public void Deleted_server_summaries_do_not_reappear_as_visible_mail()
    {
        var summaries = new[]
        {
            new MailKit.MessageSummary(0) { UniqueId = new MailKit.UniqueId(1), Flags = MailKit.MessageFlags.Deleted },
            new MailKit.MessageSummary(1) { UniqueId = new MailKit.UniqueId(2), Flags = MailKit.MessageFlags.Seen },
            new MailKit.MessageSummary(2) { UniqueId = new MailKit.UniqueId(3), Flags = MailKit.MessageFlags.Deleted | MailKit.MessageFlags.Seen },
        };
        Assert.Equal(2u, Assert.Single(summaries, AccountSync.IsVisibleSummary).UniqueId.Id);
    }

    [Fact]
    public void Schema_nine_upgrade_retains_trash_history_and_adds_reliability_columns()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var message = Assert.Single(store.InsertMessages(new[] { Rows.Make("A", inbox, "trash") }));
        store.RecordTrashed(new[] { message }, DateTimeOffset.Now);
        var ev = Event(); store.SaveLocalEvent(ev);
        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection("Data Source=" + dir.File("mail.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "ALTER TABLE pending_ops DROP COLUMN uidvalidity; ALTER TABLE cal_events DROP COLUMN revision; ALTER TABLE cal_events DROP COLUMN creation_id; DROP TABLE pending_rules; PRAGMA user_version=9;";
            cmd.ExecuteNonQuery();
        }
        store = new MailStore(dir.File("mail.db"));
        Assert.Equal(inbox, store.TrashOrigin("A", message.MessageId));
        Assert.Equal(ev.Title, store.GetEvent(ev.Id)!.Title);
        store.UpdateFolderState(inbox, 42, 100, 0);
        store.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 10, Kind = PendingOpKind.Delete });
        Assert.True(AccountSync.CanReplay(Assert.Single(store.GetPendingOps("A")), 42));
        using var check = new SqliteConnection("Data Source=" + dir.File("mail.db"));
        check.Open(); using var version = check.CreateCommand(); version.CommandText = "PRAGMA user_version";
        Assert.Equal((long)MailStore.SchemaVersion, (long)version.ExecuteScalar()!);
    }

    [Fact]
    public void Original_beta_schema_nine_gains_trash_tables_without_losing_deferred_work()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var message = Assert.Single(store.InsertMessages(new[] { Rows.Make("A", inbox, "pending") }));
        var rule = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Body, Value = "wait" } }, Actions = { new RuleAction { Kind = RuleActionKind.Pin } } };
        store.DeferRules(message.Id, true, new[] { rule });
        var ev = Event(); store.SaveLocalEvent(ev);
        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection("Data Source=" + dir.File("mail.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "DROP TABLE trash_from; PRAGMA user_version=9;";
            cmd.ExecuteNonQuery();
        }
        store = new MailStore(dir.File("mail.db"));
        Assert.True(Assert.Single(store.DeferredRules(message.Id)).Notify);
        Assert.Equal(ev.Revision, store.GetEvent(ev.Id)!.Revision);
        store.RecordTrashed(new[] { message }, DateTimeOffset.Now);
        Assert.Equal(inbox, store.TrashOrigin("A", message.MessageId));
    }

    [Fact]
    public void Removing_account_also_removes_its_trash_history_without_affecting_other_accounts()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var a = Rows.Make("A", inbox, "a"); var b = Rows.Make("B", inbox, "b");
        store.RecordTrashed(new[] { a, b }, DateTimeOffset.Now);
        store.DeleteAccount("A");
        Assert.Null(store.TrashOrigin("A", a.MessageId));
        Assert.Equal(inbox, store.TrashOrigin("B", b.MessageId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Upload_completion_keeps_a_newer_edit_or_deletion(bool delete)
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var sent = Event(); store.SaveLocalEvent(sent);
        var edited = store.GetEvent(sent.Id)!;
        edited.Title = "Newer edit";
        edited.Pending = delete ? PendingEventOp.Delete : PendingEventOp.Create;
        store.SaveLocalEvent(edited);
        var response = sent.Clone(); response.Title = "Server's old response"; response.EventId = sent.CreationId;
        Assert.False(store.CompleteEventUpload(sent, response));
        var current = store.GetEvent(sent.Id)!;
        Assert.Equal("Newer edit", current.Title);
        Assert.Equal(sent.CreationId, current.EventId);
        Assert.Equal(delete ? PendingEventOp.Delete : PendingEventOp.Update, current.Pending);
        store.DeleteEventIfUnchanged(sent);
        Assert.NotNull(store.GetEvent(sent.Id));
    }

    [Fact]
    public void Matching_upload_completion_clears_pending_work()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var sent = Event(); store.SaveLocalEvent(sent);
        var saved = sent.Clone(); saved.EventId = sent.CreationId; saved.MeetLink = "https://meet.google.com/example";
        Assert.True(store.CompleteEventUpload(sent, saved));
        var row = store.GetEvent(sent.Id)!;
        Assert.Equal(PendingEventOp.None, row.Pending);
        Assert.Equal(saved.MeetLink, row.MeetLink);
        Assert.Empty(store.PendingEvents("A"));
    }

    private sealed class LostReply : HttpMessageHandler
    {
        public readonly List<string> Ids = new();
        private string? _body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                var posted = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Ids.Add(posted["id"]!.GetValue<string>());
                if (_body == null)
                {
                    _body = posted.ToJsonString();
                    throw new HttpRequestException("connection lost after Google accepted the event");
                }
                return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("{}") };
            }
            Assert.EndsWith(Ids[0], request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body!) };
        }
    }

    [Fact]
    public async Task Calendar_insert_recovers_a_lost_reply_using_one_persisted_id()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var e = Event(); store.SaveLocalEvent(e);
        using var handler = new LostReply();
        var client = new GoogleCalendarClient(new HttpClient(handler), _ => Task.FromResult("token"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.InsertAsync(e, "me@test.local", CancellationToken.None));
        e = store.GetEvent(e.Id)!; // retry after restart also uses the stored id
        var saved = await client.InsertAsync(e, "me@test.local", CancellationToken.None);
        Assert.Equal(e.CreationId, saved!.EventId);
        Assert.Equal(new[] { e.CreationId, e.CreationId }, handler.Ids);
        var first = GoogleCalendarClient.ToJson(e, "UTC")["conferenceData"]!["createRequest"]!["requestId"]!.GetValue<string>();
        Assert.Equal(first, GoogleCalendarClient.ToJson(e, "UTC")["conferenceData"]!["createRequest"]!["requestId"]!.GetValue<string>());
    }

    private sealed class DelayedCalendars : HttpMessageHandler
    {
        public readonly TaskCompletionSource Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.SetResult();
            await Continue.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("""{"items":[{"id":"c","summary":"Calendar","accessRole":"owner"}]}""") };
        }
    }

    [Fact]
    public async Task Removing_an_account_prevents_an_inflight_sync_from_recreating_its_calendar()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.SaveCalendars("A", new[] { new CalendarInfo { Id = "c" } });
        var e = Event(); store.SaveLocalEvent(e); store.DeleteEventRow(e.Id); // list-only sync
        var account = new Account { Id = "A", Email = "me@gmail.com", Kind = AccountKind.Gmail, Auth = AuthMethod.OAuth2 };
        using var handler = new DelayedCalendars();
        var service = new CalendarService(store, new HttpClient(handler), () => new[] { account },
            (_, _) => Task.FromResult("token"), _ => OAuthService.GoogleCalendarRead);
        var sync = service.SyncNowAsync(CancellationToken.None);
        await handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ForgetAccount("A"); store.DeleteAccount("A"); handler.Continue.SetResult();
        await sync;
        Assert.Empty(store.GetCalendars("A"));
        Assert.Empty(store.PendingEvents("A"));
        Assert.Throws<InvalidOperationException>(() => service.Save(Event()));
    }

    [Fact]
    public async Task At_start_reminders_are_claimed_once_even_with_concurrent_polling()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.SaveCalendars("A", new[] { new CalendarInfo { Id = "c" } });
        var e = Event(); e.ReminderMinutes = 0; store.SaveLocalEvent(e);
        Assert.Empty(store.TakeDueEventReminders(e.Start.AddMilliseconds(-1)));
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => store.TakeDueEventReminders(e.Start.AddSeconds(30)))));
        Assert.Single(results.SelectMany(x => x));
        Assert.Empty(store.TakeDueEventReminders(e.Start.AddMinutes(1)));
    }

    [Fact]
    public async Task Body_rules_wait_for_the_full_body_and_keep_the_selected_rule_snapshot()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Role = FolderRole.Inbox });
        var row = engine.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t", preview: "Short preview") })[0];
        var rule = new MailRule
        {
            Conditions = { new RuleCondition { Field = RuleField.Body, Op = RuleOp.DoesNotContain, Value = "secret" } },
            Actions = { new RuleAction { Kind = RuleActionKind.Pin } },
        };
        Assert.Contains(row.Id, engine.RunRules("A", new[] { row }, new[] { rule }));
        Assert.False(engine.Store.GetMessage(row.Id)!.IsFlagged); // cannot infer a negative match from a preview
        Assert.Empty(engine.RuleMatchesInInbox(rule)); // previews cannot guess a negative body match
        Assert.Single(engine.Store.DeferredRules()); // previewing leaves queued work intact
        rule.Conditions[0].Value = "different"; // editing the original cannot alter queued work
        engine.Store.SaveBody(row.Id, new MessageBody { Text = "The secret is past the preview" });
        await engine.ProcessDeferredRulesAsync(CancellationToken.None);
        Assert.False(engine.Store.GetMessage(row.Id)!.IsFlagged);
        Assert.Empty(engine.Store.DeferredRules());
        rule.Conditions[0].Op = RuleOp.Contains; rule.Conditions[0].Value = "secret";
        engine.RunRules("A", new[] { row }, new[] { rule });
        Assert.True(engine.Store.GetMessage(row.Id)!.IsFlagged);
    }

    [Fact]
    public async Task Deferred_notifications_are_delivered_once_after_rules_finish()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Role = FolderRole.Inbox });
        var row = engine.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t") })[0];
        var rule = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Body, Value = "absent" } }, Actions = { new RuleAction { Kind = RuleActionKind.SkipNotification } } };
        var announcements = 0; engine.NewMail += rows => announcements += rows.Count;
        engine.RunRules("A", new[] { row }, new[] { rule }, new HashSet<long> { row.Id });
        engine.Store.SaveBody(row.Id, new MessageBody { Text = "Full body" });
        await Task.WhenAll(engine.ProcessDeferredRulesAsync(CancellationToken.None), engine.ProcessDeferredRulesAsync(CancellationToken.None));
        Assert.Equal(1, announcements);
    }

    [Fact]
    public async Task Unavailable_bodies_do_not_starve_later_deferred_rules()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Role = FolderRole.Inbox });
        var rows = engine.Store.InsertMessages(Enumerable.Range(0, 21).Select(i => Rows.Make("A", inbox, "t" + i)).ToArray());
        var rule = new MailRule { Conditions = { new RuleCondition { Field = RuleField.Body, Value = "match" } }, Actions = { new RuleAction { Kind = RuleActionKind.Pin } } };
        engine.RunRules("A", rows, new[] { rule });
        engine.Store.SaveBody(rows[20].Id, new MessageBody { Text = "match" });
        await engine.ProcessDeferredRulesAsync(CancellationToken.None);
        await engine.ProcessDeferredRulesAsync(CancellationToken.None);
        Assert.True(engine.Store.GetMessage(rows[20].Id)!.IsFlagged);
        Assert.Empty(engine.Store.DeferredRules(rows[20].Id));
        Assert.False(engine.Store.GetMessage(rows[0].Id)!.IsFlagged);
    }

    [Theory]
    [InlineData("https://example.com/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://example.com/v1", false)]
    [InlineData("file:///tmp/model", false)]
    public void Ai_configuration_rejects_nonlocal_plaintext_and_non_http_endpoints(string endpoint, bool valid)
    {
        Assert.Equal(valid, AiService.IsConfigured(new AiSettings { Endpoint = endpoint, Model = "model", Provider = AiProviderKind.Custom }, "key"));
    }

    [Fact]
    public async Task Ai_service_blocks_unconsented_text_and_scopes_consent_to_the_full_connection()
    {
        var settings = new AiSettings { Enabled = true, Summarise = true, Model = "model", Endpoint = "https://example.com/v1", ActiveId = "one" };
        var handler = new FakeHttp();
        var service = new AiService(new HttpClient(handler), () => settings, () => "key");
        await Assert.ThrowsAsync<AiException>(() => service.SummariseAsync(new ThreadForAi { Text = "private" }, _ => { }, CancellationToken.None));
        Assert.Empty(handler.Requests);
        settings.Consents.Add(AiService.ConsentKey(AiFeature.Summarise, settings));
        Assert.False(service.NeedsConsent(AiFeature.Summarise));
        foreach (var endpoint in new[] { "https://example.com/v2", "https://example.com:8443/v1" })
        {
            settings.Endpoint = endpoint; Assert.True(service.NeedsConsent(AiFeature.Summarise));
        }
        settings.Endpoint = "https://example.com/v1"; settings.ActiveId = "two";
        Assert.True(service.NeedsConsent(AiFeature.Summarise));
        Assert.Equal(new[] { "Thanks" }, AiService.ParseReplies("[null, \"\", \" Thanks \"]"));
    }

    [Fact]
    public void Quoted_recipient_names_and_oversized_versions_do_not_fail_validation()
    {
        const string recipients = "\"Doe, Jane\" <jane@example.com>; sam@example.com";
        Assert.Empty(Composer.InvalidAddresses(recipients));
        Assert.Equal(2, Composer.ParseAddresses(recipients).Mailboxes.Count());
        Assert.Null(AppVersion.TryParse("v999999999999999999999.0.0"));
        Assert.Null(AppVersion.TryParse("1.999999999999999999999.0"));
        Assert.Null(AppVersion.TryParse("1.0.999999999999999999999"));
    }

    [Fact]
    public async Task Ai_request_uses_the_consented_endpoint_and_its_key_when_settings_change()
    {
        var settings = new AiSettings { Enabled = true, Summarise = true, Model = "model", Endpoint = "https://one.example/v1", ActiveId = "one" };
        settings.Consents.Add(AiService.ConsentKey(AiFeature.Summarise, settings));
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"choices\":[{\"delta\":{\"content\":\"Summary\"}}]}", "[DONE]") };
        var service = new AiService(new HttpClient(http), () => settings, id =>
        {
            Assert.Equal("one", id);
            settings.ActiveId = "two"; settings.Endpoint = "https://two.example/v1"; settings.Consents.Clear();
            return "key-for-one";
        });
        Assert.Equal("Summary", await service.SummariseAsync(new ThreadForAi { Text = "private" }, _ => { }, CancellationToken.None));
        var request = Assert.Single(http.Requests).req;
        Assert.Equal("one.example", request.RequestUri!.Host);
        Assert.Equal("key-for-one", request.Headers.Authorization!.Parameter);
        Assert.True(service.NeedsConsent(AiFeature.Summarise));
    }

    [Fact]
    public void Quoted_semicolons_and_escaped_quotes_are_preserved_in_recipient_names()
    {
        const string addresses = "\"Doe; \\\"Jane\\\"\" <jane@example.com>; sam@example.com";
        Assert.Empty(Composer.InvalidAddresses(addresses));
        var parsed = Composer.ParseAddresses(addresses).Mailboxes.ToArray();
        Assert.Equal(2, parsed.Length);
        Assert.Equal("Doe; \"Jane\"", parsed[0].Name);
        Assert.Equal("jane@example.com", parsed[0].Address);
    }

    private sealed class FailProtect : ISecretProtector
    {
        private readonly FakeProtector _inner = new();
        public byte[] Protect(byte[] plain) => throw new IOException("vault write failed");
        public byte[] Unprotect(byte[] cipher) => _inner.Unprotect(cipher);
    }

    [Fact]
    public void A_failed_restore_keeps_original_settings_credentials_and_the_pending_backup()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path); var protector = new FakeProtector();
        File.WriteAllText(paths.Settings, "{\"Accounts\":[]}");
        new SecretVault(paths.Secrets, protector).Set("old", "original-token");
        var before = File.ReadAllBytes(paths.Secrets);
        SettingsBackup.StagePending(paths, new SettingsBackup.Contents { SettingsJson = "{\"QuickReplies\":[\"New\"]}", Secrets = new() { ["new"] = "new-token" } }, protector);
        Assert.False(SettingsBackup.ApplyPending(paths, new FailProtect()));
        Assert.Equal("{\"Accounts\":[]}", File.ReadAllText(paths.Settings));
        Assert.Equal(before, File.ReadAllBytes(paths.Secrets));
        Assert.True(File.Exists(dir.File(SettingsBackup.PendingFile)));
        Assert.True(SettingsBackup.ApplyPending(paths, protector));
        Assert.Equal("new-token", new SecretVault(paths.Secrets, protector).Get("new"));
    }

    [Fact]
    public void Startup_recovers_a_partially_installed_restore_before_loading_settings()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.Settings + ".before-restore", "original");
        File.WriteAllText(paths.Settings, "partial new settings");
        File.WriteAllText(paths.Secrets, "partial secrets");
        File.WriteAllText(dir.File("mail-folder.txt"), dir.File("partial-folder"));
        File.WriteAllText(dir.File(SettingsBackup.RestoreJournal), "[true,false,false]");
        Assert.False(SettingsBackup.ApplyPending(paths, new FakeProtector()));
        Assert.Equal("original", File.ReadAllText(paths.Settings));
        Assert.False(File.Exists(paths.Secrets));
        Assert.False(File.Exists(dir.File(SettingsBackup.RestoreJournal)));
        Assert.Equal(paths.Root, paths.MailRoot);
    }

    private sealed class FailCopy : IProgress<(long done, long total)>
    {
        public void Report((long done, long total) value) => throw new IOException("disk interrupted");
    }

    [Fact]
    public void Failed_mail_move_preserves_source_and_existing_destination_mail()
    {
        using var dir = new TempDir();
        var source = dir.File("source"); var target = dir.File("target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "mail.db"), "source");
        File.WriteAllText(Path.Combine(target, "mail.db"), "destination");
        Assert.Throws<IOException>(() => MailLocation.Move(source, target, true, new FailCopy()));
        Assert.Equal("source", File.ReadAllText(Path.Combine(source, "mail.db")));
        Assert.Equal("destination", File.ReadAllText(Path.Combine(target, "mail.db")));
        Assert.Empty(Directory.EnumerateDirectories(target));
        Assert.Throws<InvalidOperationException>(() => MailLocation.Move(source, Path.Combine(source, "child"), true));
        MailLocation.Move(source, target, true);
        Assert.Equal("source", File.ReadAllText(Path.Combine(target, "mail.db")));
        Assert.False(File.Exists(Path.Combine(source, "mail.db")));
        Assert.Throws<InvalidOperationException>(() => MailLocation.Move(source, target, true));
        Assert.Equal("source", File.ReadAllText(Path.Combine(target, "mail.db")));
    }

    [Fact]
    public void Schema_eight_migrates_calendar_revisions_and_safely_marks_old_uid_work_unknown()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var e = Event(); store.SaveLocalEvent(e);
        store.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 1, Kind = PendingOpKind.Delete, UidValidity = 42 });
        SqliteConnection.ClearAllPools();
        using (var db = new SqliteConnection("Data Source=" + dir.File("mail.db")))
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "ALTER TABLE pending_ops DROP COLUMN uidvalidity; ALTER TABLE cal_events DROP COLUMN revision; ALTER TABLE cal_events DROP COLUMN creation_id; DROP TABLE pending_rules; PRAGMA user_version=8;";
            cmd.ExecuteNonQuery();
        }
        store = new MailStore(dir.File("mail.db"));
        Assert.NotNull(store.GetEvent(e.Id));
        var pending = Assert.Single(store.GetPendingOps("A"));
        Assert.Equal(0u, pending.UidValidity);
        Assert.False(AccountSync.CanReplay(pending, 42));
        Assert.Empty(store.DeferredRules());
    }
}
