using System.Net;
using System.Net.Sockets;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using MimeKit;

namespace Magpie.Core.Tests;

public class DeliveryReceiptTests
{
    private static Account Account() => new() { Id = "sender", Email = "sender@example.test", Enabled = false };

    private static Draft Draft(bool requested = true) => new()
    {
        AccountId = "sender", To = "recipient@example.test", Cc = "copy@example.test", Bcc = "hidden@example.test",
        Subject = "Delivery request", Html = "<p>Unchanged message</p>", RequestDeliveryReceipt = requested,
        MessageId = "receipt@example.test", ThreadKey = "receipt-thread",
    };

    private static MailEngine Engine(TempDir dir, Func<Account, MimeMessage, CancellationToken, Task<DeliveryReceiptResult>>? send = null)
    {
        var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(),
            sendMessage: (_, _, _) => Task.CompletedTask, sendWithDeliveryReceipt: send);
        if (engine.AccountById("sender") == null) engine.Config.Accounts.Add(Account());
        return engine;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Server_draft_MIME_keeps_the_option_but_normal_outgoing_MIME_never_exposes_it(bool requested)
    {
        var draft = Draft(requested);
        using var saved = Composer.BuildForDraft(draft, Account());
        using var transported = Composer.FromBytes(Composer.ToBytes(saved));
        var restored = Composer.FromMime(transported, draft.AccountId, draft.ThreadKey);
        Assert.Equal(requested, restored.RequestDeliveryReceipt);
        Assert.False(restored.RequestReadReceipt);
        using var outgoing = Composer.Build(restored, Account());
        Assert.Null(outgoing.Headers[DeliveryReceipts.DraftHeader]);
        Assert.Null(outgoing.Headers["Disposition-Notification-To"]);
        Assert.Equal(saved.TextBody, outgoing.TextBody);
        Assert.Equal(saved.To.ToString(), outgoing.To.ToString());
    }

    [Fact]
    public void Read_and_delivery_receipts_are_independent_and_unknown_draft_values_are_not_requests()
    {
        var draft = Draft(false);
        draft.RequestReadReceipt = true;
        using var saved = Composer.BuildForDraft(draft, Account());
        saved.Headers[DeliveryReceipts.DraftHeader] = "unsupported-command";
        var restored = Composer.FromMime(saved, draft.AccountId, draft.ThreadKey);
        Assert.True(restored.RequestReadReceipt);
        Assert.False(restored.RequestDeliveryReceipt);
        restored.RequestDeliveryReceipt = true;
        using var outgoing = Composer.Build(restored, Account());
        Assert.NotNull(outgoing.Headers["Disposition-Notification-To"]);
        Assert.Null(outgoing.Headers[DeliveryReceipts.DraftHeader]);
    }

    [Fact]
    public void Local_drafts_history_scheduling_restart_and_undo_preserve_the_request()
    {
        using var dir = new TempDir();
        long queued;
        using (var engine = Engine(dir))
        {
            engine.Settings.Save(notify: false);
            var draft = Draft();
            var localId = engine.SaveLocalDraft(draft, pendingUpload: true);
            var reopened = Assert.IsType<Draft>(engine.OpenLocalDraft(localId));
            Assert.True(reopened.RequestDeliveryReceipt);
            var revision = Assert.Single(engine.Store.GetDraftRevisions(draft.AccountId, draft.MessageId));
            draft.RequestDeliveryReceipt = false;
            engine.SaveLocalDraft(draft, pendingUpload: true);
            var history = Assert.IsType<Draft>(engine.Store.OpenDraftRevision(draft.AccountId, draft.MessageId, revision.Id));
            Assert.True(history.RequestDeliveryReceipt);
            queued = engine.QueueSend(history, DateTimeOffset.Now.AddDays(2), null);
            var item = Assert.Single(engine.Outbox());
            Assert.True(item.RequestDeliveryReceipt);
            Assert.Equal(DeliveryReceiptState.Pending, item.DeliveryReceipt!.State);
            using var outgoing = Composer.FromBytes(item.Mime);
            Assert.Null(outgoing.Headers[DeliveryReceipts.DraftHeader]);
            engine.Store.RescheduleOutbox(queued, DateTimeOffset.Now.AddDays(3));
        }
        using var restarted = Engine(dir);
        var pending = Assert.Single(restarted.OutboxSummary());
        Assert.True(pending.RequestDeliveryReceipt);
        Assert.Empty(pending.Mime);
        var undone = Assert.IsType<Draft>(restarted.Recall(queued));
        Assert.True(undone.RequestDeliveryReceipt);
        Assert.Empty(restarted.Outbox());
        undone.RequestDeliveryReceipt = false;
        restarted.QueueSend(undone, DateTimeOffset.Now.AddDays(1), null);
        Assert.False(Assert.Single(restarted.Outbox()).RequestDeliveryReceipt);
    }

    [Theory]
    [InlineData(DeliveryReceiptState.Requested)]
    [InlineData(DeliveryReceiptState.Unavailable)]
    public async Task Actual_server_outcome_is_persisted_for_sent_mail_and_events_without_claiming_delivery(DeliveryReceiptState state)
    {
        using var dir = new TempDir();
        using (var engine = Engine(dir, (_, message, _) =>
        {
            Assert.Null(message.Headers[DeliveryReceipts.DraftHeader]);
            return Task.FromResult(new DeliveryReceiptResult(state, "2.0.0 queued as fixture-123"));
        }))
        {
            engine.Settings.Save(notify: false);
            OutboxItem? reported = null;
            engine.Sent += item => reported = item;
            var queued = engine.QueueSend(Draft(), DateTimeOffset.Now.AddSeconds(-1), null);
            await engine.ProcessOutboxAsync(CancellationToken.None);
            Assert.NotNull(reported);
            Assert.Equal(OutboxStatus.Sent, reported.Status);
            Assert.Equal(state, reported.DeliveryReceipt!.State);
            Assert.Contains("queued as fixture-123", reported.DeliveryReceipt.Detail);
            Assert.DoesNotContain("Delivered", reported.DeliveryReceipt.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(engine.Outbox());
            Assert.Equal(queued, Assert.Single(engine.Store.GetOutbox(includeDone: true)).Id);
        }
        using var restarted = Engine(dir);
        var saved = Assert.Single(restarted.Store.DeliveryReceiptsForThread("sender", "receipt-thread"));
        Assert.Equal("receipt@example.test", saved.Key);
        Assert.Equal(state, saved.Value.State);
        Assert.Equal("2.0.0 queued as fixture-123", saved.Value.ServerResponse);
        Assert.Empty(restarted.Store.DeliveryReceiptsForThread("other-account", "receipt-thread"));
        Assert.Empty(restarted.Store.DeliveryReceiptsForThread("sender", "other-thread"));
    }

    [Fact]
    public async Task Sent_result_follows_the_message_when_Gmail_assigns_its_server_thread_key()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir, (_, _, _) => Task.FromResult(
            new DeliveryReceiptResult(DeliveryReceiptState.Unavailable, "2.0.0 accepted without DSN")));
        var draft = Draft();
        draft.ThreadKey = "";
        engine.QueueSend(draft, DateTimeOffset.Now.AddSeconds(-1), null);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        var sent = engine.Store.UpsertFolder(new MailFolder
        {
            AccountId = "sender", Path = "Sent", Name = "Sent", Role = FolderRole.Sent,
        });
        engine.Store.InsertMessages(new[]
        {
            Rows.Make("sender", sent, "gm:server-thread", from: Account().Email, messageId: draft.MessageId),
            Rows.Make("sender", sent, "gm:unrelated-thread", from: Account().Email, messageId: "unrelated@example.test"),
        });
        var result = Assert.Single(engine.Store.DeliveryReceiptsForThread("sender", "gm:server-thread"));
        Assert.Equal(draft.MessageId, result.Key);
        Assert.Equal(DeliveryReceiptState.Unavailable, result.Value.State);
        Assert.Empty(engine.Store.DeliveryReceiptsForThread("sender", "gm:unrelated-thread"));
        Assert.Empty(engine.Store.DeliveryReceiptsForThread("other-account", "gm:server-thread"));
    }

    [Fact]
    public async Task Rejected_send_keeps_request_and_failure_state_and_a_retry_records_only_its_real_result()
    {
        using var dir = new TempDir();
        var attempts = 0;
        using var engine = Engine(dir, (_, _, _) =>
        {
            if (++attempts == 1) throw new IOException("SMTP refused the request");
            return Task.FromResult(new DeliveryReceiptResult(DeliveryReceiptState.Requested, "2.0.0 accepted on retry"));
        });
        var queued = engine.QueueSend(Draft(), DateTimeOffset.Now.AddSeconds(-1), null);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        var failed = Assert.Single(engine.Outbox());
        Assert.Equal(OutboxStatus.Failed, failed.Status);
        Assert.True(failed.RequestDeliveryReceipt);
        Assert.Equal(DeliveryReceiptState.Pending, failed.DeliveryReceipt!.State);
        Assert.Contains("SMTP refused", failed.LastError);
        Assert.Empty(engine.Store.DeliveryReceiptsForThread("sender", "receipt-thread"));
        engine.Store.RescheduleOutbox(queued, DateTimeOffset.Now.AddSeconds(-1));
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.Equal(DeliveryReceiptState.Requested, Assert.Single(engine.Store.DeliveryReceiptsForThread("sender", "receipt-thread")).Value.State);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Cancelling_a_queued_request_never_contacts_SMTP_or_records_a_result()
    {
        using var dir = new TempDir();
        using var engine = Engine(dir, (_, _, _) => throw new InvalidOperationException("Cancelled messages must not send"));
        var id = engine.QueueSend(Draft(), DateTimeOffset.Now.AddDays(1), null);
        Assert.True(engine.Recall(id)!.RequestDeliveryReceipt);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Empty(engine.Store.DeliveryReceiptsForThread("sender", "receipt-thread"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Real_SMTP_negotiation_requests_success_and_failure_only_when_advertised(bool supportsDsn)
    {
        await using var server = new SmtpFixture(supportsDsn);
        using var smtp = new DeliveryReceiptSmtpClient();
        await smtp.ConnectAsync(IPAddress.Loopback.ToString(), server.Port, SecureSocketOptions.None, server.Token);
        using var message = Composer.BuildForDraft(Draft(), Account());
        var result = await smtp.SendWithReceiptAsync(message, server.Token);
        await smtp.DisconnectAsync(true, server.Token);
        await server.Finished;
        Assert.Equal(supportsDsn ? DeliveryReceiptState.Requested : DeliveryReceiptState.Unavailable, result.State);
        Assert.Equal("2.0.0 accepted fixture", result.ServerResponse);
        var recipients = server.Commands.Where(c => c.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(3, recipients.Length);
        Assert.All(recipients, command => Assert.Equal(supportsDsn, command.Contains("NOTIFY=SUCCESS,FAILURE", StringComparison.Ordinal)));
        var mail = Assert.Single(server.Commands, c => c.StartsWith("MAIL FROM:", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(supportsDsn, mail.Contains("RET=HDRS", StringComparison.Ordinal));
        Assert.Equal(supportsDsn, mail.Contains("ENVID=magpie-", StringComparison.Ordinal));
        Assert.DoesNotContain(DeliveryReceipts.DraftHeader, Assert.Single(server.Messages), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Disposition-Notification-To", server.Messages.Single(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bcc:", server.Messages.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Advertised_DSN_rejection_is_an_error_and_is_not_silently_retried_without_notifications()
    {
        await using var server = new SmtpFixture(true, rejectFirstRecipient: true);
        using var smtp = new DeliveryReceiptSmtpClient();
        await smtp.ConnectAsync(IPAddress.Loopback.ToString(), server.Port, SecureSocketOptions.None, server.Token);
        using var message = Composer.Build(Draft(), Account());
        await Assert.ThrowsAsync<SmtpCommandException>(() => smtp.SendWithReceiptAsync(message, server.Token));
        Assert.Empty(server.Messages);
        var result = await smtp.SendWithReceiptAsync(message, server.Token);
        Assert.Equal(DeliveryReceiptState.Requested, result.State);
        await smtp.DisconnectAsync(true, server.Token);
        await server.Finished;
        Assert.All(server.Commands.Where(c => c.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase)),
            command => Assert.Contains("NOTIFY=SUCCESS,FAILURE", command));
        Assert.Single(server.Messages);
    }

    [Fact]
    public void Receipt_strings_are_bounded_and_safe_and_envelope_ids_meet_SMTP_limits()
    {
        Assert.Equal("2.0 accepted<script>", DeliveryReceipts.SafeServerResponse("\r\n2.0 accepted<script>\0"));
        Assert.Equal(512, DeliveryReceipts.SafeServerResponse(new string('x', 1000)).Length);
        using var message = Composer.Build(Draft(), Account());
        var envelope = DeliveryReceipts.EnvelopeId(message);
        Assert.InRange(envelope.Length, 1, 100);
        Assert.All(envelope, c => Assert.InRange((int)c, 33, 126));
        Assert.Equal(envelope, DeliveryReceipts.EnvelopeId(message));
        message.MessageId = "another@example.test";
        Assert.NotEqual(envelope, DeliveryReceipts.EnvelopeId(message));
    }

    /// <summary>A loopback-only SMTP peer; records the actual wire commands produced by MailKit.</summary>
    private sealed class SmtpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(15));
        private readonly bool _supportsDsn;
        private bool _rejectFirstRecipient;
        public List<string> Commands { get; } = new();
        public List<string> Messages { get; } = new();
        public int Port { get; }
        public CancellationToken Token => _timeout.Token;
        public Task Finished { get; }

        public SmtpFixture(bool supportsDsn, bool rejectFirstRecipient = false)
        {
            _supportsDsn = supportsDsn;
            _rejectFirstRecipient = rejectFirstRecipient;
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Finished = RunAsync();
        }

        private async Task RunAsync()
        {
            using var client = await _listener.AcceptTcpClientAsync(Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 fixture.example.test ready");
            while (await reader.ReadLineAsync(Token) is { } line)
            {
                Commands.Add(line);
                if (line.StartsWith("EHLO ", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("250-fixture.example.test");
                    if (_supportsDsn) await writer.WriteLineAsync("250-DSN");
                    await writer.WriteLineAsync("250 SIZE 1000000");
                }
                else if (line.StartsWith("RCPT TO:", StringComparison.OrdinalIgnoreCase) && _rejectFirstRecipient)
                {
                    _rejectFirstRecipient = false;
                    await writer.WriteLineAsync("555 5.5.4 notification option rejected");
                }
                else if (line.Equals("DATA", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("354 begin message");
                    var data = new StringBuilder();
                    while (await reader.ReadLineAsync(Token) is { } body && body != ".") data.AppendLine(body);
                    Messages.Add(data.ToString());
                    await writer.WriteLineAsync("250 2.0.0 accepted fixture");
                }
                else if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("221 bye");
                    return;
                }
                else await writer.WriteLineAsync("250 2.0.0 ok");
            }
        }

        public async ValueTask DisposeAsync()
        {
            _timeout.Cancel();
            _listener.Stop();
            try { await Finished; }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
            _timeout.Dispose();
        }
    }
}
