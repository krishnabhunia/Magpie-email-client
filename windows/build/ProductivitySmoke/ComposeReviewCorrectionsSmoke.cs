using System.IO;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckComposeReviewCorrectionsAsync(MailEngine originalEngine, string theme)
    {
        var http = new RejectHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(originalEngine.Paths.Root, "compose-review-" + theme)),
            new TestProtector(), http);
        AppServices.Engine = engine;
        ComposeViewModel? vm = null;
        ComposeViewModel? unknown = null;
        try
        {
            var sourceAccount = new Account { Id = "source", Email = "source@example.test", Enabled = false };
            var senderAccount = new Account { Id = "sender", Email = "sender@example.test", Enabled = false };
            engine.Config.Accounts.Add(sourceAccount);
            engine.Config.Accounts.Add(senderAccount);
            engine.Config.Ai.Enabled = false;
            var original = new MessageRow
            {
                AccountId = sourceAccount.Id, ThreadKey = "review-origin", MessageId = "original@example.test",
                FromAddress = "recipient@example.test", Subject = "Original conversation",
            };
            var draft = Composer.Prepare(ComposeMode.Reply, sourceAccount, original, null,
                new[] { sourceAccount.Email, senderAccount.Email }, null);
            draft.MessageId = "compose-review-" + theme.ToLowerInvariant() + "@example.test";
            var html = draft.Html = "<p>A reviewed reply.</p>";
            var nested = new MimeMessage
            {
                MessageId = "nested@example.test", Subject = "Nested message", Body = new TextPart("plain") { Text = "Nested body stays intact" },
            };
            // Serializing MIME legitimately adds its transport newline; preserve this exact snapshot throughout.
            var originalNestedMime = Composer.ToBytes(nested);
            var carried = new MessagePart("rfc822")
            {
                Message = nested, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = "original.eml" },
            };
            draft.CarriedParts.Add(carried);
            vm = new ComposeViewModel(draft, null)
            {
                GetHtml = () => Task.FromResult(html), GetNewText = () => Task.FromResult("A reviewed reply."),
                RestoreRevisionEditor = (restored, _) => { html = restored; return Task.CompletedTask; },
            };
            var originalItem = vm.Attachments.Single();
            Assert(originalItem.Name == "original.eml", "Compose displays a carried message's actual MIME filename (" + theme + ")");
            vm.RenameAttachment(originalItem, "reviewed résumé.eml");
            Assert(originalItem.Name == "reviewed résumé.eml" && carried.ContentDisposition!.FileName == originalItem.Name
                && carried.ContentType.Name == originalItem.Name && originalNestedMime.SequenceEqual(Composer.ToBytes(nested)),
                "Renaming a carried message changes its displayed and transmitted metadata, not nested content");
            Assert(vm.CanSendAndArchive && draft.ReplyArchiveSource == new ReplyArchiveSource(sourceAccount.Id, original.ThreadKey),
                "A new reply exposes Send & Archive for its original source account");
            vm.From = senderAccount;
            await vm.CaptureDraftRevisionAsync();
            var revision = engine.Store.GetDraftRevisions(senderAccount.Id, draft.MessageId).Single();
            Assert(draft.AccountId == senderAccount.Id && vm.CanSendAndArchive
                && draft.ReplyArchiveSource?.AccountId == sourceAccount.Id,
                "Switching From changes delivery identity without changing archive origin");
            vm.From = sourceAccount;
            await vm.CaptureDraftRevisionAsync();
            vm.From = senderAccount;
            await vm.CaptureDraftRevisionAsync();
            Assert(vm.CanSendAndArchive && draft.ReplyArchiveSource?.AccountId == sourceAccount.Id,
                "Switching the sender back and forth does not retarget the reply source");
            vm.RenameAttachment(vm.Attachments.Single(), "current-unsaved.eml");
            Assert(await vm.RestoreDraftRevisionAsync(revision.Id), "A carried-message revision can be restored");
            Assert(vm.Attachments.Single().Name == "reviewed résumé.eml"
                && MimeAttachmentNames.Get(vm.Attachments.Single().Part!) == "reviewed résumé.eml"
                && draft.AccountId == senderAccount.Id && draft.ReplyArchiveSource?.AccountId == sourceAccount.Id,
                "Restoring content retains source account identity and restored .eml display metadata");
            vm.RenameAttachment(originalItem, "stale-selection.eml");
            Assert(vm.Attachments.Single().Name == "reviewed résumé.eml",
                "A rename action targeting a replaced attachment cannot mutate the restored attachment");
            var localId = vm.LocalDraftId!.Value;
            // Restoration already saved the local row. Drain the shared save gate with no dirty timer work
            // before opening a second fixture view model for that same row.
            vm.Dirty = false;
            await vm.CaptureDraftRevisionAsync();
            vm.Detach();
            var reopened = engine.OpenLocalDraft(localId)!;
            html = reopened.Html;
            vm = new ComposeViewModel(reopened, null)
            {
                GetHtml = () => Task.FromResult(html), GetNewText = () => Task.FromResult("A reviewed reply."),
            };
            Assert(vm.From?.Id == senderAccount.Id && vm.CanSendAndArchive && vm.Attachments.Single().Name == "reviewed résumé.eml",
                "A reopened saved reply retains original-source availability and the renamed .eml attachment");
            await vm.SendAsync(DateTimeOffset.Now.AddDays(1), null, archiveAfterSend: true);
            var queued = engine.Store.GetOutbox().Single();
            Assert(queued.AccountId == senderAccount.Id && queued.Status == OutboxStatus.Queued
                && engine.Store.GetPostSendArchiveSource(queued.Id) == new ReplyArchiveSource(sourceAccount.Id, original.ThreadKey),
                "The real compose send flow schedules from the selected account and archives only the original source");
            using var sent = Composer.FromBytes(queued.Mime);
            var sentAttachment = (MessagePart)sent.Attachments.Single();
            Assert(MimeAttachmentNames.Get(sentAttachment) == "reviewed résumé.eml"
                && sentAttachment.Message is { } sentMessage && originalNestedMime.SequenceEqual(Composer.ToBytes(sentMessage)),
                "The queued MIME preserves both renamed .eml metadata and original nested message content");
            Assert(engine.Store.GetLocalDraft(localId) == null && engine.Store.PendingPostSendArchives().Count == 0,
                "Queue cleanup removes the local compose draft but does not archive before successful delivery");
            var recalled = engine.Recall(queued.Id)!;
            Assert(recalled.AccountId == senderAccount.Id && recalled.ReplyArchiveSource?.AccountId == sourceAccount.Id
                && engine.CanSendAndArchive(recalled) && engine.Store.GetOutbox().Count == 0,
                "Undo returns an unsent draft with its original archive source intact");
            unknown = new ComposeViewModel(new Draft
            {
                AccountId = senderAccount.Id, Mode = ComposeMode.EditDraft, ThreadKey = original.ThreadKey,
                MessageId = "unknown-review-" + theme.ToLowerInvariant() + "@example.test", InReplyTo = "<original@example.test>",
                To = "recipient@example.test", Subject = "Imported draft", Html = "<p>Imported content</p>",
            }, null);
            Assert(!unknown.CanSendAndArchive, "An imported draft with no trusted source does not expose Send & Archive");
            await unknown.SendAsync(DateTimeOffset.Now.AddDays(1), null, archiveAfterSend: true);
            Assert(!unknown.Sending && engine.Store.GetOutbox().Count == 0
                && unknown.Status.Contains("original reply account or conversation", StringComparison.Ordinal),
                "A stale Send & Archive invocation fails closed with a clear status instead of guessing From");
            Assert(http.Requests == 0, "Source-identity and attachment correction smoke never contacts a provider");
            Console.WriteLine("COMPOSE_REVIEW_CORRECTIONS_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            vm?.Detach();
            unknown?.Detach();
            AppServices.Engine = originalEngine;
        }
    }
}
