using Magpie.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Magpie.App.Services;

/// <summary>Shared WebView2 environment (reading pane + compose editor).</summary>
public static class WebHost
{
    public const string Host = "magpie.local";
    private static Task<CoreWebView2Environment>? _env;

    // %LOCALAPPDATA%\Magpie\…, or MagpieData\local\… next to the EXE in portable mode (design Z1).
    public static string DataDir => AppPaths.Default().WebView2Data;
    public static string RenderDir { get; } = AppPaths.Default().RenderCache;

    public static bool RuntimeAvailable(out string? version)
    {
        try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); return !string.IsNullOrEmpty(version); }
        catch (WebView2RuntimeNotFoundException) { version = null; return false; }
        catch (Exception ex) { Log.Warn("WebView2 check: " + ex.Message); version = null; return false; }
    }

    private static Task<CoreWebView2Environment> Env()
    {
        if (_env == null)
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(RenderDir);
            try { foreach (var f in Directory.GetFiles(RenderDir)) File.Delete(f); } catch { }
            _env = CoreWebView2Environment.CreateAsync(null, DataDir);
        }
        return _env;
    }

    /// <summary>Initialises a WebView2 with locked-down settings. Returns false when the runtime is missing.</summary>
    public static async Task<bool> InitAsync(IWebView2 view, bool scripts)
    {
        if (!RuntimeAvailable(out _)) return false;
        await view.EnsureCoreWebView2Async(await Env());
        var s = view.CoreWebView2.Settings;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = scripts;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.IsZoomControlEnabled = true;
        s.IsGeneralAutofillEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        view.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, RenderDir, CoreWebView2HostResourceAccessKind.Allow);
        return true;
    }

    private static int _n;

    /// <summary>Writes a page to the render folder and returns its https://magpie.local/ URL (no 2 MB NavigateToString limit).</summary>
    public static string Publish(string html, string prefix)
    {
        var name = $"{prefix}-{Interlocked.Increment(ref _n)}.html";
        var path = Path.Combine(RenderDir, name);
        Directory.CreateDirectory(RenderDir);
        File.WriteAllText(path, html);
        // Keep the folder small: delete older pages of the same kind.
        try
        {
            foreach (var old in Directory.GetFiles(RenderDir, prefix + "-*.html").Where(f => !f.EndsWith(name)))
            {
                var fi = new FileInfo(old);
                if (fi.LastWriteTimeUtc < DateTime.UtcNow.AddSeconds(-30)) fi.Delete();
            }
        }
        catch { }
        return $"https://{Host}/{name}";
    }

    public static bool IsOwnPage(string? uri) =>
        uri != null && (uri.StartsWith($"https://{Host}/", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
                        || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase));
}
