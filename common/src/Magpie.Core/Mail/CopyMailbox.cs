using MailKit;
using MailKit.Net.Imap;
using Magpie.Core.Settings;
using MimeKit;
using KitFlags = MailKit.MessageFlags;
using KitSearch = MailKit.Search.SearchQuery;

namespace Magpie.Core.Mail;

/// <summary>
/// Design SY1 (choice A): the accounts copy lives as one email in a folder "Magpie Sync" at the top of the home
/// account's mailbox (Gmail shows it as a label with one email). The email carries the header
/// <see cref="AccountsCopy.Header"/> and the locked file as its attachment. Writing appends the new email, then removes
/// the older ones; on Gmail they are moved to Trash and deleted there, since removing a label alone keeps the email in
/// All Mail. Magpie leaves the folder and the email out of its own lists (AccountSync).
/// </summary>
public static class CopyMailbox
{
    /// <summary>The copy folder if it exists (any case of the name, top level of the personal namespace).</summary>
    public static async Task<IMailFolder?> FindFolderAsync(ImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var top = await client.GetFolderAsync(ns.Path, ct);
        foreach (var f in await top.GetSubfoldersAsync(false, ct))
            if (f.Name.Equals(AccountsCopy.FolderName, StringComparison.OrdinalIgnoreCase) && !f.Attributes.HasFlag(FolderAttributes.NonExistent))
                return f;
        return null;
    }

    private static async Task<IMailFolder> EnsureFolderAsync(ImapClient client, CancellationToken ct)
    {
        if (await FindFolderAsync(client, ct) is { } found) return found;
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var top = await client.GetFolderAsync(ns.Path, ct);
        try { return await top.CreateAsync(AccountsCopy.FolderName, true, ct) ?? throw new ImapCommandException(ImapCommandResponse.No, "create failed"); }
        catch (ImapCommandException)
        {
            // Made by someone else meanwhile, or the server wants the full name.
            return await FindFolderAsync(client, ct)
                ?? await client.GetFolderAsync(string.IsNullOrEmpty(ns.Path) ? AccountsCopy.FolderName : ns.Path + ns.DirectorySeparator + AccountsCopy.FolderName, ct);
        }
    }

    /// <summary>The email that holds the copy (also readable in a mail client: it says what it is).</summary>
    public static MimeMessage BuildMessage(string address, byte[] file, AccountsCopy.Summary summary)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Magpie", address));
        msg.To.Add(new MailboxAddress("", address));
        msg.Subject = "Magpie accounts copy: please don't delete";
        msg.Date = summary.Updated == default ? DateTimeOffset.Now : summary.Updated;
        msg.Headers.Add(AccountsCopy.Header, "1");
        var body = new TextPart("plain")
        {
            Text = "Magpie keeps a locked copy of your accounts here, so Magpie on another PC can add them all after one sign-in.\r\n"
                 + $"Last written by Magpie on {summary.Pc}; {summary.Accounts} account(s).\r\n\r\n"
                 + "It is locked with your sync password. To stop it, use Magpie: Settings > Accounts > Bring my accounts to other PCs > Stop and delete the copy.\r\n",
        };
        var attachment = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(file)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = AccountsCopy.FileName,
        };
        msg.Body = new Multipart("mixed") { body, attachment };
        return msg;
    }

    /// <summary>Writes the copy: the new email first, then the older ones go (never without a copy in between).</summary>
    public static async Task WriteAsync(ImapClient client, string address, byte[] file, CancellationToken ct)
    {
        var folder = await EnsureFolderAsync(client, ct);
        var summary = AccountsCopy.Peek(file) ?? new AccountsCopy.Summary("", 0, DateTimeOffset.Now);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var before = await folder.SearchAsync(KitSearch.HeaderContains(AccountsCopy.Header, ""), ct);
        var appended = await folder.AppendAsync(new AppendRequest(BuildMessage(address, file, summary), KitFlags.Seen), ct);
        var old = before.Where(u => appended == null || u != appended.Value).ToList();
        if (old.Count > 0) await RemoveAsync(client, folder, old, ct);
        try { await folder.CloseAsync(false, ct); } catch { }
    }

    /// <summary>The newest copy's file, or null when the mailbox has none.</summary>
    public static async Task<byte[]?> ReadAsync(ImapClient client, CancellationToken ct)
    {
        var folder = await FindFolderAsync(client, ct);
        if (folder == null) return null;
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            var uids = await folder.SearchAsync(KitSearch.HeaderContains(AccountsCopy.Header, ""), ct);
            foreach (var uid in uids.OrderByDescending(u => u.Id))
            {
                var msg = await folder.GetMessageAsync(uid, ct);
                if (msg.Attachments.OfType<MimePart>().FirstOrDefault(p => p.FileName == AccountsCopy.FileName) is not { Content: { } content }) continue;
                using var ms = new MemoryStream();
                await content.DecodeToAsync(ms, ct);
                return ms.ToArray();
            }
            return null;
        }
        finally { try { await folder.CloseAsync(false, ct); } catch { } }
    }

    /// <summary>"Stop and delete the copy": every copy email goes, then the folder.</summary>
    public static async Task DeleteAsync(ImapClient client, CancellationToken ct)
    {
        var folder = await FindFolderAsync(client, ct);
        if (folder == null) return;
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var all = await folder.SearchAsync(KitSearch.All, ct);
        if (all.Count > 0) await RemoveAsync(client, folder, all.ToList(), ct);
        try { await folder.CloseAsync(false, ct); } catch { }
        try { await folder.DeleteAsync(ct); } catch (ImapCommandException) { }   // emptied is enough when the server keeps it
    }

    private static async Task RemoveAsync(ImapClient client, IMailFolder folder, IList<UniqueId> uids, CancellationToken ct)
    {
        if (client.Capabilities.HasFlag(ImapCapabilities.GMailExt1) && client.GetFolder(SpecialFolder.Trash) is { } trash)
        {
            // Gmail: a label removed from an email leaves it in All Mail. Through Trash, then deleted there for good.
            await folder.MoveToAsync(uids, trash, ct);
            await trash.OpenAsync(FolderAccess.ReadWrite, ct);
            try
            {
                var inTrash = await trash.SearchAsync(KitSearch.HeaderContains(AccountsCopy.Header, ""), ct);
                if (inTrash.Count > 0)
                {
                    await trash.StoreAsync(inTrash, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
                    if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await trash.ExpungeAsync(inTrash, ct);
                }
            }
            finally { try { await trash.CloseAsync(false, ct); } catch { } }
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            return;
        }
        await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
        // The folder holds only Magpie's copies, so expunging all of it is safe without UIDPLUS.
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await folder.ExpungeAsync(uids, ct);
        else await folder.ExpungeAsync(ct);
    }
}
