using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Auth;
using Magpie.Core.CloudFiles;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>Explicit, separate existing-file browsing and write-access confirmation.</summary>
public sealed class CloudExistingFileDialog : Window
{
    private readonly ComposeViewModel _compose;
    private readonly MailEngine _engine = AppServices.Engine;
    private readonly Func<CloudUploadedFile, Task<bool>> _insert;
    private readonly ComboBox _accounts = new() { DisplayMemberPath = nameof(Account.Label), MinHeight = 32 };
    private readonly Button _connect = new() { Content = "Connect existing-file access…", Margin = new Thickness(0, 7, 0, 0) };
    private readonly TextBox _searchText = new() { MinHeight = 30, MaxLength = 100 };
    private readonly Button _search = new() { Content = "Search cloud files", Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListBox _files = new() { DisplayMemberPath = nameof(CloudExistingFile.Name), MinHeight = 100, MaxHeight = 180 };
    private readonly TextBox _title = new() { MinHeight = 30, MaxLength = 160 };
    private readonly ComboBox _access = new() { MinHeight = 32, ItemsSource = new[] { "View only", "Can edit" }, SelectedIndex = 0 };
    private readonly CheckBox _confirm = new() { Margin = new Thickness(0, 13, 0, 12) };
    private readonly Button _share = new() { Content = "Share existing file & insert link", MinWidth = 210 };
    private readonly Button _cancel = new() { Content = "Cancel", MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _recovery = new() { Content = "Review cloud file permissions", Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ProgressBar _progress = new() { Height = 5, IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private readonly string[] _recipients = Array.Empty<string>();
    private readonly string? _recipientProblem;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private bool _closeAfterCancel;
    private string? _recoveryUrl;
    public CloudUploadedFile? SharedFile { get; private set; }

    public CloudExistingFileDialog(ComposeViewModel compose, Func<CloudUploadedFile, Task<bool>> insert)
    {
        _compose = compose;
        _insert = insert;
        Title = "Browse an existing cloud file — Magpie";
        Width = 640; MaxHeight = 860;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Window.Dialog");
        NameScope.SetNameScope(this, new NameScope());
        try { _recipients = CloudSharePlan.RecipientsFor(compose.To, compose.Cc, compose.Bcc); }
        catch (InvalidOperationException ex) { _recipientProblem = ex.Message; }
        var available = _engine.Accounts.Where(a => a.Kind is AccountKind.Gmail or AccountKind.Microsoft).ToArray();
        _accounts.ItemsSource = available;
        _accounts.SelectedItem = available.FirstOrDefault(a => a.Id == compose.From?.Id) ?? available.FirstOrDefault();
        _confirm.Content = new TextBlock
        {
            Text = "I approve changing this existing file's sharing permissions for exactly the named recipients and role above.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 535,
        };
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock { Text = "Browse & share an existing cloud file", FontSize = 19, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "Connecting existing-file browsing is a separate permission. Google requests broad Drive access to list and share files; OneDrive requests read/write access to your files. "
                + "Magpie only lists files you own. Connecting or searching does not share anything.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10),
        });
        AddLabel(root, "Cloud account"); root.Children.Add(_accounts); root.Children.Add(_connect);
        AddLabel(root, "Search existing files in this account");
        var searchRow = new DockPanel();
        DockPanel.SetDock(_search, Dock.Right);
        searchRow.Children.Add(_search); searchRow.Children.Add(_searchText);
        root.Children.Add(searchRow);
        root.Children.Add(new TextBlock { Text = "Empty search shows recent files in your Drive root (OneDrive) or owned files (Google). Search by name to find more.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 6) });
        root.Children.Add(_files);
        AddLabel(root, "Editable link title"); root.Children.Add(_title);
        AddLabel(root, "People receiving access · To and Cc");
        var people = new ListBox { ItemsSource = _recipients, MaxHeight = 120, MinHeight = 36 };
        root.Children.Add(people);
        AddLabel(root, "Access role"); root.Children.Add(_access);
        root.Children.Add(new TextBlock
        {
            Text = "Sharing changes the original file's permissions immediately. Discarding the draft, removing the link, or undoing Send does not revoke access. "
                + "Public, inherited, group, or unreviewed existing permissions block sharing. Magpie never deletes or overwrites your existing file. "
                + "Bcc-only recipients cannot be granted hidden access.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 11, 0, 0),
        });
        root.Children.Add(_confirm);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_share); actions.Children.Add(_cancel);
        root.Children.Add(actions);
        root.Children.Add(_progress); root.Children.Add(_status); root.Children.Add(_recovery);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        foreach (var button in new[] { _connect, _search, _cancel, _recovery }) button.SetResourceReference(StyleProperty, "Button.Secondary");
        _share.SetResourceReference(StyleProperty, "Button.Primary");
        AutomationProperties.SetName(_accounts, "Existing cloud account");
        AutomationProperties.SetName(_searchText, "Search existing cloud files by name");
        AutomationProperties.SetName(_files, "Existing cloud file search results");
        AutomationProperties.SetName(_title, "Cloud file link title");
        AutomationProperties.SetName(people, "Reviewed existing cloud recipients");
        AutomationProperties.SetName(_confirm, "Approve existing-file sharing permission changes");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        RegisterName("ExistingCloudAccounts", _accounts); RegisterName("ExistingCloudConnect", _connect);
        RegisterName("ExistingCloudSearchText", _searchText); RegisterName("ExistingCloudSearch", _search);
        RegisterName("ExistingCloudFiles", _files); RegisterName("ExistingCloudTitle", _title);
        RegisterName("ExistingCloudRecipients", people); RegisterName("ExistingCloudAccess", _access);
        RegisterName("ExistingCloudConfirm", _confirm); RegisterName("ExistingCloudShare", _share);
        RegisterName("ExistingCloudStatus", _status);
        _accounts.SelectionChanged += (_, _) => { _files.ItemsSource = null; _title.Text = ""; ResetApproval(); };
        _files.SelectionChanged += (_, _) =>
        {
            _title.Text = (_files.SelectedItem as CloudExistingFile)?.Name ?? "";
            ResetApproval();
        };
        _access.SelectionChanged += (_, _) => ResetApproval();
        _title.TextChanged += (_, _) => ResetApproval();
        _searchText.TextChanged += (_, _) => { _files.ItemsSource = null; _title.Text = ""; ResetApproval(); };
        _confirm.Checked += (_, _) => Refresh(); _confirm.Unchecked += (_, _) => Refresh();
        _connect.Click += async (_, _) => await ConnectAsync();
        _search.Click += async (_, _) => await SearchAsync();
        _share.Click += async (_, _) => await ShareAsync();
        _cancel.Click += (_, _) => { if (_busy) RequestCancel(); else Close(); };
        _recovery.Click += (_, _) => { if (_recoveryUrl != null) Ui.OpenExternal(_recoveryUrl); };
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closeAfterCancel = true; RequestCancel(); } };
        if (_recipientProblem != null) _status.Text = _recipientProblem;
        else if (available.Length == 0) _status.Text = "Add a Google or Microsoft account in Settings first. Ordinary file attachments remain available.";
        Refresh();
    }

    private static void AddLabel(Panel panel, string label) => panel.Children.Add(new TextBlock
    {
        Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 5),
    });

    private void ResetApproval() { _confirm.IsChecked = false; Refresh(); }

    private void Refresh()
    {
        var account = _accounts.SelectedItem as Account;
        var connected = account != null && _engine.CloudFiles.SignIn?.IsExistingConnected(account) == true;
        _accounts.IsEnabled = _access.IsEnabled = _title.IsEnabled = _searchText.IsEnabled = _files.IsEnabled = _confirm.IsEnabled = !_busy;
        _connect.IsEnabled = !_busy && account != null;
        _search.IsEnabled = !_busy && connected;
        _share.IsEnabled = !_busy && connected && _files.SelectedItem is CloudExistingFile
            && _confirm.IsChecked == true && _recipientProblem == null && !_compose.AttachmentOperationBusy;
        _connect.Content = account == null ? "Connect existing-file access…"
            : (connected ? "Reconnect " : "Connect ") + CloudFilesAuthorization.ProviderName(account.Kind) + " existing-file access…";
        _progress.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = _busy ? "Cancel operation" : "Cancel";
    }

    private async Task ConnectAsync()
    {
        if (_busy || _accounts.SelectedItem is not Account account || _engine.CloudFiles.SignIn is not { } auth) return;
        if (!_compose.TryBeginAttachmentOperation()) return;
        _busy = true; _operation = new CancellationTokenSource(); Refresh();
        _status.Text = "Approve the separate existing-file browse/share permission in your browser. No file is shared by connecting.";
        try
        {
            await _engine.WithProfileOperationAsync(token => auth.SignInExistingAsync(account.Clone(), Ui.OpenExternal, token), _operation.Token);
            _status.Text = "Connected. Search for a file, review its existing access, and explicitly confirm sharing.";
        }
        catch (OperationCanceledException) { _status.Text = "Existing-file connection cancelled."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _compose.AttachmentOperationBusy = false; Finish(); }
    }

    private async Task SearchAsync()
    {
        if (_busy || _accounts.SelectedItem is not Account account) return;
        if (!_compose.TryBeginAttachmentOperation()) return;
        _busy = true; _operation = new CancellationTokenSource(); Refresh();
        var search = _searchText.Text;
        _status.Text = "Searching files owned by " + account.Email + "…";
        try
        {
            var results = await _engine.WithProfileOperationAsync(token => _engine.CloudFiles.BrowseExistingAsync(account.Clone(), search, token), _operation.Token);
            if (account != _accounts.SelectedItem || search != _searchText.Text) throw new InvalidOperationException("The account or search changed. Search again.");
            _files.ItemsSource = results.Files;
            _status.Text = results.Files.Count == 0 ? "No shareable files found. Search by a more specific name or attach a local copy."
                : $"Found {results.Files.Count} file(s). " + (results.HasMore ? "More exist; narrow your search. " : "")
                    + "Select a file, confirm the recipient list, then grant access.";
        }
        catch (OperationCanceledException) { _status.Text = "Search cancelled. No file was modified."; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _compose.AttachmentOperationBusy = false; Finish(); }
    }

    private async Task ShareAsync()
    {
        if (_busy || _accounts.SelectedItem is not Account account || _files.SelectedItem is not CloudExistingFile file
            || _confirm.IsChecked != true) return;
        _busy = true; _operation = new CancellationTokenSource(); _recovery.Visibility = Visibility.Collapsed; Refresh();
        _status.Text = "Reviewing existing access before sharing. No public links will be created.";
        try
        {
            var plan = CloudExistingSharePlan.Create(account, file, _title.Text,
                _compose.To, _compose.Cc, _compose.Bcc, _access.SelectedIndex == 1 ? CloudFileAccess.Edit : CloudFileAccess.View);
            if (!_recipients.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(plan.Recipients))
                throw new InvalidOperationException("Recipients changed. Close this window and review the list again.");
            SharedFile = await _compose.ShareExistingCloudFileAsync(plan,
                token => _engine.CloudFiles.ShareExistingAsync(plan, token), _insert, _operation.Token);
            if (SharedFile != null) { _busy = false; DialogResult = true; }
        }
        catch (OperationCanceledException) { _status.Text = "Cancelled before access was granted. No link was inserted."; }
        catch (CloudSharingException ex)
        {
            _status.Text = ex.Message;
            _recoveryUrl = ex.RecoveryUrl;
            _recovery.Visibility = ex.CleanupRequired && _recoveryUrl != null ? Visibility.Visible : Visibility.Collapsed;
            _closeAfterCancel = false;
        }
        catch (ReauthRequiredException ex) { _status.Text = ex.Message; }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { Finish(); }
    }

    private void RequestCancel()
    {
        _operation?.Cancel();
        _status.Text = "Cancelling. If existing-file sharing already started, review its permissions in the cloud app.";
        _cancel.IsEnabled = false;
    }

    private void Finish()
    {
        _operation?.Dispose(); _operation = null;
        _busy = false; _cancel.IsEnabled = true; Refresh();
        if (_closeAfterCancel && SharedFile == null) Close();
    }
}
