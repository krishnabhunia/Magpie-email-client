using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Magpie.Core;
using Magpie.Mac.Services;
using Magpie.Mac.ViewModels;

namespace Magpie.Mac;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    /// <summary>The reading pane's web view.</summary>
    public WebSurface ReaderSurface { get; }
    private readonly ReaderPresenter _presenter;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new MainViewModel();
        DataContext = ViewModel;
        NativeMenu.SetMenu(this, MacMenus.ForWindow(this));
        RestorePlacement();

        ReaderSurface = new WebSurface();
        ReaderHost.Child = ReaderSurface;
        _presenter = new ReaderPresenter(ReaderSurface, () => AppServices.Engine.Config.Appearance.FolderHover.DelayMs);
        ReaderSurface.Message += ViewModel.Reader.OnPageMessage;
        ReaderSurface.MailtoClicked += href => Views.ComposeWindow.OpenMailto(href, ViewModel.Reader.AccountId);
        ViewModel.Reader.PageReady += _presenter.Show;
        _presenter.LoadShell(App.IsDark);
        ViewModel.Reader.Clear();
        App.ThemeChanged += OnThemeChanged;

        TopBar.PointerPressed += OnTopBarPressed;
        TopBar.DoubleTapped += (_, e) =>
        {
            if (e.Source is Button) return;
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        };
        ThreadList.KeyDown += OnListKey;
    }

    /// <summary>The updater starts after the window: its button appears once it exists.</summary>
    public void AttachUpdates(MacUpdateService updates) => ViewModel.UpdatesAttached();

    private void OnThemeChanged()
    {
        _presenter.LoadShell(App.IsDark);
        ViewModel.Reader.Redraw();
    }

    /// <summary>The top bar sits in the title bar: dragging it moves the window.</summary>
    private void OnTopBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1) BeginMoveDrag(e);
    }

    /// <summary>⌫ or Delete on the list moves the conversation to Trash (⌘⌫ works everywhere through the Message menu).</summary>
    private void OnListKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Back or Key.Delete && e.KeyModifiers == KeyModifiers.None && ViewModel.Reader.HasThread)
        {
            ViewModel.Reader.DeleteCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>⌘F: the search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>Closing the main window hides it (Magpie keeps checking mail); the Dock icon or Window → Magpie brings it back.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!App.Quitting && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
        {
            e.Cancel = true;
            SavePlacement();
            Hide();
            return;
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ThemeChanged -= OnThemeChanged;
        ReaderSurface.DisposeView();
        base.OnClosed(e);
    }

    private void RestorePlacement()
    {
        var w = AppServices.Engine.Config.Window;
        if (w.Width >= MinWidth && w.Height >= MinHeight) { Width = w.Width; Height = w.Height; }
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    public void SavePlacement()
    {
        try
        {
            var w = AppServices.Engine.Config.Window;
            w.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { w.Width = Width; w.Height = Height; }
            AppServices.Engine.Settings.Save(notify: false);
        }
        catch (Exception ex) { Log.Warn("save window size: " + ex.Message); }
    }
}
