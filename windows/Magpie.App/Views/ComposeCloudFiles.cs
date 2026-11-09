using System.Text.Json;
using System.Windows;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core.CloudFiles;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private async void OnCloudAttach(object sender, RoutedEventArgs e) => await OpenCloudFileDialogAsync(sender, existing: false);

    private async void OnCloudBrowseExisting(object sender, RoutedEventArgs e) => await OpenCloudFileDialogAsync(sender, existing: true);

    private async Task OpenCloudFileDialogAsync(object sender, bool existing)
    {
        if (_vm.Sending || _vm.AttachmentOperationBusy || _vm.RevisionBusy || _forceClose) return;
        var original = existing ? null : (sender as FrameworkElement)?.DataContext as AttachmentItem;
        if (original?.Carried == true) return;
        var nativeText = PlainEditor.Text;
        var nativeCaret = PlainEditor.CaretIndex;
        var native = _plain;
        try
        {
            if (!native && (!_editorReady || Editor.CoreWebView2 == null
                || await Editor.CoreWebView2.ExecuteScriptAsync("captureCloudLink()") != "true"))
                throw new InvalidOperationException("Click where the cloud file link should go in the message, then try again.");
            async Task<bool> Insert(CloudUploadedFile file)
            {
                if (_forceClose || !IsLoaded || native != _plain) return false;
                if (native)
                {
                    if (PlainEditor.Text != nativeText) return false;
                    var linkText = file.DisplayName + ": " + file.WebUrl;
                    PlainEditor.Select(nativeCaret, 0);
                    PlainEditor.SelectedText = linkText;
                    PlainEditor.CaretIndex = nativeCaret + linkText.Length;
                    return true;
                }
                return _editorReady && Editor.CoreWebView2 != null
                    && await Editor.CoreWebView2.ExecuteScriptAsync("insertCloudLink(" + JsonSerializer.Serialize(file.WebUrl)
                        + "," + JsonSerializer.Serialize(file.DisplayName) + ")") == "true";
            }
            if (existing) new CloudExistingFileDialog(_vm, Insert) { Owner = this }.ShowDialog();
            else
            {
                var dialog = new CloudFileDialog(_vm, Insert, original?.Path, original?.Name) { Owner = this };
                if (dialog.ShowDialog() == true && original != null && _vm.Attachments.Contains(original)
                    && string.Equals(dialog.SourcePath, original.Path, StringComparison.OrdinalIgnoreCase))
                    _vm.RemoveAttachmentCommand.Execute(original);
            }
        }
        catch (Exception ex) { if (!_forceClose) Ui.Error("Cloud file link", ex.Message, this); }
        finally
        {
            try { if (Editor.CoreWebView2 != null) await Editor.CoreWebView2.ExecuteScriptAsync("cancelCloudLink()"); }
            catch { /* Closing an editor must not revive a stale insertion target. */ }
        }
    }
}
