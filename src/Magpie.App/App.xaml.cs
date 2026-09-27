using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Magpie.App.Services;
using Magpie.App.Views;
using Magpie.Core;
using Magpie.Core.Models;
using Magpie.Core.Security;

namespace Magpie.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _cts = new();
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("unobserved task", a.Exception); a.SetObserved(); };

        MailEngine engine;
        try
        {
            engine = new MailEngine(AppPaths.Default(), new DpapiProtector());
        }
        catch (Exception ex)
        {
            Log.Error("start-up failed", ex);
            MessageBox.Show("Magpie could not open its data folder:\n\n" + ex.Message, "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        AppServices.Engine = engine;
        Log.Info($"Magpie {typeof(App).Assembly.GetName().Version} starting, {engine.Accounts.Count} account(s)");
        try { StartUi(engine); }
        catch (Exception ex)
        {
            // Without this, a failure here would leave an invisible process holding the single-instance lock.
            Log.Error("start-up failed", ex);
            MessageBox.Show("Magpie could not start:\n\n" + ex.Message + "\n\nDetails are in " + Log.FilePath, "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
            try { engine.Dispose(); } catch { }
            AppServices.Tray?.Dispose();
            Shutdown(1);
        }
    }

    private void StartUi(MailEngine engine)
    {
        // Windows sign-out / restart: give unsent messages in open compose windows a chance to be saved.
        SessionEnding += (_, a) =>
        {
            if (Windows.OfType<ComposeWindow>().Any(w => w.HasUnsavedContent))
            {
                a.Cancel = true;
                ExitApp();
            }
            else ExitApp();
        };

        var tray = new TrayIcon();
        AppServices.Tray = tray;
        tray.OpenRequested += () => ShowMain();
        tray.ComposeRequested += () => { ShowMain(); ComposeWindow.Open(Core.Mail.ComposeMode.New, null); };
        tray.SyncRequested += () => engine.SyncNow();
        tray.ExitRequested += ExitApp;

        engine.NewMail += OnNewMail;
        engine.SendFailed += (item, err) => Ui.Post(() => tray.ShowBalloon("Couldn't send \"" + item.Subject + "\"", err + " Magpie will retry.", () => ShowMain()));
        engine.ReminderDue += r => Ui.Post(() => tray.ShowBalloon("Follow up", r.Subject, () => ShowMain()?.OpenThread(r.AccountId, r.ThreadKey)));
        engine.SnoozeWoke += rows => Ui.Post(() =>
        {
            var first = rows.OrderByDescending(r => r.Date).First();
            tray.ShowBalloon("Back from snooze", first.Subject, () => ShowMain()?.OpenThread(first.AccountId, first.ThreadKey));
        });

        var main = new MainWindow();
        MainWindow = main;
        var startHidden = Program.StartupArgs.Contains("--tray") && engine.Accounts.Count > 0;
        if (!startHidden) main.Show();

        Program.StartActivationServer(args =>
        {
            var m = ShowMain();
            var mailto = args.FirstOrDefault(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
            if (mailto != null) ComposeWindow.OpenMailto(mailto);
        }, _cts.Token);

        engine.Start();
        if (engine.Accounts.Count == 0)
            main.Dispatcher.BeginInvoke(() => AddAccountWindow.ShowWelcome(main), DispatcherPriority.ApplicationIdle);
        var startMailto = Program.StartupArgs.FirstOrDefault(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase));
        if (startMailto != null) main.Dispatcher.BeginInvoke(() => ComposeWindow.OpenMailto(startMailto), DispatcherPriority.ApplicationIdle);
    }

    private void OnNewMail(IReadOnlyList<MessageRow> rows)
    {
        var cfg = AppServices.Engine.Config;
        if (!cfg.Notifications) return;
        var interesting = rows.Where(r => !cfg.NotifyPeopleOnly || r.Category == Category.People).ToList();
        if (interesting.Count == 0) return;
        Ui.Post(() =>
        {
            var main = MainWindow as MainWindow;
            if (main is { IsActive: true }) return; // user is looking at it
            var first = interesting.OrderByDescending(r => r.Date).First();
            var title = interesting.Count == 1 ? first.Sender : $"{interesting.Count} new messages";
            var text = interesting.Count == 1 ? first.Subject : string.Join("\n", interesting.Take(3).Select(r => $"{r.Sender}: {r.Subject}"));
            AppServices.Tray?.ShowBalloon(title, text, () => ShowMain()?.OpenThread(first.AccountId, first.ThreadKey));
            if (cfg.NotificationSound) System.Media.SystemSounds.Asterisk.Play();
        });
    }

    public MainWindow? ShowMain()
    {
        if (MainWindow is not MainWindow m) return null;
        if (!m.IsVisible) m.Show();
        if (m.WindowState == WindowState.Minimized) m.WindowState = WindowState.Normal;
        m.Activate();
        return m;
    }

    private bool _exitPending;

    public async void ExitApp()
    {
        if (_exiting || _exitPending) return;
        _exitPending = true;
        try
        {
            // Open compose windows get to save or discard their message first; Cancel stops quitting.
            foreach (var w in Windows.OfType<ComposeWindow>().ToList())
                if (!await w.CloseForExitAsync()) return;
        }
        finally { _exitPending = false; }
        _exiting = true;
        try { (MainWindow as MainWindow)?.SavePlacement(); } catch { }
        _cts.Cancel();
        AppServices.Tray?.Dispose();
        try { AppServices.Engine?.Dispose(); } catch { }
        Log.Info("Magpie exiting");
        Shutdown();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI exception", e.Exception);
        var root = e.Exception;
        while (root.InnerException != null) root = root.InnerException;
        e.Handled = true;
        try
        {
            MessageBox.Show($"Something went wrong:\n\n{root.Message}\n\nDetails were written to {Log.FilePath}.", "Magpie",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
    }
}

public static class AppServices
{
    public static MailEngine Engine { get; set; } = null!;
    public static TrayIcon? Tray { get; set; }
    public static App Current => (App)Application.Current;
}
