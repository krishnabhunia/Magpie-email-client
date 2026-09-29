using System.Windows;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Storage;

namespace Magpie.App.Services;

/// <summary>Design EX1: saving and restoring a settings backup (Settings → General, and the Add account window).</summary>
public static class BackupUi
{
    private const string Filter = "Magpie settings backup|*" + SettingsBackup.Extension + "|All files|*.*";

    public static async Task SaveAsync(Window owner)
    {
        var e = AppServices.Engine;
        var password = PasswordDialog.Ask(owner, "Save a backup of your settings",
            "The backup holds your passwords and sign-ins, so it's locked with a password. You'll need it to restore; Magpie can't recover it for you.\n\n"
            + "In it: accounts with their sign-ins, sign-in apps, rules, auto-delete rules, signatures, quick replies, templates, tags, Gatekeeper lists, look and layout, updates, the AI key and where your mail is kept. "
            + "Not in it: your emails (they download again, or stay in your mail folder).",
            confirm: true, SettingsBackup.MinPasswordLength, "Save backup…");
        if (password == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the settings backup", Filter = Filter, DefaultExt = SettingsBackup.Extension,
            FileName = "Magpie settings " + DateTime.Now.ToString("d MMM yyyy") + SettingsBackup.Extension,
        };
        if (dlg.ShowDialog(owner) != true) return;
        try
        {
            var contents = SettingsBackup.Collect(e.Paths, e.Vault, UpdateService.Current.ToString());
            var bytes = await Task.Run(() => SettingsBackup.Lock(contents, password));
            await File.WriteAllBytesAsync(dlg.FileName, bytes);
            e.Config.LastBackup = DateTimeOffset.Now;
            e.Settings.Save(notify: false);
            Log.Info("settings backup saved");
            MessageBox.Show(owner, "Backup saved:\n" + dlg.FileName + "\n\nKeep it somewhere other than this PC's C: drive, with its password.", "Magpie", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) { Ui.Error("Save a backup", ex.Message, owner); }
    }

    public static async Task RestoreAsync(Window owner)
    {
        var e = AppServices.Engine;
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Restore from a settings backup", Filter = Filter };
        if (dlg.ShowDialog(owner) != true) return;
        byte[] file;
        try { file = await File.ReadAllBytesAsync(dlg.FileName); }
        catch (Exception ex) { Ui.Error("Restore from a backup", ex.Message, owner); return; }

        SettingsBackup.Contents? contents = null;
        var message = "Type the password you chose when you saved this backup.";
        while (contents == null)
        {
            var password = PasswordDialog.Ask(owner, "Restore from a backup", message, confirm: false, 1, "Open");
            if (password == null) return;
            try { contents = await Task.Run(() => SettingsBackup.Unlock(file, password)); }
            catch (SettingsBackup.WrongPasswordException) { message = "That password doesn't open this backup. Try again."; }
            catch (Exception ex) { Ui.Error("Restore from a backup", ex.Message, owner); return; }
        }

        var lines = SettingsBackup.Describe(contents);
        var folderNote = "";
        if (contents.MailFolder.Length > 0)
            folderNote = System.IO.Directory.Exists(contents.MailFolder)
                ? MailLocation.HasMail(contents.MailFolder) ? "\n\nYour mail is still in " + contents.MailFolder + ", so it's back straight away." : ""
                : "\n\n" + contents.MailFolder + " isn't available now. Magpie will ask for it when it starts.";
        var choice = ChoiceDialog.Ask(owner, "Restore this backup?",
            $"Saved by Magpie {contents.AppVersion} on {contents.Created:d MMM yyyy, HH:mm}.\n\n" + string.Join("\n", lines)
            + "\n\nThis replaces everything set in Magpie now. The accounts sign in by themselves; you don't type passwords again." + folderNote,
            "Replace my settings and restart");
        if (choice != 0) return;
        try
        {
            SettingsBackup.StagePending(e.Paths, contents, new DpapiProtector());
            ((App)Application.Current).RestartApp();
        }
        catch (Exception ex) { Ui.Error("Restore from a backup", ex.Message, owner); }
    }
}
