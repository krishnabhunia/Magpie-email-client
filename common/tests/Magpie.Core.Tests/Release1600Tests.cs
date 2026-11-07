using Magpie.Core.CloudFiles;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using MimeKit;

namespace Magpie.Core.Tests;

/// <summary>The remaining compose features must preserve one another's choices across real MIME and outbox boundaries.</summary>
public sealed class Release1600Tests(SecureMailFixtures keys) : IClassFixture<SecureMailFixtures>
{
    private static Account SendingAccount() => new()
    {
        Id = "A", Email = "login@example.test", Enabled = false,
        SenderAliases = new() { new() { Email = SecureMailFixtures.Sender, Name = "Project identity" } },
    };

    private void ImportKeys(MailEngine engine, MailSecurityFormat format)
    {
        engine.SecureMail.Keys.Import("A", SecureMailFixtures.Sender, format,
            keys.Key(format, SecureMailFixtures.Sender).Private, SecureMailFixtures.Password, true);
        var recipient = engine.SecureMail.Keys.Import("A", SecureMailFixtures.Recipient, format,
            keys.Key(format, SecureMailFixtures.Recipient).Public, "", false);
        engine.SecureMail.Keys.Select("A", recipient.Id, recipient.Fingerprint);
    }

    private static Draft ProtectedDraft(MailSecurityFormat format, string html) => new()
    {
        AccountId = "A", SenderAddress = SecureMailFixtures.Sender, To = SecureMailFixtures.Recipient,
        Subject = "Integrated compose", Html = html, RequestDeliveryReceipt = true,
        Security = new() { Format = format, Sign = true, Encrypt = true },
    };

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public async Task Alias_security_and_delivery_receipt_survive_draft_revision_undo_and_actual_send_path(MailSecurityFormat format)
    {
        using var dir = new TempDir();
        var sent = 0;
        MailEngine? current = null;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(),
            sendMessage: (_, _, _) => throw new InvalidOperationException("A requested receipt must use the receipt-aware transport."),
            sendWithDeliveryReceipt: (account, message, _) =>
            {
                sent++;
                Assert.Equal("login@example.test", account.Email);
                Assert.Equal(SecureMailFixtures.Sender, Assert.Single(message.From.Mailboxes).Address);
                Assert.True(SecureMailService.IsProtected(message.Body));
                Assert.Null(message.Headers[SecureMailOptions.DraftHeader]);
                Assert.Null(message.Headers[DeliveryReceipts.DraftHeader]);
                using var opened = current!.SecureMail.Open(message, "A");
                Assert.Contains("Protected project content", Assert.IsType<string>(opened.Message.HtmlBody));
                Assert.True(Assert.Single(opened.Signatures).Valid);
                return Task.FromResult(new DeliveryReceiptResult(DeliveryReceiptState.Requested, "250 fixture accepted"));
            });
        current = engine;
        engine.Config.Accounts.Add(SendingAccount());
        ImportKeys(engine, format);
        var draft = ProtectedDraft(format, "<p>Protected project content</p>");
        var localId = engine.SaveLocalDraft(draft, pendingUpload: true);
        var local = Assert.IsType<Draft>(engine.OpenLocalDraft(localId));
        void Choices(Draft value)
        {
            Assert.Equal("A", value.AccountId);
            Assert.Equal(SecureMailFixtures.Sender, value.SenderAddress);
            Assert.True(value.RequestDeliveryReceipt);
            Assert.True(value.Security.Sign && value.Security.Encrypt);
            Assert.Equal(format, value.Security.Format);
        }
        Choices(local);
        var revision = Assert.Single(engine.Store.GetDraftRevisions("A", draft.MessageId));
        Choices(Assert.IsType<Draft>(engine.Store.OpenDraftRevision("A", draft.MessageId, revision.Id)));
        var firstId = engine.QueueSend(local, DateTimeOffset.UtcNow.AddHours(1), null);
        var recalled = Assert.IsType<Draft>(engine.Recall(firstId));
        Choices(recalled);
        var secondId = engine.QueueSend(recalled, DateTimeOffset.UtcNow.AddSeconds(-1), null);
        var queued = engine.Store.GetOutbox().Single(o => o.Id == secondId);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(1, sent);
        var receipt = Assert.Single(engine.Store.DeliveryReceiptsForThread("A", queued.ThreadKey)).Value;
        Assert.Equal(DeliveryReceiptState.Requested, receipt.State);
        Assert.Equal("250 fixture accepted", receipt.ServerResponse);
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public async Task Encrypted_cloud_link_is_rechecked_from_protected_recovery_content_before_smtp(MailSecurityFormat format)
    {
        using var dir = new TempDir();
        var sent = 0;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(),
            sendWithDeliveryReceipt: (_, _, _) =>
            {
                sent++;
                return Task.FromResult(new DeliveryReceiptResult(DeliveryReceiptState.Requested, "250 should not happen"));
            });
        engine.Config.Accounts.Add(SendingAccount());
        var cloud = new Account { Id = "cloud", Email = "owner@example.test", Kind = AccountKind.Gmail, Enabled = false };
        engine.Config.Accounts.Add(cloud);
        ImportKeys(engine, format);
        var link = new CloudUploadedFile
        {
            AccountId = cloud.Id, AccountEmail = cloud.Email, Provider = cloud.Kind,
            FileId = "private-project", FileName = "project.pdf", WebUrl = "https://drive.google.com/file/d/private-project/view",
            Recipients = new[] { SecureMailFixtures.Recipient }, PermissionIds = new() { [SecureMailFixtures.Recipient] = "named-grant" },
        };
        engine.Store.RememberCloudFile(link);
        var draft = ProtectedDraft(format, "<p>Private link <a href=\"" + link.WebUrl + "\">Project</a></p>");
        var id = engine.QueueSend(draft, DateTimeOffset.UtcNow.AddSeconds(-1), null);
        using var wire = Composer.FromBytes(engine.Store.GetOutbox().Single(o => o.Id == id).Mime);
        Assert.Null(wire.HtmlBody);
        Assert.Null(wire.TextBody);
        // The permissions can no longer be checked after account removal. An encrypted MIME body must not hide the link from that gate.
        engine.Config.Accounts.Remove(cloud);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(0, sent);
        var failed = engine.Store.GetOutbox().Single(o => o.Id == id);
        Assert.Equal(OutboxStatus.Failed, failed.Status);
        Assert.Contains("account used to share", failed.LastError, StringComparison.OrdinalIgnoreCase);
        var recalled = Assert.IsType<Draft>(engine.Recall(id));
        Assert.Contains(link.WebUrl, recalled.Html);
        Assert.True(recalled.Security.Encrypt && recalled.RequestDeliveryReceipt);
        Assert.Equal(SecureMailFixtures.Sender, recalled.SenderAddress);
    }
}
