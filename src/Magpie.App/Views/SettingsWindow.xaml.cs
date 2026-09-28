using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;

namespace Magpie.App.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    private readonly SettingsViewModel _vm;

    public static void Open(string? page)
    {
        if (_open != null)
        {
            if (page != null) _open._vm.Page = page;
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

    public SettingsWindow(string? page)
    {
        InitializeComponent();
        _vm = new SettingsViewModel(page);
        DataContext = _vm;
        ApiKeyBox.Password = _vm.ApiKey;
        _vm.ApiKeyChanged = false;
        _vm.Saved += Close;
        _vm.HighlightRequested += Highlight;
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (ctrl && e.Key == Key.F) { SettingsSearchBox.Focus(); SettingsSearchBox.SelectAll(); e.Handled = true; }
        };
        FillKeys();
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

    private void OnApiKeyChanged(object sender, RoutedEventArgs e)
    {
        _vm.ApiKey = ApiKeyBox.Password;
        _vm.ApiKeyChanged = true;
    }

    private void OnSave(object sender, RoutedEventArgs e) => _vm.SaveCommand.Execute(null);
    private void OnCancel(object sender, RoutedEventArgs e) => Close();

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
            ("E", "Archive"), ("Delete", "Move to Trash"), ("S", "Snooze"), ("P", "Pin / unpin"), ("U", "Mark as unread"),
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
