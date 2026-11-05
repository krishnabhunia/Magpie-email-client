using System.Windows;
using System.Windows.Media.Imaging;
using Magpie.App.Services;

namespace Magpie.App.Views;

/// <summary>Attach file paths copied in Explorer or a clipboard screenshot saved to a durable, user-chosen path.</summary>
public partial class ComposeWindow
{
    private void OnPasteAttachments(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length > 0)
                {
                    _vm.AddAttachments(files);
                    return;
                }
            }

            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image == null) return;
                var picker = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Save and attach clipboard screenshot",
                    Filter = "PNG image|*.png",
                    DefaultExt = ".png",
                    AddExtension = true,
                    FileName = "Magpie-screenshot.png",
                };
                if (picker.ShowDialog(this) != true) return;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var output = File.Create(picker.FileName)) encoder.Save(output);
                _vm.AddAttachments(new[] { picker.FileName });
                return;
            }

            Ui.Error("Paste attachment", "Copy one or more files in File Explorer, or take a screenshot (Win+Shift+S), then choose this action.", this);
        }
        catch (Exception ex)
        {
            Ui.Error("Paste attachment", "The clipboard file or picture could not be attached: " + ex.Message, this);
        }
    }
}
