using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Magpie.Core.Mail;
using Magpie.Core.Settings;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckEditorPageAsync(string root)
    {
        // Dedicated profile: this never opens the user's mail, browser profile or Magpie data.
        var view = new WebView2();
        var window = new Window { Content = view, Width = 640, Height = 460, ShowActivated = false, Left = -3000, Top = -3000 };
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        view.Loaded += (_, _) => loaded.TrySetResult(true);
        var runtime = "not created";
        var initialization = "not requested";
        var navigation = "not requested";
        var processFailure = "none";
        string State() => $"runtime={runtime}; loaded={view.IsLoaded}; visible={view.IsVisible}; "
            + $"hostHwnd=0x{new WindowInteropHelper(window).Handle.ToInt64():x}; initialization={initialization}; "
            + $"navigation={navigation}; processFailure={processFailure}";
        view.CoreWebView2InitializationCompleted += (_, e) =>
        {
            initialization = e.IsSuccess ? "succeeded" : "failed: " + e.InitializationException;
            Console.WriteLine("EDITOR_SMOKE initialization=" + initialization);
        };
        window.Show();
        try
        {
            // Match production's async Loaded path. Do not poll nested DispatcherFrames while COM completes.
            await WaitEditorAsync(loaded.Task, "WPF host loaded", State);
            Assert(new WindowInteropHelper(window).Handle != IntPtr.Zero && view.IsVisible,
                "Real editor has a loaded, visible native host before initialization");
            var environment = await WaitEditorAsync(CoreWebView2Environment.CreateAsync(null, Path.Combine(root, "editor-profile")),
                "environment creation", State);
            runtime = environment.BrowserVersionString;
            initialization = "pending";
            await WaitEditorAsync(view.EnsureCoreWebView2Async(environment), "controller initialization", State);
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            view.CoreWebView2.ProcessFailed += (_, e) =>
            {
                processFailure = e.ProcessFailedKind.ToString();
                ready.TrySetException(new InvalidOperationException("WebView2 editor process failed: " + processFailure));
                Console.Error.WriteLine("EDITOR_SMOKE " + State());
            };
            view.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                navigation = e.IsSuccess ? "succeeded" : "failed: " + e.WebErrorStatus;
                if (!e.IsSuccess) ready.TrySetException(new InvalidOperationException("WebView2 editor navigation " + navigation));
            };
            view.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (message.RootElement.GetProperty("t").GetString() == "ready") ready.TrySetResult(true);
            };
            navigation = "pending";
            view.CoreWebView2.NavigateToString(EditorPage.Html);
            await WaitEditorAsync(ready.Task, "editor navigation and ready message", State);
            var scriptNumber = 0;
            Task<string> Script(string script)
            {
                return WaitEditorAsync(view.CoreWebView2.ExecuteScriptAsync(script), "script " + ++scriptNumber, State);
            }
            const string original = "<p>My own opening.</p><div class=\"magpie-signature\"><p>Regards, Example User</p></div><div class=\"magpie-quote\"><blockquote>Original quoted message</blockquote></div>";
            await Script("setHtml(" + JsonSerializer.Serialize(original) + ")");
            await Script(EditorPage.TypographyScript(new TypographySettings(), false));
            using (var style = JsonDocument.Parse(await Script("JSON.stringify({family:getComputedStyle(ed.firstElementChild).fontFamily,size:getComputedStyle(ed.firstElementChild).fontSize,weight:getComputedStyle(ed.firstElementChild).fontWeight})")))
            using (var values = JsonDocument.Parse(style.RootElement.GetString()!))
                Assert(values.RootElement.GetProperty("family").GetString()!.Contains("Arial")
                    && values.RootElement.GetProperty("size").GetString() == "18px"
                    && values.RootElement.GetProperty("weight").GetString() == "400", "Real editor defaults are Arial18 normal");
            var typed = JsonSerializer.Deserialize<string>(await Script("getNewText()"))!;
            Assert(typed == "My own opening.", "Real editor excludes protected signature and quote from typed-body detection");
            Task<string> Insert(string expected, string mode) => Script("cmd('replyDraft'," + JsonSerializer.Serialize(JsonSerializer.Serialize(new { text = "Generated answer.", expectedNewText = expected, mode })) + ")");
            Assert(await Insert("stale text", "replaceBody") == "false", "Real editor rejects a stale insertion atomically");
            Assert(JsonSerializer.Deserialize<string>(await Script("getNewText()")) == typed, "Rejected insertion preserves typed text");
            Assert(await Insert(typed, "appendBody") == "true", "Real editor appends after explicit choice");
            var appended = JsonSerializer.Deserialize<string>(await Script("getNewText()"))!;
            Assert(appended.StartsWith("My own opening.") && appended.Contains("Generated answer."), "Append retains the user's opening");
            var afterAppend = JsonSerializer.Deserialize<string>(await Script("getHtml()"))!;
            Assert(afterAppend.Contains("font-size: 18px") && afterAppend.Split("class=\"magpie-signature\"").Length == 2
                && afterAppend.Split("class=\"magpie-quote\"").Length == 2, "Real HTML serializes explicit type and one signature/quote");
            var latest = JsonSerializer.Deserialize<string>(await Script("getNewText()"))!;
            Assert(await Insert(latest, "replaceBody") == "true" && JsonSerializer.Deserialize<string>(await Script("getNewText()")) == "Generated answer.", "Replace changes only the new body");
            // Replacing the whole document invalidates every old DOM range, even if its HTML is identical.
            await Script("setHtml('<p>Selected text</p><table><tr><td>Cell</td></tr></table><img alt=\"Local fixture\">')");
            await Script("rewrite={range:document.createRange(),html:ed.innerHTML};mentionRange=document.createRange();mentionToken='@old';mentionMenuOpen=true;selectedImage=ed.querySelector('img');selectedTableCell=ed.querySelector('td')");
            await Script("setHtml(ed.innerHTML)");
            Assert(await Script("rewrite===null && mentionRange===null && mentionToken==='' && !mentionMenuOpen && selectedImage===null && selectedTableCell===null") == "true",
                "Whole-draft restoration clears stale rewrite, mention, picture and table selections");
            Assert(await Script("cmd('replaceSelection','Must not insert')") == "false"
                && await Script("!ed.textContent.includes('Must not insert') && saved!==null && ed.contains(saved.startContainer)") == "true",
                "An old rewrite cannot target a restored document and the new caret belongs to the current DOM");
            await Script("setHtml('<p style=\"font:700 24px Georgia\">Existing formatted draft</p>')");
            Assert(await Script("ed.getAttribute('role')") == "\"textbox\""
                && await Script("ed.getAttribute('aria-label')") == "\"Message body\""
                && await Script("ed.getAttribute('aria-multiline')") == "\"true\"",
                "Message editor exposes a named multiline textbox to screen readers");
            Assert(await Script("Array.from(document.styleSheets[0].cssRules).some(r => r.selectorText === '#ed:focus' && r.style.boxShadow.includes('inset'))") == "true",
                "Keyboard focus styling is available for the message editor");
            await Script(EditorPage.TypographyScript(new TypographySettings(), true));
            Assert(await Script("getComputedStyle(ed.firstElementChild).fontSize") == "\"24px\""
                && await Script("getComputedStyle(ed.firstElementChild).fontWeight") == "\"700\"", "Existing draft formatting survives new defaults");
            await Script("setHtml('<p>New text</p><div class=\"magpie-signature\"><p><b>Existing bold signature</b></p></div><div class=\"magpie-quote\"><blockquote><b>Existing bold quote</b></blockquote></div>')");
            await Script(EditorPage.TypographyScript(new TypographySettings(), false));
            Assert(await Script("getComputedStyle(ed.querySelector('.magpie-signature b')).fontWeight") == "\"700\""
                && await Script("getComputedStyle(ed.querySelector('.magpie-quote b')).fontWeight") == "\"700\"", "Reply defaults preserve existing rich signature and quote emphasis");
            await Script("setHtml('<p style=\"font-weight:700\">Typed bold opening</p><div class=\"magpie-signature\"><span style=\"font-family:Georgia;font-size:24px;font-weight:700\">Custom signature</span></div><div class=\"magpie-quote\"><span style=\"font-family:Tahoma;font-size:11px\">Custom quote</span></div>')");
            await Script(EditorPage.TypographyScript(new TypographySettings(), false));
            var changed = new TypographySettings();
            changed.Styles["signature"] = new TextStyle { Family = "Arial", Size = 20 };
            changed.Styles["composeQuote"] = new TextStyle { Family = "Arial", Size = 21 };
            await Script(EditorPage.TypographyScript(changed, false));
            Assert(await Script("getComputedStyle(ed.querySelector('.magpie-signature span')).fontSize") == "\"20px\""
                && await Script("getComputedStyle(ed.querySelector('.magpie-quote span')).fontSize") == "\"21px\"", "Open composer applies independent protected-role overrides");
            await Script(EditorPage.TypographyScript(new TypographySettings(), false));
            Assert(await Script("getComputedStyle(ed.querySelector('.magpie-signature span')).fontSize") == "\"24px\""
                && await Script("getComputedStyle(ed.querySelector('.magpie-quote span')).fontSize") == "\"11px\"", "Reset restores original rich signature and quote fonts in the open editor");
            await Script("ed.firstElementChild.style.fontWeight='700'");
            await Script(EditorPage.TypographyScript(new TypographySettings(), false));
            Assert(await Script("getComputedStyle(ed.firstElementChild).fontWeight") == "\"700\"", "Settings refresh preserves newly typed bold formatting");
            await Script("setHtml('<table><tbody><tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></tbody></table>')");
            await Script("selectedTableCell=ed.querySelector('td')");
            Assert(await Script("editTable('mergeRight')") == "true"
                && await Script("ed.querySelector('tr').cells[0].colSpan") == "2"
                && await Script("ed.querySelector('tr').cells.length") == "1",
                "Table merge-right combines cells without removing other rows");
            Assert(await Script("editTable('mergeDown')") == "false",
                "Table merge-down refuses a non-rectangular selection");
            Assert(await Script("editTable('splitCell')") == "true"
                && await Script("ed.querySelector('tr').cells.length") == "2"
                && await Script("ed.querySelector('tr').textContent.includes('AB')") == "true",
                "Split restores columns and preserves merged content");
            Assert(await Script("editTable('mergeDown')") == "true"
                && await Script("ed.querySelector('td').rowSpan") == "2",
                "Table merge-down combines vertically adjacent cells");
            Assert(await Script("editTable('insertColumnBefore')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).grid[0].length") == "3"
                && await Script("selectedTableCell.rowSpan") == "2",
                "Insert-column in a row-spanned table preserves its merged cell");
            Assert(await Script("editTable('splitCell')") == "true"
                && await Script("ed.querySelectorAll('tr')[1].cells.length") == "3",
                "Split restores row-spanned cells without deleting neighbors");
            Assert(await Script("editTable('deleteTable')") == "true"
                && await Script("ed.querySelector('table')===null") == "true",
                "Delete-table remains available even for table cells previously merged");
            await Script("setHtml('<table><tbody><tr><td><strong>A</strong></td><td>B</td></tr><tr><td>C</td><td>D</td></tr></tbody></table>')");
            await Script("selectedTableCell=ed.querySelector('td')");
            Assert(await Script("editTable('mergeRight')") == "true"
                && await Script("editTable('insertColumnBefore')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).grid[0].length") == "3"
                && await Script("ed.querySelector('strong')?.textContent") == "\"A\"",
                "Insert-column beside a col-spanned cell preserves rich content");
            await Script("selectedTableCell=ed.querySelector('tr').cells[0]");
            Assert(await Script("editTable('deleteColumn')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).grid[0].length") == "2"
                && await Script("ed.querySelector('strong')?.textContent") == "\"A\"",
                "Delete-column removes a blank column without destroying adjacent merged rich content");
            await Script("setHtml('<table><tbody><tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></tbody></table>')");
            await Script("selectedTableCell=ed.querySelector('td')");
            Assert(await Script("editTable('mergeDown')") == "true"
                && await Script("editTable('insertRowBefore')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).rows.length") == "3"
                && await Script("selectedTableCell.rowSpan") == "2",
                "Insert-row before a vertically merged cell retains its span");
            Assert(await Script("editTable('insertRowAfter')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).rows.length") == "4",
                "Insert-row after a vertically merged cell retains a valid grid");
            await Script("selectedTableCell=ed.querySelector('tr').cells[0]");
            Assert(await Script("editTable('deleteRow')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).rows.length") == "3"
                && await Script("ed.querySelector('table').textContent.includes('AC')") == "true",
                "Delete-row in a merged table preserves the other rows and merged content");
            await Script("setHtml('<table><tbody><tr><td>A</td><td>B</td></tr><tr><td>C</td><td>D</td></tr></tbody></table>')");
            await Script("selectedTableCell=ed.querySelector('td')");
            Assert(await Script("editTable('mergeDown')") == "true"
                && await Script("editTable('deleteRow')") == "true"
                && await Script("tableGrid(ed.querySelector('table')).rows.length") == "1"
                && await Script("ed.querySelector('table').textContent.includes('ACD')") == "true",
                "Deleting the anchor row of a row-spanned cell moves its original rich content into the surviving row");
            await Script("setHtml('<p>Example source code</p>')");
            Assert(await Script("setParagraphStyle('h3')") == "true"
                && await Script("ed.querySelector('h3')!==null") == "true",
                "Real editor applies Heading 3 to the current paragraph");
            Assert(await Script("setParagraphStyle('pre')") == "true"
                && await Script("ed.querySelector('pre')!==null") == "true",
                "Real editor applies a semantic code block");
            Assert(await Script("setParagraphStyle('script')") == "false",
                "Paragraph style rejects unsupported HTML tags");
            await Script("setHtml('<p><b>Bold opening</b></p><div class=\"magpie-quote\">Full original quote</div>')");
            Assert(await Script("setEditorPlainText(true)") == "true"
                && await Script("ed.contentEditable") == "\"plaintext-only\""
                && await Script("ed.querySelector('b')===null") == "true",
                "Plain-text switch removes rich formatting from the editor");
            var plainHtml = JsonSerializer.Deserialize<string>(await Script("getHtml()"))!;
            Assert(plainHtml.Contains("Bold opening") && plainHtml.Contains("Full original quote"),
                "Plain-text conversion retains author text and any previously folded original quote");
            Assert(await Script("setEditorPlainText(false)") == "true"
                && await Script("ed.contentEditable") == "\"true\""
                && await Script("ed.querySelector('p')!==null") == "true",
                "Plain-text switch can return to rich editing without restoring removed formatting");
            Console.WriteLine("EDITOR_SMOKE_PASSED scripts=" + scriptNumber);
        }
        finally { view.Dispose(); window.Close(); }
    }

    private static async Task WaitEditorAsync(Task task, string phase, Func<string> state)
    {
        Console.WriteLine("EDITOR_SMOKE phase=" + phase + "; " + state());
        try { await task.WaitAsync(TimeSpan.FromSeconds(45)); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"WebView2 editor did not complete {phase} within 45 seconds. {state()}", ex);
        }
    }

    private static async Task<T> WaitEditorAsync<T>(Task<T> task, string phase, Func<string> state)
    {
        await WaitEditorAsync((Task)task, phase, state);
        return await task;
    }
}
