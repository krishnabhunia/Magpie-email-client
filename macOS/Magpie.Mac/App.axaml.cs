using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Magpie.Core;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Updates;
using Magpie.Mac.Services;
using Magpie.Mac.Views;

namespace Magpie.Mac;

public partial class App : Application
{
    private bool _quitting, _quitConfirmed;

    /// <summary>Magpie draws dark when the Mac is in Dark Mode.</summary>
    public static bool IsDark => Current?.ActualThemeVariant == ThemeVariant.Dark;

    /// <summary>Raised when the Mac switches between light and dark.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Tests (and anything else that makes its own engine) set this first; otherwise the real one is opened.</summary>
    public static Func<MailEngine>? EngineFactory { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        NativeMenu.SetMenu(this, MacMenus.AppMenu());
        ActualThemeVariantChanged += (_, _) => ThemeChanged?.Invoke();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life)
        {
            // The main window hides when closed (the Mac way); Quit (⌘Q) ends Magpie.
            life.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!StartEngine(out var problem))
            {
                if (problem == null) life.Shutdown(1);
                else Dispatcher.UIThread.Post(async () => { await Dialogs.Error("Magpie", problem); life.Shutdown(1); });
                base.OnFrameworkInitializationCompleted();
                return;
            }
            var main = new MainWindow();
            life.MainWindow = main;
            main.Show();
            life.ShutdownRequested += OnShutdownRequested;
            life.Exit += (_, _) => OnExit();
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += (_, e) => { if (e.Kind == ActivationKind.Reopen) MacMenus.ShowMain(); };
            AfterStart(main);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static bool StartEngine(out string? problem)
    {
        problem = null;
        if (AppServices.Engine != null) return true;
        try
        {
            if (EngineFactory != null) { AppServices.Engine = EngineFactory(); return true; }
            var paths = AppPaths.Default();
            ISecretProtector protector = new KeychainProtector(new MacKeychainStore());
            if (SettingsBackup.ApplyPending(paths, protector)) Log.Info("started with restored settings");
            AppServices.Engine = new MailEngine(paths, protector);
            // Auto update put this version in place when the previous one quit: this is its first start.
            var note = InstalledUpdate.Read(paths.Updates);
            if (note != null && note.To == AppServices.Current.ToString() && !AppServices.AfterUpdate) AppServices.UpdatedFrom = note.From;
            if (note != null) InstalledUpdate.Clear(paths.Updates);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("start-up failed", ex);
            if (AppServices.AfterUpdate && MacInstaller.OfferRollback(ex, AppServices.UpdatedFrom)) return false;
            problem = "Magpie could not open its data folder:\n\n" + ex.Message + (Log.FilePath != null ? "\n\nDetails are in " + Log.FilePath : "");
            return false;
        }
    }

    private static void AfterStart(MainWindow main)
    {
        var e = AppServices.Engine;
        Log.Info($"Magpie for Mac {AppServices.Current} starting, {e.Accounts.Count} account(s)" + (AppServices.AfterUpdate ? $" (updated from {AppServices.UpdatedFrom})" : ""));
        e.Start();
        var updates = new MacUpdateService();
        AppServices.Updates = updates;
        main.AttachUpdates(updates);
        updates.Start();
        Program.StartupComplete = true;
        if (AppServices.AfterUpdate)
            Dispatcher.UIThread.Post(() => main.ViewModel.ShowNote($"Magpie updated to {AppServices.Current}. Your mail and settings are as they were."),
                DispatcherPriority.Background);
        if (e.Accounts.Count == 0) Dispatcher.UIThread.Post(() => AddAccountWindow.Open(null), DispatcherPriority.Background);
    }

    /// <summary>Open compose windows get to keep or discard their message first. False = the user chose Cancel.</summary>
    public static async Task<bool> CloseComposeWindowsAsync()
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime life) return true;
        foreach (var w in life.Windows.OfType<ComposeWindow>().ToList())
            if (!await w.CloseForQuitAsync()) return false;
        return true;
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_quitConfirmed) return;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime life && life.Windows.OfType<ComposeWindow>().Any(w => w.HasUnsavedContent))
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (!await CloseComposeWindowsAsync()) return;
                _quitConfirmed = true;
                life.Shutdown();
            });
            return;
        }
        _quitConfirmed = true;
    }

    /// <summary>The updater has installed the new version and will start it: quit without asking again.</summary>
    public static void QuitForUpdate()
    {
        if (Current is App app) app._quitConfirmed = true;
        (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public static bool Quitting => (Current as App)?._quitting == true;

    private void OnExit()
    {
        if (_quitting) return;
        _quitting = true;
        try { (AppServices.MainWindow as MainWindow)?.SavePlacement(); } catch { }
        try { AppServices.Updates?.InstallOnQuit(); } catch (Exception ex) { Log.Error("install on quit", ex); }
        try { AppServices.Engine?.Dispose(); } catch { }
        Log.Info("Magpie exiting");
    }
}
