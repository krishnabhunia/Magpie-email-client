using System.Net;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;
using Magpie.Mac.Views;
using MimeKit;

namespace Magpie.Mac.Tests;

public partial class HeadlessTests
{
    [AvaloniaFact]
    public Task Mac_server_plain_draft_restores_editor_mode_and_keeps_literal_link_when_saved_and_sent() =>
        CheckMacPlainDraftAsync(local: false, "https://example.com/guide?language=hi&mode=plain#section-2");

    [AvaloniaFact]
    public Task Mac_local_plain_draft_restores_editor_mode_and_keeps_literal_mailto_when_saved_and_sent() =>
        CheckMacPlainDraftAsync(local: true, "mailto:person@example.net?subject=Hello%20there");

    private static async Task CheckMacPlainDraftAsync(bool local, string address)
    {
        using var fixture = new PlainComposeFixture();
        const string original = "  code\tvalue\n\nLiteral <tag> & text\n";
        using var source = new MimeMessage
        {
            Subject = "Plain draft fixture",
            Body = new TextPart("plain") { Text = original, ContentTransferEncoding = ContentEncoding.Base64 },
        };
        source.From.Add(new MailboxAddress("Sender", fixture.Account.Email));
        source.To.Add(new MailboxAddress("Recipient", "recipient@example.org"));
        var draft = Composer.FromMime(source, fixture.Account.Id, "");
        if (local)
        {
            var id = fixture.Engine.SaveLocalDraft(draft, pendingUpload: false);
            draft = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(id));
        }
        Assert.True(draft.IsPlainText);
        var (window, vm, editor) = fixture.Open(draft);
        vm.Dirty = false;
        Assert.False(await window.InsertLinkAsync(address)); // An uninitialized editor must not accept rich edits.
        await window.InitializeEditorAsync();
        Assert.True(vm.IsPlainText);
        Assert.True(editor.PlainText);
        Assert.True(editor.BodySectionsLost);
        Assert.Equal(original, MimeText.EditorHtmlToPlainText(editor.Html));
        Assert.False(vm.Dirty);

        Assert.True(await window.InsertLinkAsync(address));
        Assert.Equal("insertText", editor.LastInsertion);
        Assert.True(vm.Dirty);
        var expected = original + ComposeViewModel.AllowedLink(address);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(editor.Html));
        Assert.DoesNotContain("<a ", editor.Html, StringComparison.OrdinalIgnoreCase);
        var calls = editor.Calls.Count;
        Assert.False(await window.InsertLinkAsync("javascript:alert(1)"));
        Assert.Equal(calls, editor.Calls.Count);

        Assert.True(await vm.SaveDraftAsync()); // No account is connected: the actual offline fallback persists it.
        Assert.True(draft.IsPlainText);
        var reopened = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(draft.LocalDraftId!.Value));
        Assert.True(reopened.IsPlainText);
        Assert.True(reopened.BodySectionsLost);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(reopened.Html));
        using var saved = Composer.FromBytes(fixture.Engine.LocalDrafts().Single().Mime);
        Assert.Null(saved.HtmlBody);
        Assert.Equal(expected, Lines(saved.TextBody));

        await vm.SendAsync(DateTimeOffset.UtcNow.AddDays(2));
        var queued = Assert.Single(fixture.Engine.Store.GetOutbox());
        using var sent = Composer.FromBytes(queued.Mime);
        Assert.Null(sent.HtmlBody);
        Assert.Equal(expected, Lines(sent.TextBody));
        var recalled = Assert.IsType<Draft>(fixture.Engine.Recall(queued.Id));
        Assert.True(recalled.IsPlainText);
        Assert.True(recalled.BodySectionsLost);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(recalled.Html));
        var (recalledWindow, _, recalledEditor) = fixture.Open(recalled);
        await recalledWindow.InitializeEditorAsync();
        Assert.True(recalledEditor.PlainText);
        Assert.Equal(expected, MimeText.EditorHtmlToPlainText(recalledEditor.Html));
    }

    [AvaloniaFact]
    public async Task Mac_rich_draft_keeps_rich_link_editing_and_the_html_alternative()
    {
        using var fixture = new PlainComposeFixture();
        var draft = new Draft
        {
            AccountId = fixture.Account.Id, To = "recipient@example.org", Subject = "Rich fixture",
            Html = "<p>Rich <strong>message</strong></p>", IsPlainText = false,
        };
        var (window, vm, editor) = fixture.Open(draft);
        await window.InitializeEditorAsync();
        Assert.False(vm.IsPlainText);
        Assert.False(editor.PlainText);
        Assert.False(editor.BodySectionsLost);
        Assert.True(await window.InsertLinkAsync("https://example.com/rich"));
        Assert.Equal("createLink", editor.LastInsertion);
        Assert.True(await vm.SaveDraftAsync());
        var reopened = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(draft.LocalDraftId!.Value));
        Assert.False(reopened.IsPlainText);
        Assert.False(reopened.BodySectionsLost);
        using var saved = Composer.FromBytes(fixture.Engine.LocalDrafts().Single().Mime);
        Assert.Contains("href=\"https://example.com/rich\"", saved.HtmlBody);
        Assert.Contains("<strong>message</strong>", saved.HtmlBody);
    }

    [AvaloniaFact]
    public async Task Mac_reopened_rich_draft_keeps_prior_plain_conversion_boundary_restriction()
    {
        using var fixture = new PlainComposeFixture();
        var draft = new Draft
        {
            AccountId = fixture.Account.Id, To = "recipient@example.org", Subject = "Previously converted",
            Html = "<p>Writing</p><p>Flattened signature</p><p>Flattened quoted message</p>", BodySectionsLost = true,
        };
        var (window, vm, editor) = fixture.Open(draft);
        await window.InitializeEditorAsync();
        Assert.False(vm.IsPlainText);
        Assert.True(vm.BodySectionsLost);
        Assert.True(editor.BodySectionsLost);
        Assert.True(await vm.SaveDraftAsync());
        var reopened = Assert.IsType<Draft>(fixture.Engine.OpenLocalDraft(draft.LocalDraftId!.Value));
        Assert.True(reopened.BodySectionsLost);
        var (restoredWindow, _, restoredEditor) = fixture.Open(reopened);
        await restoredWindow.InitializeEditorAsync();
        Assert.True(restoredEditor.BodySectionsLost);
        Assert.Contains("Flattened signature", restoredEditor.Html);
        Assert.Contains("Flattened quoted message", restoredEditor.Html);
    }

    private static string Lines(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// A strict native-script boundary, not a browser emulator. It rejects unrecognized host commands and supplies
    /// representative editor HTML so actual Mac save/queue and MIME round trips run without a native WKWebView.
    /// </summary>
    private sealed class PlainEditorBridge
    {
        public List<string> Calls { get; } = new();
        public string Html { get; private set; } = "";
        public bool PlainText { get; private set; }
        public string LastInsertion { get; private set; } = "";
        public bool BodySectionsLost { get; private set; }

        public Task<string?> RunAsync(string script)
        {
            Calls.Add(script);
            string? result;
            if (script.StartsWith("setDark(", StringComparison.Ordinal)) result = null;
            else if (script.StartsWith("setHtml(", StringComparison.Ordinal))
            {
                const string boundary = ");setEditorPlainText(";
                var split = script.LastIndexOf(boundary, StringComparison.Ordinal);
                Assert.True(split > 0, "The native host must restore format together with setHtml, which resets rich mode.");
                using var args = JsonDocument.Parse("[" + script[8..split] + "]");
                Html = args.RootElement[0].GetString()!;
                BodySectionsLost = args.RootElement[1].GetBoolean();
                var mode = script[(split + boundary.Length)..];
                Assert.Contains(mode, new[] { "true,false)", "false,false)" });
                PlainText = mode == "true,false)";
                if (PlainText) Html = MimeText.PlainTextToEditorHtml(MimeText.EditorHtmlToPlainText(Html));
                result = "true";
            }
            else if (script == "getHtml()") result = JsonSerializer.Serialize(Html);
            else if (script.StartsWith("fmt('", StringComparison.Ordinal))
            {
                var split = script.IndexOf("', ", StringComparison.Ordinal);
                var command = script[5..split];
                var address = JsonSerializer.Deserialize<string>(script[(split + 3)..^1])!;
                Assert.Contains(command, new[] { "insertText", "createLink" });
                LastInsertion = command;
                if (PlainText && command == "createLink") result = "false";
                else
                {
                    Html = command == "insertText"
                        ? MimeText.PlainTextToEditorHtml(MimeText.EditorHtmlToPlainText(Html) + address)
                        : Html + "<a href=\"" + WebUtility.HtmlEncode(address) + "\">selected label</a>";
                    result = "true";
                }
            }
            else throw new InvalidOperationException("Unexpected native editor command: " + script);
            return Task.FromResult(result);
        }
    }

    private sealed class PlainComposeFixture : IDisposable
    {
        private readonly MailEngine? _previous = AppServices.Engine;
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "magpie-mac-plain-" + Guid.NewGuid().ToString("N"));
        private readonly List<ComposeWindow> _windows = new();
        public MailEngine Engine { get; }
        public Account Account { get; } = new() { Id = "plain-account", Email = "sender@example.com", Enabled = false };

        public PlainComposeFixture()
        {
            Engine = new MailEngine(new AppPaths(_directory), new PlainComposeProtector());
            Engine.Config.Accounts.Add(Account);
            Engine.Settings.Save();
            AppServices.Engine = Engine;
        }

        public (ComposeWindow Window, ComposeViewModel Model, PlainEditorBridge Editor) Open(Draft draft)
        {
            var editor = new PlainEditorBridge();
            var window = new ComposeWindow(draft, editor.RunAsync);
            _windows.Add(window);
            window.Show();
            return (window, Assert.IsType<ComposeViewModel>(window.DataContext), editor);
        }

        public void Dispose()
        {
            foreach (var window in _windows)
            {
                ((ComposeViewModel)window.DataContext!).Dirty = false;
                window.Close();
            }
            AppServices.Engine = _previous!;
            Engine.Dispose();
            try { Directory.Delete(_directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private sealed class PlainComposeProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.ToArray();
        public byte[] Unprotect(byte[] cipher) => cipher.ToArray();
    }
}
