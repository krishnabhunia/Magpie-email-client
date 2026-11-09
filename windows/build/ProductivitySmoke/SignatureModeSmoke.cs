using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckSignatureModesAsync(Func<string, Task<string>> script)
    {
        await CheckNativeSignatureModesAsync();
        const string original = "<p><strong>Author opening</strong></p><div class=\"magpie-signature\"><p>Old signature</p></div><div class=\"magpie-quote\"><blockquote>Original conversation</blockquote></div>";
        const string replacement = "<div class=\"magpie-signature\"><p><em>Variant signature</em></p></div>";
        await script("window.signatureModeOriginalPost=post;window.signatureModeDirty=0;post=function(message){if(message.t==='dirty')window.signatureModeDirty++;window.signatureModeOriginalPost(message)}");
        try
        {
            await script("setHtml(" + JsonSerializer.Serialize(original) + ")");
            Assert(await script("canReplaceSignatureBlock()") == "true"
                && await script("replaceSignatureBlock(" + JsonSerializer.Serialize(replacement) + ")") == "true",
                "Rich editing permits replacement of a structurally identified signature");
            Assert(await script("ed.querySelectorAll('.magpie-signature').length===1&&ed.querySelector('.magpie-signature em').textContent==='Variant signature'&&ed.querySelector('strong').textContent==='Author opening'&&ed.querySelector('.magpie-quote blockquote').textContent==='Original conversation'&&ed.querySelector('.magpie-signature').nextElementSibling.classList.contains('magpie-quote')") == "true",
                "Rich signature replacement preserves author formatting, quote contents and block order");
            Assert(await script("replaceSignatureBlock('')") == "true"
                && await script("ed.querySelector('.magpie-signature')===null&&ed.querySelector('strong').textContent==='Author opening'&&ed.querySelector('.magpie-quote blockquote').textContent==='Original conversation'") == "true",
                "No signature removes only the rich signature block");
            var withoutSignature = await script("getHtml()");
            await script("window.signatureModeDirty=0");
            Assert(await script("replaceSignatureBlock('')") == "false" && await script("getHtml()") == withoutSignature
                && await script("window.signatureModeDirty") == "0",
                "No signature on a rich document without a signature is a no-op and does not mark the message dirty");
            Assert(await script("replaceSignatureBlock(" + JsonSerializer.Serialize(replacement) + ")") == "true",
                "A fresh rich document without a signature can still add one");

            await script("setEditorPlainText(true,false);window.signatureModeDirty=0");
            var plain = await script("getHtml()");
            Assert(await script("canReplaceSignatureBlock()") == "false"
                && await script("replaceSignatureBlock('')") == "false"
                && await script("replaceSignatureBlock(" + JsonSerializer.Serialize(replacement) + ")") == "false"
                && await script("getHtml()") == plain && await script("window.signatureModeDirty") == "0",
                "Plain-text signature selection/removal rejects mutation and retains all flattened message text");
            Assert(await script("ed.textContent.includes('Variant signature')&&ed.textContent.includes('Original conversation')&&ed.querySelector('.magpie-signature')===null") == "true",
                "Plain mode retains signature and quoted text without pretending their structural boundaries survived");

            await script("setEditorPlainText(false,false);window.signatureModeDirty=0");
            var roundTrip = await script("getHtml()");
            Assert(await script("canReplaceSignatureBlock()") == "false"
                && await script("replaceSignatureBlock('')") == "false"
                && await script("replaceSignatureBlock(" + JsonSerializer.Serialize(replacement) + ")") == "false"
                && await script("getHtml()") == roundTrip && await script("window.signatureModeDirty") == "0",
                "Returning to rich mode cannot guess flattened signature boundaries, delete arbitrary text or append a duplicate");

            await script("setHtml('<p>Fresh rich message</p>')");
            Assert(await script("canReplaceSignatureBlock()") == "true"
                && await script("replaceSignatureBlock(" + JsonSerializer.Serialize(replacement) + ")") == "true"
                && await script("ed.querySelectorAll('.magpie-signature').length===1&&ed.firstElementChild.textContent==='Fresh rich message'") == "true",
                "A new whole rich document resets boundary-loss state and permits a first signature");
            await script("setHtml(" + JsonSerializer.Serialize(original) + ")");
            Assert(await script("replaceSignatureBlock('')") == "true",
                "Restoring a whole rich document with explicit signature markers restores safe signature removal");
        }
        finally
        {
            await script("post=window.signatureModeOriginalPost;delete window.signatureModeOriginalPost;delete window.signatureModeDirty;setHtml('<p><br></p>')");
        }
        Console.WriteLine("SIGNATURE_MODE_SMOKE_PASSED");
    }

    private static async Task CheckNativeSignatureModesAsync()
    {
        var window = new ComposeWindow(new Draft { AccountId = "A", Html = "<p>Signature fixture</p>" }, null);
        var vm = (ComposeViewModel)window.DataContext;
        var account = vm.From ?? throw new InvalidOperationException("Signature fixture needs its local test account.");
        var variants = account.SignatureVariants;
        try
        {
            account.SignatureVariants = new() { new NamedSignature { Name = "Fixture variant", Html = "<p>Variant</p>" } };
            typeof(ComposeWindow).GetField("_editorReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            var editor = (TextBox)window.FindName("PlainEditor");
            editor.Text = "Author text\nRetained signature\nOriginal quote";
            foreach (var fallback in new[] { false, true })
            {
                vm.IsPlainText = !fallback;
                typeof(ComposeWindow).GetField("_plain", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, fallback);
                vm.Dirty = false;
                var menu = await (Task<ContextMenu>)RecipientInvoke(window, "BuildSignatureMenuAsync")!;
                var choices = menu.Items.OfType<MenuItem>().Where(item => item.Header is string label
                    && (label is "Default signature" or "Fixture variant" or "No signature")).ToArray();
                Assert(choices.Length == 3 && choices.All(item => !item.IsEnabled)
                    && menu.Items.OfType<MenuItem>().Select(item => item.Header).OfType<TextBlock>()
                        .Any(text => text.Text.Contains("manually", StringComparison.Ordinal)),
                    "Native " + (fallback ? "fallback" : "plain-text") + " signature menu disables default/variant/removal and explains manual editing");
                Assert(menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "Manage signatures…").IsEnabled,
                    "Signature settings remain accessible while automatic message signature changes are unavailable");
                var textBefore = editor.Text;
                await (Task)RecipientInvoke(window, "SetComposeSignatureAsync", "<div class=\"magpie-signature\">New signature</div>")!;
                await (Task)RecipientInvoke(window, "SetComposeSignatureAsync", "")!;
                Assert(editor.Text == textBefore && !vm.Dirty,
                    "Direct native signature callbacks cannot mutate or dirty a plain/fallback draft");
            }
        }
        finally
        {
            account.SignatureVariants = variants;
            typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            vm.Detach();
        }
    }
}
