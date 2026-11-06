using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Settings;

namespace ProductivitySmoke;

internal static partial class Program
{
    private static async Task CheckComposeWritingToolsAsync(MailEngine engine, string theme)
    {
        var window = new ComposeWindow(new Draft
        {
            AccountId = "A", MessageId = "writing-tools-" + theme + "@example.test", To = "Jane Example <jane@example.test>",
            Subject = "Existing subject", Html = "<p>Existing opening</p>",
        }, null);
        var vm = (ComposeViewModel)window.DataContext;
        try
        {
            var text = "Existing opening";
            vm.GetWritingText = () => Task.FromResult(text);
            vm.GetHtml = () => Task.FromResult(Composer.TextToParagraphs(text));
            vm.EditorCommand = (kind, value) =>
            {
                Assert(kind == "insert", theme + ": templates insert at the current selection, not by replacing the draft");
                text += "\n" + value;
                return Task.CompletedTask;
            };
            await vm.RefreshWritingStatisticsAsync();
            Flush(window);
            var footer = (TextBlock)window.FindName("WritingStatisticsFooter");
            Assert(vm.WritingWordCount == 2 && vm.WritingCharacterCount == text.Length
                && footer.Text == vm.WritingStatisticsSummary && footer.Text.Contains("min read"),
                theme + ": live footer renders word, character and reading-time counts");
            Assert(footer.ToolTip?.ToString()?.Contains("signatures, quoted email") == true,
                theme + ": footer states the count scope and reading-time assumption");
            vm.IsPlainText = true;
            Flush(window);
            Assert(footer.Text.StartsWith("All plain text") && footer.ToolTip?.ToString()?.Contains("retained signature or quoted email") == true,
                theme + ": plain-text footer explicitly counts all retained text after section boundaries are removed");
            vm.IsPlainText = false;

            text = "A e\u0301 👩‍💻";
            await vm.RefreshWritingStatisticsAsync();
            Assert(vm.WritingWordCount == 2 && vm.WritingCharacterCount == 5,
                theme + ": live count handles combining marks and joined emoji");

            var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.GetWritingText = () => delayed.Task;
            var olderCount = vm.RefreshWritingStatisticsAsync();
            vm.GetWritingText = () => Task.FromResult("Latest text wins");
            await vm.RefreshWritingStatisticsAsync();
            delayed.SetResult("Old");
            await olderCount;
            Assert(vm.WritingWordCount == 3 && vm.WritingCharacterCount == "Latest text wins".Length,
                theme + ": a delayed old editor count cannot replace the newest result");
            vm.GetWritingText = () => Task.FromException<string>(new InvalidOperationException("Fixture editor was disposed"));
            await vm.RefreshWritingStatisticsAsync();
            Assert(vm.WritingWordCount == 3, theme + ": editor count failure is non-blocking and preserves the last count");

            var reads = 0;
            vm.GetWritingText = () => { reads++; return Task.FromResult(text); };
            vm.MarkEdited();
            vm.MarkEdited();
            vm.MarkEdited();
            PumpUntil(() => reads > 0);
            Assert(reads == 1, theme + ": rapid edits are coalesced into one live count refresh");

            var legacy = new QuickTemplate { Name = "Old template", Body = "Hello {{first_name}} about {{subject}}" };
            var initial = text;
            Assert(await vm.InsertTemplateAsync(legacy) && vm.Subject == "Existing subject"
                && text.StartsWith(initial) && text.EndsWith("Hello Jane about Existing subject"),
                theme + ": legacy body-only templates keep the subject and existing writing");
            var withSubject = new QuickTemplate { Name = "Subject template", Subject = "Agenda for {{first_name}}", Body = "Regarding {{subject}}" };
            Assert(await vm.InsertTemplateAsync(withSubject) && vm.Subject == "Existing subject",
                theme + ": a template cannot overwrite an existing subject by default");
            Assert(await vm.InsertTemplateAsync(withSubject, replaceSubject: true, expectedSubject: "Existing subject")
                && vm.Subject == "Agenda for Jane" && text.EndsWith("Regarding Existing subject"),
                theme + ": explicit subject approval uses one original expansion context for both fields");
            var beforeStale = text;
            Assert(!await vm.InsertTemplateAsync(withSubject, replaceSubject: true, expectedSubject: "Stale subject")
                && text == beforeStale && vm.Subject == "Agenda for Jane",
                theme + ": stale subject approval changes neither field");
            vm.Subject = "";
            Assert(await vm.InsertTemplateAsync(withSubject) && vm.Subject == "Agenda for Jane",
                theme + ": a subject template fills a blank subject");
            vm.EditorCommand = (_, value) => { text += value; vm.Subject = "Newer typed subject"; return Task.CompletedTask; };
            Assert(await vm.InsertTemplateAsync(withSubject, replaceSubject: true) && vm.Subject == "Newer typed subject",
                theme + ": subject edits made during insertion are not overwritten");

            vm.MarkEdited();
            vm.Detach();
            Assert(typeof(ComposeViewModel).GetField("_writingStatisticsTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm) == null,
                theme + ": detaching stops and releases the live-count timer");
            var readsBeforeClose = reads;
            await vm.RefreshWritingStatisticsAsync();
            Assert(reads == readsBeforeClose, theme + ": detached editors are not queried for counts");
        }
        finally
        {
            typeof(ComposeWindow).GetField("_forceClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.Close();
            vm.Detach();
        }

        var originalTemplates = engine.Config.Templates;
        SettingsViewModel? settings = null;
        try
        {
            engine.Config.Templates = new() { new QuickTemplate { Name = "Subject fixture", Subject = "Saved subject", Body = "Saved body" } };
            settings = new SettingsViewModel("Templates");
            var editable = settings.Templates.Single();
            Assert(editable.Subject == "Saved subject" && editable.Body == "Saved body", theme + ": template Settings load both fields");
            editable.Subject = "Edited subject";
            PumpUntil(() => settings.HasChanges);
            Assert(settings.CanApplySettings, theme + ": a subject-only template edit enables Apply");
            settings.ApplyCommand.Execute(null);
            Assert(engine.Config.Templates.Single().Subject == "Edited subject" && engine.Config.Templates.Single().Body == "Saved body",
                theme + ": Apply preserves the body and persists the edited template subject");
        }
        finally
        {
            settings?.Detach();
            engine.Config.Templates = originalTemplates;
            engine.Settings.Save(notify: false);
        }
    }

    private static async Task CheckEditorWritingToolsAsync(Func<string, Task<string>> script)
    {
        const string html = "<p>Only my words.</p><p><img alt=\"Fixture\"></p><div class=\"magpie-signature\">My saved signature</div><div class=\"magpie-quote\">Original quoted conversation</div>";
        await script("setHtml(" + JsonSerializer.Serialize(html) + ")");
        var before = await script("ed.innerHTML");
        var text = JsonSerializer.Deserialize<string>(await script("getNewText(false)"))!;
        Assert(text == "Only my words." && ComposeWritingStatistics.Measure(text).Words == 3,
            "Live writing statistics exclude signature, quotation and synthetic image labels in the real editor");
        Assert(JsonSerializer.Deserialize<string>(await script("getNewText()"))!.Contains("[Embedded content]"),
            "Default new-text extraction retains existing embedded-content safety markers");
        Assert(await script("ed.innerHTML") == before,
            "Counting visible new text does not mutate signature, quote, picture or body DOM");
        await script("setEditorPlainText(true)");
        var plainText = JsonSerializer.Deserialize<string>(await script("getNewText(false)"))!;
        Assert(plainText.Contains("My saved signature") && plainText.Contains("Original quoted conversation"),
            "Plain text retains signature and quote as text, matching the footer's explicit all-text scope");
        await script("setHtml('<p>Keep this opening</p>');findInEditor('opening',true)");
        Assert(await script("cmd('insert','new wording')") == "true"
            && JsonSerializer.Deserialize<string>(await script("ed.textContent"))!.Contains("Keep this new wording"),
            "Template body insertion changes only the current rich-text selection");
        await script("setHtml('<p>Plain draft</p>');setEditorPlainText(true);findInEditor('draft',true)");
        Assert(await script("cmd('insert','saved message')") == "true"
            && JsonSerializer.Deserialize<string>(await script("ed.textContent")) == "Plain saved message"
            && await script("ed.contentEditable") == "\"plaintext-only\"",
            "Templates insert body text into plain-text mode without attempting HTML insertion");
        await script("saved=document.createRange();saved.selectNodeContents(document.getElementById('linkAddress'))");
        var prior = await script("ed.innerHTML");
        Assert(await script("cmd('insert','Must not insert')") == "false" && await script("ed.innerHTML") == prior,
            "Template insertion rejects a stale or out-of-editor selection without changing the message");
    }
}
