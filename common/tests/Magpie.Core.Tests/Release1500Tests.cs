using Magpie.Core.Mail;
using Magpie.Core.Contacts;
using Magpie.Core.Models;
using System.IO.Compression;
using System.Xml;
using MimeKit;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

/// <summary>CW1: recipient safety, attachments and delivery options for the compose redesign.</summary>
public class Release1500Tests
{
    [Fact]
    public void Compose_more_order_is_normalised_and_cloned_independently()
    {
        var appearance = new Appearance
        {
            ComposeMoreOrder = new List<string> { "find", "quote", "find", "unrecognised" },
        };
        appearance.Normalise();
        Assert.Equal(Appearance.ComposeMoreIds.Length, appearance.ComposeMoreOrder.Count);
        Assert.Equal(new[] { "find", "quote" }, appearance.ComposeMoreOrder.Take(2));
        Assert.All(Appearance.ComposeMoreIds, id => Assert.Contains(id, appearance.ComposeMoreOrder));

        var copy = appearance.Clone();
        copy.ComposeMoreOrder.Reverse();
        Assert.Equal("find", appearance.ComposeMoreOrder[0]);
        Assert.NotEqual(appearance.ComposeMoreOrder[0], copy.ComposeMoreOrder[0]);
    }

    [Fact]
    public void Quick_templates_expand_only_known_variables_with_current_compose_context()
    {
        var sender = new Account { DisplayName = "Krishna Bhunia", Email = "krishna@example.com" };
        var expanded = ComposeSafety.ExpandQuickTemplate(
            "Hi {{first_name}} {{last_name}} / {{full_name}} ({{recipient_email}}), from {{sender_name}} {{sender_email}}; {{subject}} on {{date}} at {{time}}. {{custom}}",
            sender, "Ananya Roy <ananya@example.com>", "Project update", new DateTimeOffset(2026, 11, 5, 14, 25, 0, TimeSpan.FromHours(5.5)));
        Assert.Contains("Hi Ananya Roy / Ananya Roy (ananya@example.com)", expanded);
        Assert.Contains("from Krishna Bhunia krishna@example.com; Project update", expanded);
        Assert.Contains("05 Nov 2026 at 14:25", expanded);
        Assert.EndsWith("{{custom}}", expanded);
    }

    [Fact]
    public void Quick_templates_leave_missing_recipient_name_visible_and_never_expand_recursively()
    {
        var sender = new Account { DisplayName = "{{sender_email}}", Email = "me@example.com" };
        var expanded = ComposeSafety.ExpandQuickTemplate("{{FIRST_NAME}} {{recipient_email}} {{sender_name}}", sender,
            "unknown@example.com", "", DateTimeOffset.Now);
        Assert.Equal("{{FIRST_NAME}} unknown@example.com {{sender_email}}", expanded);
    }

    [Fact]
    public void Accounts_clone_named_signatures_independently_and_old_default_remains()
    {
        var original = new Account
        {
            SignatureHtml = "<p>Best wishes</p>",
            SignatureVariants = new List<NamedSignature> { new() { Name = "Formal", Html = "<p>Regards</p>" } },
        };
        var copy = original.Clone();
        copy.SignatureVariants[0].Name = "Casual";
        Assert.Equal("Formal", original.SignatureVariants[0].Name);
        Assert.Contains("Best wishes", Composer.SignatureHtml(original, reply: false));
        Assert.Contains("replaceSignatureBlock(blockHtml)", EditorPage.Html);
    }

    [Theory]
    [InlineData("photo.JPG", "image")]
    [InlineData("receipt.pdf", "pdf")]
    [InlineData("notes.md", "text")]
    [InlineData("transaction.csv", "text")]
    [InlineData("notes.docx", "office")]
    [InlineData("budget.xlsx", "office")]
    [InlineData("deck.pptx", "office")]
    [InlineData("document.docx", "office")]
    [InlineData("dangerous.exe", "none")]
    [InlineData("evil.pdf.exe", "none")]
    public void Compose_preview_only_accepts_known_safe_types(string file, string type) =>
        Assert.Equal(type, ComposeSafety.AttachmentPreviewKind(file));

    [Theory]
    [InlineData("report.pdf", "application/pdf", "pdf")]
    [InlineData("photo.jpg", "image/jpeg", "image")]
    [InlineData("notes.txt", "text/plain", "text")]
    [InlineData("data.json", "application/json", "text")]
    [InlineData("photo.jpg", "application/x-msdownload", "none")]
    [InlineData("archive.zip", "application/zip", "none")]
    [InlineData("report.pdf", "text/html", "none")]
    [InlineData("contract.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "office")]
    [InlineData("budget.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "office")]
    [InlineData("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", "office")]
    [InlineData("deck.pptx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "none")]
    [InlineData("contract.docx", "application/zip", "none")]
    public void Forwarded_preview_requires_approved_type_and_filename(string file, string mime, string expected) =>
        Assert.Equal(expected, ComposeSafety.ForwardedAttachmentPreviewKind(file, mime));

    [Fact]
    public void Word_preview_extracts_text_only_from_a_valid_open_xml_package()
    {
        using var stream = WordPackage("""
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
            <w:body><w:p><w:r><w:t>Hello </w:t><w:t>Krishna</w:t></w:r></w:p>
            <w:p><w:r><w:t>Second paragraph</w:t></w:r></w:p></w:body></w:document>
            """);
        var preview = OfficeTextPreview.ReadWordDocument(stream);
        Assert.Contains("Hello Krishna", preview);
        Assert.Contains("Second paragraph", preview);
        Assert.DoesNotContain("<w:", preview);
    }

    [Fact]
    public void Word_preview_rejects_xml_with_dtd()
    {
        using var stream = WordPackage("<!DOCTYPE doc [<!ENTITY example 'unsafe'>]><doc>&example;</doc>");
        Assert.ThrowsAny<XmlException>(() => OfficeTextPreview.ReadWordDocument(stream));
    }

    [Fact]
    public void PowerPoint_preview_extracts_slide_text_in_numeric_order()
    {
        const string slideNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";
        using var stream = OfficePackage(
            ("ppt/slides/slide10.xml", $"<a:slide xmlns:a=\"{slideNamespace}\"><a:p><a:t>Ten</a:t></a:p></a:slide>"),
            ("ppt/slides/slide2.xml", $"<a:slide xmlns:a=\"{slideNamespace}\"><a:p><a:t>Two</a:t></a:p></a:slide>"));
        var preview = OfficeTextPreview.ReadDocument(stream, "deck.pptx");
        Assert.True(preview.IndexOf("[Slide 2]", StringComparison.Ordinal) < preview.IndexOf("[Slide 10]", StringComparison.Ordinal));
        Assert.Contains("Two", preview);
        Assert.Contains("Ten", preview);
    }

    [Fact]
    public void Excel_preview_extracts_shared_and_inline_strings_without_formulas()
    {
        const string sheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using var stream = OfficePackage(
            ("xl/sharedStrings.xml", $"<sst xmlns=\"{sheetNamespace}\"><si><t>Transaction</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{sheetNamespace}\"><sheetData><row r=\"1\">" +
                "<c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>125</v></c>" +
                "<c r=\"C1\" t=\"inlineStr\"><is><t>Pending</t></is></c></row></sheetData></worksheet>"));
        var preview = OfficeTextPreview.ReadDocument(stream, "budget.xlsx");
        Assert.Contains("A1: Transaction", preview);
        Assert.Contains("B1: 125", preview);
        Assert.Contains("C1: Pending", preview);
    }

    private static MemoryStream WordPackage(string documentXml) => OfficePackage(("word/document.xml", documentXml));

    private static MemoryStream OfficePackage(params (string Path, string Xml)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var output = new StreamWriter(entry.Open());
                output.Write(xml);
            }
        }
        stream.Position = 0;
        return stream;
    }

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
        Assert.Contains("function tableGrid(table)", EditorPage.Html);
        Assert.Contains("function editMergedTableCell(action,cell,table)", EditorPage.Html);
        Assert.Contains("action==='mergeRight'||action==='mergeDown'", EditorPage.Html);
        Assert.Contains("action==='splitCell'", EditorPage.Html);
        Assert.Contains("post({t:'tableMenu'})", EditorPage.Html);
        Assert.Contains("function setEditorSpellcheck(enabled)", EditorPage.Html);
        Assert.Contains("function setParagraphStyle(style)", EditorPage.Html);
        Assert.Contains("['p','h1','h2','h3','pre'].includes(style)", EditorPage.Html);
        Assert.Contains("function setEditorPlainText(enabled,notify)", EditorPage.Html);
        Assert.Contains("ed.contentEditable='plaintext-only'", EditorPage.Html);
    }

    [Fact]
    public void Plain_text_draft_sends_only_text_plain_and_remains_plain_after_reopening()
    {
        var account = new Account { Email = "sender@example.com" };
        var draft = new Draft
        {
            To = "recipient@example.com", Subject = "Plain text", IsPlainText = true,
            Html = "<p>Hello <b>there</b></p><p>Next line</p>",
        };
        var outgoing = Composer.Build(draft, account);
        Assert.Null(outgoing.HtmlBody);
        Assert.Contains("Hello there", outgoing.TextBody);
        Assert.Contains("Next line", outgoing.TextBody);
        var reopened = Composer.FromMime(outgoing, "account-one", "");
        Assert.True(reopened.IsPlainText);
        Assert.Contains("Hello", reopened.Html);
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
