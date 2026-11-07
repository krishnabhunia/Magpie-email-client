using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Mail;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckAiApplyUndoAsync(Func<string, Task<string>> script)
    {
        const string original = "<p><b>My own opening.</b></p><div class=\"magpie-signature\"><p style=\"font-family:Georgia;font-size:24px\">Regards, <b>Example User</b></p></div><div class=\"magpie-quote\"><blockquote style=\"font-family:Tahoma;font-size:11px\">Original <b>quoted</b> message</blockquote><p>Entire older thread remains.</p></div>";
        static string Read(string value) => JsonSerializer.Deserialize<string>(value) ?? "";
        static string Bounded(string value) => value.Length <= 4000 ? value : value[..4000] + " [truncated]";
        async Task CheckNativeStep(string command, string expected, string label)
        {
            var result = await script("fmt(" + JsonSerializer.Serialize(command) + ")");
            var actual = await script("ed.innerHTML");
            Assert(result == "true" && actual == expected, label + "\nCommand result: " + result
                + "\nExpected HTML: " + Bounded(expected) + "\nActual HTML: " + Bounded(actual));
        }
        foreach (var dark in new[] { false, true })
        foreach (var mode in new[] { "appendBody", "replaceBody" })
        {
            await script("setQuoteFoldingSupported(true);setDark(" + (dark ? "true" : "false") + ");setHtml(" + JsonSerializer.Serialize(original) + ")");
            await script("setTypography({family:'Arial',size:18},{family:'Arial',size:20,overrideExisting:true},{family:'Arial',size:19,overrideExisting:true},false)");
            var before = await script("ed.innerHTML");
            var protectedBefore = await script("JSON.stringify(Array.from(ed.querySelectorAll('.magpie-signature,.magpie-quote')).map(n=>n.outerHTML))");
            var expected = Read(await script("getNewText()"));
            var payload = JsonSerializer.Serialize(new { text = "Generated answer.\n\nSecond paragraph <literal>.", expectedNewText = expected, mode });
            Assert(await script("cmd('replyDraft'," + JsonSerializer.Serialize(payload) + ")") == "true", "AI " + mode + " uses a native edit transaction");
            var after = await script("ed.innerHTML");
            var protectedAfter = await script("JSON.stringify(Array.from(ed.querySelectorAll('.magpie-signature,.magpie-quote')).map(n=>n.outerHTML))");
            Assert(after != before && protectedAfter == protectedBefore,
                "AI " + mode + " preserves exact rich signature and entire folded quote markup (dark=" + dark + ")"
                + "\nBefore HTML: " + Bounded(before) + "\nAfter HTML: " + Bounded(after)
                + "\nExpected protected HTML: " + Bounded(protectedBefore) + "\nActual protected HTML: " + Bounded(protectedAfter));
            await CheckNativeStep("undo", before, "One native Undo restores the exact pre-AI document, including formatting, signature and quote");
            await CheckNativeStep("redo", after, "One native Redo reapplies only the accepted AI document");
            Assert(await script("fmt('insertText',' Later typed note.')") == "true", "A later manual edit remains a separate native edit");
            await CheckNativeStep("undo", after, "Undo of later typing preserves the accepted AI change");
            await CheckNativeStep("undo", before, "The preceding native Undo still reaches the AI change after later typing");
            await CheckNativeStep("redo", after, "Native Redo can restore the AI change independently of later typing");
            await script("setTypography({family:'Arial',size:18},{family:'Arial',size:16,overrideExisting:false},{family:'Arial',size:14,overrideExisting:false},false)");
            var signatureSize = await script("getComputedStyle(ed.querySelector('.magpie-signature p')).fontSize");
            var quoteSize = await script("getComputedStyle(ed.querySelector('.magpie-quote blockquote')).fontSize");
            Assert(signatureSize == "\"24px\"" && quoteSize == "\"11px\"",
                "AI native transactions and Undo/Redo retain protected typography reset records: signature=" + signatureSize + ", quote=" + quoteSize
                + "\nActual HTML: " + Bounded(await script("ed.innerHTML")));
        }
        await script("setHtml('<p>Plain opening</p>');setEditorPlainText(true)");
        var plainBefore = await script("ed.innerText");
        Assert(await script("cmd('replaceBody','Plain generated text\\nwith a second line')") == "true", "Plain-mode AI apply uses native insertText");
        var plainAfter = await script("ed.innerText");
        Assert(await script("fmt('undo')") == "true" && await script("ed.innerText") == plainBefore
            && await script("fmt('redo')") == "true" && await script("ed.innerText") == plainAfter,
            "Plain-mode WebView2 AI replacement participates in real native Undo and Redo");
        await script("setEditorPlainText(false);setHtml('<p><br></p>')");
        Console.WriteLine("AI_APPLY_NATIVE_UNDO_SMOKE_PASSED");
    }

    private static async Task CheckPlainFallbackAiUndoAsync()
    {
        const string original = "<p>My opening.</p><div class=\"magpie-signature\"><p>My signature</p></div><div class=\"magpie-quote\"><blockquote>Quoted text</blockquote></div>";
        foreach (var mode in new[] { "appendBody", "replaceBody" })
        foreach (var reply in new[] { false, true })
        {
            var window = new ComposeWindow(new Draft { AccountId = "A", Html = original }, null);
            var vm = (ComposeViewModel)window.DataContext;
            try
            {
                RecipientInvoke(window, "UsePlainEditor");
                var editor = (TextBox)window.FindName("PlainEditor");
                var initial = editor.Text;
                editor.Select(0, 0);
                editor.SelectedText = "Earlier typed text. ";
                var before = editor.Text;
                var expected = (string)RecipientInvoke(window, "PlainNewText")!;
                var tail = (string)typeof(ComposeWindow).GetField("_plainProtectedTail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var command = reply ? "replyDraft" : mode;
                var value = reply ? JsonSerializer.Serialize(new { text = "Generated answer.", expectedNewText = expected, mode }) : "Generated answer.";
                await (Task)RecipientInvoke(window, "EditorCommandAsync", command, value)!;
                var after = editor.Text;
                Assert(after != before && after.EndsWith(tail, StringComparison.Ordinal) && editor.CanUndo,
                    "Native fallback AI " + command + " changes only the new body and creates a real undo entry");
                RecipientInvoke(window, "OnFormat", new Button { Tag = "undo" }, new RoutedEventArgs());
                Assert(editor.Text == before && editor.CanRedo, "Fallback toolbar Undo restores the exact pre-AI text and protected tail");
                RecipientInvoke(window, "OnFormat", new Button { Tag = "redo" }, new RoutedEventArgs());
                Assert(editor.Text == after, "Fallback toolbar Redo reapplies the accepted AI change");
                editor.Undo();
                editor.Undo();
                Assert(editor.Text == initial, "Applying AI preserves the earlier typing entry in the native fallback undo manager");
            }
            finally
            {
                typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
                vm.Detach();
            }
        }
        Console.WriteLine("PLAIN_FALLBACK_AI_UNDO_SMOKE_PASSED");
    }
}
