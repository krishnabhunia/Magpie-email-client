using Avalonia.Controls;
using Magpie.Core.Models;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;

namespace Magpie.Mac.Views;

public partial class AddAccountWindow : Window
{
    private static AddAccountWindow? _open;

    public AddAccountViewModel ViewModel { get; }

    /// <summary>Add an account (<paramref name="reauth"/> null) or sign one in again. One such window at a time.</summary>
    public static void Open(Account? reauth)
    {
        if (_open != null) { _open.Activate(); return; }
        var w = new AddAccountWindow(reauth);
        _open = w;
        w.Closed += (_, _) => _open = null;
        var owner = Dialogs.ActiveWindow();
        if (owner is { IsVisible: true } && owner != w) w.Show(owner);
        else w.Show();
        w.Activate();
    }

    public AddAccountWindow() : this(null) { }

    public AddAccountWindow(Account? reauth)
    {
        InitializeComponent();
        ViewModel = new AddAccountViewModel(reauth);
        DataContext = ViewModel;
        NativeMenu.SetMenu(this, MacMenus.ForWindow(this));
        ViewModel.Done += Close;
        EmailBox.LostFocus += async (_, _) => await ViewModel.FillServersAsync();
        Opened += (_, _) => { if (!ViewModel.IsReauth) EmailBox.Focus(); };
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.CancelCommand.Execute(null);
        base.OnClosed(e);
    }
}
