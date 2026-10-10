using Avalonia;
using Magpie.Core;
using Magpie.Mac.Services;

namespace Magpie.Mac;

/// <summary>Magpie for Mac. macOS itself keeps one copy running per app; a second "open" brings it forward.</summary>
public static class Program
{
    /// <summary>Set once the main window is up: from then on a crash is not a reason to offer the previous version.</summary>
    public static bool StartupComplete { get; set; }

    private static bool _rollbackOffered;

    [STAThread]
    public static int Main(string[] args)
    {
        var i = Array.IndexOf(args, "--after-update");
        if (i >= 0) AppServices.UpdatedFrom = i + 1 < args.Length ? args[i + 1] : "?";
        var started = DateTime.UtcNow;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            var ex = a.ExceptionObject as Exception ?? new Exception("unknown error");
            Log.Error("unhandled", ex);
            // A crash in the first minutes after an update restart also offers the previous version, even once the
            // windows are up: an exception thrown inside an AppKit callback ends the app without reaching the catch
            // below (found on a real Mac, where 8.0.0-beta.99 crashed 3 s after its restart and no offer came).
            if (a.IsTerminating && AppServices.AfterUpdate && !_rollbackOffered && DateTime.UtcNow - started < TimeSpan.FromMinutes(3))
            {
                _rollbackOffered = true;
                MacInstaller.OfferRollback(ex, AppServices.UpdatedFrom);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("unobserved task", a.Exception); a.SetObserved(); };
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (AppServices.AfterUpdate && !StartupComplete && !_rollbackOffered)
        {
            // The new version can't even start its windows: offer the previous one (kept in ~/Library/Caches/Magpie/previous).
            Log.Error("start-up failed after update", ex);
            _rollbackOffered = true;
            MacInstaller.OfferRollback(ex, AppServices.UpdatedFrom);
            return 1;
        }
    }

    // Also used by the Avalonia designer and the headless tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = true })
            .LogToTrace();
}
