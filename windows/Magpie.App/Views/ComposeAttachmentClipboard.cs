using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Magpie.App.Services;

namespace Magpie.App.Views;

/// <summary>Paste files or explicitly choose whether a clipboard picture is inline or a durable PNG attachment.</summary>
public partial class ComposeWindow
{
    private bool _clipboardPasteBusy;

    private void InitializeClipboardPaste()
    {
        PlainEditor.PreviewKeyDown += (_, e) =>
        {
            var paste = e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control
                || e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift;
            if (!paste || !ClipboardHasAttachment()) return;
            e.Handled = true;
            _ = PasteClipboardAttachmentAsync(showEmptyMessage: false);
        };
        DataObject.AddPastingHandler(PlainEditor, (_, e) =>
        {
            if (!ClipboardHasAttachment()) return;
            e.CancelCommand();
            _ = PasteClipboardAttachmentAsync(showEmptyMessage: false);
        });
    }

    private static bool ClipboardHasAttachment()
    {
        try { return Clipboard.ContainsFileDropList() || Clipboard.ContainsImage(); }
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }

    private async void OnPasteAttachments(object sender, RoutedEventArgs e)
        => await PasteClipboardAttachmentAsync(showEmptyMessage: true);

    private async Task PasteClipboardAttachmentAsync(bool showEmptyMessage)
    {
        if (_clipboardPasteBusy || !_vm.TryBeginAttachmentOperation()) return;
        _clipboardPasteBusy = true;
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length > 0)
                {
                    var available = files.Where(File.Exists).ToArray();
                    _vm.AddAttachments(available);
                    var unavailable = files.Where(path => !File.Exists(path)).ToArray();
                    if (unavailable.Length > 0)
                        Ui.Error("Paste attachment", "These items are folders or are no longer available:\n\n"
                            + string.Join("\n", unavailable.Take(10)), this);
                    return;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image == null) return;
                // Capture the picture before opening a modal choice: another application may change the clipboard.
                var editor = !_plain && _editorReady && !_vm.IsPlainText ? Editor.CoreWebView2 : null;
                var canInline = editor != null && await editor.ExecuteScriptAsync("captureClipboardPicture()") == "true";
                // Design CW1 (C4): Settings → General → Writing emails can answer this question once for every paste.
                // "In the message" still asks when the rich editor can't take the picture here.
                ClipboardPictureChoice picked;
                var preset = AppServices.Engine.Config.Compose.PastedPicture;
                if (preset == Magpie.Core.Settings.PastedPictureChoice.Attach) picked = ClipboardPictureChoice.Attachment;
                else if (preset == Magpie.Core.Settings.PastedPictureChoice.Inline && canInline) picked = ClipboardPictureChoice.Inline;
                else
                {
                    var choice = new ClipboardPictureDialog(canInline) { Owner = this };
                    if (choice.ShowDialog() != true) return;
                    picked = choice.Choice;
                }
                var png = ComposeClipboardImage.EncodePng(image, picked == ClipboardPictureChoice.Inline
                    ? ComposeClipboardImage.InlineLimitBytes : ComposeClipboardImage.AttachmentLimitBytes);
                if (picked == ClipboardPictureChoice.Inline)
                {
                    if (_plain || !_editorReady || _vm.IsPlainText || Editor.CoreWebView2 == null)
                        throw new InvalidOperationException("The rich-text editor is no longer available. Choose Save and attach PNG instead.");
                    var data = "data:image/png;base64," + Convert.ToBase64String(png);
                    var inserted = await Editor.CoreWebView2.ExecuteScriptAsync("insertClipboardPicture(" + JsonSerializer.Serialize(data) + ")");
                    if (inserted != "true")
                        throw new InvalidOperationException("The message or insertion point changed. Click in the message and paste again.");
                    return;
                }
                var picker = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save and attach clipboard picture",
                    Filter = "PNG image|*.png",
                    DefaultExt = ".png",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = "Magpie-picture.png",
                };
                if (picker.ShowDialog(this) != true) return;
                var temporary = Path.Combine(Path.GetDirectoryName(picker.FileName)!, ".magpie-clipboard-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        await output.WriteAsync(png);
                    File.Move(temporary, picker.FileName, overwrite: true);
                    _vm.AddAttachments(new[] { picker.FileName });
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
                }
                return;
            }

            if (showEmptyMessage)
                Ui.Error("Paste attachment", "Copy files in File Explorer or a picture (for example, Win+Shift+S), then press Ctrl+V in the message or choose Paste attachment.", this);
        }
        catch (Exception ex)
        {
            Ui.Error("Paste attachment", "The clipboard file or picture could not be attached: " + ex.Message, this);
        }
        finally
        {
            try { if (Editor.CoreWebView2 != null) await Editor.CoreWebView2.ExecuteScriptAsync("cancelClipboardPicture()"); }
            catch (Exception) { /* The editor may have been disposed by application shutdown. */ }
            _vm.AttachmentOperationBusy = false;
            _clipboardPasteBusy = false;
        }
    }
}
