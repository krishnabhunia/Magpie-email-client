using Magpie.Core.Mail;
using Magpie.Core.Contacts;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Tests;

/// <summary>CW1: recipient safety, attachments and delivery options for the compose redesign.</summary>
public class Release1500Tests
{
    [Theory]
    [InlineData("photo.JPG", "image")]
    [InlineData("receipt.pdf", "pdf")]
    [InlineData("notes.md", "text")]
    [InlineData("transaction.csv", "text")]
    [InlineData("document.docx", "none")]
    [InlineData("dangerous.exe", "none")]
    [InlineData("evil.pdf.exe", "none")]
    public void Compose_preview_only_accepts_known_safe_types(string file, string type) =>
        Assert.Equal(type, ComposeSafety.AttachmentPreviewKind(file));

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.PNG", true)]
    [InlineData("scan.bmp", true)]
    [InlineData("animation.gif", false)]
    [InlineData("document.pdf", false)]
    [InlineData("dangerous.jpg.exe", false)]
    public void Only_static_picture_formats_can_be_compressed(string file, bool expected) =>
        Assert.Equal(expected, ComposeSafety.CanCompressImage(file));

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/page", true)]
    [InlineData("mailto:hello@example.com", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/secret.txt", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("not a link", false)]
    public void Compose_link_editor_rejects_unsafe_schemes(string link, bool expected) =>
        Assert.Equal(expected, ComposeSafety.ValidComposeLink(link));

    [Fact]
    public void Shared_editor_includes_safe_link_edit_and_image_resize_commands()
    {
        Assert.Contains("function setSelectedLink(url)", EditorPage.Html);
        Assert.Contains("function removeSelectedLink()", EditorPage.Html);
        Assert.Contains("function resizeSelectedImage(percent)", EditorPage.Html);
        Assert.Contains("[25,50,75,100].includes(percent)", EditorPage.Html);
        Assert.Contains("function setWritingDirection(direction)", EditorPage.Html);
        Assert.Contains("function setWritingLanguage(language)", EditorPage.Html);
        Assert.Contains("post({t:'shortcuts'})", EditorPage.Html);
        Assert.Contains("function findInEditor(query,matchCase)", EditorPage.Html);
        Assert.Contains("function replaceFoundText(query,replacement,matchCase)", EditorPage.Html);
        Assert.Contains("function replaceAllInEditor(query,replacement,matchCase)", EditorPage.Html);
        Assert.Contains("post({t:'find'})", EditorPage.Html);
        Assert.Contains("post({t:'replace'})", EditorPage.Html);
        Assert.Contains("function toggleQuoteFold()", EditorPage.Html);
        Assert.Contains("ed.classList.toggle('quote-folded'", EditorPage.Html);
        Assert.Contains("function insertTableGrid(rows,cols)", EditorPage.Html);
        Assert.Contains("function editTable(action)", EditorPage.Html);
        Assert.Contains("post({t:'tableMenu'})", EditorPage.Html);
    }

    [Fact]
    public void Compose_message_can_be_exported_and_loaded_as_eml()
    {
        var account = new Account { Email = "sender@example.com", DisplayName = "Sender" };
        var draft = new Draft { To = "recipient@example.com", Subject = "Export test", Html = "<p>Export me</p>" };
        var mime = Composer.Build(draft, account);
        using var stream = new MemoryStream();
        mime.WriteTo(stream);
        stream.Position = 0;
        var loaded = MimeMessage.Load(stream);
        Assert.Equal("Export test", loaded.Subject);
        Assert.Equal("recipient@example.com", loaded.To.Mailboxes.Single().Address);
    }

    [Fact]
    public void Duplicate_recipients_are_detected_across_all_fields()
    {
        var repeated = ComposeSafety.DuplicateRecipients("Ananya <A@Example.com>, b@example.com", "a@example.com", "B@EXAMPLE.COM, hidden@example.com");
        Assert.Equal(new[] { "A@Example.com", "b@example.com" }, repeated, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Moving_large_visible_recipient_list_to_bcc_keeps_all_people_private()
    {
        var to = string.Join(", ", Enumerable.Range(1, 21).Select(i => $"person{i}@example.com"));
        const string cc = "person22@example.com";
        const string bcc = "private@example.com, PERSON2@example.com";
        Assert.Equal(22, ComposeSafety.VisibleRecipientCount(to, cc));

        var rearranged = ComposeSafety.MoveVisibleRecipientsToBcc(to, cc, bcc);
        var recipients = Composer.ParseAddresses(rearranged.To).Mailboxes
            .Concat(Composer.ParseAddresses(rearranged.Cc).Mailboxes)
            .Concat(Composer.ParseAddresses(rearranged.Bcc).Mailboxes).ToArray();
        Assert.Single(Composer.ParseAddresses(rearranged.To).Mailboxes);
        Assert.Empty(Composer.ParseAddresses(rearranged.Cc).Mailboxes);
        Assert.Equal(23, recipients.Length);
        Assert.Equal(23, recipients.Select(address => address.Address).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(recipients, address => address.Address == "private@example.com");
        Assert.Contains(recipients, address => address.Address == "person22@example.com");
    }

    [Fact]
    public void Dragging_a_recipient_between_fields_preserves_name_and_does_not_duplicate()
    {
        var result = ComposeSafety.MoveRecipientBetweenFields("Ananya <a@example.com>, b@example.com", "manager@example.com",
            "private@example.com", "a@example.com", "to", "bcc");
        Assert.True(result.Moved);
        Assert.Equal("b@example.com", Composer.ParseAddresses(result.To).Mailboxes.Single().Address);
        Assert.Equal("manager@example.com", Composer.ParseAddresses(result.Cc).Mailboxes.Single().Address);
        Assert.Contains(Composer.ParseAddresses(result.Bcc).Mailboxes, mailbox => mailbox.Address == "a@example.com" && mailbox.Name == "Ananya");

        var repeated = ComposeSafety.MoveRecipientBetweenFields(result.To, result.Cc, result.Bcc,
            "manager@example.com", "cc", "bcc");
        Assert.True(repeated.Moved);
        var duplicate = ComposeSafety.MoveRecipientBetweenFields(repeated.To, repeated.Cc, repeated.Bcc,
            "a@example.com", "bcc", "to");
        Assert.True(duplicate.Moved);
        Assert.Single(Composer.ParseAddresses(duplicate.To).Mailboxes, m => m.Address == "a@example.com");
    }

    [Fact]
    public void Dragging_recipients_never_discards_invalid_address_text()
    {
        var result = ComposeSafety.MoveRecipientBetweenFields("valid@example.com", "not-an-address", "",
            "valid@example.com", "to", "bcc");
        Assert.False(result.Moved);
        Assert.Equal("not-an-address", result.Cc);
        Assert.Equal("valid@example.com", result.To);
    }

    [Fact]
    public void Saved_quick_templates_remain_available_after_settings_serialization()
    {
        var templates = new List<Magpie.Core.Settings.QuickTemplate>
        {
            new() { Name = "Client update", Body = "Here is the latest status." },
        };
        var restored = System.Text.Json.JsonSerializer.Deserialize<List<Magpie.Core.Settings.QuickTemplate>>(
            System.Text.Json.JsonSerializer.Serialize(templates));
        Assert.Equal("Client update", restored!.Single().Name);
        Assert.Equal("Here is the latest status.", restored!.Single().Body);
    }

    [Fact]
    public void Compose_window_placement_defaults_and_invalid_sizes_are_safe()
    {
        var placement = new Magpie.Core.Settings.ComposeWindowPlacement
        {
            HasSavedPlacement = true, Left = double.NaN, Top = 20,
            Width = double.PositiveInfinity, Height = 30,
        };
        placement.Normalise();
        Assert.False(placement.HasSavedPlacement);
        Assert.Equal(980, placement.Width);
        Assert.Equal(460, placement.Height);
    }

    [Theory]
    [InlineData(AccountKind.Gmail, 0, 25_000_000L)]
    [InlineData(AccountKind.Microsoft, 0, 20_000_000L)]
    [InlineData(AccountKind.Imap, 12, 12_000_000L)]
    [InlineData(AccountKind.Imap, 0, 25_000_000L)]
    [InlineData(AccountKind.Imap, 150, 100_000_000L)]
    public void Compose_message_limit_is_provider_aware_and_advisory(AccountKind kind, int configuredMb, long expectedBytes)
    {
        var account = new Account { Kind = kind, MaxOutgoingMessageMb = configuredMb };
        Assert.Equal(expectedBytes, ComposeSafety.SuggestedOutgoingLimitBytes(account));
    }

    [Fact]
    public void Recent_attachment_history_deduplicates_and_keeps_newest_files_first()
    {
        var paths = ComposeSafety.RecentAttachmentPaths(new[] { "C:/new.pdf", "C:/OLD.pdf" },
            new[] { "c:/old.pdf", "C:/older.docx", "C:/third.txt" }, maximum: 3);
        Assert.Equal(new[] { "C:/new.pdf", "C:/OLD.pdf", "C:/older.docx" }, paths);
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

    [Fact]
    public void External_recipient_check_uses_sender_domain_and_deduplicates()
    {
        var external = ComposeSafety.ExternalRecipients("me@work.example", "one@work.example, customer@outside.example",
            "CUSTOMER@OUTSIDE.EXAMPLE", "private@another.example");
        Assert.Equal(new[] { "customer@outside.example", "private@another.example" }, external, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Word_count_and_reading_estimate_are_available_for_review()
    {
        Assert.Equal(6, ComposeSafety.WordCount("Hello Krishna, I've attached two files."));
        Assert.Equal(2, ComposeSafety.EstimatedReadingMinutes(205));
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("quarterly report (final).xlsx", true)]
    [InlineData("../../data.txt", false)]
    [InlineData("folder\\data.txt", false)]
    [InlineData("report?.pdf", false)]
    [InlineData("bad\nfile.pdf", false)]
    [InlineData("renamed.", false)]
    public void Outgoing_attachment_names_are_checked(string name, bool expected) =>
        Assert.Equal(expected, ComposeSafety.ValidAttachmentName(name));

    [Fact]
    public void Interleaved_attachment_order_and_names_survive_saving_as_mime()
    {
        var first = Path.Combine(Path.GetTempPath(), "magpie-compose-first-" + Guid.NewGuid() + ".txt");
        var second = Path.Combine(Path.GetTempPath(), "magpie-compose-second-" + Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(first, "first");
            File.WriteAllText(second, "second");
            var forwarded = new MimePart("application", "pdf")
            {
                FileName = "forward.pdf",
                Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3 })),
            };
            var account = new Account { Email = "me@example.com", DisplayName = "Me" };
            var draft = new Draft
            {
                To = "client@example.com", Html = "<p>Hello</p>",
                AttachmentPaths = new List<string> { first, second },
                AttachmentNames = new Dictionary<string, string> { [first] = "renamed-first.txt" },
                CarriedParts = new List<MimeEntity> { forwarded },
                AttachmentOrder = new List<DraftAttachmentPosition>
                {
                    new() { CarriedIndex = 0 }, new() { Path = second }, new() { Path = first },
                },
            };

            var message = Composer.Build(draft, account);
            var names = message.Attachments.OfType<MimePart>().Select(part => part.FileName).ToArray();
            Assert.Equal(new[] { "forward.pdf", Path.GetFileName(second), "renamed-first.txt" }, names);
            var reopened = Composer.FromMime(message, account.Id, "thread");
            Assert.Equal(names, reopened.CarriedParts.OfType<MimePart>().Select(part => part.FileName));
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
        }
    }

    [Fact]
    public void Contact_picker_groups_include_only_group_members()
    {
        var saved = new List<SavedContact>
        {
            new() { Id = 1, Name = "Ananya", Emails = new() { "ananya@example.com" }, Groups = new() { "Finance" } },
            new() { Id = 2, Name = "Meera", Emails = new() { "meera@example.com" }, Groups = new() { "Finance", "Project" } },
            new() { Id = 3, Name = "Rahul", Emails = new() { "rahul@example.com" }, Groups = new() { "Project" } },
        };
        var people = new List<(string Address, string Name, int Count, DateTimeOffset Last, bool Sent)>();
        var entries = ContactSuggest.Entries(saved, people);
        var groups = ContactSuggest.Groups(entries);

        Assert.Equal(new[] { "Finance", "Project" }, groups.Select(group => group.Group));
        Assert.Equal(new[] { "Ananya", "Meera" }, groups.Single(group => group.Group == "Finance").Members.Select(member => member.Name));
    }
}
