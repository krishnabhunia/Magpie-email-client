using System.Net;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Tests;

public class ComposePlainTextRegressionTests
{
    private static Account Sender() => new()
    {
        Id = "plain-account", Email = "sender@example.test", DisplayName = "Plain Sender", Enabled = false,
    };

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    // Independent fixture for EditorPage.getHtml() in plain mode: every visible line becomes one escaped p,
    // including empty first/last lines. Do not use Composer.TextToParagraphs: that older rich helper trims lines.
    private static string EditorParagraphs(string text) =>
        string.Concat(Normalize(text).Split('\n').Select(line => "<p>" + WebUtility.HtmlEncode(line) + "</p>"));

    private static Draft PlainDraft(string html) => new()
    {
        AccountId = "plain-account", MessageId = "plain-whitespace@example.test", To = "recipient@example.test",
        Subject = "Plain whitespace regression", IsPlainText = true, Html = html,
    };

    public static IEnumerable<object[]> PlainTextSamples()
    {
        yield return new object[] { "" };
        yield return new object[] { " " };
        yield return new object[] { "\t  \t" };
        yield return new object[] { "\n" };
        yield return new object[] { "\n\n\n\n" };
        yield return new object[] { "  leading  repeated   spaces  " };
        yield return new object[] { "\tcolumn one\t\tcolumn two\t" };
        yield return new object[] { "\n  First line  \n\n\n\tLast line\t \n\n" };
        yield return new object[] { "\r\n  First\tline\r\n\r\n\r\nLast  line \r\n" };
        yield return new object[] { "One\rTwo\r\r\tThree\r" };
        yield return new object[] { "  \t\n \n\t\n" };
        yield return new object[] { "Literal <b>bold?</b> & <script>alert('not code')</script>\n&lt;already encoded&gt; &nbsp; &#10;" };
        yield return new object[] { "https://example.test/a?x=1&y=2  mail@example.test\nKeep_this {{subject}} literally." };
        yield return new object[] { "নতুন  লেখা\t中文 👩‍💻\n\u00a0nonbreaking\u00a0space\u00a0\n" };
    }

    [Theory]
    [MemberData(nameof(PlainTextSamples))]
    public void Editor_line_elements_preserve_all_plain_text_except_line_ending_convention(string text)
    {
        Assert.Equal(Normalize(text), MimeText.EditorHtmlToPlainText(EditorParagraphs(text)));
    }

    [Theory]
    [MemberData(nameof(PlainTextSamples))]
    public void Plain_wrapper_roundtrips_literal_text_without_linkification_or_trimming(string text)
    {
        var html = MimeText.PlainTextToEditorHtml(text);
        Assert.Equal(Normalize(text), MimeText.EditorHtmlToPlainText(html));
        Assert.DoesNotContain("<a ", html);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("white-space:pre-wrap", html);
    }

    [Theory]
    [InlineData(" \t\r\n  ", " \t\n  ")]
    [InlineData("<p></p>", "")]
    [InlineData("<p></p><p></p><p></p>", "\n\n")]
    [InlineData("<p>  A\t\t B </p><p></p><p> C  </p>", "  A\t\t B \n\n C  ")]
    [InlineData("<div><p>A  B</p><p>C\tD</p></div>", "A  B\nC\tD")]
    [InlineData("<div> A<br> B<br><br> C </div>", " A\n B\n\n C ")]
    [InlineData("<p>Hello <b>there</b></p><p> Next line </p>", "Hello there\n Next line ")]
    [InlineData("<p>&amp;lt;tag&amp;gt; &lt;literal&gt; &amp; &nbsp;</p>", "&lt;tag&gt; <literal> & \u00a0")]
    [InlineData("<p> A \tB<p> C &lt;x&gt;", " A \tB\n C <x>")]
    [InlineData("<p>A<!--ignored--><span></span>B</p>", "AB")]
    [InlineData("<p>Kept</p><script>do not include()</script><style>secret CSS</style><template>hidden</template><p>  After  </p>", "Kept\n  After  ")]
    public void Compatible_fragments_keep_whitespace_and_decode_entities_once(string html, string expected)
    {
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(html));
    }

    [Fact]
    public void Empty_null_and_deep_markup_have_explicit_safe_behavior()
    {
        Assert.Equal("", MimeText.EditorHtmlToPlainText(null));
        Assert.Equal("", MimeText.EditorHtmlToPlainText(""));
        Assert.Equal("", MimeText.EditorHtmlToPlainText(MimeText.PlainTextToEditorHtml(null)));
        var nested = string.Concat(Enumerable.Repeat("<span>", 300)) + "Keep  this\ttext"
            + string.Concat(Enumerable.Repeat("</span>", 300));
        Assert.Throws<FormatException>(() => MimeText.EditorHtmlToPlainText(nested));
        // The same angle brackets as literal message text are not interpreted as nested markup.
        Assert.Equal(nested, MimeText.EditorHtmlToPlainText(MimeText.PlainTextToEditorHtml(nested)));
    }

    [Theory]
    [MemberData(nameof(PlainTextSamples))]
    public void Plain_mime_save_reopen_and_rebuild_preserve_body_over_repeated_roundtrips(string text)
    {
        var expected = Normalize(text);
        var draft = PlainDraft(EditorParagraphs(text));
        var account = Sender();
        for (var cycle = 0; cycle < 3; cycle++)
        {
            using var built = Composer.Build(draft, account);
            Assert.Null(built.HtmlBody);
            Assert.Equal(expected, Normalize(built.TextBody));
            Assert.DoesNotContain(built.BodyParts, part => part.ContentType.IsMimeType("text", "html"));
            // SMTP may prepare a message for a server without 8BITMIME support before serializing it.
            built.Prepare(EncodingConstraint.SevenBit);
            using var saved = Composer.FromBytes(Composer.ToBytes(built));
            Assert.Null(saved.HtmlBody);
            Assert.Equal(expected, Normalize(saved.TextBody));
            Assert.Equal(expected, Normalize(MimeText.Extract(saved).Text));
            draft = Composer.FromMime(saved, account.Id, "");
            Assert.True(draft.IsPlainText);
            Assert.Equal(expected, MimeText.EditorHtmlToPlainText(draft.Html));
            Assert.DoesNotContain("<a ", draft.Html);
        }
    }

    [Fact]
    public void Entire_plain_signature_and_quote_are_kept_without_collapsing_their_content()
    {
        var draft = PlainDraft("<p>  My  reply\t</p>"
            + "<div class=\"magpie-signature\"><p>-- </p><p>  Full Name\tTeam  </p></div>"
            + "<div class=\"magpie-quote\"><blockquote><p>&gt;  First quoted line  </p><p></p><p>&gt;\tLast quoted line </p></blockquote></div>");
        const string expected = "  My  reply\t\n-- \n  Full Name\tTeam  \n>  First quoted line  \n\n>\tLast quoted line ";
        using var message = Composer.Build(draft, Sender());
        Assert.Equal(expected, Normalize(message.TextBody));
        using var saved = Composer.FromBytes(Composer.ToBytes(message));
        Assert.Equal(expected, Normalize(saved.TextBody));
        var reopened = Composer.FromMime(saved, draft.AccountId, "");
        Assert.True(reopened.IsPlainText);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(reopened.Html));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  Accepted\tmeeting  ")]
    [InlineData("\n  Accepted\tmeeting  \n\n\nThanks  \n")]
    public void Calendar_reply_uses_the_same_whitespace_preserving_plain_body(string expected)
    {
        var draft = PlainDraft(EditorParagraphs(expected));
        draft.CalendarReply = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nMETHOD:REPLY\r\nEND:VCALENDAR\r\n";
        using var built = Composer.Build(draft, Sender());
        Assert.Null(built.HtmlBody);
        Assert.Equal(expected, Normalize(built.TextBody));
        Assert.Contains(built.BodyParts, part => part.ContentType.IsMimeType("text", "calendar"));
        using var saved = Composer.FromBytes(Composer.ToBytes(built));
        Assert.Equal(expected, Normalize(saved.TextBody));
        Assert.Contains(saved.BodyParts, part => part.ContentType.IsMimeType("text", "calendar"));
    }

    [Fact]
    public void Rich_message_text_alternative_keeps_its_existing_normalization()
    {
        var draft = PlainDraft("<p>  Rich\t \ttext  </p><p></p><p>More</p>");
        draft.IsPlainText = false;
        using var built = Composer.Build(draft, Sender());
        Assert.NotNull(built.HtmlBody);
        Assert.Equal(Normalize(MimeText.HtmlToText(draft.Html)), Normalize(built.TextBody));
        Assert.NotEqual(MimeText.EditorHtmlToPlainText(draft.Html), Normalize(built.TextBody));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\t  Main  text \n\n\n-- \n  Signature  \n>  Complete quote\t\n\n")]
    [InlineData("\t  No final newline\t  ")]
    public void Local_saved_mime_reopened_draft_and_restored_history_keep_whitespace_and_attachment_bytes(string expected)
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var account = Sender();
        engine.Config.Accounts.Add(account);
        var draft = PlainDraft(EditorParagraphs(expected));
        var attachmentBytes = new byte[] { 0, 1, 2, 13, 10, 255 };
        draft.CarriedParts.Add(new MimePart("application", "octet-stream")
        {
            FileName = "kept.bin", ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            Content = new MimeContent(new MemoryStream(attachmentBytes)), ContentTransferEncoding = ContentEncoding.Base64,
        });
        var id = engine.SaveLocalDraft(draft, pendingUpload: true);
        Assert.True(id > 0);
        var stored = Assert.Single(engine.LocalDrafts());
        using (var saved = Composer.FromBytes(stored.Mime))
        {
            Assert.Null(saved.HtmlBody);
            Assert.Equal(expected, Normalize(saved.TextBody));
        }
        var reopened = Assert.IsType<Draft>(engine.OpenLocalDraft(id));
        Assert.True(reopened.IsPlainText);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(reopened.Html));
        var revision = Assert.Single(engine.Store.GetDraftRevisions(account.Id, draft.MessageId));

        draft.Html = MimeText.PlainTextToEditorHtml("Changed  body\t");
        Assert.Equal(id, engine.SaveLocalDraft(draft, pendingUpload: true));
        var restored = Assert.IsType<Draft>(engine.Store.OpenDraftRevision(account.Id, draft.MessageId, revision.Id));
        Assert.True(restored.IsPlainText);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(restored.Html));
        using var rebuilt = Composer.Build(restored, account);
        Assert.Null(rebuilt.HtmlBody);
        Assert.Equal(expected, Normalize(rebuilt.TextBody));
        var attachment = Assert.IsType<MimePart>(Assert.Single(rebuilt.Attachments));
        using var content = new MemoryStream();
        attachment.Content.DecodeTo(content);
        Assert.Equal(attachmentBytes, content.ToArray());
    }

    [Fact]
    public void Preserving_plain_body_framing_does_not_reencode_carried_text_or_nested_email()
    {
        const string expected = "\t  Authored body without a final newline  ";
        var draft = PlainDraft(EditorParagraphs(expected));
        var textAttachment = new TextPart("plain")
        {
            Text = "Original  text\tattachment\n", FileName = "notes.txt",
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
            ContentTransferEncoding = ContentEncoding.SevenBit,
        };
        var nested = new MimeMessage
        {
            MessageId = "carried-plain-framing@example.test", Subject = "Original attached email",
            Body = new TextPart("plain") { Text = "Original  message\tcontent", ContentTransferEncoding = ContentEncoding.SevenBit },
        };
        var originalNested = Composer.ToBytes(nested);
        draft.CarriedParts.Add(textAttachment);
        draft.CarriedParts.Add(new MessagePart("rfc822")
        {
            Message = nested, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "original.eml" },
        });

        using var built = Composer.Build(draft, Sender());
        Assert.Equal(expected, Normalize(built.TextBody));
        Assert.Equal(ContentEncoding.SevenBit, textAttachment.ContentTransferEncoding);
        Assert.Equal(originalNested, Composer.ToBytes(nested));
        using var saved = Composer.FromBytes(Composer.ToBytes(built));
        Assert.Equal(expected, Normalize(saved.TextBody));
        var attachments = saved.Attachments.ToArray();
        Assert.Equal(2, attachments.Length);
        var savedText = Assert.IsType<TextPart>(attachments[0]);
        Assert.Equal(ContentEncoding.SevenBit, savedText.ContentTransferEncoding);
        Assert.Equal("Original  text\tattachment\n", Normalize(savedText.Text));
        var savedNested = Assert.IsType<MessagePart>(attachments[1]);
        Assert.Equal(originalNested, Composer.ToBytes(Assert.IsType<MimeMessage>(savedNested.Message)));
    }
}
