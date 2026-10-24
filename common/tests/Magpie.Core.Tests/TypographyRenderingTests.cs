using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

public class TypographyRenderingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-24T10:00:00Z", CultureInfo.InvariantCulture);
    private static RenderMessage Message(MessageBody? body = null) => new()
    {
        Row = Rows.Make("Primary", 1, "thread", from: "maya@example.com", subject: "Agenda"),
        Body = body ?? new MessageBody { Html = "<p style=\"font-family:Georgia;font-size:22px\">Hello</p>" },
        Expanded = true,
    };

    [Fact]
    public void Built_in_reader_does_not_emit_font_overrides_or_rewrite_sender_content()
    {
        var settings = new TypographySettings();
        Assert.Equal("", TypographyCss.ReaderChrome(settings));
        Assert.Equal("", TypographyCss.ReaderBody(settings));
        var body = new MessageBody { Html = "<h1 style=\"font-family:Georgia;font-size:32px\">Agenda</h1>" };
        var before = body.Html;
        var page = HtmlRenderer.BuildConversation("Agenda", new[] { Message(body) }, false, Now, typography: settings);
        Assert.Equal(before, body.Html);
        Assert.Contains("Georgia", WebUtility.HtmlDecode(page.Html));
        Assert.DoesNotContain("html,body,body *{font-family", page.Html);
    }

    [Fact]
    public void Received_display_fonts_beat_inline_important_without_losing_bold_italic_or_source_html()
    {
        const string html = "<style>#greeting{font:italic 60px Georgia!important}</style><p id=\"greeting\" style=\"font-family:Georgia!important;font-size:60px!important;font-weight:700;font-style:italic\">Hello <em>again</em></p>";
        var settings = new TypographySettings { OverrideReceivedFonts = true };
        settings.Styles["readBody"] = new TextStyle { Family = "Arial", Size = 18.5 };
        var rendered = TypographyCss.ReaderContent(html, settings);
        var document = new HtmlParser().ParseDocument(rendered);
        var paragraph = document.QuerySelector("#greeting")!;
        Assert.EndsWith("font-family:\"Arial\",\"Segoe UI\",Arial,sans-serif!important;font-size:18.5px!important;", paragraph.GetAttribute("style"));
        Assert.Contains("font-weight:700", paragraph.GetAttribute("style"));
        Assert.Contains("font-style:italic", paragraph.GetAttribute("style"));
        Assert.NotNull(paragraph.QuerySelector("em"));
        Assert.Contains("font-size:18.5px!important", paragraph.QuerySelector("em")!.GetAttribute("style"));
        Assert.Equal(html, TypographyCss.ReaderContent(html, new TypographySettings()));
    }

    [Fact]
    public void Reader_chrome_overrides_only_selected_role_with_culture_independent_size()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var settings = new TypographySettings();
            settings.Styles["readSender"] = new TextStyle { Family = "Georgia", Size = 18.5 };
            settings.Styles["fileInfo"] = new TextStyle { Family = "Arial", Size = 11 };
            var css = TypographyCss.ReaderChrome(settings);
            Assert.Contains(".name{font-family:\"Georgia\"", css);
            Assert.Contains("font-size:18.5px", css);
            Assert.Contains(".att .sz{font-family:\"Arial\"", css);
            Assert.DoesNotContain(".addr", css);
            Assert.DoesNotContain(".filename", css);
            Assert.Equal("", TypographyCss.ReaderBody(settings));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Received_font_switch_is_display_only_and_keeps_script_sandbox()
    {
        var settings = new TypographySettings { OverrideReceivedFonts = true };
        settings.Styles["readBody"] = new TextStyle { Family = "Arial", Size = 18 };
        settings.Styles["readH2"] = new TextStyle { Family = "Georgia", Size = 24.5 };
        var css = TypographyCss.ReaderBody(settings);
        Assert.Contains("html,body,body *{font-family:\"Arial\"", css);
        Assert.Contains("h2,h2 *{font-family:\"Georgia\"", css);
        Assert.Contains("font-size:24.5px!important", css);
        Assert.Contains("blockquote,blockquote *", css);
        var body = new MessageBody { Html = "<h2>Agenda</h2><p>Hello</p><script>bad()</script>" };
        var original = body.Html;
        var page = HtmlRenderer.BuildConversation("Agenda", new[] { Message(body) }, false, Now, typography: settings);
        Assert.Equal(original, body.Html);
        Assert.Contains("html,body,body *{font-family", WebUtility.HtmlDecode(page.Html));
        Assert.DoesNotContain("allow-scripts", page.Html);
        Assert.DoesNotContain("bad()", page.Html);
    }

    [Fact]
    public void Reader_cache_changes_for_display_fonts_and_switch_but_ignores_unrelated_writing_settings()
    {
        var settings = new TypographySettings();
        var messages = new[] { Message() };
        string Key() => ReaderPage.ContentFingerprint("Agenda", messages, false, false, 600, Now, typography: settings);
        var initial = Key();
        settings.Styles["composeBody"] = new TextStyle { Family = "Georgia", Size = 19 };
        Assert.Equal(initial, Key());
        settings.Styles["readSender"] = new TextStyle { Family = "Georgia", Size = 19 };
        var chrome = Key();
        Assert.NotEqual(initial, chrome);
        settings.OverrideReceivedFonts = true;
        var body = Key();
        Assert.NotEqual(chrome, body);
        settings.Styles["readH1"] = new TextStyle { Family = "Arial", Size = 31.5 };
        Assert.NotEqual(body, Key());
    }

    [Fact]
    public void Attachment_filename_and_details_have_independent_selectors()
    {
        var body = new MessageBody { Html = "<p>Agenda</p>", Attachments = { new AttachmentInfo { FileName = "notes.pdf", Size = 2048, Index = 0 } } };
        var page = HtmlRenderer.BuildConversation("Agenda", new[] { Message(body) }, false, Now);
        Assert.Matches(new Regex("class=\"filename\">notes\\.pdf</span>"), page.Html);
        Assert.Contains("class=\"sz\"", page.Html);
    }

    [Fact]
    public void Css_family_encoding_cannot_close_style_or_inject_markup()
    {
        var css = TypographyCss.Family("A\"</style><script>&\\\n");
        Assert.DoesNotContain("<", css);
        Assert.DoesNotContain(">", css);
        Assert.DoesNotContain("&", css);
        Assert.DoesNotContain("\n", css);
        Assert.Contains("\\3c ", css);
        Assert.EndsWith(",\"Segoe UI\",Arial,sans-serif", css);
    }

    [Fact]
    public void Writing_helper_serializes_three_independent_styles_and_preserve_existing_flag()
    {
        var settings = new TypographySettings();
        settings.Styles["signature"] = new TextStyle { Family = "Georgia", Size = 16.5 };
        var script = EditorPage.TypographyScript(settings, true);
        Assert.Equal("setTypography({\"family\":\"Arial\",\"size\":18,\"overrideExisting\":false},{\"family\":\"Georgia\",\"size\":16.5,\"overrideExisting\":true},{\"family\":\"Arial\",\"size\":18,\"overrideExisting\":false},true)", script);
        Assert.EndsWith(",false)", EditorPage.TypographyScript(settings, false));
    }
}
