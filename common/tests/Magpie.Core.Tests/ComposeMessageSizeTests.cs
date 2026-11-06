using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using MimeKit;

namespace Magpie.Core.Tests;

public sealed class ComposeMessageSizeTests
{
    [Fact]
    public void Full_size_matches_serialized_mime_with_local_forwarded_and_inline_payloads()
    {
        using var dir = new TempDir();
        var path = dir.File("local.bin");
        File.WriteAllBytes(path, new byte[4096]);
        var draft = new Draft
        {
            AccountId = "A", MessageId = "size@test.local", To = "other@example.test", Subject = "Payload",
            Html = "<p>Body with inline image <img src='data:image/png;base64,AQIDBAUG'></p>",
            AttachmentPaths = new() { path },
            CarriedParts = new() { new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(new byte[8192])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64, FileName = "forwarded.bin",
            } },
        };
        var message = Composer.Build(draft, new Account { Id = "A", Email = "me@example.test" });
        var size = ComposeSafety.MeasureEncodedMessage(message);
        Assert.False(size.AtLeast);
        Assert.Equal(Composer.ToBytes(message).LongLength, size.Bytes);
        Assert.True(size.Bytes > 4096 + 8192);
        Assert.Equal(size, ComposeSafety.MeasureEncodedMessage(message));
        Assert.True(((MimePart)draft.CarriedParts[0]).Content.Stream.CanRead);
    }

    [Fact]
    public void Inline_only_message_is_measured_even_when_no_file_is_attached()
    {
        var html = "<p>Inline only<img src='data:image/png;base64," + Convert.ToBase64String(new byte[20_000]) + "'></p>";
        var draft = new Draft { AccountId = "A", MessageId = "inline@test.local", Html = html };
        var message = Composer.Build(draft, new Account { Id = "A", Email = "me@example.test" });
        Assert.Empty(draft.AttachmentPaths);
        Assert.Empty(draft.CarriedParts);
        Assert.True(ComposeSafety.MeasureEncodedMessage(message).Bytes > 20_000);
    }

    [Fact]
    public void Measurement_cap_returns_a_lower_bound_without_disposing_caller_content()
    {
        using var content = new MemoryStream(new byte[20_000]);
        var part = new MimePart("application", "octet-stream")
        {
            Content = new MimeContent(content), ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment), FileName = "large.bin",
        };
        var message = new MimeMessage { Body = part };
        var measured = ComposeSafety.MeasureEncodedMessage(message, maximumBytes: 4096);
        Assert.True(measured.AtLeast);
        Assert.Equal(4097, measured.Bytes);
        Assert.True(content.CanRead);
        Assert.False(ComposeSafety.MeasureEncodedMessage(message).AtLeast);
    }

    [Fact]
    public void Local_draft_size_query_reads_the_complete_stored_message()
    {
        using var dir = new TempDir();
        var store = new MailStore(dir.File("mail.db"));
        Assert.Null(store.GetLocalDraftMimeSize(123));
        var id = store.SaveLocalDraft(new LocalDraft { AccountId = "A", Mime = new byte[12345] });
        Assert.Equal(12345, store.GetLocalDraftMimeSize(id));
        store.DeleteLocalDraft(id);
        Assert.Null(store.GetLocalDraftMimeSize(id));
    }
}
