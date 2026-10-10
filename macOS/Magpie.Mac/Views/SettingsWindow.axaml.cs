using Avalonia.Controls;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;

namespace Magpie.Mac.Views;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;
    public SettingsViewModel ViewModel { get; }

    /// <summary>Opens Settings (or brings it forward) at a tab: "Accounts", "Updates" or "About".</summary>
    public static void Open(string tab = "Accounts")
    {
        var w = _open;
        if (w == null)
        {
            w = new SettingsWindow();
            _open = w;
            w.Closed += (_, _) => _open = null;
            if (AppServices.MainWindow is { IsVisible: true } main) w.Show(main);
            else w.Show();
        }
        w.SelectTab(tab);
        w.Activate();
    }

    public SettingsWindow()
    {
        InitializeComponent();
        ViewModel = new SettingsViewModel();
        DataContext = ViewModel;
        NativeMenu.SetMenu(this, MacMenus.ForWindow(this));
        ImportGoogleButton.Click += async (_, _) =>
        {
            var files = await Dialogs.PickFiles(this, "Choose the client_secret….json you downloaded", many: false, jsonOnly: "Google OAuth client");
            if (files.Count == 0) return;
            if (ViewModel.ImportGoogleJson(files[0]) is { } error) await Dialogs.Error("Import Google client JSON", error, this);
        };
    }

    public void SelectTab(string tab) => Tabs.SelectedItem = tab switch
    {
        "Updates" => UpdatesTab,
        "About" => AboutTab,
        _ => AccountsTab,
    };

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.Detach();
        base.OnClosed(e);
    }
}
