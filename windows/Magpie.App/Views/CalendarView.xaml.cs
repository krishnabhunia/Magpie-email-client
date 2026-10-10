using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.App.Services;
using Magpie.App.ViewModels;

namespace Magpie.App.Views;

/// <summary>Design B2: the calendar page (its model is <see cref="CalendarViewModel"/>).</summary>
public partial class CalendarView : UserControl
{
    private CalendarViewModel? _vm;

    public CalendarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as CalendarViewModel);
        IsVisibleChanged += (_, _) => { if (IsVisible) { _vm?.Refresh(); ScrollToMorning(); } };
    }

    private void Attach(CalendarViewModel? vm)
    {
        if (_vm != null) { _vm.ScrollToMorning -= ScrollToMorning; _vm.OpenEvent -= OnOpenEvent; }
        _vm = vm;
        if (_vm != null) { _vm.ScrollToMorning += ScrollToMorning; _vm.OpenEvent += OnOpenEvent; }
    }

    /// <summary>Working hours in view: from 07:30, or an hour before now on today's page.</summary>
    private void ScrollToMorning() => Dispatcher.BeginInvoke(() =>
    {
        var hour = _vm?.Days.Any(d => d.IsToday) == true ? Math.Max(0, DateTime.Now.TimeOfDay.TotalHours - 1.5) : 7.5;
        DayScroll.ScrollToVerticalOffset(Math.Min(hour, 16) * CalendarViewModel.HourHeight);
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void OnOpenEvent(Magpie.Core.Models.CalendarEvent e) => EventWindow.Open(Window.GetWindow(this), e, _vm!);

    /// <summary>Double-click a time in Day / Week: a new event there.</summary>
    private void OnDayGridClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement el || el.DataContext is not DayColumn day || _vm == null) return;
        _vm.NewEventAt(day.Date, e.GetPosition(el).Y / CalendarViewModel.HourHeight * 60);
        e.Handled = true;
    }

    private void OnSignInAgain(object sender, RoutedEventArgs e)
    {
        if (_vm == null || AppServices.Engine.AccountById(_vm.ProblemAccountId) is not { } account) return;
        AddAccountWindow.ShowReauth(Window.GetWindow(this), account);
    }
}
