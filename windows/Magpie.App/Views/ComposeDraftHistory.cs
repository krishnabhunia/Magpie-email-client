using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Magpie.App.Services;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private void OnDraftHistory(object sender, RoutedEventArgs e)
    {
        if (!_editorReady || _vm.Sending || _vm.RevisionBusy) return;
        var dialog = new DraftHistoryDialog(_vm) { Owner = this };
        dialog.ShowDialog();
        RefreshAllRecipientChips();
        UpdatePlainTextControls();
    }

    private async Task RestoreRevisionEditorAsync(string html, bool plainText)
    {
        if (!_editorReady) throw new InvalidOperationException("The message editor is still loading.");
        HideMentionSuggestions();
        _plainRewrite = null;
        if (_plain)
        {
            PlainEditor.Text = plainText ? MimeText.EditorHtmlToPlainText(html) : MimeText.HtmlToText(html);
            var protectedStart = Regex.Match(html, "<(?:div|blockquote)\\b[^>]*class=[\"'][^\"']*\\bmagpie-(?:signature|quote)\\b", RegexOptions.IgnoreCase);
            _plainProtectedTail = protectedStart.Success ? MimeText.HtmlToText(html[protectedStart.Index..]).TrimStart() : "";
            PlainEditor.CaretIndex = 0;
        }
        else
        {
            if (Editor.CoreWebView2 == null) throw new InvalidOperationException("The message editor is unavailable.");
            // A plain revision uses one p per line, not rich paragraph spacing. Rehydrate its exact text before
            // asking the browser for innerText, otherwise adjacent lines gain extra paragraph breaks.
            var editorHtml = plainText ? MimeText.PlainTextToEditorHtml(MimeText.EditorHtmlToPlainText(html)) : html;
            await Editor.CoreWebView2.ExecuteScriptAsync($"setHtml({JsonSerializer.Serialize(editorHtml)})");
            if (plainText) await Editor.CoreWebView2.ExecuteScriptAsync("setEditorPlainText(true,false)");
            await Editor.CoreWebView2.ExecuteScriptAsync($"setEditorSpellcheck({(_spellcheckEnabled ? "true" : "false")})");
            await Editor.CoreWebView2.ExecuteScriptAsync(EditorPage.TypographyScript(AppServices.Engine.Config.Typography, true));
        }
        QuoteToggleButton.Visibility = !plainText && html.Contains("magpie-quote", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
