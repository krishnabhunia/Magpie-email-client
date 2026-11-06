using System.Text.Json;
using System.Windows;
using Magpie.App.Services;
using Magpie.Core.Mail;
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
            var originalSubject = _vm.Subject;
            var expanded = ComposeTemplates.Expand(template, _vm.From, _vm.To, originalSubject, DateTimeOffset.Now);
            var replaceSubject = false;
            if (expanded.WouldReplaceSubject(originalSubject))
            {
                var choice = MessageBox.Show(this,
                    $"This template includes a subject:\n\n{expanded.Subject}\n\nReplace the current subject?\nYes: use the template subject and insert its message text.\nNo: keep the current subject and insert only the message text.\nCancel: leave the message unchanged.",
                    "Use template subject?", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                replaceSubject = choice == MessageBoxResult.Yes;
            }
            if (!await _vm.InsertTemplateAsync(template, replaceSubject, originalSubject))
                Ui.Error("Insert template", "The message changed or is no longer editable. Review it and choose the template again.", this);
        }
        catch (Exception ex) { Ui.Error("Insert template", ex.Message, this); }
    }
}
