using Magpie.Core.Mail;
using MimeKit;

namespace Magpie.Core.Tests;

public class MimeAttachmentNameDisplayTests
{
    public static IEnumerable<object?[]> MessageAttachmentNames()
    {
        yield return new object?[] { "renamed.eml", "type-fallback.eml", "Original subject", "renamed.eml" };
        yield return new object?[] { "Résumé message.eml", null, "Original subject", "Résumé message.eml" };
        yield return new object?[] { "custom-name-without-extension", null, "Original subject", "custom-name-without-extension" };
        yield return new object?[] { null, "content-name.eml", "Original subject", "content-name.eml" };
        yield return new object?[] { "", "content-name.eml", "Original subject", "content-name.eml" };
        yield return new object?[] { null, null, "Original subject", "Original subject.eml" };
        yield return new object?[] { null, null, null, "message.eml" };
        yield return new object?[] { null, null, "", "message.eml" };
    }

    private static MimeMessage InnerMessage(string? subject) => new()
    {
        MessageId = "attached-message@example.test",
        Date = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        Subject = subject ?? "",
        Body = new TextPart("plain") { Text = "Original attached message body\twith  spaces.\n" },
    };

    [Theory]
    [MemberData(nameof(MessageAttachmentNames))]
    public void Downloaded_attached_message_displays_explicit_name_without_appending_another_extension(
        string? dispositionName, string? contentTypeName, string? subject, string expected)
    {
        var part = new MessagePart { Message = InnerMessage(subject) };
        if (dispositionName != null)
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = dispositionName };
        if (contentTypeName != null) part.ContentType.Name = contentTypeName;
        using var container = new MimeMessage
        {
            Body = new Multipart("mixed") { new TextPart("plain") { Text = "Outer body." }, part },
        };
        var originalBody = Composer.ToBytes(part.Message);
        var originalPart = Composer.ToBytes(container);
        var listed = Assert.Single(MimeText.ListAttachments(container));
        Assert.Equal(expected, listed.FileName);
        Assert.Equal("message/rfc822", listed.ContentType);
        Assert.Equal(originalBody, Composer.ToBytes(part.Message));
        Assert.Equal(originalPart, Composer.ToBytes(container));

        using var reopened = Composer.FromBytes(originalPart);
        Assert.Equal(expected, Assert.Single(MimeText.ListAttachments(reopened)).FileName);
    }

    [Theory]
    [MemberData(nameof(MessageAttachmentNames))]
    public void Pending_attached_message_uses_the_same_filename_precedence_as_downloaded_mime(
        string? dispositionName, string? contentTypeName, string? subject, string expected)
    {
        var type = new ContentType("message", "rfc822");
        if (contentTypeName != null) type.Name = contentTypeName;
        var part = new MailKit.BodyPartMessage(type, "2")
        {
            Envelope = new MailKit.Envelope { Subject = subject },
        };
        if (dispositionName != null)
            part.ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = dispositionName };
        var listed = Assert.Single(MimeText.PendingAttachments(new[] { part }, textBody: null, htmlBody: null));
        Assert.Equal(expected, listed.FileName);
        Assert.Equal("message/rfc822", listed.ContentType);
        Assert.Equal(MimeText.PendingIndex, listed.Index);
        Assert.Equal(dispositionName, part.ContentDisposition?.FileName);
        Assert.Equal(contentTypeName, part.ContentType.Name);
        Assert.Equal(subject, part.Envelope.Subject);
    }

    [Fact]
    public void Renaming_an_eml_keeps_inner_message_bytes_and_reader_display_consistent()
    {
        var attached = new MessagePart { Message = InnerMessage("Original subject") };
        using var container = new MimeMessage
        {
            Body = new Multipart("mixed") { new TextPart("plain") { Text = "Outer body." }, attached },
        };
        var original = Composer.ToBytes(attached.Message);
        MimeAttachmentNames.Rename(attached, "reviewed-message.eml");
        Assert.Equal(original, Composer.ToBytes(attached.Message));
        Assert.Equal("reviewed-message.eml", Assert.Single(MimeText.ListAttachments(container)).FileName);
        using var reopened = Composer.FromBytes(Composer.ToBytes(container));
        Assert.Equal("reviewed-message.eml", Assert.Single(MimeText.ListAttachments(reopened)).FileName);
        var restoredPart = Assert.IsType<MessagePart>(Assert.Single(reopened.Attachments));
        Assert.Equal(original, Composer.ToBytes(restoredPart.Message));
    }
}
