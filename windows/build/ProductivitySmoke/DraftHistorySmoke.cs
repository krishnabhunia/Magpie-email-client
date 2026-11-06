using System.IO;
using System.Text;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace ProductivitySmoke;

internal static partial class Program
{
    /// <summary>Exercise the real compose lifecycle with an isolated store and a deterministic, local editor.</summary>
    private static async Task CheckDraftHistoryAsync(string root, MailEngine originalEngine)
    {
        var http = new RejectHttp();
        var paths = new AppPaths(Path.Combine(root, "draft-history"));
        using var engine = new MailEngine(paths, new TestProtector(), http);
        AppServices.Engine = engine;
        ComposeViewModel? vm = null;
        try
        {
            var account = new Account { Id = "history", Email = "me@example.test", DisplayName = "Example User" };
            engine.Config.Accounts.Add(account);
            engine.Config.Ai.Enabled = false;
            var originalPath = Path.Combine(root, "initial-source.txt");
            var changedPath = Path.Combine(root, "changed-source.txt");
            var originalBytes = Encoding.UTF8.GetBytes("Original attachment snapshot.\n");
            var changedBytes = Encoding.UTF8.GetBytes("Current unsent attachment snapshot.\n");
            File.WriteAllBytes(originalPath, originalBytes);
            File.WriteAllBytes(changedPath, changedBytes);
            const string originalHtml = "<p>Original body.</p>";
            const string changedHtml = "<p><b>Current unsent body.</b></p>";
            const string originalTo = "Original Person <original@example.test>; unfinished-address";
            const string originalCc = "Copy Person <copy@example.test>\nsecond-copy@example.test";
            const string originalBcc = "Private Person <private@example.test>; another-private@example.test";
            const string originalReplyTo = "Replies <reply@example.test>";
            var draft = new Draft
            {
                AccountId = account.Id, Mode = ComposeMode.ReplyAll, MessageId = "history-smoke@example.test",
                ThreadKey = "history-smoke-thread", SourceDraftRow = 77,
                InReplyTo = "<original-message@example.test>", References = "<older-message@example.test> <original-message@example.test>",
                Subject = "Original kept draft", To = originalTo, Cc = originalCc, Bcc = originalBcc,
                ReplyTo = originalReplyTo, Priority = "High", RequestReadReceipt = true,
                Html = originalHtml, IsPlainText = true, AttachmentPaths = new() { originalPath },
                AttachmentNames = new(StringComparer.OrdinalIgnoreCase) { [originalPath] = "original-attachment.txt" },
            };
            var localId = engine.SaveLocalDraft(draft, pendingUpload: true);
            Assert(localId != 0, "Draft-history fixture has a kept local baseline");
            // Snapshots preserve serialized MIME bytes. Text attachments can have their line endings
            // canonicalized by MIME serialization, before revision history ever receives them.
            using var originalMime = Composer.FromBytes(engine.Store.GetLocalDraft(localId)!.Mime);
            var originalSnapshotBytes = DraftAttachmentBytes(originalMime.Attachments.Single());
            using var changedMime = Composer.FromBytes(Composer.ToBytes(Composer.Build(new Draft
            {
                AccountId = account.Id, MessageId = "history-expected-current@example.test", Html = changedHtml,
                AttachmentPaths = new() { changedPath },
            }, account)));
            var changedSnapshotBytes = DraftAttachmentBytes(changedMime.Attachments.Single());
            var identity = (draft.AccountId, draft.MessageId, draft.Mode, draft.ThreadKey, draft.SourceDraftRow,
                draft.InReplyTo, draft.References, draft.ForwardOfRow, draft.CalendarReply);
            var editorHtml = originalHtml;
            var editorPlain = true;
            var editorWrites = 0;
            vm = new ComposeViewModel(draft, null);
            vm.GetHtml = () => Task.FromResult(editorHtml);
            Task ReplaceEditor(string html, bool plain)
            {
                editorWrites++;
                editorHtml = html;
                editorPlain = plain;
                return Task.CompletedTask;
            }
            vm.RestoreRevisionEditor = ReplaceEditor;
            var revisions = await vm.LoadDraftHistoryAsync();
            var original = revisions.First(r => r.Subject == "Original kept draft");
            Assert(!vm.Dirty && editorWrites == 0 && revisions.All(r => r.AccountId == account.Id && r.MessageId == draft.MessageId),
                "Opening history checkpoints locally without replacing content or marking the draft changed");

            vm.To = "Current Person <current@example.test>; current-incomplete";
            vm.Cc = "current-copy@example.test";
            vm.Bcc = "current-private@example.test";
            vm.ReplyTo = "current-reply@example.test";
            vm.Priority = "Low";
            vm.RequestReadReceipt = false;
            vm.Subject = "Current unsent draft";
            vm.IsPlainText = false;
            vm.RemoveAttachmentCommand.Execute(vm.Attachments.Single());
            // Adding the fixture directly avoids writing the user's recently attached files list.
            vm.Attachments.Add(new AttachmentItem { Path = changedPath });
            editorHtml = changedHtml;
            editorPlain = false;
            vm.MarkEdited();
            File.Delete(originalPath);
            vm.AiPreview = "Stale selection rewrite";
            vm.PreviewKind = "rewrite";
            vm.ReplyDraftPreview = "Stale generated reply";
            vm.SelectedText = "Old selection";

            vm.SupportsRichDraftRestore = false;
            Assert(await vm.RestoreDraftRevisionAsync(original.Id),
                "The plain-editor fallback can restore an explicitly selected plain-text revision");
            Assert(editorHtml == originalHtml && editorPlain && vm.IsPlainText && draft.Html == originalHtml,
                "Restore replaces the complete editor body and restores its plain-text format");
            Assert(vm.To == originalTo && vm.Cc == originalCc && vm.Bcc == originalBcc && vm.ReplyTo == originalReplyTo
                && vm.Subject == "Original kept draft" && vm.Priority == "High" && vm.RequestReadReceipt && vm.ShowCcBcc,
                "Restore preserves raw recipient input, Bcc, Reply-To, subject, priority and receipt options");
            Assert(identity == (draft.AccountId, draft.MessageId, draft.Mode, draft.ThreadKey, draft.SourceDraftRow,
                    draft.InReplyTo, draft.References, draft.ForwardOfRow, draft.CalendarReply)
                && draft.LocalDraftId == localId && vm.LocalDraftId == localId,
                "Restore keeps this compose window's account, stable message identity, reply context and local row");
            Assert(vm.Attachments.Count == 1, $"Restore has one attachment, actual count={vm.Attachments.Count}");
            Assert(vm.Attachments[0].Carried, "The restored attachment is carried by the MIME snapshot");
            Assert(vm.Attachments[0].Name == "original-attachment.txt",
                $"Restore keeps the attachment name, actual name={vm.Attachments[0].Name}");
            Assert(draft.AttachmentPaths.Count == 0,
                $"Restore retains no original attachment paths, actual count={draft.AttachmentPaths.Count}");
            AssertDraftAttachmentBytes(vm.Attachments[0].Part!, originalSnapshotBytes,
                "Restored attachment after its original source file was deleted");
            Assert(vm.Dirty && !vm.RevisionBusy && vm.AiPreview.Length == 0 && vm.ReplyDraftPreview.Length == 0
                && vm.PreviewKind.Length == 0 && vm.SelectedText.Length == 0,
                "Restore is an unsent edit and clears stale AI previews and selections");
            var current = engine.Store.GetDraftRevisions(account.Id, draft.MessageId).First(r => r.Subject == "Current unsent draft");
            var backup = engine.Store.OpenDraftRevision(account.Id, draft.MessageId, current.Id);
            Assert(backup != null && backup.Html == changedHtml && backup.Subject == "Current unsent draft"
                && backup.To.Contains("current-incomplete") && !backup.IsPlainText,
                "Restore checkpoints the latest unsaved content and attachment bytes before replacing them");
            AssertDraftAttachmentBytes(backup!.CarriedParts.Single(), changedSnapshotBytes, "Pre-restore current-content checkpoint");
            Assert(engine.Store.GetLocalDraft(localId)?.PendingUpload == true,
                "Restore preserves the existing kept draft's pending-upload policy");

            var beforeUnsupportedRestoreWrites = editorWrites;
            Exception? unsupportedRestoreFailure = null;
            try { await vm.RestoreDraftRevisionAsync(current.Id); }
            catch (InvalidOperationException ex) { unsupportedRestoreFailure = ex; }
            var afterUnsupportedRestore = engine.OpenLocalDraft(localId);
            Assert(unsupportedRestoreFailure?.Message.Contains("rich message editor", StringComparison.OrdinalIgnoreCase) == true
                && editorWrites == beforeUnsupportedRestoreWrites && editorHtml == originalHtml && editorPlain
                && vm.Subject == "Original kept draft" && vm.To == originalTo && vm.IsPlainText
                && vm.Priority == "High" && vm.RequestReadReceipt && vm.Dirty && !vm.RevisionBusy
                && draft.LocalDraftId == localId && draft.MessageId == identity.MessageId
                && vm.Attachments.Count == 1
                && afterUnsupportedRestore?.Subject == "Original kept draft" && afterUnsupportedRestore.IsPlainText
                && afterUnsupportedRestore.Html.Contains("Original body."),
                "The plain-editor fallback rejects a rich revision before changing the editor, fields or saved draft");
            AssertDraftAttachmentBytes(vm.Attachments[0].Part!, originalSnapshotBytes, "Unsupported-restore editor attachment");
            AssertDraftAttachmentBytes(afterUnsupportedRestore!.CarriedParts.Single(), originalSnapshotBytes,
                "Unsupported-restore saved attachment");
            vm.SupportsRichDraftRestore = true;

            // The editor can fail after replacing DOM/text. The view model must put back content and fields.
            var beforeFailedRestoreWrites = editorWrites;
            var failOnce = true;
            vm.RestoreRevisionEditor = (html, plain) =>
            {
                ReplaceEditor(html, plain);
                if (failOnce)
                {
                    failOnce = false;
                    throw new InvalidOperationException("Synthetic editor replacement failure");
                }
                return Task.CompletedTask;
            };
            Exception? restoreFailure = null;
            try { await vm.RestoreDraftRevisionAsync(current.Id); }
            catch (InvalidOperationException ex) { restoreFailure = ex; }
            Assert(restoreFailure?.Message == "Synthetic editor replacement failure"
                && editorWrites == beforeFailedRestoreWrites + 2 && editorHtml == originalHtml && editorPlain
                && vm.Subject == "Original kept draft" && vm.To == originalTo && vm.RequestReadReceipt
                && vm.Attachments.Count == 1
                && draft.LocalDraftId == localId && vm.Dirty && !vm.RevisionBusy,
                "A partial editor replacement failure rolls back content and fields without losing identity or dirty state");
            AssertDraftAttachmentBytes(vm.Attachments[0].Part!, originalSnapshotBytes, "Failed-editor rollback attachment");
            Assert(engine.OpenLocalDraft(localId)?.Subject == "Original kept draft",
                "Failure rollback also restores the current local draft on disk");
            vm.RestoreRevisionEditor = ReplaceEditor;

            // A future non-modal caller must not overwrite input changed while the checkpoint was being built.
            var beforeRacingRestoreWrites = editorWrites;
            vm.GetHtml = () =>
            {
                vm.Subject = "Typed while history was loading";
                return Task.FromResult(editorHtml);
            };
            Exception? concurrentEditFailure = null;
            try { await vm.RestoreDraftRevisionAsync(current.Id); }
            catch (InvalidOperationException ex) { concurrentEditFailure = ex; }
            Assert(concurrentEditFailure?.Message.Contains("draft changed", StringComparison.OrdinalIgnoreCase) == true
                && editorWrites == beforeRacingRestoreWrites && editorHtml == originalHtml
                && vm.Subject == "Typed while history was loading" && !vm.RevisionBusy,
                "A concurrent edit aborts restore before any editor replacement");
            vm.GetHtml = () => Task.FromResult(editorHtml);

            Assert(await vm.RestoreDraftRevisionAsync(current.Id) && editorHtml == changedHtml && !editorPlain
                && vm.Subject == "Current unsent draft" && vm.Priority == "Low" && !vm.RequestReadReceipt,
                "A later explicit restore can recover the earlier unsent rich-text version");
            // The restored local row is already persisted. Test clear independently of the normal pending
            // autosave policy; a scheduled autosave may legitimately create a new history entry while dirty.
            vm.Dirty = false;
            await vm.ClearDraftHistoryAsync();
            Assert(engine.Store.GetDraftRevisions(account.Id, draft.MessageId).Count == 0
                && vm.Subject == "Current unsent draft" && editorHtml == changedHtml && !vm.Dirty
                && engine.Store.GetLocalDraft(localId)?.Subject == "Current unsent draft",
                "Clearing history deletes only revisions, not the open message or saved local draft");

            Assert(await vm.DiscardAsync(), "Discard remains available after revision restoration");
            var kept = engine.OpenLocalDraft(localId);
            Assert(kept?.Subject == "Original kept draft" && kept.Html.Contains("Original body."),
                "Discard restores the originally kept baseline, not the last revision chosen from history");
            AssertDraftAttachmentBytes(kept!.CarriedParts.Single(), originalSnapshotBytes, "Discard baseline attachment");

            var binaryPath = Path.Combine(root, "history-binary-source.bin");
            var binaryBytes = new byte[] { 0, 10, 13, 255, 128, 13, 10, 1, 2, 3 };
            File.WriteAllBytes(binaryPath, binaryBytes);
            var binaryDraft = new Draft
            {
                AccountId = account.Id, MessageId = "history-binary@example.test", Html = "<p>Binary fixture</p>",
                AttachmentPaths = new() { binaryPath },
            };
            var binaryId = engine.CaptureDraftRevision(binaryDraft);
            Assert(binaryId != 0, "A binary attachment is captured as a self-contained revision");
            File.Delete(binaryPath);
            var binaryRevision = engine.Store.OpenDraftRevision(account.Id, binaryDraft.MessageId, binaryId);
            Assert(binaryRevision != null && binaryRevision.AttachmentPaths.Count == 0,
                "A binary revision restores independently of its deleted file path");
            AssertDraftAttachmentBytes(binaryRevision!.CarriedParts.Single(), binaryBytes, "Binary revision preserves exact source bytes");
            Assert(http.Requests == 0 && engine.Store.GetOutbox().Count == 0,
                "History capture, preview, restore, rollback and clear never contact a provider or queue mail");
            Console.WriteLine("DRAFT_HISTORY_SMOKE_PASSED");
        }
        finally
        {
            vm?.Detach();
            AppServices.Engine = originalEngine;
        }
    }

    private static byte[] DraftAttachmentBytes(MimeEntity entity)
    {
        Assert(entity is MimePart { Content: not null }, "Revision attachment has independently readable MIME content");
        using var bytes = new MemoryStream();
        ((MimePart)entity).Content!.DecodeTo(bytes);
        return bytes.ToArray();
    }

    private static void AssertDraftAttachmentBytes(MimeEntity entity, byte[] expected, string reason)
    {
        var actual = DraftAttachmentBytes(entity);
        Assert(actual.SequenceEqual(expected),
            $"{reason}: expected {expected.Length} bytes ({Convert.ToHexString(expected)}), actual {actual.Length} bytes ({Convert.ToHexString(actual)})");
    }
}
