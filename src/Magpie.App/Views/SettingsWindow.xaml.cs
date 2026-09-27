using System.Windows;
using System.Windows.Controls;
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
        FillKeys();
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
            ("J  /  K", "Next / previous conversation"), ("/  or  Ctrl+F", "Search"), ("Esc", "Clear search / close a new message"), ("F5", "Check for mail"),
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
