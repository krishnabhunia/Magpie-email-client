using System.Text.Json;
using Magpie.App.Services;
using Magpie.Core.Settings;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private async Task<string> GetWritingTextAsync()
    {
        if (_plain) return _vm.IsPlainText ? PlainEditor.Text : PlainNewText();
        if (!_editorReady || Editor.CoreWebView2 == null) return "";
        var value = await Editor.CoreWebView2.ExecuteScriptAsync("getNewText(false)");
        return JsonSerializer.Deserialize<string>(value) ?? "";
    }

    private async Task UseQuickTemplateAsync(QuickTemplate template)
    {
        try
        {
            if (!_editorReady)
            {
                Ui.Error("Insert template", "The message editor is still opening. Try again when it is ready.", this);
                return;
            }
            var review = _vm.ReviewTemplate(template);
            var dialog = new TemplatePreviewDialog(review) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (!await _vm.InsertReviewedTemplateAsync(review, dialog.ReplaceSubject))
                Ui.Error("Insert template", _vm.Status, this);
        }
        catch (Exception ex) { Ui.Error("Insert template", ex.Message, this); }
    }

    private async Task UseQuickReplyAsync(string text)
    {
        try
        {
            if (!_editorReady)
            {
                _vm.Status = "The message editor is still opening. Choose the quick reply again when it is ready.";
                return;
            }
            await _vm.InsertQuickReplyAsync(text);
        }
        catch (Exception ex) { Ui.Error("Insert quick reply", ex.Message, this); }
    }
}
