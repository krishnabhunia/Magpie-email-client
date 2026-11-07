using System.IO;
using System.Text.Json;
using System.Windows;
using Magpie.App;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckInlineImageEditorAsync(Func<string, Task<string>> script)
    {
        const string text = "Before picture. After picture.";
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aY7kAAAAASUVORK5CYII=");
        var vm = new ComposeViewModel(new Draft
        {
            AccountId = "A", MessageId = "inline-image-editor@example.test", To = "other@example.test",
            Subject = "Real inline-picture insertion", Html = "<p>" + text + "</p>",
        }, null);
        vm.GetHtml = async () => JsonSerializer.Deserialize<string>(await script("getHtml()")) ?? "";
        try
        {
            await script("setHtml('<p>Before picture. After picture.</p>');(()=>{const r=document.createRange();r.setStart(ed.firstElementChild.firstChild,16);r.collapse(true);selectEditorRange(r);})()");
            vm.Dirty = false;
            Assert(await ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png",
                    async data => await script("fmt('insertImage'," + JsonSerializer.Serialize(data) + ")") == "true",
                    () => true, _ => Task.FromResult(png)) && vm.Dirty && !vm.AttachmentOperationBusy,
                "Production inline-image operation completes only after real WebView insertion acknowledgement");
            var observed = await script("(()=>{const images=ed.querySelectorAll('img');return {count:images.length,"
                + "source:images[0]?.src,text:ed.textContent,codes:Array.from(ed.textContent).map(c=>c.codePointAt(0)),html:getHtml()};})()");
            using var result = JsonDocument.Parse(observed);
            Assert(result.RootElement.GetProperty("count").GetInt32() == 1
                    && result.RootElement.GetProperty("source").GetString() == "data:image/png;base64," + Convert.ToBase64String(png)
                    && result.RootElement.GetProperty("text").GetString() == text,
                "File-picker inline image reaches the actual editor without deleting surrounding message text: " + observed);
        }
        finally
        {
            vm.Detach();
            await script("setHtml('<p></p>')");
        }
        Console.WriteLine("INLINE_IMAGE_EDITOR_SMOKE_PASSED");
    }

    private static async Task CheckInlineImageInsertionAsync(string root, MailEngine originalEngine, string theme)
    {
        var http = new RejectHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "inline-image-" + theme)), new TestProtector(), http);
        AppServices.Engine = engine;
        engine.Config.Accounts.Add(new Account { Id = "inline", Email = "me@example.test" });
        engine.Config.Ai.Enabled = false;
        var window = new ComposeWindow(new Draft
        {
            AccountId = "inline", MessageId = "inline-image-" + theme + "@example.test",
            To = "other@example.test", Subject = "Inline image operation", Html = "<p>Preserved body</p>",
        }, null);
        var vm = (ComposeViewModel)window.DataContext;
        vm.GetHtml = () => Task.FromResult("<p>Preserved body</p>");
        vm.GetNewText = () => Task.FromResult("Preserved body");
        vm.Dirty = false;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aY7kAAAAASUVORK5CYII=");
        var read = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var insertionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inserted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCalls = 0;
        var insertCalls = 0;
        string? dataUri = null;
        Task<bool>? operation = null;
        try
        {
            // Call the same operation used by the real file-picker handler; only the slow I/O and editor
            // acknowledgement are deterministic test delegates. No clipboard, modal picker or mail server.
            operation = ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png", async data =>
            {
                insertCalls++;
                dataUri = data;
                insertionStarted.TrySetResult(true);
                return await inserted.Task;
            }, () => true, _ => { readCalls++; return read.Task; });
            Assert(vm.AttachmentOperationBusy && !operation.IsCompleted && readCalls == 1,
                theme + ": file-picker image claims its operation before waiting for a slow read");
            Assert(window.HasUnsavedContent && !vm.Dirty,
                theme + ": pending inline image protects a previously clean draft from session ending");
            await vm.SendAsync(null, null);
            Assert(!vm.Sending && engine.Store.GetOutbox().Count == 0 && vm.Status.Contains("Finish attaching"),
                theme + ": Send cannot queue a message while the selected image is still loading");
            window.Close();
            Assert(!closed && !await window.CloseForExitAsync(),
                theme + ": native Close and application exit cannot overtake the file-picker image read");
            Assert(!await vm.DiscardAsync() && !await vm.KeepAsDraftAsync(closing: true),
                theme + ": discard and save-and-close wait for inline-picture completion");
            Assert(!await ComposeInlineImageInsertion.InsertAsync(vm, "second.png", _ => Task.FromResult(true), () => true,
                    _ => { readCalls++; return Task.FromResult(png); }) && readCalls == 1 && vm.AttachmentOperationBusy,
                theme + ": overlapping insertion is rejected without clearing the first operation's claim");

            read.SetResult(png);
            await insertionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(vm.AttachmentOperationBusy && !operation.IsCompleted && insertCalls == 1
                && dataUri == "data:image/png;base64," + Convert.ToBase64String(png),
                theme + ": image MIME/data are correct and protection lasts until the editor acknowledges insertion");
            await vm.SendAsync(null, null);
            Assert(engine.Store.GetOutbox().Count == 0 && vm.AttachmentOperationBusy,
                theme + ": Send also waits for a pending editor insertion after file loading finishes");
            inserted.SetResult(true);
            Assert(await operation && !vm.AttachmentOperationBusy && vm.Dirty,
                theme + ": successful insertion explicitly marks the edit and releases its claim");

            vm.Dirty = false;
            Assert(!await ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png", _ => Task.FromResult(false),
                    () => true, _ => Task.FromResult(png)) && !vm.AttachmentOperationBusy && !vm.Dirty,
                theme + ": rejected editor insertion releases the claim without claiming a saved edit");

            foreach (var failure in new[] { "read", "editor" })
            {
                Exception? observed = null;
                try
                {
                    await ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png",
                        _ => Task.FromException<bool>(new IOException("editor failure")), () => true,
                        _ => failure == "read" ? Task.FromException<byte[]>(new IOException("read failure")) : Task.FromResult(png));
                }
                catch (IOException ex) { observed = ex; }
                Assert(observed?.Message == failure + " failure" && !vm.AttachmentOperationBusy && !vm.Dirty,
                    theme + ": " + failure + " failure releases insertion protection without modifying content");
            }

            var cancelled = false;
            try
            {
                await ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png", _ => Task.FromResult(true), () => true,
                    _ => Task.FromCanceled<byte[]>(new CancellationToken(canceled: true)));
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !vm.AttachmentOperationBusy && !vm.Dirty,
                theme + ": cancellation during image reading also releases the operation claim");

            var available = true;
            var delayedRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var lostEditorCalls = 0;
            var lostEditor = ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png",
                _ => { lostEditorCalls++; return Task.FromResult(true); }, () => available, _ => delayedRead.Task);
            available = false;
            delayedRead.SetResult(png);
            Assert(!await lostEditor && lostEditorCalls == 0 && !vm.AttachmentOperationBusy && !vm.Dirty,
                theme + ": losing the editor during the read safely rejects the stale callback");

            delayedRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var plainChange = ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png",
                _ => { lostEditorCalls++; return Task.FromResult(true); }, () => true, _ => delayedRead.Task);
            vm.IsPlainText = true;
            delayedRead.SetResult(png);
            Assert(!await plainChange && lostEditorCalls == 0 && !vm.AttachmentOperationBusy,
                theme + ": switching to plain text while reading never inserts an unsupported picture");
            var plainReadCalls = 0;
            Assert(!await ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png", _ => Task.FromResult(true), () => true,
                    _ => { plainReadCalls++; return Task.FromResult(png); }) && plainReadCalls == 0,
                theme + ": plain-text compose cannot start a file-picker inline-image operation");
            vm.IsPlainText = false;

            foreach (var payload in new[] { Array.Empty<byte>(), new byte[ComposeInlineImageInsertion.MaximumBytes + 1] })
            {
                Exception? observed = null;
                try
                {
                    await ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png",
                        _ => { lostEditorCalls++; return Task.FromResult(true); }, () => true, _ => Task.FromResult(payload));
                }
                catch (InvalidDataException ex) { observed = ex; }
                Assert(observed != null && lostEditorCalls == 0 && !vm.AttachmentOperationBusy,
                    theme + ": empty/oversized read results never reach the editor and always release the claim");
            }

            var localPath = Path.Combine(root, "inline-file-" + theme + ".png");
            await File.WriteAllBytesAsync(localPath, png);
            string? localData = null;
            Assert(await ComposeInlineImageInsertion.InsertAsync(vm, localPath,
                    value => { localData = value; return Task.FromResult(true); }, () => true)
                && localData == "data:image/png;base64," + Convert.ToBase64String(png) && !vm.AttachmentOperationBusy,
                theme + ": the production bounded file reader inserts exactly the selected local bytes");
            var tooLargePath = Path.Combine(root, "inline-too-large-" + theme + ".jpg");
            using (var oversizedFile = File.Create(tooLargePath)) oversizedFile.SetLength(ComposeInlineImageInsertion.MaximumBytes + 1L);
            Exception? tooLarge = null;
            try
            {
                await ComposeInlineImageInsertion.InsertAsync(vm, tooLargePath,
                    _ => { lostEditorCalls++; return Task.FromResult(true); }, () => true);
            }
            catch (InvalidDataException ex) { tooLarge = ex; }
            Assert(tooLarge != null && lostEditorCalls == 0 && !vm.AttachmentOperationBusy,
                theme + ": production file reads refuse oversized files before allocating their content");

            var lateVm = new ComposeViewModel(new Draft
            {
                AccountId = "inline", MessageId = "inline-late-editor-" + theme + "@example.test",
                To = "other@example.test", Subject = "Closed before image acknowledgement", Html = "<p>Body</p>",
            }, null);
            var lateAcknowledgement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var acknowledgementStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<bool>? lateInsertion = null;
            try
            {
                lateVm.Dirty = false;
                lateInsertion = ComposeInlineImageInsertion.InsertAsync(lateVm, "fixture.png",
                    _ => { acknowledgementStarted.TrySetResult(true); return lateAcknowledgement.Task; },
                    () => true, _ => Task.FromResult(png));
                await acknowledgementStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                lateVm.Detach();
                lateAcknowledgement.SetResult(true);
                Assert(!await lateInsertion && !lateVm.AttachmentOperationBusy && !lateVm.Dirty,
                    theme + ": a late editor acknowledgement cannot mark a detached draft edited");
            }
            finally
            {
                lateAcknowledgement.TrySetResult(false);
                if (lateInsertion != null) try { await lateInsertion; } catch { }
                lateVm.Detach();
            }

            delayedRead = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var detached = ComposeInlineImageInsertion.InsertAsync(vm, "fixture.png",
                _ => { lostEditorCalls++; return Task.FromResult(true); }, () => true, _ => delayedRead.Task);
            vm.Detach();
            delayedRead.SetResult(png);
            Assert(!await detached && lostEditorCalls == 0 && !vm.AttachmentOperationBusy,
                theme + ": a forced detach during a slow read never calls the disposed editor");
            Assert(http.Requests == 0 && engine.Store.GetOutbox().Count == 0,
                theme + ": inline-image regression fixtures neither contact services nor queue email");
            Console.WriteLine("INLINE_IMAGE_INSERTION_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            read.TrySetResult(png);
            inserted.TrySetResult(false);
            if (operation != null) try { await operation; } catch { }
            vm.AttachmentOperationBusy = false;
            vm.Dirty = false;
            if (!closed) await window.CloseForExitAsync();
            vm.Detach();
            AppServices.Engine = originalEngine;
        }
    }
}
