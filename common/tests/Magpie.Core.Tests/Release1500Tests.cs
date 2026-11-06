using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using MimeKit;

namespace Magpie.Core.Tests;

/// <summary>CW1 (15.0.0): the full compose window (Q90) — headers, recipients, attachments, signatures, settings, editor page.</summary>
public class Release1500Tests
{
    private static Account Gmail() => new() { Email = "k@gmail.com", DisplayName = "Krishna", Kind = AccountKind.Gmail };

    // ───────────────────────── D1 / D2 / A5 / B16: what goes out ─────────────────────────

    [Fact]
    public void High_priority_sets_the_three_headers_and_normal_sets_none()
    {
        var high = Composer.Build(new Draft { To = "a@b.com", Subject = "x", Html = "<p>hi</p>", Priority = MailPriority.High }, Gmail());
        Assert.StartsWith("1", high.Headers["X-Priority"]);
        Assert.Equal("High", high.Headers["Importance"]);
        Assert.Equal("High", high.Headers["X-MSMail-Priority"]);
        var low = Composer.Build(new Draft { To = "a@b.com", Html = "<p>hi</p>", Priority = MailPriority.Low }, Gmail());
        Assert.StartsWith("5", low.Headers["X-Priority"]);
        Assert.Equal("Low", low.Headers["Importance"]);
        var normal = Composer.Build(new Draft { To = "a@b.com", Html = "<p>hi</p>" }, Gmail());
        Assert.Null(normal.Headers["X-Priority"]);
        Assert.Null(normal.Headers["Importance"]);
    }

    [Fact]
    public void Read_receipt_and_reply_to_go_out_only_when_set()
    {
        var msg = Composer.Build(new Draft { To = "a@b.com", Html = "<p>hi</p>", ReadReceipt = true, ReplyTo = "Work <me@work.example>" }, Gmail());
        Assert.Contains("k@gmail.com", msg.Headers["Disposition-Notification-To"]);
        Assert.Equal("me@work.example", msg.ReplyTo.Mailboxes.Single().Address);
        var plain = Composer.Build(new Draft { To = "a@b.com", Html = "<p>hi</p>" }, Gmail());
        Assert.Null(plain.Headers["Disposition-Notification-To"]);
        Assert.Empty(plain.ReplyTo);
    }

    [Fact]
    public void Plain_text_message_has_no_html_part()
    {
        var msg = Composer.Build(new Draft { To = "a@b.com", Html = "<p>Hello <b>there</b></p>", PlainText = true }, Gmail());
        Assert.Null(msg.HtmlBody);
        Assert.Contains("Hello there", msg.TextBody);
        var rich = Composer.Build(new Draft { To = "a@b.com", Html = "<p>Hello <b>there</b></p>" }, Gmail());
        Assert.Contains("<b>there</b>", rich.HtmlBody);
    }

    [Fact]
    public void Priority_receipt_reply_to_and_plain_text_come_back_when_a_draft_is_reopened()
    {
        var d = new Draft { To = "a@b.com", Subject = "s", Html = "<p>hi</p>", Priority = MailPriority.High, ReadReceipt = true, ReplyTo = "me@work.example" };
        var again = Composer.FromMime(Composer.FromBytes(Composer.ToBytes(Composer.Build(d, Gmail()))), "acc", "t");
        Assert.Equal(MailPriority.High, again.Priority);
        Assert.True(again.ReadReceipt);
        Assert.Equal("me@work.example", again.ReplyTo);
        Assert.False(again.PlainText);
        var plain = Composer.FromMime(Composer.FromBytes(Composer.ToBytes(Composer.Build(new Draft { To = "a@b.com", Html = "<p>hi</p>", PlainText = true }, Gmail()))), "acc", "t");
        Assert.True(plain.PlainText);
    }

    [Fact]
    public void Plain_text_draft_keeps_its_signature_and_quote_boundaries_but_the_sent_message_is_text_only()
    {
        var html = "<p>new words</p><div class=\"magpie-signature\"><br><p>Me</p></div><div class=\"magpie-quote\"><p>On x, Ann wrote:</p><blockquote><p>q</p></blockquote></div>";
        var d = new Draft { To = "a@b.com", Subject = "s", Html = html, PlainText = true };
        // Saved as a draft: both parts + the flag, so reopening finds the protected blocks and stays plain text.
        var draft = Composer.Build(d, Gmail(), forDraft: true);
        Assert.NotNull(draft.HtmlBody);
        Assert.Equal("yes", draft.Headers[Composer.PlainTextDraftHeader]);
        var again = Composer.FromMime(Composer.FromBytes(Composer.ToBytes(draft)), "acc", "t");
        Assert.True(again.PlainText);
        Assert.Contains("magpie-signature", again.Html);
        Assert.Contains("magpie-quote", again.Html);
        // Sent: text only, no flag header.
        var sent = Composer.Build(d, Gmail());
        Assert.Null(sent.HtmlBody);
        Assert.False(sent.Headers.Contains(Composer.PlainTextDraftHeader));
        Assert.Contains("new words", sent.TextBody);
    }

    [Theory]
    [InlineData("1 (Highest)", "", "", MailPriority.High)]
    [InlineData("2", "", "", MailPriority.High)]
    [InlineData("3 (Normal)", "", "", MailPriority.Normal)]
    [InlineData("5 (Lowest)", "", "", MailPriority.Low)]
    [InlineData("", "high", "", MailPriority.High)]
    [InlineData("", "", "Low", MailPriority.Low)]
    [InlineData("", "", "", MailPriority.Normal)]
    [InlineData("urgent", "", "", MailPriority.Normal)]
    public void Received_priority_is_read_from_any_of_the_three_headers(string x, string imp, string ms, MailPriority expected)
    {
        Assert.Equal(expected, Composer.PriorityOf(x, imp, ms));
        Assert.Equal(expected == MailPriority.High, Composer.IsHighPriority(x, imp, ms));
    }

    [Fact]
    public void High_priority_flag_survives_a_flag_update_from_the_server()
    {
        var merged = AccountSync.Merge(MessageFlags.HighPriority | MessageFlags.Seen, MailKit.MessageFlags.Flagged);
        Assert.True(merged.HasFlag(MessageFlags.HighPriority));
        Assert.True(merged.HasFlag(MessageFlags.Flagged));
        Assert.False(merged.HasFlag(MessageFlags.Seen));
    }

    [Fact]
    public void Eml_export_is_the_same_message_send_would_build()
    {
        var d = new Draft { To = "a@b.com", Bcc = "secret@b.com", Subject = "File", Html = "<p>hi</p>" };
        var msg = MimeMessage.Load(new MemoryStream(Composer.ToEml(d, Gmail())));
        Assert.Equal("File", msg.Subject);
        Assert.Equal("secret@b.com", msg.Bcc.Mailboxes.Single().Address);
    }

    // ───────────────────────── A6 / C2 / C3 / D8 ─────────────────────────

    [Fact]
    public void Duplicates_in_a_field_are_merged_keeping_the_first_spelling()
    {
        Assert.Equal("Anita <a@x.com>, b@x.com", Composer.MergeDuplicates("Anita <a@x.com>; b@x.com, A@X.COM, b@x.com"));
        Assert.Equal("", Composer.MergeDuplicates(""));
        Assert.Equal(3, Composer.VisibleRecipients("a@x.com, b@x.com", "B@x.com, c@x.com"));
        Assert.Equal(20, Composer.ManyRecipients);
    }

    [Fact]
    public void Attachment_limit_follows_the_kind_of_account_unless_set()
    {
        Assert.Equal(25L * 1024 * 1024, Composer.AttachmentLimit(Gmail()));
        Assert.Equal(20L * 1024 * 1024, Composer.AttachmentLimit(new Account { Kind = AccountKind.Microsoft }));
        Assert.Equal(25L * 1024 * 1024, Composer.AttachmentLimit(new Account { Kind = AccountKind.Imap }));
        Assert.Equal(40L * 1024 * 1024, Composer.AttachmentLimit(new Account { Kind = AccountKind.Imap, AttachmentLimitMb = 40 }));
        Assert.Equal(25L * 1024 * 1024, Composer.AttachmentLimit(null));
    }

    [Fact]
    public void Attached_bytes_count_files_carried_parts_and_inline_pictures()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, new byte[1000]);
            var html = "<p>x</p><img src=\"data:image/png;base64," + Convert.ToBase64String(new byte[300]) + "\">";
            var total = Composer.AttachedBytes(new[] { tmp, "C:\\missing\\file.bin" }, new long[] { 50 }, html);
            Assert.Equal(1000 + 50 + 300, total);
        }
        finally { File.Delete(tmp); }
    }

    [Theory]
    [InlineData("Please find the numbers attached.", true)]
    [InlineData("See the attachment for details", true)]
    [InlineData("PFA the invoice", true)]
    [InlineData("I enclosed the form", true)]
    [InlineData("I will attach it tomorrow", true)]
    [InlineData("Nothing to see here", false)]
    [InlineData("", false)]
    public void Forgot_attachment_check_looks_for_the_usual_words(string text, bool expected) => Assert.Equal(expected, Composer.MentionsAttachment(text));

    [Fact]
    public void Word_count_counts_words_not_punctuation()
    {
        Assert.Equal(0, Composer.WordCount(""));
        Assert.Equal(4, Composer.WordCount("Hello, world — it's 2026!"));
        Assert.Equal(3, Composer.WordCount("one\ntwo\n\nthree"));
    }

    // ───────────────────────── B9 / D3 / D4 ─────────────────────────

    [Fact]
    public void Picture_file_becomes_an_inline_image_and_is_sent_as_cid()
    {
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        try
        {
            File.WriteAllBytes(tmp, new byte[] { 137, 80, 78, 71, 1, 2, 3 });
            var html = Composer.PictureHtml(tmp);
            Assert.StartsWith("<img src=\"data:image/png;base64,", html);
            Assert.True(Composer.IsPicture(tmp));
            Assert.False(Composer.IsPicture("a.docx"));
            var msg = Composer.Build(new Draft { To = "a@b.com", Html = "<p>pic</p>" + html }, Gmail());
            Assert.Contains("cid:", msg.HtmlBody);
            Assert.Contains(msg.BodyParts.OfType<MimePart>(), p => p.ContentType.MediaType == "image");
            Assert.Throws<InvalidOperationException>(() => Composer.PictureHtml("x.docx"));
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void Signature_can_be_swapped_or_removed_in_the_html()
    {
        var html = "<p>Hi</p><div class=\"magpie-signature\"><br><p>Old</p></div><div class=\"magpie-quote\"><p>On x wrote:</p></div>";
        var swapped = Composer.ReplaceSignature(html, "<p>New</p>");
        Assert.Contains("<p>New</p>", swapped);
        Assert.DoesNotContain("Old", swapped);
        Assert.Contains("magpie-quote", swapped);
        var removed = Composer.ReplaceSignature(html, "");
        Assert.DoesNotContain("magpie-signature", removed);
        Assert.Contains("magpie-quote", removed);
        var added = Composer.ReplaceSignature("<p>Hi</p><div class=\"magpie-quote\"><p>q</p></div>", "<p>S</p>");
        Assert.True(added.IndexOf("magpie-signature", StringComparison.Ordinal) < added.IndexOf("magpie-quote", StringComparison.Ordinal));
        Assert.EndsWith("</div>", Composer.ReplaceSignature("<p>Hi</p>", "<p>S</p>"));
    }

    [Fact]
    public void Pasted_picture_types_map_to_fixed_extensions_and_never_to_a_path()
    {
        Assert.Equal("png", Composer.PastedPictureExtension("image/png"));
        Assert.Equal("jpg", Composer.PastedPictureExtension("IMAGE/JPEG"));
        Assert.Equal("webp", Composer.PastedPictureExtension("image/webp;charset=binary"));
        Assert.Null(Composer.PastedPictureExtension("image/x\\..\\..\\Startup\\p.cmd"));
        Assert.Null(Composer.PastedPictureExtension("image/svg+xml"));
        Assert.Null(Composer.PastedPictureExtension("text/html"));
        Assert.Null(Composer.PastedPictureExtension(null));
        var dir = Path.Combine(Path.GetTempPath(), "magpie-paste-test");
        Assert.True(Composer.IsInside(dir, Path.Combine(dir, "picture-1.png")));
        Assert.False(Composer.IsInside(dir, Path.Combine(dir, "..", "picture-1.png")));
        Assert.False(Composer.IsInside(dir, Path.Combine(dir, "sub", "picture-1.png")));
    }

    [Fact]
    public void Quote_header_encodes_the_senders_name_and_address()
    {
        var row = Rows.Make("acc", 1, "t1");
        row.FromName = "<img src=x onerror=\"alert(1)\">";
        row.FromAddress = "a@b.c";
        var q = Composer.QuoteBlock(row, new MessageBody { Html = "<p>x</p>" });
        Assert.DoesNotContain("<img", q);
        Assert.Contains("&lt;img src=x onerror=", q);
        row.FromName = "";
        row.FromAddress = "<b>a@b.c</b>";
        Assert.DoesNotContain("<b>", Composer.QuoteBlock(row, null));
    }

    [Fact]
    public void Editor_shortcuts_yield_to_keys_the_app_took_and_font_size_touches_only_the_selection()
    {
        // Mac: the menu bar's ⌘ keys are taken by MacMenus.KeyForwardScript (preventDefault) before the editor sees them.
        Assert.Contains("if(e.defaultPrevented) return;", EditorPage.ForMac);
        // Font size: elements that were already size 7 / xxx-large (reopened draft, quote) are left alone.
        Assert.Contains("before=new Set(ed.querySelectorAll(sel))", EditorPage.Html);
        Assert.Contains("if(before.has(el)) return;", EditorPage.Html);
    }

    [Fact]
    public void Recipient_entries_are_quoted_so_names_with_separators_stay_one_recipient()
    {
        Assert.Equal("a@b.com", Composer.FormatAddress("", "a@b.com"));
        Assert.Equal("a@b.com", Composer.FormatAddress("a@b.com", "a@b.com"));
        Assert.Equal("Ann <a@b.com>", Composer.FormatAddress("Ann", "a@b.com"));
        foreach (var name in new[] { "Sales; Support", "Bhunia, Krishna", "Ann \"Boss\" Lee" })
        {
            var entry = Composer.FormatAddress(name, "a@b.com");
            var pieces = Composer.SplitAddresses(entry);
            Assert.Single(pieces);
            Assert.Empty(Composer.InvalidAddresses(entry));
            var (n, a) = Composer.NameAndAddress(pieces[0]);
            Assert.Equal(name, n);
            Assert.Equal("a@b.com", a);
        }
    }

    [Fact]
    public void Signature_swap_leaves_the_other_persons_signature_in_the_quote_alone()
    {
        // A reply to a message Magpie sent: the quote carries their signature block; the writer has none yet.
        var quote = "<div class=\"magpie-quote\"><p>On x, Ann wrote:</p><blockquote><p>Hello</p><div class=\"magpie-signature\"><br><p>Ann</p></div><p>tail</p></blockquote></div>";
        var html = "<p>Hi</p>" + quote;
        var added = Composer.ReplaceSignature(html, "<p>Me</p>");
        Assert.Contains("<p>Ann</p>", added);
        Assert.Contains("<p>tail</p>", added);
        Assert.True(added.IndexOf("<p>Me</p>", StringComparison.Ordinal) < added.IndexOf("magpie-quote", StringComparison.Ordinal));
        var none = Composer.ReplaceSignature(html, "");
        Assert.Equal(html, none);
        // The editor page does the same: its swap only looks outside the quote.
        var page = EditorPage.Html;
        Assert.Contains("function ownSignature()", page);
        Assert.Contains("closest('.magpie-quote')", page);
    }

    [Fact]
    public void Signature_by_id_falls_back_to_the_default_and_respects_the_switches()
    {
        var a = Gmail();
        a.Signatures.Add(new SignatureDef { Id = "one", Name = "Personal", Html = "<p>One</p>" });
        a.Signatures.Add(new SignatureDef { Id = "two", Name = "Short", Html = "<p>Two</p>" });
        a.DefaultSignatureId = "two";
        a.NormaliseSignatures();
        Assert.Contains("Two", Composer.SignatureHtmlFor(a, null, reply: false));
        Assert.Contains("One", Composer.SignatureHtmlFor(a, "one", reply: false));
        Assert.Contains("Two", Composer.SignatureHtmlFor(a, "unknown", reply: false));
        a.SignatureOnReplies = false;
        Assert.Equal("", Composer.SignatureHtmlFor(a, "one", reply: true));
        Assert.Contains("One", Composer.SignatureHtmlFor(a, "one", reply: false));
    }

    [Fact]
    public void Template_body_is_the_new_text_trimmed() => Assert.Equal("Hi\nthere", Composer.TemplateBody("  Hi\r\nthere \r\n"));

    // ───────────────────────── several signatures per account (D3) ─────────────────────────

    [Fact]
    public void Old_single_signature_becomes_the_first_signature_and_stays_mirrored()
    {
        var a = JsonSerializer.Deserialize<Account>("""{"Email":"k@x.com","SignatureHtml":"<p>Old</p>","Signature":"Old"}""", AppSettings.Json)!;
        a.NormaliseSignatures();
        var sig = Assert.Single(a.Signatures);
        Assert.Equal("Signature", sig.Name);
        Assert.Equal("<p>Old</p>", sig.Html);
        Assert.Equal(sig.Id, a.DefaultSignatureId);
        Assert.Equal("<p>Old</p>", a.SignatureHtml);
        // Editing the default signature updates the mirror an older Magpie reads.
        sig.Html = "<p>New</p>";
        a.NormaliseSignatures();
        Assert.Equal("<p>New</p>", a.SignatureHtml);
        Assert.Equal("New", a.Signature);
    }

    [Fact]
    public void Signatures_get_ids_names_and_a_valid_default()
    {
        var a = new Account { Email = "k@x.com" };
        a.Signatures.Add(new SignatureDef { Id = "", Name = "", Html = "<p>A</p>" });
        a.Signatures.Add(new SignatureDef { Id = "b", Name = " ", Html = "<p>B</p>" });
        a.DefaultSignatureId = "nope";
        a.NormaliseSignatures();
        Assert.All(a.Signatures, s => Assert.False(string.IsNullOrWhiteSpace(s.Id)));
        Assert.Equal("Signature", a.Signatures[0].Name);
        Assert.Equal("Signature 2", a.Signatures[1].Name);
        Assert.Equal(a.Signatures[0].Id, a.DefaultSignatureId);
        Assert.Equal("<p>A</p>", a.SignatureHtml);
        var clone = a.Clone();
        clone.Signatures[0].Html = "changed";
        Assert.Equal("<p>A</p>", a.Signatures[0].Html);
    }

    [Fact]
    public void Settings_load_normalises_signatures_and_compose_block()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{"Accounts":[{"Email":"k@x.com","SignatureHtml":"<p>S</p>"}],"Compose":{"Toolbar":[{"Id":"bold","Visible":false},{"Id":"junk"}],"RecentEmoji":["😀","😀",""]}}""");
            var s = new SettingsStore(path).Load();
            Assert.Single(s.Accounts[0].Signatures);
            Assert.False(s.Compose.Toolbar.First(b => b.Id == "bold").Visible);
            Assert.DoesNotContain(s.Compose.Toolbar, b => b.Id == "junk");
            Assert.Equal(ComposeSettings.ToolbarIds.Count(ComposeSettings.IsButton), s.Compose.Toolbar.Count);
            Assert.Equal("bold", s.Compose.Toolbar[0].Id);   // kept order: the saved ones first, then the rest
            Assert.Single(s.Compose.RecentEmoji);
            Assert.True(s.Compose.CheckSpelling);
            Assert.True(s.Compose.FoldQuote);
            Assert.True(s.Compose.AttachmentCheck);
            Assert.Equal(PastePictureChoice.Ask, s.Compose.PastePicture);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Compose_settings_keep_recent_lists_short_and_newest_first()
    {
        var c = new ComposeSettings();
        for (var i = 0; i < 15; i++) c.NoteAttachment($"C:\\f{i}.txt");
        Assert.Equal(ComposeSettings.MaxRecentAttachments, c.RecentAttachments.Count);
        Assert.Equal("C:\\f14.txt", c.RecentAttachments[0]);
        c.NoteAttachment("c:\\F3.TXT");
        Assert.Equal("c:\\F3.TXT", c.RecentAttachments[0]);
        Assert.Equal(1, c.RecentAttachments.Count(p => p.Equals("c:\\f3.txt", StringComparison.OrdinalIgnoreCase)));
        for (var i = 0; i < 25; i++) c.NoteEmoji(i.ToString());
        Assert.Equal(ComposeSettings.MaxRecentEmoji, c.RecentEmoji.Count);
        Assert.Equal("24", c.RecentEmoji[0]);
        c.Width = 10; c.Height = double.NaN; c.PastePicture = (PastePictureChoice)9;
        c.Normalise();
        Assert.Equal(980, c.Width);
        Assert.Equal(720, c.Height);
        Assert.Equal(PastePictureChoice.Ask, c.PastePicture);
        Assert.All(ComposeSettings.ToolbarIds.Where(ComposeSettings.IsButton), id => Assert.NotEqual(id, ComposeSettings.NameOf(id)));
    }

    [Fact]
    public void Account_limits_are_clamped()
    {
        var a = new Account { AttachmentLimitMb = 999 };
        a.NormaliseSignatures();
        Assert.Equal(150, a.AttachmentLimitMb);
        a.AttachmentLimitMb = -3;
        a.NormaliseSignatures();
        Assert.Equal(0, a.AttachmentLimitMb);
    }

    // ───────────────────────── the editor page ─────────────────────────

    [Fact]
    public void Editor_page_has_the_new_commands_for_both_apps()
    {
        foreach (var fn in new[] { "function insertTable", "function tableCmd", "function findAll", "function replaceAll", "function foldQuote", "function unfoldQuote",
                                   "function mention", "function setSignature", "function setFontSize", "function setBlock", "function insertEmoji", "function insertPicture",
                                   "function setSpell", "function cleanCopy", "function imgSize", "function editLink", "function unlinkAt" })
        {
            Assert.Contains(fn, EditorPage.Html);
            Assert.Contains(fn, EditorPage.ForMac);
        }
        Assert.Contains("(e.ctrlKey || e.metaKey) && e.key === 'Enter'", EditorPage.ForMac);
        Assert.Contains("styleWithCSS", EditorPage.Html);
        // An @mention link is built with DOM APIs and esc() escapes quotes, so a quoted-local-part address can't smuggle an attribute.
        Assert.Contains("replace(/\"/g,'&quot;')", EditorPage.Html);
        Assert.Contains("a.href='mailto:'+address; a.textContent=name;", EditorPage.Html);
        Assert.DoesNotContain("'<a href=\"mailto:'+esc(address)", EditorPage.Html);
    }
}
