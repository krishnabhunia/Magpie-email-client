using MailKit;
using MailKit.Net.Imap;
using Magpie.Core.Settings;
using MimeKit;
using KitFlags = MailKit.MessageFlags;
using KitSearch = MailKit.Search.SearchQuery;

namespace Magpie.Core.Mail;

/// <summary>
/// Design SY1 (choice A): the accounts copy lives as one email in a folder "Magpie Sync" at the top of the home
/// account's mailbox (Gmail shows it as a label with one email; servers that keep every folder under INBOX get
/// "INBOX.Magpie Sync"). The email carries the header <see cref="AccountsCopy.Header"/> and the locked file as its
/// attachment. The folder holds only Magpie's copies, so they are found by reading the headers of every email in it
/// (no header search: Gmail and Exchange don't all do that). Writing appends the new email, then removes the ones that
/// were there before; on Gmail they go through Trash and are deleted there, since a label removed alone keeps the email
/// in All Mail. Magpie leaves the folder and the email out of its own lists (AccountSync).
/// </summary>
public static class CopyMailbox
{
    private static IMailFolder Top(ImapClient client) =>
        client.GetFolder(client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', ""));

    /// <summary>The copy folder if it exists (any case of the name, at the top of the personal namespace).</summary>
    public static async Task<IMailFolder?> FindFolderAsync(ImapClient client, CancellationToken ct)
    {
        foreach (var f in await Top(client).GetSubfoldersAsync(false, ct))
            if (f.Name.Equals(AccountsCopy.FolderName, StringComparison.OrdinalIgnoreCase) && !f.Attributes.HasFlag(FolderAttributes.NonExistent)
                && !f.Attributes.HasFlag(FolderAttributes.NoSelect))
                return f;
        return null;
    }

    private static async Task<IMailFolder> EnsureFolderAsync(ImapClient client, CancellationToken ct)
    {
        if (await FindFolderAsync(client, ct) is { } found) return found;
        try { return await Top(client).CreateAsync(AccountsCopy.FolderName, true, ct) ?? throw new ImapCommandException(ImapCommandResponse.No, "create failed"); }
        catch (ImapCommandException)
        {
            // Made by another PC meanwhile.
            return await FindFolderAsync(client, ct) ?? throw new InvalidOperationException("The \"Magpie Sync\" folder couldn't be made in this mailbox.");
        }
    }

    /// <summary>The email that holds the copy (also readable in a mail client: it says what it is).</summary>
    public static MimeMessage BuildMessage(string address, byte[] file, AccountsCopy.Summary summary)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress("Magpie", address));
        msg.To.Add(new MailboxAddress("", address));
        msg.Subject = AccountsCopy.Subject;
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

    /// <summary>One copy email: its UID, size, and on Gmail its message id (the same in every label and All Mail).</summary>
    private sealed record CopyEmail(UniqueId Uid, uint Size, ulong? GmailId, DateTimeOffset? Date);

    /// <summary>The copy emails in an open folder (by their header), oldest first.</summary>
    private static async Task<List<CopyEmail>> CopiesAsync(ImapClient client, IMailFolder folder, CancellationToken ct)
    {
        if (folder.Count == 0) return new();
        var what = MessageSummaryItems.UniqueId | MessageSummaryItems.Size | MessageSummaryItems.Envelope;
        if (client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)) what |= MessageSummaryItems.GMailMessageId;
        var items = await folder.FetchAsync(0, -1, new FetchRequest(what) { Headers = new HeaderSet(new[] { AccountsCopy.Header }) }, ct);
        return items.Where(s => s.Headers?.Contains(AccountsCopy.Header) == true)
            .Select(s => new CopyEmail(s.UniqueId, s.Size ?? 0, s.GMailMessageId, s.Envelope?.Date)).OrderBy(c => c.Uid.Id).ToList();
    }

    /// <summary>A copy email bigger than this isn't downloaded (a real copy is at most <see cref="AccountsCopy.MaxBytes"/>,
    /// plus the base64 of the attachment).</summary>
    private const long MaxEmailBytes = AccountsCopy.MaxBytes * 2L;

    /// <summary>The newest copy in the mailbox isn't the one the round read (another PC wrote meanwhile): nothing was written.</summary>
    public sealed class ChangedMeanwhileException : Exception
    {
        public ChangedMeanwhileException() : base("The copy changed on another PC meanwhile.") { }
    }

    /// <summary>Writes the copy: the new email first, then the ones that were there before go (never without a copy).
    /// <paramref name="appended"/> runs as soon as the new email is in the mailbox (before the old ones go).
    /// <paramref name="stillSame"/> (given the newest copy's file there now, or null) says whether it is still the one
    /// the round read; when not, nothing is written (<see cref="ChangedMeanwhileException"/>).</summary>
    public static async Task WriteAsync(ImapClient client, string address, byte[] file, CancellationToken ct, Action? appended = null, Func<byte[]?, bool>? stillSame = null)
    {
        var folder = await EnsureFolderAsync(client, ct);
        var summary = AccountsCopy.Peek(file) ?? new AccountsCopy.Summary("", 0, DateTimeOffset.Now);
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var before = await CopiesAsync(client, folder, ct);
            if (stillSame != null)
            {
                byte[]? newest = null;
                foreach (var c in before.AsEnumerable().Reverse()) if ((newest = await FileOfAsync(folder, c, ct)) != null) break;
                if (!stillSame(newest)) throw new ChangedMeanwhileException();
            }
            await folder.AppendAsync(new AppendRequest(BuildMessage(address, file, summary), KitFlags.Seen), ct);
            if (AnswerLostForTests?.Invoke(address) is { } lost) throw lost;   // tests: the server kept it, its answer was lost
            appended?.Invoke();
            var gmail = client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
            if (before.Count == 0 && !gmail) return;
            // Gmail: the old copies go from Trash / All Mail, and older ones an earlier round left behind; never the new one,
            // nor one written after it (another PC at the same moment). If the new one can't be told apart, only the old ones go.
            HashSet<ulong>? keep = null;
            if (gmail)
            {
                await folder.OpenAsync(FolderAccess.ReadWrite, ct);
                var fresh = (await CopiesAsync(client, folder, ct)).Where(c => before.All(b => b.Uid != c.Uid) && c.GmailId != null).Select(c => c.GmailId!.Value).ToHashSet();
                if (fresh.Count > 0) keep = fresh;
            }
            var old = before.Where(c => c.GmailId != null).Select(c => c.GmailId!.Value).ToHashSet();
            // Dates in emails have whole seconds and PCs' clocks differ a little: a margin, so one written about now stays.
            var cut = summary.Updated.AddSeconds(-5);
            await RemoveAsync(client, folder, before,
                keep != null ? (id, date) => !keep.Contains(id) && (old.Contains(id) || date < cut) : (id, _) => old.Contains(id), ct);
        }
        finally { try { await folder.CloseAsync(false, ct); } catch { } }
    }

    /// <summary>Tests only: an error to throw right after an append to this address, as if its answer was lost.</summary>
    internal static Func<string, Exception?>? AnswerLostForTests;

    /// <summary>The newest copy's file. Null only when the mailbox has no "Magpie Sync" folder or no copy in it; a read
    /// that fails throws (it never counts as "no copy", which would write a copy from this PC alone).</summary>
    public static async Task<byte[]?> ReadAsync(ImapClient client, CancellationToken ct)
    {
        var folder = await FindFolderAsync(client, ct);
        if (folder == null) return null;
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        try
        {
            foreach (var copy in (await CopiesAsync(client, folder, ct)).AsEnumerable().Reverse())
                if (await FileOfAsync(folder, copy, ct) is { } file) return file;
            return null;
        }
        finally { try { await folder.CloseAsync(false, ct); } catch { } }
    }

    private static async Task<byte[]?> FileOfAsync(IMailFolder folder, CopyEmail copy, CancellationToken ct)
    {
        // Never downloaded when too big: it isn't Magpie's (and could fill the memory).
        if (copy.Size > MaxEmailBytes) { Log.Warn($"accounts copy: skipped an email of {copy.Size / 1024 / 1024} MB in the copy folder"); return null; }
        var msg = await folder.GetMessageAsync(copy.Uid, ct);
        if (msg.Attachments.OfType<MimePart>().FirstOrDefault(p => p.FileName == AccountsCopy.FileName) is not { Content: { } content }) return null;
        using var ms = new MemoryStream();
        await content.DecodeToAsync(ms, ct);
        return ms.ToArray();
    }

    /// <summary>Only the newest copy email stays: every older copy goes, also on Gmail from Trash / All Mail. For a stop
    /// or move whose clean-up didn't finish the first time. When the newest is a "stopped" / "moved" note
    /// (<paramref name="newestIsNote"/>; null = look), every other copy goes; when it is a live copy, only the older ones
    /// (another PC may be writing one right now).</summary>
    public static async Task KeepOnlyNewestAsync(ImapClient client, CancellationToken ct, bool? newestIsNote = null)
    {
        var folder = await FindFolderAsync(client, ct);
        if (folder == null) return;
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var copies = await CopiesAsync(client, folder, ct);
            if (copies.Count == 0) return;
            var newest = copies[^1];
            var older = copies.Take(copies.Count - 1).ToList();
            if (older.Count == 0 && !client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)) return;
            newestIsNote ??= await FileOfAsync(folder, newest, ct) is { } file && AccountsCopy.Peek(file) is { IsNote: true };
            var olderIds = older.Where(c => c.GmailId != null).Select(c => c.GmailId!.Value).ToHashSet();
            var cut = newest.Date?.AddSeconds(-5);
            // After a note every other copy goes (also leftovers dated ahead by a fast clock); one in the label right now stays.
            await RemoveAsync(client, folder, older, newestIsNote == true
                ? (id, _) => id != newest.GmailId
                : (id, date) => id != newest.GmailId && (olderIds.Contains(id) || date < cut), ct);
        }
        finally { try { await folder.CloseAsync(false, ct); } catch { } }
    }

    /// <summary>Removes only the copy emails written at <paramref name="updated"/> (their time inside), wherever they are in
    /// the folder; the rest stay.</summary>
    public static async Task<int> RemoveCopyAsync(ImapClient client, DateTimeOffset updated, CancellationToken ct)
    {
        var folder = await FindFolderAsync(client, ct);
        if (folder == null) return 0;
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var mine = new List<CopyEmail>();
            foreach (var copy in await CopiesAsync(client, folder, ct))
                if (await FileOfAsync(folder, copy, ct) is { } file && AccountsCopy.Peek(file)?.Updated == updated) mine.Add(copy);
            if (mine.Count == 0) return 0;
            var ids = mine.Where(c => c.GmailId != null).Select(c => c.GmailId!.Value).ToHashSet();
            await RemoveAsync(client, folder, mine, (id, _) => ids.Contains(id), ct);
            return mine.Count;
        }
        finally { try { await folder.CloseAsync(false, ct); } catch { } }
    }

    /// <summary>Every copy email in the folder goes, then the folder itself (when nothing else is in it).</summary>
    public static async Task DeleteAsync(ImapClient client, CancellationToken ct)
    {
        var folder = await FindFolderAsync(client, ct);
        if (folder == null) return;
        await folder.OpenAsync(FolderAccess.ReadWrite, ct);
        var all = await CopiesAsync(client, folder, ct);
        // Every copy goes; on Gmail also those still in Trash / All Mail.
        if (all.Count > 0 || client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)) await RemoveAsync(client, folder, all, (_, _) => true, ct);
        // Emails that aren't Magpie's (moved there by hand) stay, and so does the folder then.
        var rest = (await folder.SearchAsync(KitSearch.All, ct)).Count;
        try { await folder.CloseAsync(false, ct); } catch { }
        if (rest > 0) return;
        try { await folder.DeleteAsync(ct); } catch (ImapCommandException) { }   // emptied is enough when the server keeps it
    }

    /// <summary>Removes these copy emails from the folder. On Gmail, copies in Trash / All Mail go too when
    /// <paramref name="gone"/> says so for their Gmail message id and date (the one just written never does).</summary>
    private static async Task RemoveAsync(ImapClient client, IMailFolder folder, IList<CopyEmail> copies, Func<ulong, DateTimeOffset?, bool> gone, CancellationToken ct)
    {
        var uids = copies.Select(c => c.Uid).ToList();
        if (client.Capabilities.HasFlag(ImapCapabilities.GMailExt1))
        {
            // Gmail: removing the label keeps the email in All Mail. Through Trash, deleted there for good.
            await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: ct);   // loads the special folders (Trash, All Mail)
            var trash = client.GetFolder(SpecialFolder.Trash);
            if (trash != null)
            {
                if (uids.Count > 0) await folder.MoveToAsync(uids, trash, ct);
                await DeleteCopiesInAsync(trash, gone, ct);
                await folder.OpenAsync(FolderAccess.ReadWrite, ct);
                return;
            }
            // Trash hidden from IMAP: delete the label's copies, then the same emails (by Gmail's message id) in All Mail,
            // which Gmail moves to its Trash.
            if (uids.Count > 0) await ExpungeAsync(client, folder, uids, ct);
            // Never one that is in the label right now (another PC may have just written its copy).
            var inLabel = (await CopiesAsync(client, folder, ct)).Where(c => c.GmailId != null).Select(c => c.GmailId!.Value).ToHashSet();
            if (client.GetFolder(SpecialFolder.All) is { } all) await DeleteCopiesInAsync(all, (id, date) => gone(id, date) && !inLabel.Contains(id), ct);
            else Log.Warn("accounts copy: Gmail shows neither Trash nor All Mail to IMAP; old copies stay in All Mail");
            await folder.OpenAsync(FolderAccess.ReadWrite, ct);
            return;
        }
        if (uids.Count > 0) await ExpungeAsync(client, folder, uids, ct);
    }

    private static async Task ExpungeAsync(ImapClient client, IMailFolder folder, IList<UniqueId> uids, CancellationToken ct)
    {
        await folder.StoreAsync(uids, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
        // Without UIDPLUS a plain EXPUNGE: it also removes emails in this folder someone else already marked deleted.
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await folder.ExpungeAsync(uids, ct);
        else await folder.ExpungeAsync(ct);
    }

    /// <summary>Deletes copy emails in a Gmail folder (Trash or All Mail): found by the subject, checked by the header,
    /// and only those <paramref name="gone"/> picks by Gmail message id and date (so the copy just written is never one).</summary>
    private static async Task DeleteCopiesInAsync(IMailFolder f, Func<ulong, DateTimeOffset?, bool> gone, CancellationToken ct)
    {
        await f.OpenAsync(FolderAccess.ReadWrite, ct);
        try
        {
            var candidates = await f.SearchAsync(KitSearch.SubjectContains(AccountsCopy.Subject), ct);
            if (candidates.Count == 0) return;
            var summaries = await f.FetchAsync(candidates, new FetchRequest(MessageSummaryItems.UniqueId | MessageSummaryItems.GMailMessageId | MessageSummaryItems.Envelope) { Headers = new HeaderSet(new[] { AccountsCopy.Header }) }, ct);
            var mine = summaries.Where(s => s.Headers?.Contains(AccountsCopy.Header) == true && s.GMailMessageId is { } id && gone(id, s.Envelope?.Date))
                .Select(s => s.UniqueId).ToList();
            if (mine.Count == 0) return;
            await f.StoreAsync(mine, new StoreFlagsRequest(StoreAction.Add, KitFlags.Deleted) { Silent = true }, ct);
            await f.ExpungeAsync(mine, ct);
        }
        finally { try { await f.CloseAsync(false, ct); } catch { } }
    }
}
