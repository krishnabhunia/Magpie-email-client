using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;

namespace Magpie.App.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly SettingsViewModel _vm;

    public static void Open(string? page)
    {
        if (_open != null)
        {
            if (page != null) _open._vm.GoTo(page);
            _open.Activate();
            return;
        }
        var w = new SettingsWindow(page);
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = w;
        w.Closed += (_, _) => _open = null;
        w.Show();
    }

    /// <summary>Design HM1: Settings → Rules with a new rule already started.</summary>
    public static void OpenNewRule(Magpie.Core.Mail.RuleField field, string value, string name)
    {
        Open("Rules:Filters");
        _open?._vm.NewRuleWith(field, value, name);
    }

    public SettingsWindow(string? page)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(page);
        DataContext = _vm;
        ApiKeyBox.Password = _vm.ApiKey;
        _vm.ApiKeyChanged = false;
        _vm.Saved += Close;
        _vm.Applied += ShowApplied;
        _vm.ApiKeyReloaded += () =>
        {
            _reloadingKey = true;
            ApiKeyBox.Password = _vm.ApiKey;
            _reloadingKey = false;
        };
        _vm.HighlightRequested += Highlight;
        Action onAutoDelete = () => Ui.Post(_vm.LoadAutoDelete);
        AppServices.Engine.AutoDeleteChanged += onAutoDelete;
        Closed += (_, _) => { AppServices.Engine.AutoDeleteChanged -= onAutoDelete; _vm.Detach(); };
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.F) { SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); e.Handled = true; }
        };
        FillKeys();
        _vm.RefreshBackupAndFolder();
    }

    // ───────────────────────── search (design SS1) ─────────────────────────

    private void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm.SearchText.Length > 0) { _vm.SearchText = ""; e.Handled = true; }
        else if (e.Key == Key.Enter && _vm.SearchHits.Count > 0) { _vm.GoToHitCommand.Execute(_vm.SearchHits[0]); e.Handled = true; }
    }

    private void OnSearchClear(object sender, RoutedEventArgs e) { _vm.SearchText = ""; SettingsSearchBox.Focus(); }

    /// <summary>Scrolls to the setting and flashes it yellow for a moment.</summary>
    private void Highlight(string anchor)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (FindName(anchor) is not FrameworkElement el) return;
            el.BringIntoView();
            var target = el;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0, 0xFF, 0xE0, 0x66));
            var anim = new System.Windows.Media.Animation.ColorAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1600) };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Color.FromArgb(0xB0, 0xFF, 0xE0, 0x66), TimeSpan.FromMilliseconds(150)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Color.FromArgb(0xB0, 0xFF, 0xE0, 0x66), TimeSpan.FromMilliseconds(900)));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearColorKeyFrame(System.Windows.Media.Color.FromArgb(0, 0xFF, 0xE0, 0x66), TimeSpan.FromMilliseconds(1600)));
            switch (target)
            {
                case Border b: { var old = b.Background; b.Background = brush; anim.Completed += (_, _) => b.Background = old; break; }
                case Panel p: { var old = p.Background; p.Background = brush; anim.Completed += (_, _) => p.Background = old; break; }
                case ItemsControl ic: { var old = ic.Background; ic.Background = brush; anim.Completed += (_, _) => ic.Background = old; break; }
                default: return;
            }
            brush.BeginAnimation(System.Windows.Media.SolidColorBrush.ColorProperty, anim);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ───────────────────────── About Me (design A1) ─────────────────────────

    /// <summary>"What's this email about?" — then Magpie's own compose window opens, pre-filled.</summary>
    private void OnFeedbackEmail(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = FeedbackLink, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(new MenuItem { Header = "What's this email about?", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        menu.Items.Add(new Separator());
        foreach (var kind in SettingsViewModel.FeedbackKinds)
        {
            var k = kind;
            var mi = new MenuItem { Header = k };
            mi.Click += (_, _) => _vm.ComposeFeedback(k);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private bool _reloadingKey;

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (_reloadingKey) return;
        _vm.ApiKey = ApiKeyBox.Password;
        _vm.ApiKeyChanged = true;
        _vm.NoteChange();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
    }

    private void OnApply(object sender, RoutedEventArgs e) => _vm.ApplyCommand.Execute(null);

    private async void ShowApplied()
    {
        AppliedNote.Visibility = Visibility.Visible;
        await Task.Delay(2500);
        AppliedNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>A sample new-mail notification, as the settings on the page say (design N1).</summary>
    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        if (!_vm.Notifications) { Ui.Error("Test notification", "Notifications are off. Switch on \"Show notifications\" first.", this); return; }
        AppServices.Tray?.ShowBalloon("Anita Rao (test)", "Lunch on Friday? — this is how a new email is announced.", () => ((App)Application.Current).ShowMain());
        if (_vm.NotificationSound) System.Media.SystemSounds.Asterisk.Play();
    }

    // ───────────────────────── signature (design B6) ─────────────────────────

    /// <summary>Opens the signature editor window for the chosen account.</summary>
    private void OnEditSignature(object sender, RoutedEventArgs e)
    {
        if (_vm.SignatureAccount is not { } a) return;
        var html = SignatureEditorWindow.Edit(this, a.Email, a.SignatureHtml);
        if (html != null) _vm.SetSignatureHtml(html);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────── backup (design EX1) and mail folder (design DL1) ─────────────────────────

    private async void OnBackupSave(object sender, RoutedEventArgs e)
    {
        await BackupUi.SaveAsync(this);
        _vm.RefreshBackupAndFolder();
    }

    private async void OnBackupRestore(object sender, RoutedEventArgs e) => await BackupUi.RestoreAsync(this);

    private void OnMailOpen(object sender, RoutedEventArgs e) => Ui.OpenExternal(AppServices.Engine.Paths.MailRoot);

    private void OnMailMove(object sender, RoutedEventArgs e)
    {
        var paths = AppServices.Engine.Paths;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Where should Magpie keep your mail?" };
        if (dlg.ShowDialog(this) != true) return;
        var target = dlg.FolderName;
        if (string.Equals(Path.GetFullPath(target), paths.MailRoot, StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetFullPath(target).StartsWith(paths.MimeCache, StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error("Move your mail", "Choose a folder outside Magpie's own message folder.", this);
            return;
        }
        var size = Services.MailFolderStartup.Size(Magpie.Core.Storage.MailLocation.Size(paths.MailRoot));
        var choice = Views.ChoiceDialog.Ask(this, "Move your mail to " + target + "?",
            $"Magpie restarts and moves {size}. Nothing is removed from {paths.MailRoot} until the copy is checked.\n\n"
            + "Settings and sign-ins stay in your Windows profile: Magpie needs them to find the folder.",
            "Move and restart");
        if (choice != 0) return;
        Magpie.Core.Storage.MailLocation.RequestMove(paths, target);
        ((App)Application.Current).RestartApp();
    }

    private void OnAddAccount(object sender, RoutedEventArgs e)
    {
        Close();
        AddAccountWindow.ShowAdd(Application.Current.MainWindow);
    }

    private void OnReauth(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is EditableAccount a) AddAccountWindow.ShowReauth(this, a.Original);
    }

    private void OnImportGoogle(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Google OAuth client (*.json)|*.json", Title = "Choose the client_secret….json you downloaded" };
        if (dlg.ShowDialog(this) != true) return;
        var err = _vm.ImportGoogleJson(dlg.FileName);
        if (err != null) Ui.Error("Import", err, this);
    }

    private void FillKeys()
    {
        var keys = new (string key, string what)[]
        {
            ("Ctrl+N", "New message"), ("Ctrl+Enter", "Send (in a new message)"), ("R", "Reply"), ("A  or  Shift+R", "Reply all"), ("F", "Forward"),
            ("E", "Archive"), ("Delete", "Move to Trash"), ("S", "Snooze"), ("L", "Set aside / back to Inbox"), ("P", "Pin / unpin"), ("U", "Mark as unread"),
            ("J  /  K", "Next / previous conversation"), ("/  or  Ctrl+F", "Search"), ("Esc", "Clear search / selection / close a new message"), ("F5", "Check for mail"),
            ("Ctrl+Shift+Enter", "Send now (a message waiting to be sent)"), ("Ctrl+Z", "Undo send (the newest one)"),
            ("Ctrl+Shift+←  /  →", "Sidebar narrower / wider (narrowest = icon rail)"), ("Ctrl+Shift+B", "Show / hide the sidebar"),
            ("Click a sender's initials", "Tick a conversation for a bulk action"),
        };
        for (int i = 0; i < keys.Length; i++)
        {
            KeysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i].key, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
            var w = new TextBlock { Text = keys[i].what, Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(k, i);
            Grid.SetRow(w, i);
            Grid.SetColumn(w, 1);
            KeysGrid.Children.Add(k);
            KeysGrid.Children.Add(w);
        }
    }
}
