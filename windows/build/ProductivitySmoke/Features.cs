using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Magpie.App.Services;
using Magpie.App;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static void CheckFeatureSettings(Window owner, MailEngine engine, string output, string theme)
    {
        Typography.Start(engine.Config.Typography);
        var window = new SettingsWindow("Fonts") { Owner = owner, ShowActivated = false };
        window.Show();
        var vm = (SettingsViewModel)window.DataContext;
        Assert(vm.TextRoles.Count == 53, "Every approved text role is editable in native Settings");
        Assert(vm.TextRoles.Single(r => r.Id == "composeBody").Family == "Arial"
            && vm.TextRoles.Single(r => r.Id == "composeBody").SizeText == "18", "Native writing defaults retain Arial 18px");
        var subject = vm.TextRoles.Single(r => r.Id == "listSubject");
        subject.Family = "Georgia";
        subject.SizeText = "21.5";
        PumpUntil(() => vm.HasChanges);
        Assert(vm.CanApplySettings && !engine.Config.Typography.HasOverride("listSubject"), "Font edits are staged until Apply");
        vm.ApplyCommand.Execute(null);
        Assert(!vm.HasChanges && engine.Config.Typography.Resolve("listSubject").Size == 21.5,
            "Apply saves the independent role and accepts the baseline");
        Typography.Apply(engine.Config.Typography);

        var text = new TextBlock { Text = "A conversation subject", FontSize = 13, FontFamily = new FontFamily("Segoe UI") };
        Typography.SetRole(text, "listSubject");
        var probe = new Window { Content = text, Width = 420, Height = 180, Owner = owner, ShowActivated = false };
        probe.Show();
        Typography.ApplyWindow(probe);
        Assert(text.FontSize == 21.5 && text.FontFamily.Source.Contains("Georgia"), "The saved role changes real WPF text");
        vm.TextRoleSearch = "signature";
        Assert(vm.TextRoleView.Cast<EditableTextRole>().Any(r => r.Id == "signature"), "Native font search spans all areas");
        vm.TextRoleSearch = "";
        subject.SizeText = "2";
        Assert(vm.HasTypographyErrors && !vm.CanApplySettings, "Invalid native size input cannot be applied");
        subject.Reset();
        PumpUntil(() => vm.HasChanges);
        vm.ApplyCommand.Execute(null);
        Typography.Apply(engine.Config.Typography);
        Assert(text.FontSize == 13 && text.FontFamily.Source.Contains("Segoe UI"), "Reset restores the original WPF style");
        probe.Close();
        window.UpdateLayout();
        Capture(window, Path.Combine(output, theme + "-fonts-text.png"));
        vm.Page = "AiReplies";
        window.UpdateLayout();
        Assert(window.FindName("RowAiReplySettings") is AiReplySettingsView, "The native AI-reply settings page is mounted");
        Capture(window, Path.Combine(output, theme + "-ai-reply-settings.png"));
        window.Close();
        Assert(SettingsViewModel.Search("font size").Any(h => h.Page == "Fonts"), "Global Settings search finds typography");
        Assert(SettingsViewModel.Search("reply tone").Any(h => h.Page == "AiReplies"), "Global Settings search finds reply preferences");
    }

    private static void CheckAiReplyFlow(string root, MailEngine originalEngine)
    {
        var handler = new ReplyHttp();
        using var engine = new MailEngine(new AppPaths(Path.Combine(root, "reply-flow")), new TestProtector(), handler);
        AppServices.Engine = engine;
        try
        {
            var account = new Account { Id = "primary", Email = "me@example.test", DisplayName = "Example User", Signature = "Regards, Example User", SignatureOnReplies = true };
            engine.Config.Accounts.Add(account);
            engine.Config.Accounts.Add(new Account { Id = "separate", Email = "other@example.test" });
            engine.Config.Ai.Enabled = true;
            engine.Config.Ai.Connections = new() { new AiConnection { Id = "reply-smoke", Name = "Local fake", Provider = AiProviderKind.Ollama, Endpoint = "http://localhost/v1", Model = "smoke-model" } };
            engine.Config.Ai.ActiveId = "reply-smoke";
            engine.Config.Ai.UseActive();
            engine.Config.Ai.ReplyDrafts.Enabled = true;
            engine.Settings.Save(notify: false);
            var folder = engine.Store.UpsertFolder(new MailFolder { AccountId = account.Id, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
            var original = engine.Store.InsertMessages(new[] { new MessageRow { AccountId = account.Id, FolderId = folder, Uid = 1,
                MessageId = "reply-smoke@example.test", ThreadKey = "reply-smoke", FromAddress = "maya@example.test", FromName = "Maya",
                To = "me@example.test, alex@example.test", Cc = "nina@example.test, other@example.test", Subject = "Design agenda",
                Date = DateTimeOffset.Now, SortDate = DateTimeOffset.Now } }).Single();
            var body = new MessageBody { Text = "Here is the design agenda. Please share any comments.", Html = "<p>Here is the design agenda. Please share any comments.</p>", ImagesComplete = true };
            engine.Store.SaveBody(original.Id, body);
            var draft = Composer.Prepare(ComposeMode.ReplyAll, account, original, body, engine.MyAddresses, null);
            var vm = new ComposeViewModel(draft, null);
            var typed = "";
            var insertedMode = "";
            var insertionChoices = 0;
            vm.GetHtml = () => Task.FromResult(ComposeViewModel.TextToParagraphs(typed) + Composer.SignatureHtml(account, true));
            vm.GetNewText = () => Task.FromResult(typed);
            vm.ChooseReplyInsertion = () => { insertionChoices++; return Task.FromResult<string?>("appendBody"); };
            vm.EditorCommand = (command, value) =>
            {
                Assert(command == "replyDraft", "Reply insertion uses the guarded editor command");
                using var payload = JsonDocument.Parse(value);
                Assert(payload.RootElement.GetProperty("expectedNewText").GetString() == typed, "Insertion checks the current new text");
                insertedMode = payload.RootElement.GetProperty("mode").GetString()!;
                var generated = payload.RootElement.GetProperty("text").GetString();
                typed = insertedMode == "appendBody" ? typed + "\n\n" + generated : generated!;
                return Task.CompletedTask;
            };
            var initial = vm.ReplyEditorReadyAsync();
            PumpUntil(() => initial.IsCompleted);
            initial.GetAwaiter().GetResult();
            Assert(handler.Requests == 1 && vm.ReplyDraftPreview.Contains("Thank you"), "Reply All automatically generates one preview");
            Assert(typed.Length == 0, "Automatic generation never silently inserts text");
            Assert(vm.To.Contains("maya@example.test") && vm.Cc.Contains("alex@example.test") && vm.Cc.Contains("nina@example.test")
                && !vm.To.Contains(account.Email) && !vm.Cc.Contains("other@example.test"), "Reply All preserves recipients and excludes every own address");
            vm.ReplyEditorReadyAsync().GetAwaiter().GetResult();
            Assert(handler.Requests == 1, "Repeated editor-ready events do not trigger a second request");
            typed = "My own opening.";
            var use = vm.UseReplyDraftCommand.ExecuteAsync(null);
            PumpUntil(() => use.IsCompleted);
            use.GetAwaiter().GetResult();
            Assert(insertionChoices == 1 && insertedMode == "appendBody" && typed.StartsWith("My own opening.") && typed.Contains("Thank you"),
                "Using a reply draft asks before appending to typed text");

            vm.ReplyTone = "Polite";
            var regenerate = vm.GenerateReplyDraftCommand.ExecuteAsync(null);
            PumpUntil(() => regenerate.IsCompleted);
            regenerate.GetAwaiter().GetResult();
            Assert(handler.Requests == 2 && engine.Config.Ai.ReplyDrafts.Tone == "Formal", "Per-draft tone overrides do not change saved defaults");
            engine.Config.Ai.Enabled = false;
            engine.Settings.Save();
            PumpUntil(() => !vm.ShowReplyAi);
            var gated = vm.GenerateReplyDraftCommand.ExecuteAsync(null);
            PumpUntil(() => gated.IsCompleted);
            gated.GetAwaiter().GetResult();
            Assert(handler.Requests == 2 && !vm.CanUseReplyDraft, "Master Off gates regeneration and stale insertion");
            vm.Detach();
        }
        finally { AppServices.Engine = originalEngine; }
    }

    private sealed class ReplyHttp : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsolutePath != "/v1/chat/completions")
                throw new InvalidOperationException("Unexpected request in isolated reply smoke");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Hi Maya,\\n\\nThank you for sharing the design agenda.\"}}]}", Encoding.UTF8, "application/json")
            });
        }
    }
}
