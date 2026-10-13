using System.Diagnostics;
using Magpie.Core;

namespace Magpie.Mac.Services;

/// <summary>macOS command-line tools Magpie uses: <c>open</c> (browser, files, Finder), <c>hdiutil</c>, <c>ditto</c>.</summary>
public static class Shell
{
    /// <summary>
    /// Opens a web page in the default browser. Only absolute http and https links are passed on (links from email
    /// content and sign-in pages come here); anything else — file: links, other schemes, text that isn't a URL and so
    /// could be read by <c>open</c> as an option — is ignored and logged.
    /// </summary>
    public static void OpenWeb(string url)
    {
        if (!IsWebLink(url, out var uri)) { Log.Warn("not opening a link that isn't http(s): " + Shorten(url)); return; }
        Start("/usr/bin/open", uri.AbsoluteUri);
    }

    /// <summary>True for an absolute http:// or https:// URL (its canonical form never starts with "-").</summary>
    public static bool IsWebLink(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return false;
        uri = u;
        return true;
    }

    /// <summary>Opens one of Magpie's own files or folders (an attachment it saved, the data folder, a disk image) in its
    /// app or in Finder. Always an absolute path, so <c>open</c> can never read it as an option.</summary>
    public static void OpenLocal(string path)
    {
        if (!TryLocal(path, out var full)) { Log.Warn("not opening a path that isn't absolute: " + Shorten(path)); return; }
        Start("/usr/bin/open", full);
    }

    /// <summary>Shows one of Magpie's own files selected in Finder (absolute path only).</summary>
    public static void Reveal(string path)
    {
        if (!TryLocal(path, out var full)) { Log.Warn("not revealing a path that isn't absolute: " + Shorten(path)); return; }
        Start("/usr/bin/open", "-R", full);
    }

    private static bool TryLocal(string? path, out string full)
    {
        full = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        full = Path.GetFullPath(path);
        return full.StartsWith('/');
    }

    private static void Start(string tool, params string[] args)
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) { Log.Info($"{tool} {string.Join(' ', args)} (not on a Mac)"); return; }
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Warn($"{Path.GetFileName(tool)} failed: " + ex.Message); }
    }

    private static string Shorten(string? s) => s == null ? "(null)" : s.Length > 120 ? s[..120] + "…" : s;

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
