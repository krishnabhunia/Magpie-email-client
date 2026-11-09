using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace ProductivitySmoke;

internal static partial class Program
{
    private const string QuoteVisibilityFixture = "<p>Current reply</p><div class=\"magpie-quote\"><p>On Monday Alex wrote:</p><blockquote><p>Complete quoted line</p><p>Last quoted line</p></blockquote></div>";

    private static async Task CheckEditorFindProgressionAsync(Func<string, Task<string>> script)
    {
        async Task Find(string query, bool matchCase, int count, int index, int? offset = null, string? parent = null)
        {
            var call = "(()=>{const hit=findInEditor(" + JsonSerializer.Serialize(query) + "," + (matchCase ? "true" : "false")
                + ");const selection=window.getSelection();const range=selection.rangeCount?selection.getRangeAt(0):null;"
                + "return {count:hit.count,index:hit.index,text:selection.toString(),start:range?.startOffset,end:range?.endOffset,parent:range?.startContainer.parentElement?.tagName};})()";
            using var result = JsonDocument.Parse(await script(call));
            var hit = result.RootElement;
            Assert(hit.GetProperty("count").GetInt32() == count && hit.GetProperty("index").GetInt32() == index,
                $"Find Next returns match {index} of {count} for '{query}'");
            if (offset is { } start)
                Assert(hit.GetProperty("start").GetInt32() == start && hit.GetProperty("end").GetInt32() == start + query.Length
                    && string.Equals(hit.GetProperty("text").GetString(), query, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                    && (parent == null || hit.GetProperty("parent").GetString() == parent),
                    $"Find Next selects the actual match at offset {start}, not just a changed result index");
        }

        Task Select(int start, int end) => script("(()=>{const text=ed.firstElementChild.firstChild;const range=document.createRange();range.setStart(text,"
            + start + ");range.setEnd(text," + end + ");selectEditorRange(range);})()");

        await script("setHtml('<p>alpha alpha alpha</p>')");
        var originalHtml = await script("getHtml()");
        await Find("alpha", true, 3, 1, 0);
        await Find("alpha", true, 3, 2, 6);
        await Find("alpha", true, 3, 3, 12);
        await Find("alpha", true, 3, 1, 0);
        await Find("alpha", true, 3, 2, 6);

        await Select(6, 6);
        await Find("alpha", true, 3, 2, 6);
        await Select(7, 7);
        await Find("alpha", true, 3, 3, 12);
        await Select(17, 17);
        await Find("alpha", true, 3, 1, 0);
        await Select(6, 11);
        await Find("alpha", true, 3, 3, 12);
        await Select(2, 8);
        await Find("alpha", true, 3, 3, 12);
        await Select(0, 6);
        await Find("alpha", true, 3, 2, 6);
        await Find("missing", true, 0, 0);
        Assert(await script("getHtml()") == originalHtml, "Finding, advancing, wrapping and unmatched searches do not edit the message");

        await script("setHtml('<p>aaaa</p>')");
        await Find("aa", true, 2, 1, 0);
        await Find("aa", true, 2, 2, 2);
        await Find("aa", true, 2, 1, 0);

        await script("setHtml('<p><strong>alpha</strong> then <em>alpha</em> then alpha</p>')");
        originalHtml = await script("getHtml()");
        await Find("alpha", true, 3, 1, 0, "STRONG");
        await Find("alpha", true, 3, 2, 0, "EM");
        await Find("alpha", true, 3, 3, 6, "P");
        await Find("alpha", true, 3, 1, 0, "STRONG");
        Assert(await script("getHtml()") == originalHtml, "Find Next crosses formatted text nodes without changing their markup");

        await script("setHtml('<p>Alpha alpha ALPHA</p>')");
        await Find("alpha", false, 3, 1, 0);
        await Find("alpha", false, 3, 2, 6);
        await Find("alpha", false, 3, 3, 12);
        await Find("alpha", true, 1, 1, 6);
        await Find("alpha", true, 1, 1, 6);
        await script("window.getSelection().removeAllRanges();saved=document.createRange();saved.selectNodeContents(document.body)");
        await Find("alpha", false, 3, 1, 0);
        using (var fixture = typeof(Program).Assembly.GetManifestResourceStream("ProductivitySmoke.Fixtures.editor-find-unicode.js")
            ?? throw new InvalidOperationException("Missing Unicode editor-search regression fixture."))
        using (var reader = new StreamReader(fixture))
        using (var result = JsonDocument.Parse(await script(await reader.ReadToEndAsync())))
        {
            var passed = result.RootElement.ValueKind == JsonValueKind.Object
                && result.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
            Assert(passed, "Unicode search/replace must preserve original UTF-16 ranges: " + result.RootElement);
            Assert(result.RootElement.GetProperty("assertions").GetInt32() >= 30,
                "The real editor executed all Unicode find/replace regressions");
        }
        using (var fixture = typeof(Program).Assembly.GetManifestResourceStream("ProductivitySmoke.Fixtures.editor-find-inline.js")
            ?? throw new InvalidOperationException("Missing inline editor-search regression fixture."))
        using (var reader = new StreamReader(fixture))
        using (var result = JsonDocument.Parse(await script(await reader.ReadToEndAsync())))
        {
            var passed = result.RootElement.ValueKind == JsonValueKind.Object
                && result.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
            Assert(passed, "Inline search/replace must map visible runs to original DOM ranges and preserve native history: " + result.RootElement);
            Assert(result.RootElement.GetProperty("assertions").GetInt32() >= 50,
                "The real editor executed the inline, boundary, Unicode and native Undo regressions");
        }
        Console.WriteLine("EDITOR_FIND_PROGRESSION_SMOKE_PASSED");
    }

    private static async Task CheckDefaultQuoteVisibilityAsync(Func<string, Task<string>> script, string host)
    {
        foreach (var dark in new[] { false, true })
        {
            await script("setDark(" + (dark ? "true" : "false") + ");setHtml(" + JsonSerializer.Serialize(QuoteVisibilityFixture) + ")");
            var html = await script("getHtml()");
            Assert(await script("!ed.classList.contains('quote-folded')&&getComputedStyle(ed.querySelector('.magpie-quote blockquote')).display!=='none'&&ed.innerText.includes('Complete quoted line')&&ed.innerText.includes('Last quoted line')") == "true",
                host + ": the complete quoted message stays visible without a host expansion control, in " + (dark ? "dark" : "light") + " mode");
            Assert(await script("toggleQuoteFold()") == "false" && await script("!ed.classList.contains('quote-folded')") == "true"
                && await script("getHtml()") == html,
                host + ": an unsupported fold request cannot hide the quote or alter outgoing HTML");
        }
    }

    private static async Task CheckWindowsQuoteFoldingAsync(Func<string, Task<string>> script)
    {
        await CheckDefaultQuoteVisibilityAsync(script, "Shared editor safe default");
        await script("window.quoteReviewOriginalPost=post;window.quoteReviewDirty=0;post=function(message){if(message.t==='dirty')window.quoteReviewDirty++;window.quoteReviewOriginalPost(message)}");
        try
        {
            Assert(await script("setQuoteFoldingSupported(true)") == "true", "Windows explicitly enables folding because it provides an expansion button");
            await script("setHtml(" + JsonSerializer.Serialize(QuoteVisibilityFixture) + ")");
            var html = await script("getHtml()");
            foreach (var dark in new[] { false, true })
            {
                await script("setDark(" + (dark ? "true" : "false") + ")");
                Assert(await script("ed.classList.contains('quote-folded')&&getComputedStyle(ed.querySelector('.magpie-quote blockquote')).display==='none'") == "true",
                    "Windows starts supported reply quotes folded in " + (dark ? "dark" : "light") + " mode");
                using (var result = JsonDocument.Parse(await script("findInEditor('Complete quoted line',true)")))
                    Assert(result.RootElement.GetProperty("count").GetInt32() == 0, "Find Next does not select text hidden inside a folded quote");
                Assert(await script("toggleQuoteFold()") == "false"
                    && await script("getComputedStyle(ed.querySelector('.magpie-quote blockquote')).display!=='none'&&ed.innerText.includes('Complete quoted line')&&ed.innerText.includes('Last quoted line')") == "true",
                    "The Windows expansion action reveals the complete original quote");
                using (var result = JsonDocument.Parse(await script("findInEditor('Complete quoted line',true)")))
                    Assert(result.RootElement.GetProperty("count").GetInt32() == 1 && result.RootElement.GetProperty("index").GetInt32() == 1
                        && await script("window.getSelection().toString()") == JsonSerializer.Serialize("Complete quoted line"),
                        "Expanded quoted text is searchable and the selected result is visible");
                Assert(await script("getHtml()") == html && await script("toggleQuoteFold()") == "true"
                    && await script("getHtml()") == html,
                    "Expanding and folding are display-only: saved/sent quote HTML is unchanged");
            }
            Assert(await script("setQuoteFoldingSupported(false)") == "false"
                && await script("!ed.classList.contains('quote-folded')&&ed.innerText.includes('Last quoted line')") == "true",
                "Removing host folding support immediately reveals an already-folded quote");
            await script("setQuoteFoldingSupported(true);setHtml(" + JsonSerializer.Serialize(QuoteVisibilityFixture) + ");setEditorPlainText(true,false)");
            Assert(await script("toggleQuoteFold()") == "false"
                && await script("ed.innerText.includes('Complete quoted line')&&ed.innerText.includes('Last quoted line')&&!ed.classList.contains('quote-folded')") == "true",
                "Plain-text conversion preserves all original quote text and cannot fold it again");
            await script("setHtml(" + JsonSerializer.Serialize(QuoteVisibilityFixture) + ")");
            Assert(await script("ed.classList.contains('quote-folded')") == "true" && await script("window.quoteReviewDirty") == "0",
                "A whole-document restore retains Windows folding capability without marking the draft dirty");
        }
        finally
        {
            await script("post=window.quoteReviewOriginalPost;delete window.quoteReviewOriginalPost;delete window.quoteReviewDirty;setHtml('<p><br></p>')");
        }
        Console.WriteLine("EDITOR_WINDOWS_QUOTE_SMOKE_PASSED");
    }

    private static async Task CheckPlainFallbackWhitespaceAsync()
    {
        const string text = "\n\t  indented  line\n\nhttps://example.test/path <literal>\nfinal spaces  \n";
        var window = new ComposeWindow(new Draft { AccountId = "A", Html = MimeText.PlainTextToEditorHtml(text), IsPlainText = true }, null);
        var vm = (ComposeViewModel)window.DataContext;
        static string Lines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
        try
        {
            RecipientInvoke(window, "UsePlainEditor");
            var editor = (TextBox)window.FindName("PlainEditor");
            Assert(Lines(editor.Text) == text, "Native fallback opening a plain draft preserves indentation, blank lines, literal markup and trailing whitespace");
            foreach (var value in new[] { text, "  \t\n\n ", "" })
            {
                editor.Text = value;
                var html = await (Task<string>)RecipientInvoke(window, "GetHtmlAsync")!;
                Assert(Lines(MimeText.EditorHtmlToPlainText(html)) == Lines(editor.Text)
                    && !html.Contains("<a ", StringComparison.OrdinalIgnoreCase),
                    "Native plain fallback serialization preserves exact typed whitespace and does not auto-link text");
            }
            vm.IsPlainText = false;
            editor.Text = "Rich fallback https://example.test/path";
            Assert(await (Task<string>)RecipientInvoke(window, "GetHtmlAsync")! == ComposeViewModel.TextToParagraphs(editor.Text),
                "The existing rich-draft fallback serialization is unchanged");
        }
        finally
        {
            typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            vm.Detach();
        }
        Console.WriteLine("PLAIN_FALLBACK_WHITESPACE_SMOKE_PASSED");
    }

    private static async Task CheckPlainEditorWhitespaceRoundTripAsync(Func<string, Task<string>> script)
    {
        var account = new Account { Id = "A", Email = "sender@example.test" };
        static string Lines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (var text in new[]
        {
            "\n\t  indented  line\n\nhttps://example.test/path <literal> & entity\nfinal spaces  \n",
            "first\nsecond\n\nlast",
            "  \t\n\n ",
            "",
        })
        {
            // The contenteditable plain editor's typed text is serialized as p-per-line HTML for drafts.
            await script("setHtml('<p><br></p>');setEditorPlainText(true,false);ed.textContent=" + JsonSerializer.Serialize(text));
            var html = JsonSerializer.Deserialize<string>(await script("getHtml()"))!;
            Assert(Lines(MimeText.EditorHtmlToPlainText(html)) == text,
                "The real plain editor serializes exact whitespace, including empty/trailing lines");
            var message = Composer.Build(new Draft { AccountId = account.Id, To = "recipient@example.test", Html = html, IsPlainText = true }, account);
            Assert(message.HtmlBody == null && Lines(message.TextBody ?? "") == text,
                "MIME creation preserves real-editor plain text without adding an HTML alternative");
            using var stream = new MemoryStream(Composer.ToBytes(message));
            using var loaded = MimeMessage.Load(stream);
            var reopened = Composer.FromMime(loaded, account.Id, "");
            Assert(reopened.IsPlainText && Lines(loaded.TextBody ?? "") == text,
                "MIME bytes and FromMime preserve the exact plain message and its mode");

            // Match both Windows startup and revision restoration: canonicalize raw p-lines before rendering.
            foreach (var storedHtml in new[] { reopened.Html, html })
            {
                var editorHtml = MimeText.PlainTextToEditorHtml(MimeText.EditorHtmlToPlainText(storedHtml));
                await script("setHtml(" + JsonSerializer.Serialize(editorHtml) + ");setEditorPlainText(true,false)");
                Assert(Lines(JsonSerializer.Deserialize<string>(await script("ed.textContent"))!) == text
                    && await script("ed.contentEditable") == JsonSerializer.Serialize("plaintext-only"),
                    "Reopening a MIME draft or restoring raw paragraph-per-line revision HTML retains exact plain text");
                var savedAgain = JsonSerializer.Deserialize<string>(await script("getHtml()"))!;
                Assert(Lines(MimeText.EditorHtmlToPlainText(savedAgain)) == text,
                    "Saving a reopened/restored plain draft does not accumulate paragraph gaps or trim whitespace");
            }
        }
        await script("setHtml('<p><br></p>')");
        Console.WriteLine("PLAIN_EDITOR_WHITESPACE_ROUNDTRIP_SMOKE_PASSED");
    }
}
