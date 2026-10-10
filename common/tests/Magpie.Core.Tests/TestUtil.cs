using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

/// <summary>Reversible stand-in for DPAPI (tests run on Linux).</summary>
public sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).Reverse().ToArray();
    public byte[] Unprotect(byte[] cipher) => Enumerable.Reverse(cipher).Select(b => (byte)(b ^ 0x5A)).ToArray();
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "magpie-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() { Directory.CreateDirectory(Path); }
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Path, true); } catch { }
    }
}

public static class Rows
{
    private static long _uid = 100;
    public static MessageRow Make(string account, long folder, string thread, string from = "anita@x.com", string subject = "Hello",
        DateTimeOffset? date = null, MessageFlags flags = MessageFlags.None, Category cat = Category.People, string messageId = "", string preview = "hi")
    {
        var d = date ?? DateTimeOffset.Now;
        return new MessageRow
        {
            AccountId = account, FolderId = folder, Uid = Interlocked.Increment(ref _uid), ThreadKey = thread,
            FromAddress = from, FromName = from.Split('@')[0], Subject = subject, Date = d, SortDate = d, Flags = flags,
            Category = cat, MessageId = messageId.Length > 0 ? messageId : Guid.NewGuid().ToString("N") + "@x.com", Preview = preview,
            To = "me@test.local",
        };
    }

    public static (MailStore store, long inbox, long sent) NewStore(TempDir dir, string account = "A")
    {
        var s = new MailStore(dir.File("mail.db"));
        var inbox = s.UpsertFolder(new MailFolder { AccountId = account, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        var sent = s.UpsertFolder(new MailFolder { AccountId = account, Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        return (s, inbox, sent);
    }
}
