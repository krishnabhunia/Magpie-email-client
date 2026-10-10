using System.Windows;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;

namespace Magpie.App.Views;

/// <summary>Design B4: the Contacts page (its model is <see cref="ContactsViewModel"/>).</summary>
public partial class ContactsView : UserControl
{
    private ContactsViewModel? _vm;

    public ContactsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ContactsViewModel);
        IsVisibleChanged += (_, _) => { if (IsVisible) _vm?.Refresh(); };
    }

    private void Attach(ContactsViewModel? vm)
    {
        if (_vm != null) { _vm.OpenThread -= OnOpenThread; _vm.OpenEvent -= OnOpenEvent; _vm.EditContact -= OnEdit; }
        _vm = vm;
        if (_vm != null) { _vm.OpenThread += OnOpenThread; _vm.OpenEvent += OnOpenEvent; _vm.EditContact += OnEdit; }
    }

    private MainViewModel? Main => Window.GetWindow(this)?.DataContext as MainViewModel;

    private void OnOpenThread(Magpie.Core.Models.ThreadRow row)
    {
        if (Main is { } main) ThreadWindow.Open(main, row);
    }

    private void OnOpenEvent(Magpie.Core.Models.CalendarEvent e)
    {
        if (Main is { } main) EventWindow.Open(Window.GetWindow(this), e, main.Cal);
    }

    private void OnEdit(Magpie.Core.Models.SavedContact c) => ContactEditWindow.Edit(Window.GetWindow(this), c);

    private void OnSignInAgain(object sender, RoutedEventArgs e)
    {
        if (_vm == null || AppServices.Engine.AccountById(_vm.ProblemAccountId) is not { } account) return;
        AddAccountWindow.ShowReauth(Window.GetWindow(this), account);
    }
}
