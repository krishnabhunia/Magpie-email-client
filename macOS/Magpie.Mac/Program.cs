using Avalonia;
using Magpie.Core;
using Magpie.Mac.Services;

namespace Magpie.Mac;

/// <summary>Magpie for Mac. macOS itself keeps one copy running per app; a second "open" brings it forward.</summary>
public static class Program
{
    /// <summary>Set once the main window is up: from then on a crash is not a reason to offer the previous version.</summary>
    public static bool StartupComplete { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var i = Array.IndexOf(args, "--after-update");
        if (i >= 0) AppServices.UpdatedFrom = i + 1 < args.Length ? args[i + 1] : "?";
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("unhandled", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("unobserved task", a.Exception); a.SetObserved(); };
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex) when (AppServices.AfterUpdate && !StartupComplete)
        {
            // The new version can't even start its windows: offer the previous one (kept in ~/Library/Caches/Magpie/previous).
            Log.Error("start-up failed after update", ex);
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
