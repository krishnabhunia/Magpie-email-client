using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Magpie.App;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

/// <summary>AW1 (11.0.0): "What the reply says", formality, versions, Save as defaults, and the Forward note — with a fake local provider.</summary>
internal static partial class Program
{
    private static void CheckAiWritingFlow(string root, MailEngine originalEngine)
    {
        var http = new WritingHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "ai-writing")), new TestProtector(), http);
        AppServices.Engine = engine;
        try
        {
            var account = new Account { Id = "primary", Email = "me@example.test", DisplayName = "Example User", Signature = "Regards", SignatureOnReplies = true };
            engine.Config.Accounts.Add(account);
            engine.Config.Ai.Enabled = true;
            engine.Config.Ai.Connections = new() { new AiConnection { Id = "writing-smoke", Name = "Local fake", Provider = AiProviderKind.Ollama, Endpoint = "http://localhost/v1", Model = "smoke-model" } };
            engine.Config.Ai.ActiveId = "writing-smoke";
            engine.Config.Ai.UseActive();
            engine.Config.Ai.ReplyDrafts.Enabled = true;
            engine.Settings.Save(notify: false);
            var folder = engine.Store.UpsertFolder(new MailFolder { AccountId = account.Id, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
            var original = engine.Store.InsertMessages(new[] { new MessageRow { AccountId = account.Id, FolderId = folder, Uid = 1,
                MessageId = "writing-smoke@example.test", ThreadKey = "writing-smoke", FromAddress = "maya@example.test", FromName = "Maya",
                To = "me@example.test", Subject = "Q3 vendor report", Date = DateTimeOffset.Now, SortDate = DateTimeOffset.Now } }).Single();
            var body = new MessageBody { Text = "Could you finish the Q3 vendor report by Friday?", Html = "<p>Could you finish the Q3 vendor report by Friday?</p>", ImagesComplete = true };
            engine.Store.SaveBody(original.Id, body);

            // ── Reply: Magpie suggests the answer; the header never shows in the draft ──
            http.Content = "ASK: Can you finish the Q3 vendor report by Friday?\nANSWER: yes\nGIST: Yes, I'll finish it by Friday.\nDRAFT:\nHi Maya,\n\nYes, I'll finish it by Friday.";
            var vm = new ComposeViewModel(Composer.Prepare(ComposeMode.Reply, account, original, body, engine.MyAddresses, null), null);
            try
            {
                Run(vm.ReplyEditorReadyAsync());
                Assert(http.Bodies.Count == 1 && http.Bodies[0].Contains("has not chosen the answer yet"), "The first draft asks Magpie to suggest the answer");
                Assert(vm.ReplyDraftPreview == "Hi Maya,\n\nYes, I'll finish it by Friday.", "The ASK / ANSWER / GIST header is not part of the draft");
                Assert(vm.ReplyAnswer == "Yes, I'll finish it by Friday." && vm.ReplyStance == "yes" && vm.AnswerIsSuggestion
                    && vm.AnswerChoices.Single(c => c.IsSelected).Key == "yes", "Magpie's suggestion fills What the reply says and selects Yes");
                Assert(vm.ReplyAskLine.StartsWith("They ask:"), "The ask line is shown");

                // Yes → No: one click rewrites with the user's answer
                http.Content = "ASK: Can you finish it?\nANSWER: no\nGIST: No.\nDRAFT:\nHi Maya,\n\nI'm not able to take it on this week.";
                Run(vm.PickAnswerCommand.ExecuteAsync(vm.AnswerChoices.Single(c => c.Key == "no")));
                Assert(http.Bodies.Count == 2 && http.Bodies[1].Contains("No, I can") && http.Bodies[1].Contains("Answer type: No"),
                    "Picking No rewrites with the user's answer");
                Assert(vm.ReplyAnswer == "No, I can't do it." && vm.ReplyDraftPreview.Contains("not able") && vm.VersionLabel == "Draft 2 of 2" && vm.HasVersions,
                    "The new draft is version 2; the user's answer is kept");
                vm.PreviousVersionCommand.Execute(null);
                Assert(vm.VersionLabel == "Draft 1 of 2" && vm.ReplyDraftPreview.Contains("Yes, I'll finish"), "Previous version shows the first draft again");
                vm.NextVersionCommand.Execute(null);

                // Typed words decide the answer
                vm.ReplyAnswer = "No, I will not do it";
                Assert(vm.ReplyStance == "other" && vm.AnswerChoices.Single(c => c.IsSelected).Key == "other", "Typing switches the answer to Custom");

                // Style changes collect until Rewrite and never change saved defaults
                vm.ReplyFormality = "Very formal";
                vm.ReplyEtiquette = "Japanese";
                Assert(vm.PendingChanges == 3 && vm.RewriteLabel == "Rewrite · 3 changes", "Changes are counted until Rewrite");
                Assert(http.Bodies.Count == 2, "Changing a style choice doesn't call the provider by itself");
                Run(vm.GenerateReplyDraftCommand.ExecuteAsync(null));
                Assert(http.Bodies.Count == 3 && http.Bodies[2].Contains("Formality: Very formal") && http.Bodies[2].Contains("Business etiquette: Japanese")
                    && http.Bodies[2].Contains("No, I will not do it"), "Rewrite sends every collected choice in one request");
                Assert(vm.PendingChanges == 0 && engine.Config.Ai.ReplyDrafts.Formality == "Formal", "Per-draft choices don't change the saved defaults");
                vm.SaveReplyDefaultsCommand.Execute(null);
                Assert(engine.Config.Ai.ReplyDrafts.Formality == "Very formal" && engine.Config.Ai.ReplyDrafts.Etiquette == "Japanese",
                    "Save these choices as my defaults stores them");
                Assert(vm.CanUseReplyDraft, "Saving defaults doesn't invalidate the shown draft");
            }
            finally { vm.Detach(); }

            // ── Forward: off until its own switch is on ──
            var forward = new ComposeViewModel(Composer.Prepare(ComposeMode.Forward, account, original, body, engine.MyAddresses, null), null);
            try
            {
                var before = http.Bodies.Count;
                Run(forward.ReplyEditorReadyAsync());
                Assert(http.Bodies.Count == before && !forward.ShowReplyAi, "Forward makes no AI request while its switch is off");
            }
            finally { forward.Detach(); }

            engine.Config.Ai.ReplyDrafts.OnForward = true;
            engine.Settings.Save(notify: false);
            http.Content = "Hi,\n\nFYI, Maya needs the Q3 vendor report by Friday.";
            forward = new ComposeViewModel(Composer.Prepare(ComposeMode.Forward, account, original, body, engine.MyAddresses, null), null);
            try
            {
                var before = http.Bodies.Count;
                Run(forward.ReplyEditorReadyAsync());
                Assert(http.Bodies.Count == before + 1 && http.Bodies[^1].Contains("covering note") && http.Bodies[^1].Contains("No recipient is chosen yet")
                    && http.Bodies[^1].Contains("For your information only"), "Forward writes an FYI note before a recipient is chosen");
                Assert(forward.ReplyDraftPreview.StartsWith("Hi,\n") && forward.CanUseReplyDraft, "The note greets with Hi, until someone is added");
                forward.To = "Alex Rivera <alex@example.test>";
                Assert(forward.ReplyDraftPreview.StartsWith("Hi Alex,\n") && http.Bodies.Count == before + 1 && forward.CanUseReplyDraft,
                    "Adding a recipient updates only the greeting, without a new request");
            }
            finally { forward.Detach(); }
        }
        finally { AppServices.Engine = originalEngine; }
    }

    private static void Run(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private sealed class WritingHttp : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public string Content { get; set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/chat/completions")
                throw new InvalidOperationException("Unexpected request in isolated AI writing smoke");
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var json = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Content } } } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
