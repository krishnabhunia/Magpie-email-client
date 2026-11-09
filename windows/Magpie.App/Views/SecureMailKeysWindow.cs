using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Microsoft.Win32;

namespace Magpie.App.Views;

/// <summary>Explicit local import and fingerprint confirmation. Never exports or stages private material.</summary>
public static class SecureMailKeysWindow
{
    public static Window Create(Window? owner, MailEngine engine, string? accountId)
    {
        Style S(string key) => (Style)Application.Current.FindResource(key);
        var window = new Window { Title = "Message security keys — Magpie", Width = 900, Height = 780,
            MinWidth = 640, MinHeight = 560, Style = S("Window.Dialog"), ResizeMode = ResizeMode.CanResize };
        if (owner is { IsVisible: true }) { window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(20) };
        window.Content = root;
        var heading = new StackPanel(); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "S/MIME certificates and OpenPGP keys", FontSize = 20, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "Keys stay on this PC, protected for your Windows user. Keep your original private-key backup. No keyservers or automatic certificate lookup are used.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) });
        var accounts = new ComboBox { ItemsSource = engine.Accounts, DisplayMemberPath = "Email", SelectedValuePath = "Id", SelectedValue = accountId,
            Margin = new Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(accounts, "Security key account");
        if (accounts.SelectedIndex < 0 && accounts.Items.Count > 0) accounts.SelectedIndex = 0;
        heading.Children.Add(accounts);
        var form = new StackPanel { Margin = new Thickness(0, 4, 0, 12) };
        heading.Children.Add(form);
        form.Children.Add(new TextBlock { Text = "Import one email identity", FontWeight = FontWeights.SemiBold });
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        var format = new ComboBox { ItemsSource = new[] { "S/MIME", "OpenPGP" }, SelectedIndex = 0, Width = 130, Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(format, "Key format"); row.Children.Add(format);
        var kind = new ComboBox { ItemsSource = new[] { "Recipient public key", "My private identity" }, SelectedIndex = 0, Width = 190 };
        AutomationProperties.SetName(kind, "Key kind"); row.Children.Add(kind); form.Children.Add(row);
        form.Children.Add(new TextBlock { Text = "Email address in the certificate or key" });
        var email = new TextBox { Style = S("TextBox.Field"), Margin = new Thickness(0, 4, 0, 6) };
        AutomationProperties.SetName(email, "Key email address"); form.Children.Add(email);
        form.Children.Add(new TextBlock { Text = "Private-key file password (leave blank for an unprotected file)" });
        var password = new PasswordBox { Margin = new Thickness(0, 4, 0, 8), Padding = new Thickness(8) };
        AutomationProperties.SetName(password, "Private-key password"); form.Children.Add(password);
        var import = new Button { Content = "Choose file and import…", Style = S("Button.Secondary"), HorizontalAlignment = HorizontalAlignment.Left };
        form.Children.Add(import);
        form.Children.Add(new TextBlock { Text = "Supported: RSA S/MIME (.p12/.pfx private, .cer/.pem public), and version 4 RSA OpenPGP (.asc/.pgp), 2048 bits or stronger. OpenPGP signing uses the primary signing key. Maximum import: 4 MB.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 7, 0, 0) });
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, BorderThickness = new Thickness(0), Margin = new Thickness(0, 10, 0, 8) };
        AutomationProperties.SetName(details, "Selected key fingerprint and usage"); bottom.Children.Add(details);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        Button Action(string text)
        {
            var button = new Button { Content = text, Style = S("Button.Secondary"), Margin = new Thickness(0, 0, 8, 8) };
            actions.Children.Add(button); return button;
        }
        var select = Action("Confirm fingerprint and select…"); var export = Action("Export public key…"); var remove = Action("Remove…");
        var close = Action("Close"); close.IsCancel = true; close.Click += (_, _) => window.Close();
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }; bottom.Children.Add(status);
        AutomationProperties.SetName(status, "Key management status");
        var list = new ListView { MinHeight = 80, SelectionMode = SelectionMode.Single };
        AutomationProperties.SetName(list, "Imported security keys");
        var columns = new GridView();
        foreach (var (title, property, width) in new[] { ("Email", "Email", 220d), ("Format", "FormatLabel", 80d), ("Kind", "KindLabel", 150d), ("Status", "Status", 260d) })
            columns.Columns.Add(new GridViewColumn { Header = title, DisplayMemberBinding = new Binding(property), Width = width });
        list.View = columns; root.Children.Add(list);
        var busy = false; var closed = false;
        Account? Account() => accounts.SelectedItem as Account;
        void Refresh()
        {
            list.ItemsSource = Account() is { } account ? engine.SecureMail.Keys.List(account.Id) : Array.Empty<MailKeyInfo>();
            details.Text = "Select a key to inspect its full fingerprint. Verify recipient fingerprints with the person through a separate trusted channel before selecting.";
        }
        async Task Run(Func<Task> operation)
        {
            if (busy || closed) return; busy = true; root.IsEnabled = false; status.Text = "Working…";
            try { await engine.WithProfileOperationAsync(_ => operation()); Refresh(); }
            catch (Exception ex) { status.Text = ex is MailSecurityException ? ex.Message : "The key operation could not be completed. Check the file and access permissions."; }
            finally { busy = false; if (!closed) root.IsEnabled = true; password.Clear(); }
        }
        window.Closing += (_, e) => { if (busy) e.Cancel = true; };
        window.Closed += (_, _) => { closed = true; password.Clear(); };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is not MailKeyInfo key) return;
            details.Text = key.Email + " · " + key.FormatLabel + " · " + key.KindLabel + "\nFingerprint: " + key.Fingerprint
                + "\nExpires: " + (key.Expires?.ToLocalTime().ToString("g") ?? "no expiry") + " · Can sign: " + key.CanSign + " · Can encrypt: " + key.CanEncrypt
                + "\nCertificate-chain trust and online revocation are not checked. Fingerprint confirmation is local.";
        };
        accounts.SelectionChanged += (_, _) => { password.Clear(); try { Refresh(); } catch (MailSecurityException ex) { status.Text = ex.Message; } };
        kind.SelectionChanged += (_, _) => { if (kind.SelectedIndex == 1 && Account() is { } account) email.Text = account.Email; };
        import.Click += async (_, _) =>
        {
            if (Account() is not { } account) { status.Text = "Choose an account first."; return; }
            var dialog = new OpenFileDialog { Title = "Import email certificate or key", Filter = "Certificate or key|*.p12;*.pfx;*.cer;*.crt;*.pem;*.asc;*.pgp;*.gpg|All files|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(window) != true) return;
            var address = email.Text.Trim(); var protocol = format.SelectedIndex == 1 ? MailSecurityFormat.OpenPgp : MailSecurityFormat.Smime;
            var personal = kind.SelectedIndex == 1; var secret = password.Password;
            await Run(async () =>
            {
                var imported = await Task.Run(() =>
                {
                    var bytes = SecureMailKeys.ReadImportFile(dialog.FileName);
                    try { return engine.SecureMail.Keys.Import(account.Id, address, protocol, bytes, secret, personal); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                });
                status.Text = imported.Active ? "Private identity imported and selected for this address." : "Imported. Confirm the full fingerprint and select this key before sending with it.";
            });
            secret = "";
        };
        select.Click += async (_, _) =>
        {
            if (list.SelectedItem is not MailKeyInfo key) { status.Text = "Select a key first."; return; }
            var confirmed = TextPromptDialog.Ask(window, "Confirm the full fingerprint", "Compare this fingerprint through a trusted channel, then enter or paste the full value.\n\n" + key.Email + "\n" + key.Fingerprint);
            if (confirmed == null) return;
            await Run(async () => { await Task.Run(() => engine.SecureMail.Keys.Select(key.AccountId, key.Id, confirmed)); status.Text = "Fingerprint confirmed. This key is selected for the address."; });
        };
        export.Click += async (_, _) =>
        {
            if (list.SelectedItem is not MailKeyInfo key) { status.Text = "Select a key first."; return; }
            var dialog = new SaveFileDialog { Title = "Export public key only", FileName = "mail-public-key" + (key.Format == MailSecurityFormat.Smime ? ".cer" : ".pgp"),
                Filter = key.Format == MailSecurityFormat.Smime ? "Public certificate|*.cer" : "OpenPGP public key|*.pgp", AddExtension = true };
            if (dialog.ShowDialog(window) != true) return;
            await Run(async () => { var bytes = await Task.Run(() => engine.SecureMail.Keys.ExportPublic(key.AccountId, key.Id)); await File.WriteAllBytesAsync(dialog.FileName, bytes); status.Text = "Public key exported. No private key or password was included."; });
        };
        remove.Click += async (_, _) =>
        {
            if (list.SelectedItem is not MailKeyInfo key) { status.Text = "Select a key first."; return; }
            if (MessageBox.Show(window, "Remove this key from this PC? Queued messages using it will require review. Old encrypted messages need the original private key to be read.\n\n" + key.Email + "\n" + key.Fingerprint,
                "Remove security key", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await Run(async () => { await Task.Run(() => engine.SecureMail.Keys.Remove(key.AccountId, key.Id)); status.Text = "Key removed from this PC."; });
        };
        try { Refresh(); } catch (MailSecurityException ex) { status.Text = ex.Message; }
        return window;
    }
}
