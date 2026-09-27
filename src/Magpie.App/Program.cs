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

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        bool createdNew;
        try { _mutex = new Mutex(true, @"Local\Magpie.Mail.SingleInstance", out createdNew); }
        catch { createdNew = true; }

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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
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
