using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Storage;

namespace Magpie.App.Services;

/// <summary>
/// Design DL1, at start before the mail database is opened: does a mail folder move asked for in Settings, and
/// waits for a chosen mail folder whose drive is locked or unplugged. Returns false when Magpie should close.
/// </summary>
public static class MailFolderStartup
{
    public static bool Prepare(AppPaths paths)
    {
        var target = MailLocation.PendingMove(paths);
        if (target != null)
        {
            MailLocation.ClearPendingMove(paths);
            DoMove(paths, target);
        }

        while (!paths.MailRootAvailable)
        {
            var choice = ChoiceDialog.Ask(null, "Your mail folder isn't available",
                paths.MailRoot + " can't be opened. The drive may be locked (BitLocker or VeraCrypt) or unplugged. Unlock or connect it, then try again.\n\n"
                + "Magpie doesn't start an empty mailbox in its place on its own, so nothing is mixed up or downloaded twice.",
                "Choose another folder…", "Try again");
            if (choice == 1) { paths.RefreshMailRoot(); continue; }
            if (choice == 0)
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Magpie keep your mail?" };
                if (dlg.ShowDialog() == true)
                {
                    try { System.IO.Directory.CreateDirectory(dlg.FolderName); paths.SetMailRoot(dlg.FolderName); }
                    catch (Exception ex) { Ui.Error("Mail folder", ex.Message); }
                }
                continue;
            }
            return false;   // Cancel: close Magpie
        }
        try { System.IO.Directory.CreateDirectory(paths.MimeCache); } catch { }
        return true;
    }

    private static void DoMove(AppPaths paths, string target)
    {
        if (string.Equals(System.IO.Path.GetFullPath(target), paths.MailRoot, StringComparison.OrdinalIgnoreCase)) return;
        var from = paths.MailRoot;
        if (!System.IO.Directory.Exists(from))
        {
            // Nothing to move (the old folder isn't there): just use the new one.
            System.IO.Directory.CreateDirectory(target);
            paths.SetMailRoot(target);
            return;
        }
        var replace = false;
        if (MailLocation.HasMail(target))
        {
            var c = ChoiceDialog.Ask(null, "That folder already has Magpie mail",
                target + " already holds Magpie mail, e.g. from before a reinstall.\n\nUse it as it is (the mail in " + from + " stays there), or replace it with the mail on this PC?",
                "Replace it", "Use the mail that's there");
            if (c == 1) { paths.SetMailRoot(target); return; }
            if (c != 0) return;
            replace = true;
        }

        var w = new Window
        {
            Title = "Moving your mail — Magpie", Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Style = (Style)Application.Current.FindResource("Window.Dialog"),
        };
        var text = new TextBlock { Text = "Moving your mail to " + target + "…", TextWrapping = TextWrapping.Wrap };
        var bar = new ProgressBar { Height = 6, Margin = new Thickness(0, 12, 0, 0), Maximum = 1 };
        var panel = new StackPanel { Margin = new Thickness(22, 20, 22, 22) };
        panel.Children.Add(text);
        panel.Children.Add(bar);
        w.Content = panel;

        Exception? error = null;
        var progress = new Progress<(long done, long total)>(p =>
        {
            bar.Value = p.total == 0 ? 1 : (double)p.done / p.total;
            text.Text = $"Moving your mail… {Size(p.done)} of {Size(p.total)}";
        });
        w.Loaded += async (_, _) =>
        {
            try { await Task.Run(() => MailLocation.Move(from, target, replace, progress)); }
            catch (Exception ex) { error = ex; }
            w.Close();
        };
        w.ShowDialog();

        if (error == null)
        {
            paths.SetMailRoot(target);
            Log.Info("mail moved to " + target);
        }
        else
        {
            Log.Error("mail move failed", error);
            Ui.Error("Moving your mail", "Your mail couldn't be moved, so it stays in " + from + ".\n\n" + error.Message);
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };
}
