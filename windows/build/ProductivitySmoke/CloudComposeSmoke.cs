using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.CloudFiles;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static CloudUploadedFile CloudReceipt(CloudSharePlan plan) => new()
    {
        AccountId = plan.Account.Id, AccountEmail = plan.Account.Email, Provider = plan.Account.Kind,
        FileId = "smoke-" + Guid.NewGuid().ToString("N"), FileName = plan.FileName,
        WebUrl = "https://drive.google.com/file/d/smoke-" + Guid.NewGuid().ToString("N") + "/view",
        Recipients = plan.Recipients,
        PermissionIds = plan.Recipients.ToDictionary(x => x, _ => "grant-" + Guid.NewGuid().ToString("N")),
        Access = plan.Access,
    };

    private static async Task CheckCloudComposeAsync(string root, MailEngine originalEngine, string theme)
    {
        var http = new RejectHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "cloud-compose-" + theme)), new TestProtector(), http);
        AppServices.Engine = engine;
        var google = new Account { Id = "cloud", Kind = AccountKind.Gmail, Email = "owner@example.test" };
        var microsoft = new Account { Id = "cloud-ms", Kind = AccountKind.Microsoft, Email = "other@example.test" };
        engine.Config.Accounts.Add(google); engine.Config.Accounts.Add(microsoft);
        engine.Config.Ai.Enabled = false;
        var window = new ComposeWindow(new Draft
        {
            AccountId = google.Id, MessageId = "cloud-" + theme + "@example.test",
            To = "alex@example.test", Cc = "ben@example.test", Subject = "Cloud file fixture", Html = "<p>Original body</p>",
        }, null);
        var vm = (ComposeViewModel)window.DataContext;
        var body = "<p>Original body</p>";
        vm.GetHtml = () => Task.FromResult(body);
        vm.GetNewText = () => Task.FromResult(body);
        vm.Dirty = false;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        Task<CloudUploadedFile?>? pending = null;
        TaskCompletionSource<CloudUploadedFile>? upload = null;
        try
        {
            Assert(window.FindName("CloudAttachButton") is Button && window.FindName("StudioCloudAttachButton") is Button,
                theme + ": cloud upload is reachable from Attach tools and Studio Files");
            var path = Path.Combine(root, "cloud-fixture-" + theme + ".txt");
            await File.WriteAllTextAsync(path, "data");
            var dialog = new CloudFileDialog(vm, _ => Task.FromResult(true), path);
            try
            {
                var accounts = (ComboBox)dialog.FindName("CloudAccounts");
                var access = (ComboBox)dialog.FindName("CloudAccess");
                var confirm = (CheckBox)dialog.FindName("CloudConfirm");
                var start = (Button)dialog.FindName("CloudUpload");
                var people = (ListBox)dialog.FindName("CloudRecipients");
                Assert(accounts.Items.Count == 2 && ((Account)accounts.SelectedItem).Id == google.Id
                    && access.SelectedIndex == 0 && confirm.IsChecked != true && !start.IsEnabled,
                    theme + ": cloud dialog selects the From account, defaults to view access and requires explicit confirmation");
                Assert(people.Items.Cast<string>().SequenceEqual(new[] { "alex@example.test", "ben@example.test" })
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(accounts))
                    && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(confirm)),
                    theme + ": the accessible approval lists the exact deduplicated To/Cc recipients");
                confirm.IsChecked = true;
                Assert(start.IsEnabled, theme + ": selecting a file and approving named access enables upload");
                access.SelectedIndex = 1;
                Assert(confirm.IsChecked == false && !start.IsEnabled, theme + ": changing the requested role requires fresh confirmation");
                confirm.IsChecked = true;
                accounts.SelectedItem = microsoft;
                Assert(confirm.IsChecked == false && !start.IsEnabled, theme + ": changing the cloud account requires fresh confirmation");
            }
            finally { dialog.Close(); }
            vm.Bcc = "hidden@example.test";
            dialog = new CloudFileDialog(vm, _ => Task.FromResult(true), path);
            try
            {
                ((CheckBox)dialog.FindName("CloudConfirm")).IsChecked = true;
                Assert(!((Button)dialog.FindName("CloudUpload")).IsEnabled
                    && ((TextBlock)dialog.FindName("CloudStatus")).Text.Contains("Bcc"),
                    theme + ": hidden recipients cannot be exposed through a provider access list");
            }
            finally { dialog.Close(); vm.Bcc = ""; }

            var plan = CloudSharePlan.Create(google, "report.txt", 4, vm.To, vm.Cc, vm.Bcc, CloudFileAccess.View);
            var receipt = CloudReceipt(plan);
            upload = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var inserted = 0;
            var removed = 0;
            vm.Dirty = false;
            pending = vm.UploadCloudFileAsync(plan, _ => upload.Task, value =>
            {
                inserted++;
                body += "<p><a href=\"" + value.WebUrl + "\">report.txt</a></p>";
                return Task.FromResult(true);
            }, _ => { removed++; return Task.CompletedTask; });
            Assert(vm.AttachmentOperationBusy && window.HasUnsavedContent && !pending.IsCompleted && !vm.Dirty,
                theme + ": upload claims the clean draft before any cloud await");
            await vm.SendAsync(null, null);
            window.Close();
            Assert(!closed && !await window.CloseForExitAsync() && !await vm.DiscardAsync()
                && !await vm.KeepAsDraftAsync(closing: true) && engine.Store.GetOutbox().Count == 0,
                theme + ": Send, Close, exit, discard and save-and-close cannot overtake an upload");
            var secondCalled = false;
            Assert(await vm.UploadCloudFileAsync(plan, _ => { secondCalled = true; return Task.FromResult(receipt); },
                _ => Task.FromResult(true), _ => Task.CompletedTask) == null && !secondCalled && vm.AttachmentOperationBusy,
                theme + ": overlapping upload cannot replace the active operation's claim");
            upload.SetResult(receipt);
            Assert(await pending == receipt && inserted == 1 && removed == 0 && !vm.AttachmentOperationBusy && vm.Dirty
                && vm.LocalDraftId != null && engine.Store.CloudFilesIn(body).Count == 1,
                theme + ": acknowledged cloud link retains its access receipt and saves the edited draft immediately");
            using (var mime = Composer.FromBytes(engine.Store.GetLocalDraft(vm.LocalDraftId!.Value)!.Mime))
                Assert(mime.HtmlBody?.Contains(receipt.WebUrl) == true
                    && !System.Text.Encoding.UTF8.GetString(Composer.ToBytes(mime)).Contains("grant-"),
                    theme + ": saved MIME carries the usable link without internal sharing records");

            foreach (var change in new[] { "recipient", "body", "account", "From", "mode" })
            {
                var waiting = new TaskCompletionSource<CloudUploadedFile>(TaskCreationOptions.RunContinuationsAsynchronously);
                var staleReceipt = CloudReceipt(plan);
                var staleInsert = 0;
                var cleanup = 0;
                var operation = vm.UploadCloudFileAsync(plan, _ => waiting.Task,
                    _ => { staleInsert++; return Task.FromResult(true); }, _ => { cleanup++; return Task.CompletedTask; });
                switch (change)
                {
                    case "recipient": vm.To = "changed@example.test"; break;
                    case "body": body += "<p>Edited while uploading</p>"; vm.MarkEdited(); break;
                    case "account": google.Email = "changed-owner@example.test"; break;
                    case "From": vm.From = microsoft; break;
                    case "mode": vm.IsPlainText = true; break;
                }
                waiting.SetResult(staleReceipt);
                Exception? failure = null;
                try { await operation; } catch (InvalidOperationException ex) { failure = ex; }
                Assert(failure != null && staleInsert == 0 && cleanup == 1 && !vm.AttachmentOperationBusy,
                    theme + ": changed " + change + " rejects insertion and removes only the just-uploaded copy");
                vm.To = "alex@example.test"; google.Email = "owner@example.test"; vm.From = google; vm.IsPlainText = false;
            }

            var rejected = CloudReceipt(plan);
            var rejectedCleanup = 0;
            Exception? rejectedFailure = null;
            try
            {
                await vm.UploadCloudFileAsync(plan, _ => Task.FromResult(rejected), _ => Task.FromResult(false),
                    _ => { rejectedCleanup++; return Task.CompletedTask; });
            }
            catch (InvalidOperationException ex) { rejectedFailure = ex; }
            Assert(rejectedFailure != null && rejectedCleanup == 1 && !vm.AttachmentOperationBusy
                && engine.Store.CloudFilesIn(rejected.WebUrl).Count == 0,
                theme + ": a rejected editor acknowledgement cleans up the remote copy and unused receipt");

            using var cancellation = new CancellationTokenSource();
            var cancelledReceipt = CloudReceipt(plan);
            var cancelledUpload = new TaskCompletionSource<CloudUploadedFile>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelCleanup = 0;
            var cancelledOperation = vm.UploadCloudFileAsync(plan, _ => cancelledUpload.Task, _ => Task.FromResult(true),
                _ => { cancelCleanup++; return Task.CompletedTask; }, cancellation.Token);
            cancellation.Cancel(); cancelledUpload.SetResult(cancelledReceipt);
            var cancelled = false;
            try { await cancelledOperation; } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && cancelCleanup == 1 && !vm.AttachmentOperationBusy,
                theme + ": cancellation after cloud success still owns cleanup before releasing the compose claim");

            var leftover = CloudReceipt(plan);
            CloudSharingException? cleanupFailure = null;
            try
            {
                await vm.UploadCloudFileAsync(plan, _ => Task.FromResult(leftover), _ => Task.FromResult(false),
                    _ => Task.FromException(new IOException("Offline cleanup")));
            }
            catch (CloudSharingException ex) { cleanupFailure = ex; }
            Assert(cleanupFailure?.CleanupRequired == true && cleanupFailure.RecoveryUrl == leftover.WebUrl && !vm.AttachmentOperationBusy,
                theme + ": incomplete cleanup remains explicit with a safe provider recovery link");

            var versionUpload = new TaskCompletionSource<CloudUploadedFile>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var versionInserts = 0;
            CancellationToken uploadToken = default;
            var changingVersion = vm.UploadCloudFileAsync(plan, token => { uploadToken = token; return versionUpload.Task; },
                _ => { versionInserts++; return Task.FromResult(true); }, async _ => { cleanupStarted.TrySetResult(); await cleanupFinished.Task; });
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stopped = engine.StopForVersionChangeAsync(stopTimeout.Token);
            try
            {
                Assert(uploadToken.IsCancellationRequested && vm.AttachmentOperationBusy && !stopped.IsCompleted,
                    theme + ": version switching cancels and waits for an admitted cloud upload");
                versionUpload.SetResult(CloudReceipt(plan));
                await cleanupStarted.Task.WaitAsync(stopTimeout.Token);
                Assert(vm.AttachmentOperationBusy && !stopped.IsCompleted && versionInserts == 0,
                    theme + ": the final version snapshot waits for cloud-copy cleanup and cannot receive a stale link");
            }
            finally { versionUpload.TrySetCanceled(); cleanupFinished.TrySetResult(); }
            var versionCancelled = false;
            try { await changingVersion; } catch (OperationCanceledException) { versionCancelled = true; }
            await stopped;
            Assert(versionCancelled && !vm.AttachmentOperationBusy && engine.IsStoppedForVersionChange,
                theme + ": the cloud operation drains completely before profile handoff");
            var lateRejected = false;
            try { await vm.UploadCloudFileAsync(plan, _ => throw new Exception("Late upload reached provider"), _ => Task.FromResult(true), _ => Task.CompletedTask); }
            catch (OperationCanceledException) { lateRejected = true; }
            Assert(lateRejected, theme + ": a frozen profile rejects new cloud work before any upload delegate runs");
            Assert(http.Requests == 0 && engine.Store.GetOutbox().Count == 0,
                theme + ": cloud dialog and lifecycle fixtures do not contact providers or queue mail");
            Console.WriteLine("CLOUD_COMPOSE_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            upload?.TrySetCanceled();
            if (pending != null) try { await pending; } catch { }
            vm.AttachmentOperationBusy = false;
            vm.Dirty = false;
            if (!closed) await window.CloseForExitAsync();
            vm.Detach();
            AppServices.Engine = originalEngine;
        }
    }

    private static async Task CheckCloudLinkEditorAsync(Func<string, Task<string>> script)
    {
        const string url = "https://drive.google.com/file/d/cloud-smoke/view";
        const string label = "Report <Q4> & costs.txt";
        const string snapshot = """
            (()=>{
              const bound=(value,limit=1600)=>typeof value==='string'&&value.length>limit?value.slice(0,limit)+' [truncated]':value;
              const point=(node,offset)=>({node:node?.nodeName,parent:node?.parentElement?.tagName,offset:offset,text:bound(node?.textContent,120)});
              const describe=range=>range?{valid:validEditorRange(range),collapsed:range.collapsed,text:bound(range.toString(),120),start:point(range.startContainer,range.startOffset),end:point(range.endContainer,range.endOffset)}:null;
              const selection=window.getSelection(),link=ed.querySelector('a'),text=ed.textContent;
              return {html:bound(ed.innerHTML),text:bound(text,600),codepoints:Array.from(text).slice(0,256).map(character=>'U+'+character.codePointAt(0).toString(16).toUpperCase().padStart(4,'0')),
                link:link?{href:bound(link.href,400),text:bound(link.textContent,400),html:bound(link.outerHTML,800)}:null,em:bound(ed.querySelector('em')?.textContent,200),bold:bound(ed.querySelector('b')?.outerHTML,400),q4:ed.querySelector('q4')!==null,
                plain:isPlainText,contentEditable:ed.contentEditable,selection:selection.rangeCount?describe(selection.getRangeAt(0)):null,saved:describe(saved),
                cloudTarget:cloudLinkTarget?{html:bound(cloudLinkTarget.html),htmlMatches:cloudLinkTarget.html===ed.innerHTML,plain:cloudLinkTarget.plain,plainMatches:cloudLinkTarget.plain===isPlainText,range:describe(cloudLinkTarget.range)}:null};
            })()
            """;
        static string Bounded(string value) => value.Length <= 7000 ? value : value[..7000] + " [truncated]";
        await script("setEditorPlainText(false,false);setHtml('<p><b>Selected</b> surrounding <em>text</em></p>');findInEditor('Selected',true)");
        Assert(await script("captureCloudLink()") == "true", "Cloud upload captures the rich-editor insertion position");
        var captured = await script(snapshot);
        await script("window.getSelection().removeAllRanges()");
        var beforeInsert = await script(snapshot);
        var inserted = await script("insertCloudLink(" + JsonSerializer.Serialize(url) + "," + JsonSerializer.Serialize(label) + ")");
        var afterInsert = await script(snapshot);
        var contentChecks = await script("({href:ed.querySelector('a')?.href===" + JsonSerializer.Serialize(url)
            + ",label:ed.querySelector('a')?.textContent===" + JsonSerializer.Serialize(label)
            + ",text:ed.textContent===" + JsonSerializer.Serialize("Selected" + label + " surrounding text")
            + ",emphasis:ed.querySelector('em')?.textContent==='text',literal:ed.querySelector('q4')===null})");
        var richContent = inserted == "true" ? await script("ed.querySelector('a').href===" + JsonSerializer.Serialize(url)
                + "&&ed.querySelector('a').textContent===" + JsonSerializer.Serialize(label)
                + "&&ed.textContent===" + JsonSerializer.Serialize("Selected" + label + " surrounding text")
                + "&&ed.querySelector('em').textContent==='text'&&ed.querySelector('q4')===null") : "not-run";
        var diagnostic = "\nInserted: " + inserted + "; strict content predicate: " + richContent + "; checks: " + contentChecks
            + "\nCaptured: " + Bounded(captured) + "\nBefore insertion: " + Bounded(beforeInsert) + "\nAfter insertion: " + Bounded(afterInsert);
        Console.WriteLine("CLOUD_EDITOR_RICH_DIAGNOSTIC" + diagnostic);
        Assert(inserted == "true" && richContent == "true",
            "Cloud link preserves selected/surrounding rich content and treats the filename as literal text" + diagnostic);
        Assert(await script("fmt('undo')&&ed.querySelector('a')===null&&ed.textContent==='Selected surrounding text'") == "true"
            && await script("fmt('redo')&&ed.querySelector('a').href===" + JsonSerializer.Serialize(url)) == "true",
            "Cloud link insertion participates in the real editor's undo and redo history");
        await script("captureCloudLink();ed.appendChild(document.createTextNode(' changed'))");
        Assert(await script("insertCloudLink('" + url + "','new.txt')") == "false",
            "A cloud upload cannot insert into an edited rich-body snapshot");
        await script("setHtml('<p>Plain draft</p>');setEditorPlainText(true,false);findInEditor('draft',true);captureCloudLink()");
        Assert(await script("insertCloudLink('" + url + "','report.txt')") == "true"
            && await script("ed.querySelector('a')===null&&ed.textContent===" + JsonSerializer.Serialize("Plain draftreport.txt: " + url)) == "true",
            "Plain-text cloud insertion includes the usable URL without introducing HTML or deleting selected text");
        await script("captureCloudLink();setEditorPlainText(false,false)");
        Assert(await script("insertCloudLink('" + url + "','new.txt')") == "false", "Changing editor mode invalidates a cloud insertion target");
        await script("captureCloudLink();setHtml('<p>Restored draft</p>')");
        Assert(await script("insertCloudLink('" + url + "','new.txt')") == "false", "Draft restoration invalidates a cloud insertion target");
        await script("findInEditor('draft',true);captureCloudLink()");
        Assert(await script("insertCloudLink('javascript:alert(1)','report.txt')") == "false", "Cloud insertion rejects a non-HTTPS URL");
        await script("captureCloudLink();cancelCloudLink()");
        Assert(await script("insertCloudLink('" + url + "','new.txt')") == "false", "Cancelling the cloud dialog invalidates its insertion target");
        await script("setHtml('<p></p>')");
        Console.WriteLine("CLOUD_EDITOR_SMOKE_PASSED");
    }
}
