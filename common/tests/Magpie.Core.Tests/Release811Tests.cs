using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>8.1.1: fixes from Krishna's magpie.log of 29 Sep – 10 Oct (queue #5, issue #1).</summary>
public class Release811Tests
{
    [Fact]
    public void Certificate_is_accepted_when_only_the_revocation_server_is_unreachable()
    {
        Assert.True(Connector.AcceptCertificate(SslPolicyErrors.None, Array.Empty<X509ChainStatusFlags>()));
        Assert.True(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateChainErrors,
            new[] { X509ChainStatusFlags.RevocationStatusUnknown, X509ChainStatusFlags.OfflineRevocation, X509ChainStatusFlags.NoError }));
        // Anything else still refuses the connection.
        Assert.False(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateChainErrors, new[] { X509ChainStatusFlags.Revoked }));
        Assert.False(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateChainErrors, new[] { X509ChainStatusFlags.UntrustedRoot, X509ChainStatusFlags.OfflineRevocation }));
        Assert.False(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateChainErrors, new[] { X509ChainStatusFlags.NotTimeValid }));
        Assert.False(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateNameMismatch, Array.Empty<X509ChainStatusFlags>()));
        Assert.False(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            new[] { X509ChainStatusFlags.OfflineRevocation }));
        Assert.False(Connector.AcceptCertificate(SslPolicyErrors.RemoteCertificateChainErrors, Array.Empty<X509ChainStatusFlags>()));
    }

    [Fact]
    public void Log_lines_name_the_error_even_without_a_message()
    {
        Assert.Equal("FileNotFoundException", AccountSync.Describe(new FileNotFoundException("")));
        Assert.Equal("IOException: No such host is known.", AccountSync.Describe(new IOException("No such host is known.")));
        var wrapped = AccountSync.Describe(new InvalidOperationException("Load failed\r\nline two", new FormatException("bad header")));
        Assert.Equal("InvalidOperationException: Load failed  line two ← FormatException: bad header", wrapped);
        // An inner exception without a message still shows its type; one whose message is already in the text isn't repeated.
        Assert.Equal("IOException: read failed ← FileNotFoundException", AccountSync.Describe(new IOException("read failed", new FileNotFoundException(""))));
        Assert.Equal("IOException: boom", AccountSync.Describe(new IOException("boom", new FormatException("boom"))));
    }

    [Fact]
    public void Queued_changes_keep_the_Message_ID()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        store.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 7, Kind = PendingOpKind.Move, Arg = 3, MessageId = "abc@x.com" });
        var op = Assert.Single(store.GetPendingOps("A"));
        Assert.Equal(("abc@x.com", 7L, PendingOpKind.Move), (op.MessageId, op.Uid, op.Kind));
    }

    [Fact]
    public void Engine_queues_moves_with_the_Message_ID()
    {
        using var dir = new TempDir();
        using var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        var inbox = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        var row = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", messageId: "keep-me@x.com") }).Single();
        e.TrashEmails(new[] { row });
        Assert.Contains(e.Store.GetPendingOps("A"), o => o.Kind == PendingOpKind.Move && o.MessageId == "keep-me@x.com");
    }

    [Fact]
    public void An_existing_mail_db_gets_the_new_column_on_start()
    {
        using var dir = new TempDir();
        var path = dir.File("mail.db");
        var (first, inbox, _) = Rows.NewStore(dir);
        first.AddPendingOp(new PendingOp { AccountId = "A", FolderId = inbox, Uid = 9, Kind = PendingOpKind.SetSeen });
        // Simulate a mail.db from 8.1.0: same schema number, no message_id column.
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "ALTER TABLE pending_ops DROP COLUMN message_id";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        var reopened = new MailStore(path);
        var op = Assert.Single(reopened.GetPendingOps("A"));
        Assert.Equal(("", 9L), (op.MessageId, op.Uid));
    }

    [Fact]
    public void Search_by_Message_ID_takes_only_the_exact_match()
    {
        static MailKit.IMessageSummary Summary(uint uid, string? mid) =>
            new MailKit.MessageSummary(0) { UniqueId = new MailKit.UniqueId(7, uid), Envelope = new MailKit.Envelope { MessageId = mid } };
        // The server's substring search also found "<prefix-abc@example.com>" (listed first).
        var hits = new[] { Summary(10, "<prefix-abc@example.com>"), Summary(11, null), Summary(12, "<abc@example.com>") };
        Assert.Equal(12u, AccountSync.ExactMessageId(hits, "abc@example.com")?.Id);
        Assert.Equal(12u, AccountSync.ExactMessageId(hits, "<abc@example.com>")?.Id);
        Assert.Null(AccountSync.ExactMessageId(hits.Take(2), "abc@example.com"));
        Assert.Null(AccountSync.ExactMessageId(hits, ""));
    }
}
