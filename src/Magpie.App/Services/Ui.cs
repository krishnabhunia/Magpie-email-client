using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Magpie.Core;

namespace Magpie.App.Services;

public static class Ui
{
    public static Dispatcher Dispatcher => Application.Current.Dispatcher;

    public static void Post(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    /// <summary>Opens a URL / file with the default app. Only http(s), mailto and local files.</summary>
    public static void OpenExternal(string target)
    {
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var u) && u.Scheme is not ("http" or "https" or "mailto" or "file")) return;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn("open external failed: " + ex.Message); }
    }

    public static Window? ActiveWindow =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

    public static void Error(string title, string message, Window? owner = null) =>
        MessageBox.Show(owner ?? ActiveWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static bool Confirm(string title, string message, Window? owner = null) =>
        MessageBox.Show(owner ?? ActiveWindow!, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>Coalesces bursts of calls into one (runs on the UI thread after <paramref name="delay"/>).</summary>
    public sealed class Debouncer
    {
        private readonly DispatcherTimer _timer;
        private Action? _action;
        public Debouncer(TimeSpan delay)
        {
            _timer = new DispatcherTimer { Interval = delay };
            _timer.Tick += (_, _) => { _timer.Stop(); var a = _action; _action = null; a?.Invoke(); };
        }
        public void Run(Action a) { _action = a; _timer.Stop(); _timer.Start(); }
    }
}
