using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Auth;
using Magpie.Core.CloudFiles;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>Explicit account/file/recipient/access approval before any upload or sharing mutation.</summary>
public sealed class CloudFileDialog : Window
{
    private readonly ComposeViewModel _compose;
    private readonly MailEngine _engine;
    private readonly Func<CloudUploadedFile, Task<bool>> _insert;
    private readonly ComboBox _accounts = new() { DisplayMemberPath = nameof(Account.Label), MinHeight = 32 };
    private readonly ComboBox _access = new() { MinHeight = 32, ItemsSource = new[] { "View only", "Can edit" }, SelectedIndex = 0 };
    private readonly TextBlock _file = new() { Text = "No file selected", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox _confirm = new() { Content = "I approve uploading this file and granting the listed people access now.", Margin = new Thickness(0, 15, 0, 12) };
    private readonly Button _upload = new() { Content = "Upload, share & insert link", MinWidth = 190 };
    private readonly Button _connect = new() { Content = "Connect cloud account…", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Button _browse = new() { Content = "Choose file…", Margin = new Thickness(0, 7, 0, 0) };
    private readonly Button _cancel = new() { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0), MinWidth = 80 };
    private readonly ProgressBar _progress = new() { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _recovery = new() { Content = "Open cloud location", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private FileInfo? _selected;
    private DateTime _selectedWrittenAt;
    private long _selectedLength;
    private string _outgoingName = "";
    private readonly string[] _recipients = Array.Empty<string>();
    private readonly string? _recipientProblem;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closeAfterCancel;
    private string? _recoveryUrl;
    public string? SourcePath => _selected?.FullName;
    public CloudUploadedFile? UploadedFile { get; private set; }

    public CloudFileDialog(ComposeViewModel compose, Func<CloudUploadedFile, Task<bool>> insert, string? selectedPath = null, string? outgoingName = null)
    {
        _compose = compose;
        _engine = AppServices.Engine;
        _insert = insert;
        Title = "Send a cloud file link — Magpie";
        Width = 610; MaxHeight = 810;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Window.Dialog");
        NameScope.SetNameScope(this, new NameScope());
        _confirm.Content = new TextBlock { Text = "I approve uploading this file and granting the listed people access now.", TextWrapping = TextWrapping.Wrap, MaxWidth = 510 };
        try { _recipients = CloudSharePlan.RecipientsFor(compose.To, compose.Cc, compose.Bcc); }
        catch (InvalidOperationException ex) { _recipientProblem = ex.Message; }
        var available = _engine.Accounts.Where(a => a.Kind is AccountKind.Gmail or AccountKind.Microsoft).ToArray();
        _accounts.ItemsSource = available;
        _accounts.SelectedItem = available.FirstOrDefault(a => a.Id == compose.From?.Id) ?? available.FirstOrDefault();
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Upload a copy and share it with named people", FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(new TextBlock
        {
            Text = "A new private copy is uploaded to Google Drive or OneDrive. Access is granted immediately when you confirm. Removing a recipient, discarding the draft, or undoing Send does not revoke that access; manage completed grants in your cloud account.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 15),
        });
        AddLabel(root, "Cloud account"); root.Children.Add(_accounts); root.Children.Add(_connect);
        AddLabel(root, "File · up to 100 MB"); root.Children.Add(_file); root.Children.Add(_browse);
        AddLabel(root, "People receiving access · To and Cc");
        var people = new ListBox { ItemsSource = _recipients, MaxHeight = 135, MinHeight = 36, Margin = new Thickness(0, 0, 0, 8) };
        root.Children.Add(people);
        AddLabel(root, "Access"); root.Children.Add(_access);
        root.Children.Add(new TextBlock
        {
            Text = "The link requires named access. No public or anonymous link is created and no separate sharing email is sent. Hidden Bcc recipients are not supported. Editors may have additional sharing powers under their provider's rules. OneDrive stores each upload in a new Magpie upload folder.",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
        });
        root.Children.Add(_confirm);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_upload); actions.Children.Add(_cancel); root.Children.Add(actions);
        root.Children.Add(_progress); root.Children.Add(_status); root.Children.Add(_recovery);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var button in new[] { _connect, _browse, _cancel, _recovery }) button.SetResourceReference(StyleProperty, "Button.Secondary");
        _upload.SetResourceReference(StyleProperty, "Button.Primary");
        AutomationProperties.SetName(_accounts, "Cloud account");
        AutomationProperties.SetName(_access, "Cloud file access");
        AutomationProperties.SetName(_confirm, "Confirm file upload and named-recipient access");
        AutomationProperties.SetName(people, "Confirmed cloud recipients");
        AutomationProperties.SetName(_file, "Selected cloud file");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        RegisterName("CloudAccounts", _accounts); RegisterName("CloudAccess", _access);
        RegisterName("CloudRecipients", people); RegisterName("CloudConfirm", _confirm);
        RegisterName("CloudUpload", _upload); RegisterName("CloudConnect", _connect); RegisterName("CloudStatus", _status);
        _accounts.SelectionChanged += (_, _) => { _confirm.IsChecked = false; Refresh(); };
        _access.SelectionChanged += (_, _) => { _confirm.IsChecked = false; Refresh(); };
        _confirm.Checked += (_, _) => Refresh(); _confirm.Unchecked += (_, _) => Refresh();
        _browse.Click += (_, _) => ChooseFile();
        _connect.Click += async (_, _) => await ConnectAsync();
        _upload.Click += async (_, _) => await UploadAsync();
        _cancel.Click += (_, _) => { if (_busy) RequestCancel(); else Close(); };
        _recovery.Click += (_, _) => { if (_recoveryUrl != null) Ui.OpenExternal(_recoveryUrl); };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closeAfterCancel = true; RequestCancel(); } };
        if (selectedPath != null) SelectFile(selectedPath, outgoingName);
        if (_recipientProblem != null) _status.Text = _recipientProblem;
        else if (available.Length == 0) _status.Text = "Add a Google or Microsoft account in Settings before using cloud attachments. You can still attach files normally.";
        Refresh();
    }

    private static void AddLabel(Panel panel, string label) => panel.Children.Add(new TextBlock
    {
        Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 13, 0, 5),
    });

    private void SelectFile(string path, string? outgoingName = null)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || (file.Attributes & FileAttributes.Directory) != 0) throw new IOException("Choose an existing file.");
            _selected = file;
            _selectedLength = file.Length;
            _selectedWrittenAt = file.LastWriteTimeUtc;
            _outgoingName = outgoingName ?? file.Name;
            _file.Text = _outgoingName + " · " + HtmlRenderer.FormatSize(file.Length) + "\n" + file.FullName;
            _confirm.IsChecked = false;
        }
        catch (Exception) { _selected = null; _file.Text = "No file selected"; _status.Text = "This file is unavailable. Choose it again."; }
    }

    private void ChooseFile()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Title = "Choose a file to upload and share", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) == true) SelectFile(picker.FileName);
        Refresh();
    }

    private void Refresh()
    {
        var account = _accounts.SelectedItem as Account;
        _accounts.IsEnabled = _access.IsEnabled = _browse.IsEnabled = _confirm.IsEnabled = !_busy;
        _connect.IsEnabled = !_busy && account != null;
        _upload.IsEnabled = !_busy && account != null && _selected != null && _confirm.IsChecked == true && _recipientProblem == null;
        _connect.Content = account == null ? "Connect cloud account…"
            : (_engine.CloudFiles.SignIn?.IsConnected(account) == true ? "Reconnect " : "Connect ") + CloudFilesAuthorization.ProviderName(account.Kind) + "…";
        _progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = _busy ? "Cancel operation" : "Cancel";
    }

    private async Task ConnectAsync()
    {
        if (_busy || _accounts.SelectedItem is not Account account || _engine.CloudFiles.SignIn is not { } auth) return;
        if (!_compose.TryBeginAttachmentOperation()) return;
        _busy = true; _operation = new CancellationTokenSource(); Refresh();
        _status.Text = "Complete cloud permission for " + account.Email + " in your browser. No file is uploaded by connecting.";
        try
        {
            await _engine.WithProfileOperationAsync(token => auth.SignInAsync(account.Clone(), Ui.OpenExternal, token), _operation.Token);
            _status.Text = "Connected " + CloudFilesAuthorization.ProviderName(account.Kind) + " for " + account.Email + ". Review the file and people, then confirm the upload.";
        }
        catch (OperationCanceledException) { _status.Text = "Cloud sign-in cancelled."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _compose.AttachmentOperationBusy = false; FinishOperation(); }
    }

    private async Task UploadAsync()
    {
        if (_busy || _accounts.SelectedItem is not Account selectedAccount || _selected == null || _confirm.IsChecked != true) return;
        _busy = true; _operation = new CancellationTokenSource(); _recovery.Visibility = Visibility.Collapsed; Refresh();
        try
        {
            _selected.Refresh();
            if (!_selected.Exists || _selected.Length != _selectedLength || _selected.LastWriteTimeUtc != _selectedWrittenAt)
                throw new InvalidOperationException("The file changed since you selected it. Choose and review it again.");
            var plan = CloudSharePlan.Create(selectedAccount, _outgoingName, _selectedLength,
                _compose.To, _compose.Cc, _compose.Bcc, _access.SelectedIndex == 1 ? CloudFileAccess.Edit : CloudFileAccess.View);
            if (!_recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.Recipients))
                throw new InvalidOperationException("The recipients changed. Close this window and review the sharing list again.");
            var progress = new Progress<CloudUploadProgress>(value =>
            {
                if (!_busy) return;
                _status.Text = value.Stage;
                _progress.IsIndeterminate = value.TotalBytes == 0;
                if (value.TotalBytes > 0) _progress.Value = 100.0 * value.UploadedBytes / value.TotalBytes;
            });
            using var file = new FileStream(_selected.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            UploadedFile = await _compose.UploadCloudFileAsync(plan,
                ct => _engine.CloudFiles.UploadAsync(plan, file, progress, ct), _insert,
                uploaded => _engine.CloudFiles.RemoveNewFileAsync(plan.Account, uploaded), _operation.Token);
            if (UploadedFile != null)
            {
                _busy = false;
                DialogResult = true;
            }
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled. Any newly created cloud copy was removed; the message was not sent."; }
        catch (CloudSharingException ex)
        {
            _status.Text = ex.Message;
            _recoveryUrl = ex.RecoveryUrl;
            _recovery.Visibility = ex.CleanupRequired && _recoveryUrl != null ? Visibility.Visible : Visibility.Collapsed;
            _closeAfterCancel = false; // Keep cleanup instructions visible instead of closing an unfinished operation.
        }
        catch (ReauthRequiredException ex) { _status.Text = ex.Message; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { FinishOperation(); }
    }

    private void RequestCancel()
    {
        _operation?.Cancel();
        _status.Text = "Cancelling and removing any new cloud copy…";
        _cancel.IsEnabled = false;
    }

    private void FinishOperation()
    {
        _operation?.Dispose(); _operation = null;
        _busy = false; _cancel.IsEnabled = true; Refresh();
        if (_closeAfterCancel && UploadedFile == null) Close();
    }
}
