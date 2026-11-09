using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
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
    private static async Task CheckComposeRemainingFeaturesAsync(MailEngine originalEngine, string theme)
    {
        var http = new WritingHttp();
        var smtpCalls = 0;
        using var engine = new MailEngine(new AppPaths(Path.Combine(originalEngine.Paths.Root, "remaining-compose-" + theme)),
            new TestProtector(), http, sendMessage: (_, _, _) =>
            {
                smtpCalls++;
                throw new InvalidOperationException("This native compose fixture must never transmit mail.");
            });
        AppServices.Engine = engine;
        ComposeWindow? window = null;
        ComposeViewModel? vm = null;
        ComposeViewModel? reopenedVm = null;
        SettingsViewModel? settings = null;
        var closed = false;
        try
        {
            var primary = new Account
            {
                Id = "primary", Email = "me@main.example.test", DisplayName = "Main identity", Enabled = false,
                SenderAliases = new() { new SenderAlias { Email = "team@brand.example.test", Name = "Brand team" } },
            };
            var other = new Account
            {
                Id = "other", Email = "other@second.example.test", DisplayName = "Second identity", Enabled = false,
                SenderAliases = new() { new SenderAlias { Email = "desk@second.example.test", Name = "Second desk" } },
            };
            engine.Config.Accounts.Add(primary);
            engine.Config.Accounts.Add(other);
            engine.Config.Templates = new() { new QuickTemplate { Name = "Category fixture", Subject = "Template subject", Body = "Template body" } };
            engine.Config.Ai.Enabled = engine.Config.Ai.Rewrite = true;
            engine.Config.Ai.Connections = new()
            {
                new AiConnection { Id = "remaining-smoke", Name = "Local fake", Provider = AiProviderKind.Ollama, Endpoint = "http://localhost/v1", Model = "smoke-model" },
            };
            engine.Config.Ai.ActiveId = "remaining-smoke";
            engine.Config.Ai.UseActive();
            engine.Settings.Save(notify: false);

            const string originalText = "Review the plan tomorrow.";
            var html = Composer.TextToParagraphs(originalText);
            var draft = new Draft
            {
                AccountId = primary.Id, MessageId = "remaining-compose-" + theme.ToLowerInvariant() + "@example.test",
                To = "recipient@brand.example.test", Subject = "Remaining compose features", Html = html,
            };
            window = new ComposeWindow(draft, null);
            window.Closed += (_, _) => closed = true;
            vm = (ComposeViewModel)window.DataContext;
            vm.GetHtml = () => Task.FromResult(html);
            vm.GetNewText = vm.GetWritingText = () => Task.FromResult(originalText);
            vm.RestoreRevisionEditor = (restored, _, _) => { html = restored; return Task.CompletedTask; };
            var picker = (ComboBox)window.FindName("SenderIdentityPicker");
            Flush(window);
            Assert(vm.SelectedSender?.Address == primary.Email && vm.HasSenderAliases
                && AutomationProperties.GetName(picker) == "Send as identity",
                theme + ": actual sender picker starts with the account identity and exposes aliases accessibly");

            void SelectSender(string address)
            {
                picker.SetCurrentValue(Selector.SelectedItemProperty, vm.SenderChoices.Single(item => item.Address == address));
                BindingOperations.GetBindingExpression(picker, Selector.SelectedItemProperty)?.UpdateSource();
                Flush(window);
                Assert(vm.SelectedSender?.Address == address, theme + ": the native sender picker writes the selected identity to the view model");
            }

            SelectSender("team@brand.example.test");
            Assert(vm.From?.Id == primary.Id && vm.TemplateSender?.Email == "team@brand.example.test"
                && vm.TemplateSender?.DisplayName == "Brand team",
                theme + ": choosing an alias changes the displayed sender without changing the authenticated account");
            Assert(await vm.KeepAsDraftAsync(closing: false), theme + ": an alias draft saves locally while the account is offline");
            var localId = vm.LocalDraftId ?? throw new InvalidOperationException("The alias draft was not saved locally.");
            var local = engine.OpenLocalDraft(localId) ?? throw new InvalidOperationException("The alias draft cannot be reopened.");
            Assert(local.SenderAddress == "team@brand.example.test" && local.AccountId == primary.Id,
                theme + ": saved draft MIME restores the chosen alias and its owning account");
            await vm.CaptureDraftRevisionAsync();
            var revision = engine.Store.GetDraftRevisions(primary.Id, draft.MessageId).First();
            Assert(engine.Store.OpenDraftRevision(primary.Id, draft.MessageId, revision.Id)?.SenderAddress == "team@brand.example.test",
                theme + ": draft history preserves the sender selection");

            vm.From = other;
            Flush(window);
            Assert(vm.SelectedSender?.Address == other.Email && vm.SenderChoices.All(item => item.Address != "team@brand.example.test"),
                theme + ": switching accounts chooses the new primary sender and removes the other account's aliases");
            SelectSender("desk@second.example.test");
            await vm.CaptureDraftRevisionAsync();
            Assert(draft.AccountId == other.Id && draft.SenderAddress == "desk@second.example.test",
                theme + ": subsequent draft capture uses the new account and its own selected alias");
            vm.From = primary;
            Flush(window);
            Assert(vm.SelectedSender?.Address == primary.Email, theme + ": switching back does not carry an alias across accounts");
            Assert(await vm.RestoreDraftRevisionAsync(revision.Id) && vm.SelectedSender?.Address == "team@brand.example.test",
                theme + ": restoring an alias revision restores the visible sender selection");

            var withoutAlias = engine.AccountById(primary.Id)!.Clone();
            withoutAlias.SenderAliases.Clear();
            engine.UpdateAccount(withoutAlias);
            Flush(window);
            Assert(vm.SelectedSender is { Available: false, Address: "team@brand.example.test" } && vm.HasSenderAliases
                && vm.SelectedSender.Label.Contains("unavailable", StringComparison.Ordinal),
                theme + ": removing the selected alias preserves an explicit unavailable choice instead of silently changing From");
            var rejected = false;
            try { engine.QueueSend(draft, DateTimeOffset.Now.AddDays(1), null); }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("sender", StringComparison.OrdinalIgnoreCase); }
            Assert(rejected && engine.Store.GetOutbox().Count == 0 && smtpCalls == 0,
                theme + ": a removed sender fails before queueing or SMTP instead of falling back to the main identity");
            var restoredAccount = engine.AccountById(primary.Id)!.Clone();
            restoredAccount.SenderAliases.Add(new SenderAlias { Email = "team@brand.example.test", Name = "Brand team" });
            engine.UpdateAccount(restoredAccount);
            Flush(window);
            Assert(vm.SelectedSender is { Available: true, Address: "team@brand.example.test" },
                theme + ": reconfiguring the alias restores its availability without changing the draft's intended From");

            settings = new SettingsViewModel("Templates");
            Assert(!settings.HasChanges, theme + ": remaining-feature Settings start at a clean baseline");
            var editableAccount = settings.Accounts.Single(account => account.Email == primary.Email);
            var originalAliases = editableAccount.SenderAliasesText;
            var originalSignature = editableAccount.SignatureHtml;
            editableAccount.SenderAliasesText = "Unfinished <alias@";
            PumpUntil(() => settings.HasChanges);
            Assert(editableAccount.SenderAliasesError.Length > 0, theme + ": incomplete alias input reports validation without crashing change tracking");
            settings.SignatureAccount = editableAccount;
            settings.UseDefaultSignatureCommand.Execute(null);
            Assert(editableAccount.SignatureHtml.Length > 0,
                theme + ": the unrelated default-signature action remains usable while an alias entry is incomplete");
            editableAccount.SenderAliasesText = originalAliases;
            editableAccount.SignatureHtml = originalSignature;
            PumpUntil(() => !settings.HasChanges);
            vm.Dirty = false;
            settings.Templates.Single().Category = "Reports";
            PumpUntil(() => settings.HasChanges);
            Assert(settings.CanApplySettings && engine.Config.Templates.Single().Category == "",
                theme + ": changing only a template category enables Apply and stays staged until approval");
            settings.ApplyCommand.Execute(null);
            Flush(window);
            Assert(!settings.HasChanges && engine.Config.Templates.Single().Category == "Reports"
                && engine.Config.Templates.Single().Subject == "Template subject" && engine.Config.Templates.Single().Body == "Template body",
                theme + ": category-only Apply persists the category and accepts a clean baseline without changing template content");
            Assert(!vm.Dirty && vm.SelectedSender is { Available: true, Address: "team@brand.example.test" },
                theme + ": an unrelated Settings save preserves the bound sender without dirtying the composer");
            var menu = (ContextMenu)typeof(ComposeWindow).GetMethod("BuildTemplatesMenu", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, new object[] { window })!;
            var group = menu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetName(item) == "Template category: Reports");
            Assert(group.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Category fixture")),
                theme + ": the actual templates menu groups the saved template under its named category");
            settings.Detach();
            settings = null;

            var editorCalls = new List<string>();
            vm.EditorCommand = (kind, _) => { editorCalls.Add(kind); return Task.CompletedTask; };
            vm.SelectedText = originalText;
            var language = (ComboBox)window.FindName("TranslationLanguagePicker");
            language.SetCurrentValue(ComboBox.TextProperty, "Hindi");
            BindingOperations.GetBindingExpression(language, ComboBox.TextProperty)?.UpdateSource();
            Flush(window);
            var translate = (Button)window.FindName("TranslateSelectionButton");
            Assert(vm.TranslationLanguage == "Hindi" && ReferenceEquals(translate.Command, vm.RewriteCommand)
                && Equals(translate.CommandParameter, "Translate") && http.Bodies.Count == 0 && editorCalls.Count == 0,
                theme + ": selecting a translation language makes no provider request, editor edit or send");
            http.Content = "कृपया कल की योजना देखें।";
            Run(vm.RewriteCommand.ExecuteAsync("Translate"));
            using var request = JsonDocument.Parse(http.Bodies.Single());
            var requestText = string.Join("\n", request.RootElement.GetProperty("messages").EnumerateArray()
                .Select(message => message.GetProperty("content").GetString()));
            Assert(requestText.Contains("into Hindi", StringComparison.Ordinal) && requestText.Contains(originalText, StringComparison.Ordinal),
                theme + ": explicit translation sends the selected text and chosen target to the configured fake provider");
            Assert(vm.PreviewKind == "rewrite" && vm.AiPreview == http.Content && vm.AiError.Length == 0
                && editorCalls.SequenceEqual(new[] { "captureRewrite" }) && html == Composer.TextToParagraphs(originalText)
                && engine.Store.GetOutbox().Count == 0 && smtpCalls == 0,
                theme + ": translated output remains a review preview and is neither applied to the editor nor queued automatically");
            vm.DiscardPreviewCommand.Execute(null);

            await vm.SendAsync(DateTimeOffset.Now.AddDays(1), null);
            var queued = engine.Store.GetOutbox().Single();
            using var outgoing = Composer.FromBytes(queued.Mime);
            Assert(queued.AccountId == primary.Id && outgoing.From.Mailboxes.Single().Address == "team@brand.example.test",
                theme + ": explicit scheduled Send keeps the alias in From while the outbox retains its authenticated account");
            var recalled = engine.Recall(queued.Id) ?? throw new InvalidOperationException("Scheduled alias message could not be undone.");
            reopenedVm = new ComposeViewModel(recalled, null);
            Assert(reopenedVm.From?.Id == primary.Id && reopenedVm.SelectedSender?.Address == "team@brand.example.test"
                && engine.Store.GetOutbox().Count == 0 && smtpCalls == 0 && http.Bodies.Count == 1,
                theme + ": Undo reopens the alias selection and none of these lifecycle operations transmits mail");
            Console.WriteLine("COMPOSE_REMAINING_FEATURES_SMOKE_PASSED theme=" + theme);
        }
        finally
        {
            settings?.Detach();
            reopenedVm?.Detach();
            if (vm != null) { vm.Dirty = false; vm.Detach(); }
            if (!closed) window?.Close();
            AppServices.Engine = originalEngine;
        }
    }
}
