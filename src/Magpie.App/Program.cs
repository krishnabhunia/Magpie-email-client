using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using Magpie.Core;

namespace Magpie.App;

/// <summary>Entry point: one Magpie per Windows user; a second launch brings the first window forward.</summary>
public static class Program
{
    private const string PipeName = "Magpie.Mail.Activate.v1";
    private static Mutex? _mutex;
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();
    /// <summary>Started by the updater (design U1): the previous version is still closing.</summary>
    public static bool AfterUpdate { get; private set; }
    public static string UpdatedFrom { get; private set; } = "";
    /// <summary>Set once the main window is up: from then on a crash is not a reason to offer the previous version.</summary>
    public static bool StartupComplete { get; set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        var i = Array.IndexOf(args, "--after-update");
        AfterUpdate = i >= 0;
        if (AfterUpdate)
        {
            UpdatedFrom = i + 1 < args.Length ? args[i + 1] : "";
            // Tell the old copy we are running, so it can quit and hand over the single-instance lock.
            try { if (EventWaitHandle.TryOpenExisting(Services.UpdateService.StartedEvent, out var started)) using (started) started.Set(); }
            catch { }
        }
        bool createdNew;
        try { _mutex = new Mutex(true, @"Local\Magpie.Mail.SingleInstance", out createdNew); }
        catch { createdNew = true; }

        if (!createdNew && AfterUpdate)
        {
            try { createdNew = _mutex!.WaitOne(TimeSpan.FromSeconds(60)); }
            catch (AbandonedMutexException) { createdNew = true; }   // the old copy exited without releasing it: ours now
            catch { createdNew = false; }
        }

        if (createdNew && !AfterUpdate)
        {
            // Auto update installed this version in the background (design A1): this is its first start.
            var note = Magpie.Core.Updates.InstalledUpdate.Read(AppPaths.Default().Updates);
            if (note != null && note.To == Services.UpdateService.Current.ToString()) { AfterUpdate = true; UpdatedFrom = note.From; }
            else if (note != null) Magpie.Core.Updates.InstalledUpdate.Clear(AppPaths.Default().Updates);
        }

        if (!createdNew)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2500);
                var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args.Length == 0 ? new[] { "--activate" } : args));
                client.Write(bytes, 0, bytes.Length);
            }
            catch { /* the other instance is closing; just exit */ }
            return 0;
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        catch (Exception ex) when (AfterUpdate && !StartupComplete)
        {
            // The new version can't even start its UI: offer the previous one.
            Log.Error("start-up failed after update", ex);
            if (!Services.UpdateService.OfferRollback(ex))
                MessageBox.Show("Magpie could not start:\n\n" + ex.Message, "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }

    /// <summary>Lets another copy start now (used before relaunching the previous version after a failed update).</summary>
    public static void ReleaseSingleInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }

    /// <summary>Listens for second launches; <paramref name="onArgs"/> runs on the UI thread.</summary>
    public static void StartActivationServer(Action<string[]> onArgs, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var ms = new MemoryStream();
                    await server.CopyToAsync(ms, ct).ConfigureAwait(false);
                    var args = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Application.Current?.Dispatcher.BeginInvoke(() => onArgs(args));
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Warn("activation pipe: " + ex.Message);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { break; }
                }
            }
        }, ct);
    }
}
