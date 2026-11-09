using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;

namespace Magpie.App.Views;

public partial class ComposeWindow
{
    private void OnSecurityKeys(object sender, RoutedEventArgs e)
    {
        if (_vm.From != null) SecureMailKeysWindow.Create(this, AppServices.Engine, _vm.From.Id).ShowDialog();
    }
}

public partial class SettingsWindow
{
    private void OnSecurityKeys(object sender, RoutedEventArgs e)
    {
        var account = (sender as FrameworkElement)?.DataContext as EditableAccount;
        SecureMailKeysWindow.Create(this, AppServices.Engine, account?.Original.Id).ShowDialog();
    }
}
