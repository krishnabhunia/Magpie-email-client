using System.IO;
using Magpie.App;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckAiReplySentOnlyFallback(string root, MailEngine originalEngine)
    {
        var http = new RejectHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "reply-sent-only")), new TestProtector(), http);
        AppServices.Engine = engine;
        try
        {
            var account = new Account { Id = "primary", Email = "me@example.test", Signature = "My signature", SignatureOnReplies = true };
            engine.Config.Accounts.Add(account);
            engine.Config.Ai.Enabled = true;
            engine.Config.Ai.Connections = new() { new AiConnection { Id = "sent-local", Provider = AiProviderKind.Ollama, Endpoint = "http://localhost/v1", Model = "isolated" } };
            engine.Config.Ai.ActiveId = "sent-local";
            engine.Config.Ai.UseActive();
            engine.Config.Ai.ReplyDrafts.Enabled = true;
            engine.Config.Ai.ReplyDrafts.Context = AiReplySettings.LatestMessageContext;
            engine.Settings.Save(notify: false);
            var folder = engine.Store.UpsertFolder(new MailFolder { AccountId = account.Id, Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
            var original = engine.Store.InsertMessages(new[] { new MessageRow { AccountId = account.Id, FolderId = folder, Uid = 1,
                MessageId = "own-sent@example.test", ThreadKey = "own-sent", FromAddress = account.Email, To = "recipient@example.test",
                Subject = "My sent message", Date = DateTimeOffset.Now, SortDate = DateTimeOffset.Now } }).Single();
            var body = new MessageBody { Text = "An earlier message I sent.", Html = "<p>An earlier message I sent.</p>", ImagesComplete = true };
            engine.Store.SaveBody(original.Id, body);
            var draft = Composer.Prepare(ComposeMode.Reply, account, original, body, engine.MyAddresses, null);
            var vm = new ComposeViewModel(draft, null);
            try
            {
                var initialHtml = vm.InitialHtml;
                var ready = vm.ReplyEditorReadyAsync();
                PumpUntil(() => ready.IsCompleted);
                ready.GetAwaiter().GetResult();
                Assert(http.Requests == 0 && !vm.ReplyDraftBusy && !vm.CanUseReplyDraft,
                    "A Sent-only conversation has no AI request under latest-received scope");
                Assert(vm.ReplyDraftError.Contains("no received message") && vm.InitialHtml == initialHtml
                    && vm.To.Contains("recipient@example.test"), "Missing received context leaves the correctly addressed manual reply available");
                var retry = vm.GenerateReplyDraftCommand.ExecuteAsync(null);
                PumpUntil(() => retry.IsCompleted);
                retry.GetAwaiter().GetResult();
                Assert(http.Requests == 0 && !vm.CanUseReplyDraft, "Retrying missing received context remains a safe manual fallback");
            }
            finally { vm.Detach(); }
        }
        finally { AppServices.Engine = originalEngine; }
    }
}
