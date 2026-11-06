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

            await CheckComposeQuickRepliesAsync(window, vm, engine, theme);
            vm.MarkEdited();
            vm.Detach();
            Assert(typeof(ComposeViewModel).GetField("_writingStatisticsTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm) == null,
                theme + ": detaching stops and releases the live-count timer");
            var readsBeforeClose = reads;
            await vm.RefreshWritingStatisticsAsync();
            Assert(reads == readsBeforeClose, theme + ": detached editors are not queried for counts");
            Assert(!await vm.InsertQuickReplyAsync("Do not insert after closing") && vm.Status.Contains("no longer editable"),
                theme + ": detached composers reject quick reply insertion");
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

    private static async Task CheckComposeQuickRepliesAsync(ComposeWindow window, ComposeViewModel vm, MailEngine engine, string theme)
    {
        const string reply = "Thanks_{{first_name}} <b>literal</b>\nPlease review {{subject}}.";
        var text = "Unchanged opening";
        var insertions = 0;
        var originalReplies = engine.Config.QuickReplies;
        var originalGetWritingText = vm.GetWritingText;
        var originalGetHtml = vm.GetHtml;
        var originalEditorCommand = vm.EditorCommand;
        var originalIsPlainText = vm.IsPlainText;
        var outboxBefore = engine.Store.GetOutbox(includeDone: true, withMime: false).Select(x => x.Id).OrderBy(x => x).ToArray();
        var attachment = new AttachmentItem
        {
            Path = "retained.txt", Carried = true,
            Part = new MimeKit.TextPart("plain") { Text = "Original attachment bytes" },
        };
        vm.Subject = "Subject must stay";
        vm.To = "Jane Example <jane@example.test>";
        vm.Cc = "copy@example.test";
        vm.Bcc = "hidden@example.test";
        vm.ReplyTo = "reply@example.test";
        vm.Priority = "High";
        vm.RequestReadReceipt = true;
        vm.IsPlainText = false;
        vm.Attachments.Add(attachment);
        var from = vm.From;
        var queuedField = typeof(ComposeViewModel).GetField("_queued", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var readyField = typeof(ComposeWindow).GetField("_editorReady", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var plainField = typeof(ComposeWindow).GetField("_plain", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalReady = (bool)readyField.GetValue(window)!;
        var originalPlain = (bool)plainField.GetValue(window)!;
        Func<string, string, Task> insert = (kind, value) =>
        {
            Assert(kind == "insert", theme + ": quick replies only request insertion, never a send or body replacement");
            insertions++;
            text += "\n" + value;
            return Task.CompletedTask;
        };
        void AssertDraftPreserved(string scenario)
        {
            Assert(ReferenceEquals(vm.From, from) && vm.Subject == "Subject must stay"
                && vm.To == "Jane Example <jane@example.test>" && vm.Cc == "copy@example.test" && vm.Bcc == "hidden@example.test"
                && vm.ReplyTo == "reply@example.test" && vm.Priority == "High" && vm.RequestReadReceipt
                && vm.Attachments.Count == 1 && ReferenceEquals(vm.Attachments[0], attachment)
                && ((MimeKit.TextPart)attachment.Part!).Text == "Original attachment bytes"
                && !(bool)queuedField.GetValue(vm)!
                && engine.Store.GetOutbox(includeDone: true, withMime: false).Select(x => x.Id).OrderBy(x => x).SequenceEqual(outboxBefore),
                theme + ": " + scenario + " preserves all addressing, subject, options and attachment content without queuing a message");
        }
        try
        {
            vm.GetWritingText = () => Task.FromResult(text);
            vm.GetHtml = () => Task.FromResult(Composer.TextToParagraphs(text));
            vm.EditorCommand = insert;
            Assert(await vm.InsertQuickReplyAsync(reply) && text == "Unchanged opening\n" + reply && !vm.IsPlainText
                && vm.Status.Contains("editable text") && vm.Dirty,
                theme + ": a quick reply inserts literal editable text without expanding template tokens");
            AssertDraftPreserved("Quick reply insertion");
            var beforeEmpty = text;
            Assert(!await vm.InsertQuickReplyAsync(null) && !await vm.InsertQuickReplyAsync(" \r\n\t")
                && text == beforeEmpty && insertions == 1 && vm.Status.Contains("empty"),
                theme + ": empty quick replies leave the draft untouched and explain how to edit the reply");

            foreach (var operation in new[] { "send", "restore", "attachment" })
            {
                vm.Sending = operation == "send";
                vm.RevisionBusy = operation == "restore";
                vm.AttachmentOperationBusy = operation == "attachment";
                Assert(!await vm.InsertQuickReplyAsync("Must not insert") && text == beforeEmpty && insertions == 1
                    && vm.Status.Contains("current send, attachment or draft restore"),
                    theme + ": quick replies cannot mutate a draft during " + operation);
                Assert(!await vm.InsertTemplateAsync(new QuickTemplate { Name = "Busy fixture", Body = "Must not insert", Subject = "Must not replace" })
                    && text == beforeEmpty && vm.Subject == "Subject must stay",
                    theme + ": templates share the same insertion protection during " + operation);
                vm.Sending = vm.RevisionBusy = vm.AttachmentOperationBusy = false;
            }
            queuedField.SetValue(vm, true);
            Assert(!await vm.InsertQuickReplyAsync("Must not insert") && text == beforeEmpty && insertions == 1,
                theme + ": an already queued message cannot receive a quick reply");
            queuedField.SetValue(vm, false);
            vm.EditorCommand = null;
            Assert(!await vm.InsertQuickReplyAsync("Must not insert") && text == beforeEmpty && vm.Status.Contains("not ready"),
                theme + ": a missing editor is a safe, explained no-op");

            var pendingInsertion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.EditorCommand = async (kind, value) => { await pendingInsertion.Task; await insert(kind, value); };
            var first = vm.InsertQuickReplyAsync("One in-flight reply");
            Assert(!first.IsCompleted && !await vm.InsertQuickReplyAsync("Do not duplicate") && text == beforeEmpty,
                theme + ": overlapping quick reply clicks do not duplicate an in-flight insertion");
            pendingInsertion.SetResult(true);
            Assert(await first && text.EndsWith("One in-flight reply", StringComparison.Ordinal) && insertions == 2,
                theme + ": the original pending insertion completes once");
            vm.EditorCommand = (_, _) => Task.FromException(new InvalidOperationException("Fixture insertion failure"));
            var failedSafely = false;
            try { await vm.InsertQuickReplyAsync("Not inserted"); }
            catch (InvalidOperationException ex) { failedSafely = ex.Message == "Fixture insertion failure"; }
            vm.EditorCommand = insert;
            Assert(failedSafely && await vm.InsertQuickReplyAsync("Recovered insertion") && insertions == 3,
                theme + ": an editor failure releases the insertion and draft-save gates for retry");

            engine.Config.QuickReplies = new() { reply, " ", "A second reply" };
            var buildMenu = typeof(ComposeWindow).GetMethod("BuildTemplatesMenu", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var menu = (ContextMenu)buildMenu.Invoke(window, new object[] { new Button() })!;
            var replies = menu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Quick replies");
            var replyItem = replies.Items.OfType<MenuItem>().Single(x => x.ToolTip?.ToString() == reply);
            Assert(replyItem.Header is TextBlock label && label.Text.Contains("Thanks_{{first_name}}")
                && System.Windows.Automation.AutomationProperties.GetName(replyItem) == "Insert quick reply: " + reply
                && replies.Items.OfType<MenuItem>().Count(x => x.ToolTip != null) == 2
                && replies.Items.OfType<MenuItem>().Any(x => x.Header?.ToString() == "Manage quick replies…"),
                theme + ": Templates exposes labelled, accessible quick replies and their existing Settings page");
            var beforeClick = insertions;
            readyField.SetValue(window, false);
            replyItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert(insertions == beforeClick && vm.Status.Contains("still opening"),
                theme + ": the actual quick reply menu handler refuses an editor that is still opening");
            readyField.SetValue(window, true);
            replyItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(() => insertions == beforeClick + 1);
            Assert(text.EndsWith(reply, StringComparison.Ordinal), theme + ": clicking a configured menu reply reaches literal draft insertion");
            AssertDraftPreserved("Quick reply menu selection");
            engine.Config.QuickReplies = new() { "", " \n " };
            var emptyMenu = (ContextMenu)buildMenu.Invoke(window, new object[] { new Button() })!;
            var emptyReplies = emptyMenu.Items.OfType<MenuItem>().Single(x => x.Header?.ToString() == "Quick replies");
            Assert(emptyReplies.Items.OfType<MenuItem>().Any(x => !x.IsEnabled && x.Header?.ToString() == "No quick replies configured")
                && emptyReplies.Items.OfType<MenuItem>().Any(x => x.IsEnabled && x.Header?.ToString() == "Manage quick replies…"),
                theme + ": an empty quick reply list remains useful without an actionable empty insertion");

            // Exercise the native fallback's real selection code without launching WebView2 or prompting for an account.
            plainField.SetValue(window, true);
            vm.IsPlainText = true;
            var plainEditor = (TextBox)window.FindName("PlainEditor");
            plainEditor.Text = "Before selected after";
            plainEditor.Select("Before ".Length, "selected".Length);
            var editorCommand = typeof(ComposeWindow).GetMethod("EditorCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            vm.EditorCommand = (kind, value) => (Task)editorCommand.Invoke(window, new object[] { kind, value })!;
            vm.GetWritingText = () => Task.FromResult(plainEditor.Text);
            vm.GetHtml = () => Task.FromResult(Composer.TextToParagraphs(plainEditor.Text));
            Assert(await vm.InsertQuickReplyAsync(reply) && plainEditor.Text.Replace("\r\n", "\n") == "Before " + reply + " after" && vm.IsPlainText,
                theme + ": native plain editor inserts a literal quick reply only at the selected text and retains plain mode");
            AssertDraftPreserved("Native plain quick reply insertion");
        }
        finally
        {
            engine.Config.QuickReplies = originalReplies;
            vm.GetWritingText = originalGetWritingText;
            vm.GetHtml = originalGetHtml;
            vm.EditorCommand = originalEditorCommand;
            vm.IsPlainText = originalIsPlainText;
            vm.Sending = vm.RevisionBusy = vm.AttachmentOperationBusy = false;
            queuedField.SetValue(vm, false);
            readyField.SetValue(window, originalReady);
            plainField.SetValue(window, originalPlain);
            vm.Attachments.Remove(attachment);
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
        const string literalReply = "Thanks_{{first_name}} <b>literal</b>\nRegards {{subject}}.";
        await script("setHtml('<p><strong>Keep bold</strong> selected <em>keep emphasis</em></p><div class=\"magpie-signature\">Saved signature</div><div class=\"magpie-quote\">Saved quote</div>');findInEditor('selected',true)");
        Assert(await script("cmd('insert'," + JsonSerializer.Serialize(literalReply) + ")") == "true"
            && JsonSerializer.Deserialize<string>(await script("ed.innerText"))!.Replace("\r\n", "\n").Contains(literalReply)
            && await script("ed.querySelector('b')===null && ed.querySelector('strong').textContent==='Keep bold' && ed.querySelector('em').textContent==='keep emphasis' && ed.querySelector('.magpie-signature').textContent==='Saved signature' && ed.querySelector('.magpie-quote').textContent==='Saved quote'") == "true",
            "Rich quick replies are literal text at selection, preserving formatting, signature and quote without interpreting markup or tokens");
        await script("setHtml('<p>Plain selected suffix</p>');setEditorPlainText(true);findInEditor('selected',true)");
        Assert(await script("cmd('insert'," + JsonSerializer.Serialize(literalReply) + ")") == "true"
            && JsonSerializer.Deserialize<string>(await script("ed.innerText"))!.Replace("\r\n", "\n") == "Plain " + literalReply + " suffix"
            && await script("ed.querySelector('b')===null && ed.contentEditable==='plaintext-only'") == "true",
            "Plain-message quick replies insert the same literal text without changing message mode");
        await script("saved=document.createRange();saved.selectNodeContents(document.getElementById('linkAddress'))");
        var prior = await script("ed.innerHTML");
        Assert(await script("cmd('insert','Must not insert')") == "false" && await script("ed.innerHTML") == prior,
            "Template insertion rejects a stale or out-of-editor selection without changing the message");
    }
}
