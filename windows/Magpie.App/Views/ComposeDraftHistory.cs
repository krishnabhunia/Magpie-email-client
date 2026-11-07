using System.Text.Json;
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

    private async Task RestoreRevisionEditorAsync(string html, bool plainText, bool bodySectionsLost)
    {
        if (!_editorReady) throw new InvalidOperationException("The message editor is still loading.");
        HideMentionSuggestions();
        _plainRewrite = null;
        if (_plain)
        {
            _plainBodySectionsKnown = !bodySectionsLost && !plainText;
            PlainEditor.Text = plainText ? MimeText.EditorHtmlToPlainText(html) : MimeText.HtmlToText(html);
            _plainProtectedTail = ComposeBodySafety.CapturePlainProtectedTail(html);
            PlainEditor.CaretIndex = 0;
        }
        else
        {
            if (Editor.CoreWebView2 == null) throw new InvalidOperationException("The message editor is unavailable.");
            // A plain revision uses one p per line, not rich paragraph spacing. Rehydrate its exact text before
            // asking the browser for innerText, otherwise adjacent lines gain extra paragraph breaks.
            var editorHtml = plainText ? MimeText.PlainTextToEditorHtml(MimeText.EditorHtmlToPlainText(html)) : html;
            await Editor.CoreWebView2.ExecuteScriptAsync($"setHtml({JsonSerializer.Serialize(editorHtml)}, {(bodySectionsLost || plainText ? "true" : "false")})");
            if (plainText) await Editor.CoreWebView2.ExecuteScriptAsync("setEditorPlainText(true,false)");
            await Editor.CoreWebView2.ExecuteScriptAsync($"setEditorSpellcheck({(_spellcheckEnabled ? "true" : "false")})");
            await Editor.CoreWebView2.ExecuteScriptAsync(EditorPage.TypographyScript(AppServices.Engine.Config.Typography, true));
        }
        QuoteToggleButton.Visibility = !plainText && html.Contains("magpie-quote", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible : Visibility.Collapsed;
    }
}
