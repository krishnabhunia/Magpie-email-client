using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Mail;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckAiBodyBoundaryStateAsync(Func<string, Task<string>> script)
    {
        const string original = "<p>Plain opening</p><div class='magpie-signature'><p>Retained signature</p></div><div class='magpie-quote'><p>Entire retained quote</p></div>";
        static string Read(string json) => JsonSerializer.Deserialize<string>(json) ?? "";
        async Task RejectBodyEdits(string stage)
        {
            var before = await script("ed.innerHTML");
            var expected = Read(await script("getNewText()"));
            foreach (var mode in new[] { "replaceBody", "appendBody", "replyDraft" })
            {
                var value = mode == "replyDraft" ? JsonSerializer.Serialize(new { text = "Generated answer", expectedNewText = expected, mode = "replaceBody" }) : "Generated answer";
                Assert(await script("cmd(" + JsonSerializer.Serialize(mode) + "," + JsonSerializer.Serialize(value) + ")") == "false"
                    && await script("ed.innerHTML") == before,
                    stage + ": " + mode + " refuses to guess the body boundary and leaves every character untouched");
            }
            var allText = Read(await script("ed.textContent"));
            Assert(allText.Contains("Retained signature") && allText.Contains("Entire retained quote"), stage + ": all retained content remains present");
        }

        await script("setHtml(" + JsonSerializer.Serialize(original) + ",false);setEditorPlainText(true,false)");
        Assert(await script("getBodySectionsLost()") == "true", "Plain conversion records that rich section boundaries were lost");
        await RejectBodyEdits("Plain mode");

        // Selection rewrite is still a real browser edit, with the ordinary native Undo/Redo history.
        await script("findInEditor('Plain opening',true)");
        Assert(await script("cmd('captureRewrite','Plain opening')") == "true", "A selected span can still be captured in plain mode");
        var plainBefore = await script("ed.innerHTML");
        var plainTextBefore = Read(await script("ed.innerText"));
        const string replacement = "Rewritten <opening> & \tvalue\nSecond line";
        Assert(await script("cmd('replaceSelection'," + JsonSerializer.Serialize(replacement) + ")") == "true", "Plain-mode selection rewrite remains available");
        var plainAfter = await script("ed.innerHTML");
        Assert(plainAfter != plainBefore && Read(await script("ed.innerText")) == plainTextBefore.Replace("Plain opening", replacement, StringComparison.Ordinal),
            "Plain selection rewrite inserts exact literal text, tabs and newlines while preserving the complete surrounding text");
        Assert(await script("fmt('undo')") == "true" && await script("ed.innerHTML") == plainBefore
            && await script("fmt('redo')") == "true" && await script("ed.innerHTML") == plainAfter,
            "Plain selection rewrite uses one real native Undo/Redo step without losing protected text");
        await RejectBodyEdits("After a selected rewrite");

        await script("setEditorPlainText(false,false)");
        await RejectBodyEdits("Return to rich mode");
        var savedHtml = Read(await script("getHtml()"));
        var lost = await script("getBodySectionsLost()") == "true";
        var account = AppServices.Engine.AccountById("A")!;
        using var stored = Composer.BuildForDraft(new Draft { AccountId = "A", Html = savedHtml, BodySectionsLost = lost }, account);
        using var transported = Composer.FromBytes(Composer.ToBytes(stored));
        var reopened = Composer.FromMime(transported, "A", "");
        Assert(reopened.BodySectionsLost && !reopened.IsPlainText, "Saved rich-mode MIME retains earlier boundary loss");
        await script("setHtml(" + JsonSerializer.Serialize(reopened.Html) + "," + (reopened.BodySectionsLost ? "true" : "false") + ")");
        await RejectBodyEdits("Reopened converted draft");
        await script("setHtml(" + JsonSerializer.Serialize(original) + ",false)");
        Assert(await script("canReplaceBody()") == "true", "Restoring known rich content explicitly re-enables ordinary body operations");
        await script("setHtml('<p><br></p>')");
        await CheckFallbackBoundaryStateAsync(original);
        await CheckFallbackBoundaryStateAsync("<article><p>Plain opening</p><span class=magpie-signature>Retained signature</span><section class=magpie-quote><p>Entire retained quote</p></section></article>");
        Console.WriteLine("AI_BODY_BOUNDARY_STATE_SMOKE_PASSED");
    }

    private static async Task CheckFallbackBoundaryStateAsync(string original)
    {
        var account = AppServices.Engine.AccountById("A")!;
        var draft = new Draft { AccountId = "A", Html = original };
        var window = new ComposeWindow(draft, null);
        var vm = (ComposeViewModel)window.DataContext;
        Draft reopened;
        try
        {
            RecipientInvoke(window, "UsePlainEditor");
            var editor = (TextBox)window.FindName("PlainEditor");
            // Saving loses roles, but this live window still has its original exact protected tail.
            draft.Html = await vm.GetHtml!();
            Assert(vm.BodySectionsLost && draft.BodySectionsLost, "Fallback serialization durably records lost rich sections");
            var tail = (string)typeof(ComposeWindow).GetField("_plainProtectedTail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert(tail.Contains("Retained signature", StringComparison.Ordinal) && tail.Contains("Entire retained quote", StringComparison.Ordinal),
                "The live fallback captures a nonempty tail containing both signature and complete quoted message");
            await vm.EditorCommand!("replaceBody", "Safe live fallback rewrite");
            Assert(editor.Text.StartsWith("Safe live fallback rewrite", StringComparison.Ordinal) && editor.Text.EndsWith(tail, StringComparison.Ordinal),
                "A known exact tail remains editable in the current fallback window after saving");
            draft.Html = await vm.GetHtml!();
            using var saved = Composer.BuildForDraft(draft, account);
            using var wire = Composer.FromBytes(Composer.ToBytes(saved));
            reopened = Composer.FromMime(wire, "A", "");
        }
        finally { CloseBoundaryWindow(window, vm); }

        window = new ComposeWindow(reopened, null); vm = (ComposeViewModel)window.DataContext;
        try
        {
            RecipientInvoke(window, "UsePlainEditor");
            var editor = (TextBox)window.FindName("PlainEditor");
            editor.ApplyTemplate(); editor.Measure(new Size(620, 240)); editor.Arrange(new Rect(0, 0, 620, 240)); editor.UpdateLayout();
            var before = editor.Text;
            Assert(before.Contains("Retained signature", StringComparison.Ordinal) && before.Contains("Entire retained quote", StringComparison.Ordinal),
                "Fallback save and reopen retain both the signature and the complete quoted message");
            foreach (var mode in new[] { "replaceBody", "appendBody", "replyDraft" })
            {
                Exception? failure = null;
                try { await vm.EditorCommand!(mode, "Generated answer"); }
                catch (InvalidOperationException ex) { failure = ex; }
                Assert(failure?.Message == ComposeBodySafety.RestrictionReason && editor.Text == before,
                    "Reopened fallback " + mode + " explains the selected-text alternative and preserves the whole document");
            }
            editor.Select(0, "Safe live fallback rewrite".Length);
            await vm.EditorCommand!("captureRewrite", editor.SelectedText);
            await vm.EditorCommand!("replaceSelection", "Selected new opening");
            var after = editor.Text;
            Assert(after == "Selected new opening" + before["Safe live fallback rewrite".Length..] && editor.CanUndo,
                "A reopened restricted fallback draft still accepts selected-text rewrites through the actual TextBox");
            editor.Undo(); Assert(editor.Text == before && editor.CanRedo, "Fallback selection rewrite has native Undo");
            editor.Redo(); Assert(editor.Text == after, "Fallback selection rewrite has native Redo");
            vm.IsPlainText = true; vm.IsPlainText = false;
            Assert(vm.BodySectionsLost, "Native format transitions cannot clear recorded boundary loss");
        }
        finally { CloseBoundaryWindow(window, vm); }
    }

    private static void CloseBoundaryWindow(ComposeWindow window, ComposeViewModel vm)
    {
        typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close(); vm.Detach();
    }
}
