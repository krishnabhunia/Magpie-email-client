using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Tests;

/// <summary>CW1: recipient safety, attachments and delivery options for the compose redesign.</summary>
public class Release1500Tests
{
    [Fact]
    public void Duplicate_recipients_are_detected_across_all_fields()
    {
        var repeated = ComposeSafety.DuplicateRecipients("Ananya <A@Example.com>, b@example.com", "a@example.com", "B@EXAMPLE.COM, hidden@example.com");
        Assert.Equal(new[] { "A@Example.com", "b@example.com" }, repeated, StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Please find the attached report", true)]
    [InlineData("See the attachment", true)]
    [InlineData("We can meet tomorrow", false)]
    public void Missing_attachment_language_is_detected(string body, bool expected) =>
        Assert.Equal(expected, ComposeSafety.MentionsAttachment(body));

    [Fact]
    public void Encoded_size_includes_base64_expansion()
    {
        Assert.True(ComposeSafety.EstimatedEncodedBytes(new[] { 20_000_000L }) > 25_000_000L);
        Assert.True(ComposeSafety.EstimatedEncodedBytes(new[] { 2_000L, 3_000L }) > 5_000);
    }

    [Fact]
    public void Missing_attachments_are_not_silently_dropped_on_send()
    {
        var account = new Account { Email = "me@example.com", DisplayName = "Me" };
        var draft = new Draft { AccountId = account.Id, To = "to@example.com", Subject = "Report", Html = "<p>Hello</p>" };
        draft.AttachmentPaths.Add(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf"));
        Assert.Single(ComposeSafety.MissingFiles(draft.AttachmentPaths));
        Assert.Throws<FileNotFoundException>(() => Composer.Build(draft, account));
    }

    [Fact]
    public void Mime_message_deduplicates_and_keeps_bcc_private()
    {
        var account = new Account { Email = "me@example.com", DisplayName = "Me" };
        var draft = new Draft
        {
            To = "first@example.com", Cc = "FIRST@example.com, second@example.com",
            Bcc = "second@example.com, secret@example.com", ReplyTo = "replies@example.com",
            Subject = "Review", Priority = "High", RequestReadReceipt = true, Html = "<p>Hi</p>",
        };
        var msg = Composer.Build(draft, account);
        Assert.Single(msg.To.Mailboxes);
        Assert.Single(msg.Cc.Mailboxes);
        Assert.Single(msg.Bcc.Mailboxes);
        Assert.Equal("secret@example.com", msg.Bcc.Mailboxes.Single().Address);
        Assert.Equal("replies@example.com", msg.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("high", msg.Headers["Importance"]);
        Assert.Equal(account.Email, msg.Headers["Disposition-Notification-To"]);
    }

    [Fact]
    public void Delivery_options_survive_mime_draft_round_trip()
    {
        var account = new Account { Email = "me@example.com", DisplayName = "Me" };
        var draft = new Draft
        {
            To = "client@example.com", ReplyTo = "replies@example.com", Priority = "Low",
            RequestReadReceipt = true, Html = "<p>Report attached separately.</p>",
        };
        var message = Composer.Build(draft, account);
        var restored = Composer.FromMime(message, account.Id, "thread");

        Assert.Equal(draft.ReplyTo, restored.ReplyTo);
        Assert.Equal(draft.Priority, restored.Priority);
        Assert.True(restored.RequestReadReceipt);
    }
}
