using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Magpie.Core;
using Magpie.Core.Updates;

namespace Magpie.Mac.Services;

/// <summary>What the whole app shares: the mail engine (Magpie.Core), the updater and the main window.</summary>
public static class AppServices
{
    public static MailEngine Engine { get; set; } = null!;
    public static MacUpdateService? Updates { get; set; }

    /// <summary>This copy's version, from the build (Directory.Build.props, or -p:Version from build.sh).</summary>
    public static AppVersion Current { get; } = ReadCurrent();

    /// <summary>"8.0.0 · released 10-Oct-2026" style date from Directory.Build.props.</summary>
    public static string ReleaseDate { get; } = ReadReleaseDate();

    /// <summary>Started by the updater (--after-update &lt;old version&gt;).</summary>
    public static string UpdatedFrom { get; set; } = "";
    public static bool AfterUpdate => UpdatedFrom.Length > 0;

    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static ViewModels.MainViewModel? Main => (MainWindow as MainWindow)?.ViewModel;

    private static AppVersion ReadCurrent()
    {
        var asm = typeof(AppServices).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return AppVersion.TryParse(info) ?? AppVersion.TryParse(asm.GetName().Version?.ToString(3)) ?? AppVersion.TryParse("0.0.0")!;
    }

    private static string ReadReleaseDate()
    {
        var raw = typeof(AppServices).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ReleaseDate")?.Value;
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread (engine events arrive on background threads).</summary>
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>Runs a little later, once: the last call wins (search typing, list reloads after many engine events).</summary>
public sealed class Debouncer
{
    private readonly TimeSpan _delay;
    private DispatcherTimer? _timer;
    private Action? _action;

    public Debouncer(TimeSpan delay) { _delay = delay; }

    public void Run(Action action)
    {
        _action = action;
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = _delay };
            _timer.Tick += (_, _) => { _timer!.Stop(); _action?.Invoke(); };
        }
        _timer.Stop();
        _timer.Start();
    }
}
