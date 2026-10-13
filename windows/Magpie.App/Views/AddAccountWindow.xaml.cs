using System.Windows;
using Magpie.App.ViewModels;
using Magpie.Core.Models;

namespace Magpie.App.Views;

public partial class AddAccountWindow : Window
{
    private readonly AddAccountViewModel _vm;

    public static void ShowWelcome(Window owner) => Show(owner, null, welcome: true);
    public static void ShowAdd(Window owner) => Show(owner, null, welcome: false);
    public static void ShowReauth(Window owner, Account account) => Show(owner, account, welcome: false);

    private static void Show(Window owner, Account? reauth, bool welcome)
    {
        var w = new AddAccountWindow(new AddAccountViewModel(reauth, welcome));
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
    }

    public AddAccountWindow(AddAccountViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Done += () => { DialogResult = true; };
        Loaded += (_, _) => { if (!vm.IsReauth) EmailBox.Focus(); else PwdBox.Focus(); };
        Closing += (_, _) => vm.CancelCommand.Execute(null);
    }

    private async void OnEmailDone(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) => await _vm.FillServersAsync();

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _vm.Password = PwdBox.Password;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnRestoreBackup(object sender, RoutedEventArgs e) => await Services.BackupUi.RestoreAsync(this);
}
