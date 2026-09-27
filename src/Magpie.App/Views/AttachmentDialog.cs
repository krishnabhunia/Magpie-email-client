using System.Windows;
using Magpie.App.Services;

namespace Magpie.App.Views;

/// <summary>Open or save an attachment; programs and scripts are only saved, never opened directly.</summary>
public static class AttachmentDialog
{
    private static readonly HashSet<string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".scr", ".pif", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1",
        ".hta", ".jar", ".lnk", ".reg", ".cpl", ".dll", ".url", ".iso", ".img", ".vhd", ".vhdx", ".appx", ".msix", ".application", ".chm",
    };

    public static void Show(string path, string name)
    {
        var owner = Ui.ActiveWindow;
        var ext = Path.GetExtension(name);
        if (Risky.Contains(ext))
        {
            if (MessageBox.Show(owner!, $"\"{name}\" is a program or script. Opening files like this from email is a common way PCs get infected.\n\nSave it to a folder instead?",
                    "Attachment", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
                SaveAs(path, name, owner);
            return;
        }
        var r = MessageBox.Show(owner!, $"Open \"{name}\"?\n\nYes = open it   ·   No = save a copy…", "Attachment", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) Ui.OpenExternal(path);
        else if (r == MessageBoxResult.No) SaveAs(path, name, owner);
    }

    private static void SaveAs(string path, string name, Window? owner)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = name,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(owner) == true)
        {
            try { File.Copy(path, dlg.FileName, true); }
            catch (Exception ex) { Ui.Error("Save attachment", ex.Message); }
        }
    }
}
