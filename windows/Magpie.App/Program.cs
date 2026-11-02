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
    private static Services.WindowsStartup? _startup;
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();
    /// <summary>Started by the updater (design U1): the previous version is still closing.</summary>
    public static bool AfterUpdate { get; private set; }
    public static string UpdatedFrom { get; private set; } = "";
    /// <summary>Set once the main window is up: from then on a crash is not a reason to offer the previous version.</summary>
    public static bool StartupComplete { get; set; }
    /// <summary>Started by Magpie itself to restart (a settings restore, a mail folder move): the old copy is closing.</summary>
    public static bool AfterRestart { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        var i = Array.IndexOf(args, "--after-update");
        AfterUpdate = i >= 0;
        AfterRestart = Array.IndexOf(args, "--restart") >= 0;
        if (AfterUpdate) UpdatedFrom = i + 1 < args.Length ? args[i + 1] : "";
        try { _startup = Services.WindowsStartup.Acquire(AfterUpdate, AfterRestart); }
        catch (Exception ex)
        {
            MessageBox.Show("Magpie could not acquire exclusive access to its data folder:\n\n" + ex.Message,
                "Magpie", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
        var createdNew = _startup.OwnsInstance;

        if (createdNew && !AfterUpdate)
        {
            // Auto update installed this version in the background (design A1): this is its first start.
            var note = Magpie.Core.Updates.InstalledUpdate.Read(AppPaths.Default().Updates);
            if (note != null && note.To == Services.UpdateService.Current.ToString()) { AfterUpdate = true; UpdatedFrom = note.From; }
            else if (note != null) Magpie.Core.Updates.InstalledUpdate.Clear(AppPaths.Default().Updates);
            // The durable transition record also covers an interruption between the EXE swap and process launch.
            try
            {
                var paths = AppPaths.Default();
                Magpie.Core.Updates.VersionBackup.RecoverInterruptedRestore(paths);
                var pending = Magpie.Core.Updates.UpdateHistory.List(paths).FirstOrDefault(e =>
                    e.Status is Magpie.Core.Updates.UpdateHistory.Preparing or Magpie.Core.Updates.UpdateHistory.Downloading
                        or Magpie.Core.Updates.UpdateHistory.Installing);
                if (pending is { Status: Magpie.Core.Updates.UpdateHistory.Installing }
                    && pending.To == Services.UpdateService.Current.ToString())
                {
                    AfterUpdate = true;
                    UpdatedFrom = pending.From;
                }
                else if (pending != null && pending.From == Services.UpdateService.Current.ToString()
                         && pending.To != Services.UpdateService.Current.ToString())
                {
                    // The old EXE is still in place. Cancel a staged data restore that never reached its target app.
                    Magpie.Core.Updates.VersionBackup.CancelPendingForVersion(paths, pending.To);
                    Magpie.Core.Updates.UpdateHistory.SetStatus(paths, pending.Id, Magpie.Core.Updates.UpdateHistory.Failed,
                        "The transition was interrupted before the selected application started. The current application was kept.");
                }
            }
            catch (Exception ex) { Log.Error("version transition recovery needs attention", ex); }
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
        try { _startup?.Dispose(); } catch { }
        _startup = null;
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
