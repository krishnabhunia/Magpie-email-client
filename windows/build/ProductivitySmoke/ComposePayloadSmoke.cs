using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpie.App;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckClipboardPictureControls(string theme)
    {
        // Pure fixtures: never read/write the runner's clipboard or open a save dialog.
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat((byte)255, 16).ToArray(), 8);
        var png = ComposeClipboardImage.EncodePng(bitmap, ComposeClipboardImage.InlineLimitBytes);
        Assert(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "Clipboard image encodes real PNG bytes (" + theme + ")");
        using var input = new MemoryStream(png);
        var decoded = new PngBitmapDecoder(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert(decoded.Frames[0].PixelWidth == 2 && decoded.Frames[0].PixelHeight == 2,
            "Clipboard PNG retains picture dimensions");
        Exception? oversized = null;
        try { ComposeClipboardImage.EncodePng(bitmap, maximumBytes: 8); }
        catch (Exception ex) { oversized = ex; }
        Assert(oversized != null, "Clipboard PNG encoding refuses to exceed its output cap");
        Exception? invalidLimit = null;
        try { ComposeClipboardImage.EncodePng(bitmap, maximumBytes: 0); }
        catch (ArgumentOutOfRangeException ex) { invalidLimit = ex; }
        Assert(invalidLimit != null, "Clipboard encoding validates its configured cap");
        foreach (var rich in new[] { false, true })
        {
            var dialog = new ClipboardPictureDialog(rich);
            var panel = (StackPanel)dialog.Content;
            var actions = panel.Children.OfType<WrapPanel>().Single();
            var buttons = actions.Children.OfType<Button>().ToArray();
            Assert(dialog.Choice == ClipboardPictureChoice.Cancel && buttons.Length == 3,
                "Picture choice starts cancelled and offers inline, attachment and cancel");
            Assert(buttons.Single(b => (string)b.Content == "Insert inline").IsEnabled == rich
                && buttons.Single(b => (string)b.Content == "Save and attach PNG…").IsEnabled
                && buttons.Single(b => (string)b.Content == "Cancel").IsCancel,
                "Plain mode offers attachments but disables inline pictures");
            Assert(buttons.All(b => !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(b))),
                "Clipboard representation choices expose accessible names");
            dialog.Close();
        }
    }

    private static async Task CheckComposeMessageSizeAsync(string root, MailEngine originalEngine)
    {
        var http = new RejectHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "payload-size")), new TestProtector(), http);
        AppServices.Engine = engine;
        ComposeViewModel? vm = null;
        try
        {
            var account = new Account { Id = "payload", Email = "me@example.test" };
            engine.Config.Accounts.Add(account);
            engine.Config.Ai.Enabled = false;
            var path = Path.Combine(root, "payload-local.bin");
            File.WriteAllBytes(path, new byte[4096]);
            var carried = new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(new byte[8192])),
                ContentDisposition = new ContentDisposition(ContentDisposition.Attachment),
                ContentTransferEncoding = ContentEncoding.Base64, FileName = "forwarded.bin",
            };
            var html = "<p>Unsent payload<img src='data:image/png;base64,"
                + Convert.ToBase64String(new byte[20_000]) + "'></p>";
            var draft = new Draft
            {
                AccountId = account.Id, MessageId = "payload-smoke@example.test", To = "other@example.test",
                Subject = "Full payload size", Html = html,
                AttachmentPaths = new() { path }, CarriedParts = new() { carried },
            };
            vm = new ComposeViewModel(draft, null) { GetHtml = () => Task.FromResult(html) };
            var first = await vm.MeasureDraftSizeAsync();
            Assert(first.Bytes > 4096 + 8192 + 20_000 && !first.AtLeast,
                "Real compose size includes local files, forwarded MIME and inline pictures");
            Assert(vm.AttachmentSummary.StartsWith("2 files") && vm.AttachmentSummary.Contains("full message /")
                && vm.AttachmentSummary.Contains("suggested cap"),
                "Attachment strip labels complete encoded size and account guidance");
            vm.Subject = "Updated payload subject";
            Assert(vm.AttachmentSummary.Contains("updates after autosave or Review"),
                "Editing invalidates the old full-message size rather than showing a stale number");
            var second = await vm.MeasureDraftSizeAsync();
            Assert(second.Bytes > 32_000 && carried.Content?.Stream?.CanRead == true,
                "Review can remeasure without disposing forwarded content");
            vm.GetHtml = () =>
            {
                vm.Subject = "Changed while Review was reading the editor";
                return Task.FromResult(html);
            };
            Exception? staleSize = null;
            try { await vm.MeasureDraftSizeAsync(); }
            catch (InvalidOperationException ex) { staleSize = ex; }
            Assert(staleSize != null && vm.AttachmentSummary.Contains("updates after autosave or Review"),
                "Review declines an edited snapshot and never publishes its stale encoded size");
            vm.GetHtml = () => Task.FromResult(html);
            Assert(http.Requests == 0 && engine.Store.GetOutbox().Count == 0,
                "Message sizing neither contacts a server nor queues the draft");
            Assert(vm.TryBeginAttachmentOperation() && !vm.TryBeginAttachmentOperation(),
                "Clipboard work is claimed once before its asynchronous save starts");
            await vm.SendAsync(null, null);
            Assert(!vm.Sending && engine.Store.GetOutbox().Count == 0 && vm.Status.Contains("Finish attaching"),
                "Sending cannot omit a picture whose asynchronous attachment save is still running");
            Assert(!await vm.DiscardAsync() && !await vm.KeepAsDraftAsync(closing: true),
                "Discard and keep-and-close wait for the active picture attachment operation");
            vm.AttachmentOperationBusy = false;
            vm.GetNewText = () => Task.FromResult("A complete message body");
            vm.GetHtml = () =>
            {
                vm.Subject = "Changed while send was reading the editor";
                return Task.FromResult(html);
            };
            await vm.SendAsync(null, null);
            Assert(engine.Store.GetOutbox().Count == 0 && !vm.Sending && vm.Status.StartsWith("Message changed during review"),
                "An edit during send preparation aborts the send and requires a fresh review");
            vm.GetHtml = () => Task.FromResult(html);
            vm.GetNewText = () =>
            {
                vm.To = "new-incomplete-recipient";
                return Task.FromResult("A complete message body");
            };
            await vm.SendAsync(null, null);
            Assert(engine.Store.GetOutbox().Count == 0 && !vm.Sending && vm.To == "new-incomplete-recipient",
                "Recipient edits during preflight are preserved and never queued using the earlier validation");

            var upper = Path.Combine(root, "Report.bin");
            var lower = Path.Combine(root, "report.bin");
            var variant = new Draft
            {
                AccountId = account.Id, Html = "<p>Separate file names</p>",
                AttachmentPaths = new() { upper, lower },
                AttachmentOrder = new() { new() { Path = lower }, new() { Path = upper } },
                AttachmentNames = new(StringComparer.Ordinal) { [upper] = "upper-report.bin", [lower] = "lower-report.bin" },
            };
            var variantVm = new ComposeViewModel(variant, null) { GetHtml = () => Task.FromResult(variant.Html) };
            try
            {
                Assert(variantVm.Attachments.Select(a => a.Name).SequenceEqual(new[] { "lower-report.bin", "upper-report.bin" }),
                    "Compose restores distinct case-variant file names in their exact saved order");
                var build = typeof(ComposeViewModel).GetMethod("BuildDraftAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                var rebuilt = await (Task<Draft?>)build.Invoke(variantVm, new object[] { false })!;
                Assert(rebuilt != null && rebuilt.AttachmentNames.Count == 2
                    && rebuilt.AttachmentNames[upper] == "upper-report.bin"
                    && rebuilt.AttachmentNames[lower] == "lower-report.bin",
                    "Compose saves both case-variant outgoing names without a case-insensitive dictionary collision");
            }
            finally { variantVm.Detach(); }
            vm.Detach();
            Assert(!vm.TryBeginAttachmentOperation() && !vm.AttachmentOperationBusy,
                "Closing or detached drafts cannot start a new asynchronous picture attachment");
        }
        finally
        {
            if (vm != null) { vm.AttachmentOperationBusy = false; await vm.DiscardAsync(); vm.Detach(); }
            AppServices.Engine = originalEngine;
        }
    }

    private static async Task CheckClipboardEditorAsync(Func<string, Task<string>> script)
    {
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==";
        Assert(await script("clipboardHasFiles(null)===false && clipboardHasFiles({files:[],items:[{kind:'string'}]})===false"
            + " && clipboardHasFiles({files:[{}],items:[]}) && clipboardHasFiles({files:[],items:[{kind:'file'}]})") == "true",
            "Clipboard routing identifies files/pictures without intercepting normal text");
        await script("setEditorPlainText(false,false);setHtml('<p>Clipboard fixture</p>');setNativeAttachmentPaste(true)");
        Assert(await script("captureClipboardPicture()") == "true", "Clipboard picture captures the current rich insertion point");
        Assert(await script("insertClipboardPicture(" + JsonSerializer.Serialize(png) + ")") == "true"
            && await script("ed.querySelectorAll('img').length===1 && ed.textContent.includes('Clipboard fixture')") == "true",
            "Inline clipboard picture inserts without replacing surrounding text");
        Assert(await script("insertClipboardPicture(" + JsonSerializer.Serialize(png) + ")") == "false",
            "Clipboard insertion consumes its captured selection and cannot duplicate silently");
        await script("captureClipboardPicture();setHtml('<p>Restored document</p>')");
        Assert(await script("insertClipboardPicture(" + JsonSerializer.Serialize(png) + ")") == "false",
            "Restoring a draft rejects a stale clipboard insertion");
        await script("captureClipboardPicture();ed.appendChild(document.createTextNode('New edit'))");
        Assert(await script("insertClipboardPicture(" + JsonSerializer.Serialize(png) + ")") == "false",
            "Editing the body while choosing a picture rejects a stale insertion");
        await script("captureClipboardPicture()");
        Assert(await script("insertClipboardPicture('https://example.test/remote.png')") == "false"
            && await script("insertClipboardPicture('data:text/html;base64,PHNjcmlwdD4=')") == "false",
            "Clipboard picture helper accepts only locally encoded PNG data");
        await script("captureClipboardPicture();cancelClipboardPicture()");
        Assert(await script("insertClipboardPicture(" + JsonSerializer.Serialize(png) + ")") == "false",
            "Cancelling the picture choice releases its pending insertion snapshot");
        await script("setEditorPlainText(true,false)");
        Assert(await script("captureClipboardPicture()") == "false"
            && await script("insertClipboardPicture(" + JsonSerializer.Serialize(png) + ")") == "false",
            "Plain-text mode never silently inserts rich clipboard content");
        await script("setEditorPlainText(false,false);setHtml('<p></p>')");
    }
}
