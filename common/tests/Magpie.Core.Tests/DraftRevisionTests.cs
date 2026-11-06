using System.Text;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Magpie.Core.Tests;

public class DraftRevisionTests
{
    private static Draft NewDraft(string accountId = "A", string messageId = "draft@test.local") => new()
    {
        AccountId = accountId, MessageId = messageId, Mode = ComposeMode.New,
        To = "Ananya <ananya@example.com>", Subject = "Project update", Html = "<p>First version</p>",
    };

    private static Account Sender(Draft draft) => new() { Id = draft.AccountId, Email = "me@test.local", DisplayName = "Krishna" };

    private static byte[] Bytes(Draft draft) => Composer.ToBytes(Composer.Build(draft, Sender(draft)));

    private static void UpdateDatabase(TempDir dir, string sql, params (string name, object value)[] values)
    {
        using var c = new SqliteConnection("Data Source=" + dir.File("mail.db"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in values) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static long DatabaseNumber(TempDir dir, string sql)
    {
        using var c = new SqliteConnection("Data Source=" + dir.File("mail.db"));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void Revision_survives_restart_and_preserves_raw_editable_fields()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        draft.To = "Ananya <ananya@example.com>; incomplete address";
        draft.Cc = "cc@example.com";
        draft.Bcc = "private@example.com; still typing";
        draft.ReplyTo = "reply@example.com";
        draft.Html = "<p lang='bn' dir='ltr'>লেখা <strong>kept</strong></p>";
        draft.Priority = "High";
        draft.RequestReadReceipt = true;
        draft.IsPlainText = true;
        draft.ComposeStyle = new TextStyle { Family = "Arial", Size = 18 };
        draft.InReplyTo = "<original@example.com>";
        draft.References = "<root@example.com> <original@example.com>";
        draft.ThreadKey = "thread-one";
        draft.LocalDraftId = 99;
        draft.SourceDraftRow = 123;
        var before = DateTimeOffset.UtcNow;

        var id = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.True(id > 0);
        var reopened = new MailStore(dir.File("mail.db"));
        var metadata = Assert.Single(reopened.GetDraftRevisions("A", "<DRAFT@Test.Local>"));
        Assert.Equal(id, metadata.Id);
        Assert.InRange(metadata.Created, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(draft.Subject, metadata.Subject);
        Assert.True(metadata.SizeBytes > 0);
        Assert.True(metadata.SizeBytes <= MailStore.MaxDraftRevisionBytes);

        var restored = reopened.OpenDraftRevision("A", draft.MessageId, id)!;
        Assert.NotNull(restored);
        Assert.Equal(draft.To, restored.To);
        Assert.Equal(draft.Cc, restored.Cc);
        Assert.Equal(draft.Bcc, restored.Bcc);
        Assert.Equal(draft.ReplyTo, restored.ReplyTo);
        Assert.Equal(draft.Subject, restored.Subject);
        Assert.Equal(draft.Html, restored.Html);
        Assert.Equal(draft.Priority, restored.Priority);
        Assert.True(restored.RequestReadReceipt);
        Assert.True(restored.IsPlainText);
        Assert.Equal("Arial", restored.ComposeStyle?.Family);
        Assert.NotNull(restored.ComposeStyle);
        Assert.Equal(18d, restored.ComposeStyle.Size);
        Assert.Equal(draft.InReplyTo, restored.InReplyTo);
        Assert.Equal(draft.References, restored.References);
        Assert.Equal(draft.ThreadKey, restored.ThreadKey);
        Assert.Null(restored.LocalDraftId);
        Assert.Null(restored.SourceDraftRow);
        Assert.Equal(draft.MessageId, restored.MessageId);
    }

    [Fact]
    public void Revision_restores_captured_attachment_bytes_names_and_order_after_source_file_is_deleted()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var path = dir.File("source.txt");
        File.WriteAllText(path, "original local attachment", new UTF8Encoding(false));
        draft.AttachmentPaths.Add(path);
        draft.AttachmentNames[path] = "renamed.txt";
        draft.CarriedParts.Add(new TextPart("plain")
        {
            Text = "forwarded attachment", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "carried.txt",
        });
        draft.AttachmentOrder.Add(new DraftAttachmentPosition { CarriedIndex = 0 });
        draft.AttachmentOrder.Add(new DraftAttachmentPosition { Path = path });
        draft.Html = "<p>Picture <img src='data:image/png;base64,AQID'></p>";
        var id = store.SaveDraftRevision(draft, Bytes(draft));
        File.Delete(path);

        var restored = store.OpenDraftRevision("A", draft.MessageId, id)!;
        Assert.NotNull(restored);
        Assert.Empty(restored.AttachmentPaths);
        Assert.Equal(draft.Html, restored.Html);
        Assert.Equal(new[] { "carried.txt", "renamed.txt" }, restored.CarriedParts.Cast<MimePart>().Select(p => p.FileName));
        Assert.Equal(new int?[] { 0, 1 }, restored.AttachmentOrder.Select(a => a.CarriedIndex));
        var localPart = Assert.IsAssignableFrom<MimePart>(restored.CarriedParts[1]);
        using var content = new MemoryStream();
        localPart.Content.DecodeTo(content);
        Assert.Equal("original local attachment", Encoding.UTF8.GetString(content.ToArray()));
        // Rebuilding never reads the now-missing source path; the snapshot owns its attachments.
        Assert.Equal(2, Composer.Build(restored, Sender(restored)).Attachments.Count());
    }

    [Fact]
    public void Repeated_saves_deduplicate_despite_date_multipart_and_inline_content_id_changes()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        draft.Html = "<p>Picture <img src='data:image/png;base64,AQID'></p>";
        draft.CarriedParts.Add(new TextPart("plain")
        {
            Text = "attachment", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "same.txt",
        });
        var first = Composer.Build(draft, Sender(draft));
        first.Date = DateTimeOffset.UtcNow.AddHours(-1);
        var firstBytes = Composer.ToBytes(first);
        var second = Composer.Build(draft, Sender(draft));
        var secondBytes = Composer.ToBytes(second);
        Assert.False(firstBytes.SequenceEqual(secondBytes));

        var firstId = store.SaveDraftRevision(draft, firstBytes);
        var secondId = store.SaveDraftRevision(draft, secondBytes);
        Assert.True(firstId > 0);
        Assert.Equal(firstId, secondId);
        Assert.Single(store.GetDraftRevisions("A", draft.MessageId));
    }

    [Fact]
    public void Attachment_content_and_order_changes_create_distinct_versions()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var one = new TextPart("plain") { Text = "one", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "one.txt" };
        var two = new TextPart("plain") { Text = "two", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "two.txt" };
        draft.CarriedParts.AddRange(new[] { one, two });
        var initial = store.SaveDraftRevision(draft, Bytes(draft));
        one.Text = "changed bytes";
        var changed = store.SaveDraftRevision(draft, Bytes(draft));
        draft.AttachmentOrder.Add(new DraftAttachmentPosition { CarriedIndex = 1 });
        draft.AttachmentOrder.Add(new DraftAttachmentPosition { CarriedIndex = 0 });
        var reordered = store.SaveDraftRevision(draft, Bytes(draft));

        Assert.True(initial > 0 && changed > initial && reordered > changed);
        Assert.Equal(3, store.GetDraftRevisions("A", draft.MessageId).Count);
        Assert.Equal(new[] { "two.txt", "one.txt" }, store.OpenDraftRevision("A", draft.MessageId, reordered)!.CarriedParts.Cast<MimePart>().Select(p => p.FileName));
    }

    [Fact]
    public void Mime_overload_roundtrips_and_deduplicates_equivalent_attachment_transfer_encoding()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var part = new TextPart("plain")
        {
            Text = "Unicode attachment: লেখা", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            FileName = "unicode.txt", ContentTransferEncoding = ContentEncoding.Base64,
        };
        draft.CarriedParts.Add(part);
        var first = store.SaveDraftRevision(draft, Composer.Build(draft, Sender(draft)));
        part.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
        var second = store.SaveDraftRevision(draft, Composer.Build(draft, Sender(draft)));
        Assert.True(first > 0);
        Assert.Equal(first, second);
        var restored = store.OpenDraftRevision("A", draft.MessageId, first)!;
        Assert.NotNull(restored);
        Assert.Equal(part.Text, Assert.IsType<TextPart>(Assert.Single(restored.CarriedParts)).Text);
    }

    [Fact]
    public void History_is_isolated_by_account_message_and_revision_id()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var a = NewDraft();
        var b = NewDraft("B");
        var other = NewDraft("A", "other@test.local");
        var aid = store.SaveDraftRevision(a, Bytes(a));
        var bid = store.SaveDraftRevision(b, Bytes(b));
        var otherId = store.SaveDraftRevision(other, Bytes(other));

        Assert.Null(store.OpenDraftRevision("B", a.MessageId, aid));
        Assert.Null(store.OpenDraftRevision("A", other.MessageId, aid));
        Assert.Null(store.OpenDraftRevision("A", a.MessageId, -1));
        Assert.Empty(store.GetDraftRevisions("missing", a.MessageId));
        store.DeleteDraftRevisions("A", "<DRAFT@Test.Local>");
        Assert.Empty(store.GetDraftRevisions("A", a.MessageId));
        Assert.NotNull(store.OpenDraftRevision("B", b.MessageId, bid));
        Assert.NotNull(store.OpenDraftRevision("A", other.MessageId, otherId));
    }

    [Fact]
    public void Invalid_or_mismatched_capture_is_rejected_without_history()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var goodBytes = Bytes(draft);
        Assert.Equal(0, store.SaveDraftRevision(draft, Array.Empty<byte>()));
        Assert.Equal(0, store.SaveDraftRevision(draft, Encoding.UTF8.GetBytes("not a MIME snapshot")));
        draft.MessageId = "other@test.local";
        Assert.Equal(0, store.SaveDraftRevision(draft, goodBytes));
        draft.MessageId = "";
        Assert.Equal(0, store.SaveDraftRevision(draft, goodBytes));
        draft.MessageId = "draft@test.local";
        draft.AccountId = "";
        Assert.Equal(0, store.SaveDraftRevision(draft, goodBytes));
        Assert.Empty(store.GetDraftRevisions("A", "draft@test.local"));
    }

    [Fact]
    public void Oversized_capture_is_rejected_before_it_can_grow_history()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        Assert.Equal(0, store.SaveDraftRevision(draft, new byte[MailStore.MaxDraftRevisionBytes + 1]));
        Assert.Empty(store.GetDraftRevisions("A", draft.MessageId));
    }

    [Fact]
    public void Mime_overload_stops_serialization_at_the_cap_and_leaves_caller_content_open()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var content = new MemoryStream(new byte[MailStore.MaxDraftRevisionBytes], writable: false);
        draft.CarriedParts.Add(new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(content), ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "large.bin",
        });
        var message = Composer.Build(draft, Sender(draft));

        Assert.Equal(0, store.SaveDraftRevision(draft, message));
        Assert.True(content.CanRead);
        Assert.Empty(store.GetDraftRevisions("A", draft.MessageId));
        message.Dispose();
    }

    [Fact]
    public void Only_latest_thirty_versions_are_retained_and_restoring_old_content_creates_a_new_version()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        long first = 0;
        for (var i = 0; i < 35; i++)
        {
            draft.Subject = "Revision " + i;
            var id = store.SaveDraftRevision(draft, Bytes(draft));
            Assert.True(id > 0);
            if (i == 0) first = id;
        }
        var history = store.GetDraftRevisions("A", draft.MessageId);
        Assert.Equal(MailStore.MaxDraftRevisions, history.Count);
        Assert.Equal("Revision 34", history[0].Subject);
        Assert.Equal("Revision 5", history[^1].Subject);
        Assert.Null(store.OpenDraftRevision("A", draft.MessageId, first));

        draft.Subject = "Revision 30"; // An explicit return to older content is a new chronological state.
        var again = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.True(again > history[0].Id);
        Assert.Equal("Revision 30", store.GetDraftRevisions("A", draft.MessageId)[0].Subject);
    }

    [Fact]
    public void Expired_history_is_not_available_and_is_pruned_on_next_save_or_restart()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var old = store.SaveDraftRevision(draft, Bytes(draft));
        UpdateDatabase(dir, "UPDATE draft_revisions SET created=$when WHERE id=$id",
            ("$when", DateTimeOffset.UtcNow.AddDays(-MailStore.DraftRevisionRetentionDays - 1).ToUnixTimeMilliseconds()), ("$id", old));
        Assert.Empty(store.GetDraftRevisions("A", draft.MessageId));
        Assert.Null(store.OpenDraftRevision("A", draft.MessageId, old));
        var next = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.True(next > old);
        Assert.Equal(1, DatabaseNumber(dir, "SELECT COUNT(*) FROM draft_revisions"));
        UpdateDatabase(dir, "UPDATE draft_revisions SET created=0");
        _ = new MailStore(dir.File("mail.db"));
        Assert.Equal(0, DatabaseNumber(dir, "SELECT COUNT(*) FROM draft_revisions"));
    }

    [Fact]
    public void Global_payload_budget_evicts_oldest_captures_across_drafts()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var ids = new List<long>();
        for (var i = 0; i < 7; i++)
        {
            var draft = NewDraft(i % 2 == 0 ? "A" : "B", $"draft-{i}@test.local");
            ids.Add(store.SaveDraftRevision(draft, Bytes(draft)));
        }
        // Exercise the retention arithmetic without writing hundreds of megabytes of fixture BLOBs.
        UpdateDatabase(dir, "UPDATE draft_revisions SET size_bytes=$bytes", ("$bytes", MailStore.MaxDraftRevisionBytes));
        var newest = NewDraft("A", "newest@test.local");
        var newestId = store.SaveDraftRevision(newest, Bytes(newest));
        Assert.True(newestId > ids[^1]);
        Assert.True(DatabaseNumber(dir, "SELECT SUM(size_bytes) FROM draft_revisions") <= MailStore.MaxDraftRevisionTotalBytes);
        Assert.Null(store.OpenDraftRevision("A", "draft-0@test.local", ids[0]));
        Assert.NotNull(store.OpenDraftRevision("A", "draft-6@test.local", ids[6]));
        Assert.NotNull(store.OpenDraftRevision("A", newest.MessageId, newestId));
    }

    [Fact]
    public void Captures_remain_in_save_order_when_the_system_clock_moves_backwards()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var old = store.SaveDraftRevision(draft, Bytes(draft));
        UpdateDatabase(dir, "UPDATE draft_revisions SET created=$future WHERE id=$id",
            ("$future", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()), ("$id", old));
        draft.Subject = "Newest capture";
        var latest = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.Equal(latest, store.GetDraftRevisions("A", draft.MessageId)[0].Id);
        Assert.Equal(latest, store.SaveDraftRevision(draft, Bytes(draft)));
    }

    [Fact]
    public async Task Concurrent_duplicate_captures_are_atomic_across_store_instances()
    {
        using var dir = new TempDir();
        var one = new MailStore(dir.File("mail.db"));
        var two = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var mime = Bytes(draft);
        var ids = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => Task.Run(() => (i % 2 == 0 ? one : two).SaveDraftRevision(draft, mime))));
        Assert.All(ids, id => Assert.True(id > 0));
        Assert.Single(ids.Distinct());
        Assert.Single(one.GetDraftRevisions("A", draft.MessageId));
    }

    [Fact]
    public void Corrupted_snapshot_or_mime_fails_closed_without_replacing_a_draft()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var id = store.SaveDraftRevision(draft, Bytes(draft));
        UpdateDatabase(dir, "UPDATE draft_revisions SET snapshot=$bad WHERE id=$id", ("$bad", Encoding.UTF8.GetBytes("{broken")), ("$id", id));
        Assert.Null(store.OpenDraftRevision("A", draft.MessageId, id));
        Assert.Equal("Project update", Assert.Single(store.GetDraftRevisions("A", draft.MessageId)).Subject);
        draft.Subject = "Good newer version";
        var next = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.NotNull(store.OpenDraftRevision("A", draft.MessageId, next));
        UpdateDatabase(dir, "UPDATE draft_revisions SET mime=$bad WHERE id=$id", ("$bad", Encoding.UTF8.GetBytes("damaged MIME")), ("$id", next));
        Assert.Null(store.OpenDraftRevision("A", draft.MessageId, next));
    }

    [Fact]
    public void Duplicate_capture_replaces_an_unusable_checkpoint_instead_of_reusing_its_id()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var draft = NewDraft();
        var first = store.SaveDraftRevision(draft, Bytes(draft));
        UpdateDatabase(dir, "UPDATE draft_revisions SET snapshot=$bad WHERE id=$id",
            ("$bad", Encoding.UTF8.GetBytes("{broken")), ("$id", first));
        var repaired = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.True(repaired > first);
        Assert.NotNull(store.OpenDraftRevision("A", draft.MessageId, repaired));

        // SQLite can contain an invalid storage class despite a BLOB column declaration.
        UpdateDatabase(dir, "UPDATE draft_revisions SET mime='text instead of BLOB' WHERE id=$id", ("$id", repaired));
        Assert.Null(store.OpenDraftRevision("A", draft.MessageId, repaired));
        var repairedAgain = store.SaveDraftRevision(draft, Bytes(draft));
        Assert.True(repairedAgain > repaired);
        Assert.NotNull(store.OpenDraftRevision("A", draft.MessageId, repairedAgain));
    }

    [Fact]
    public void History_survives_local_row_deletion_but_account_removal_and_explicit_clear_delete_it()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        var a = NewDraft();
        var b = NewDraft("B");
        var bytes = Bytes(a);
        store.SaveDraftRevision(a, bytes);
        store.SaveDraftRevision(b, Bytes(b));
        var local = store.SaveLocalDraft(new LocalDraft { AccountId = "A", MessageId = a.MessageId, Mime = bytes });
        store.DeleteLocalDraft(local);
        Assert.Single(store.GetDraftRevisions("A", a.MessageId));
        store.DeleteAccount("A");
        Assert.Empty(store.GetDraftRevisions("A", a.MessageId));
        Assert.Single(store.GetDraftRevisions("B", b.MessageId));
        store.DeleteDraftRevisionsForAccount("B");
        Assert.Empty(store.GetDraftRevisions("B", b.MessageId));
    }
}
