using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Tests;

public class MimeAttachmentNamesTests
{
    private static Account Sender() => new() { Id = "A", Email = "me@example.test", Enabled = false };

    private static MessagePart AttachedMessage()
    {
        var message = new MimeMessage
        {
            MessageId = "attached-original@example.test", Subject = "Attached original",
            Body = new TextPart("plain") { Text = "Original nested message body" },
        };
        message.From.Add(new MailboxAddress("Original sender", "original@example.test"));
        message.To.Add(new MailboxAddress("Recipient", "recipient@example.test"));
        return new MessagePart("rfc822")
        {
            Message = message, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "old.eml" },
        };
    }

    [Fact]
    public void Attached_message_rename_updates_both_filename_headers_and_preserves_nested_bytes()
    {
        var part = AttachedMessage();
        var original = Composer.ToBytes(Assert.IsType<MimeMessage>(part.Message));
        MimeAttachmentNames.Rename(part, "renamed résumé.eml");
        Assert.Equal("renamed résumé.eml", MimeAttachmentNames.Get(part));
        Assert.Equal("renamed résumé.eml", part.ContentDisposition!.FileName);
        Assert.Equal("renamed résumé.eml", part.ContentType.Name);
        Assert.Equal(original, Composer.ToBytes(Assert.IsType<MimeMessage>(part.Message)));

        var draft = new Draft { AccountId = "A", To = "recipient@example.test", Html = "<p>Forwarded note</p>" };
        draft.CarriedParts.Add(part);
        using var sent = Composer.FromBytes(Composer.ToBytes(Composer.Build(draft, Sender())));
        var attached = Assert.IsType<MessagePart>(Assert.Single(sent.Attachments));
        Assert.Equal("renamed résumé.eml", MimeAttachmentNames.Get(attached));
        Assert.Equal(original, Composer.ToBytes(Assert.IsType<MimeMessage>(attached.Message)));
    }

    [Fact]
    public void Renamed_message_survives_saved_draft_revision_and_queued_mime_roundtrips()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Config.Accounts.Add(Sender());
        var draft = new Draft { AccountId = "A", To = "recipient@example.test", Subject = "Forward", Html = "<p>Note</p>" };
        var part = AttachedMessage();
        // Compare MIME snapshots, not pre-serialization Text: the transport may add a terminal CRLF.
        var originalNestedMime = Composer.ToBytes(Assert.IsType<MimeMessage>(part.Message));
        MimeAttachmentNames.Rename(part, "saved copy.eml");
        draft.CarriedParts.Add(part);
        var localId = engine.SaveLocalDraft(draft, pendingUpload: false);
        var reopened = engine.OpenLocalDraft(localId)!;
        var reopenedPart = Assert.IsType<MessagePart>(Assert.Single(reopened.CarriedParts));
        Assert.Equal("saved copy.eml", MimeAttachmentNames.Get(reopenedPart));
        Assert.Equal(originalNestedMime, Composer.ToBytes(Assert.IsType<MimeMessage>(reopenedPart.Message)));
        var revision = Assert.Single(engine.Store.GetDraftRevisions("A", draft.MessageId));
        var restored = engine.Store.OpenDraftRevision("A", draft.MessageId, revision.Id)!;
        var restoredPart = Assert.IsType<MessagePart>(Assert.Single(restored.CarriedParts));
        Assert.Equal("saved copy.eml", MimeAttachmentNames.Get(restoredPart));
        Assert.Equal(originalNestedMime, Composer.ToBytes(Assert.IsType<MimeMessage>(restoredPart.Message)));
        var queued = engine.QueueSend(reopened, DateTimeOffset.Now.AddHours(1), null);
        using var outgoing = Composer.FromBytes(engine.Store.GetOutbox().Single(x => x.Id == queued).Mime);
        Assert.Equal("saved copy.eml", MimeAttachmentNames.Get(Assert.Single(outgoing.Attachments)));
        var outgoingPart = Assert.IsType<MessagePart>(Assert.Single(outgoing.Attachments));
        Assert.Equal(originalNestedMime, Composer.ToBytes(Assert.IsType<MimeMessage>(outgoingPart.Message)));
    }

    [Fact]
    public void Ordinary_binary_rename_keeps_payload_and_name_falls_back_to_content_type()
    {
        var payload = new byte[] { 0, 1, 13, 10, 255 };
        var part = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(new MemoryStream(payload)), ContentTransferEncoding = ContentEncoding.Base64,
        };
        part.ContentType.Name = "legacy.bin";
        Assert.Equal("legacy.bin", MimeAttachmentNames.Get(part));
        MimeAttachmentNames.Rename(part, "new.bin");
        using var decoded = new MemoryStream();
        part.Content.DecodeTo(decoded);
        Assert.Equal(payload, decoded.ToArray());
        Assert.Equal("new.bin", part.FileName);
    }

    [Theory]
    [InlineData("../escape.eml")]
    [InlineData("bad\r\nheader.eml")]
    [InlineData("")]
    public void Invalid_names_do_not_change_either_message_filename_header(string name)
    {
        var part = AttachedMessage();
        Assert.Throws<ArgumentException>(() => MimeAttachmentNames.Rename(part, name));
        Assert.Equal("old.eml", part.ContentDisposition!.FileName);
        Assert.Null(part.ContentType.Name);
    }
}
