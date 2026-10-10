using System.Diagnostics;
using Magpie.Core;

namespace Magpie.Mac.Services;

/// <summary>macOS command-line tools Magpie uses: <c>open</c> (browser, files, Finder), <c>hdiutil</c>, <c>ditto</c>.</summary>
public static class Shell
{
    /// <summary>Opens a web link in the default browser, a file in its app, a folder in Finder.</summary>
    public static void Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (Uri.TryCreate(target, UriKind.Absolute, out var u) && !u.IsFile
            && u.Scheme is not ("http" or "https" or "mailto"))
        {
            Log.Warn("not opening a link with scheme " + u.Scheme);
            return;
        }
        try
        {
            if (!OperatingSystem.IsMacOS()) { Log.Info("open (not on a Mac): " + target); return; }
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(target);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Warn("open failed: " + ex.Message); }
    }

    /// <summary>Shows a file selected in Finder.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return;
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(path);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Warn("reveal failed: " + ex.Message); }
    }

    /// <summary>Runs a tool and waits for it. Returns the exit code and what it wrote.</summary>
    public static async Task<(int Code, string Output, string Error)> RunAsync(string tool, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + tool);
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"{Path.GetFileName(tool)} took too long.");
        }
        return (p.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    /// <summary>The same, synchronously (used while quitting, when there is no more UI to keep responsive).</summary>
    public static (int Code, string Output, string Error) Run(string tool, IEnumerable<string> args, TimeSpan timeout) =>
        RunAsync(tool, args, timeout).GetAwaiter().GetResult();
}
