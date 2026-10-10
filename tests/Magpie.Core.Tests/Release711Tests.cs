using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>7.1.1: fixes from Krishna's magpie.log (queue #5, issue #1).</summary>
public class Release711Tests
{
    [Fact]
    public void Certificate_is_accepted_when_only_the_revocation_server_is_unreachable()
    {
        Assert.True(TlsCheck.Accept(SslPolicyErrors.None, Array.Empty<X509ChainStatusFlags>()));
        Assert.True(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateChainErrors,
            new[] { X509ChainStatusFlags.RevocationStatusUnknown, X509ChainStatusFlags.OfflineRevocation, X509ChainStatusFlags.NoError }));
        // Anything else still refuses the connection.
        Assert.False(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateChainErrors, new[] { X509ChainStatusFlags.Revoked }));
        Assert.False(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateChainErrors, new[] { X509ChainStatusFlags.UntrustedRoot, X509ChainStatusFlags.OfflineRevocation }));
        Assert.False(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateChainErrors, new[] { X509ChainStatusFlags.NotTimeValid }));
        Assert.False(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateNameMismatch, Array.Empty<X509ChainStatusFlags>()));
        Assert.False(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch,
            new[] { X509ChainStatusFlags.OfflineRevocation }));
        Assert.False(TlsCheck.Accept(SslPolicyErrors.RemoteCertificateChainErrors, Array.Empty<X509ChainStatusFlags>()));
    }

    [Fact]
    public void Log_lines_name_the_error_even_without_a_message()
    {
        Assert.Equal("FileNotFoundException", AccountSync.Describe(new FileNotFoundException("")));
        Assert.Equal("IOException: No such host is known.", AccountSync.Describe(new IOException("No such host is known.")));
        var wrapped = AccountSync.Describe(new InvalidOperationException("Load failed\r\nline two", new FormatException("bad header")));
        Assert.Equal("InvalidOperationException: Load failed  line two ← FormatException: bad header", wrapped);
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
        // Simulate a mail.db from 7.1.0: same schema number, no message_id column.
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
}
