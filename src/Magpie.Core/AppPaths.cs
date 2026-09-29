namespace Magpie.Core;

/// <summary>
/// Where Magpie keeps its data. Everything is local: %APPDATA%\Magpie (mail, settings, sign-ins, log) and
/// %LOCALAPPDATA%\Magpie (reading-pane cache, update downloads) by default.
/// Portable mode (design Z1): a file <see cref="PortableMarker"/> next to Magpie.exe (the release zip's portable/
/// folder has one) puts all of it in <see cref="PortableFolder"/> next to the EXE instead, e.g. on a USB stick.
/// </summary>
public sealed class AppPaths
{
    public const string PortableMarker = "portable.txt";
    public const string PortableFolder = "MagpieData";

    public string Root { get; }
    /// <summary>Caches that needn't roam: WebView2 profile, rendered pages, update downloads.</summary>
    public string LocalRoot { get; }
    public bool IsPortable { get; }

    public AppPaths(string root) : this(root, root, false) { }

    private AppPaths(string root, string localRoot, bool portable)
    {
        Root = root;
        LocalRoot = localRoot;
        IsPortable = portable;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MimeCache);
    }

    private static AppPaths? _default;

    /// <summary>The paths for this copy of Magpie (portable or not), decided once from where Magpie.exe is.</summary>
    public static AppPaths Default() => _default ??= For(ExeFolder());

    public static string ExeFolder() =>
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } d ? d : AppContext.BaseDirectory;

    /// <summary>Portable when <paramref name="exeFolder"/> holds portable.txt; otherwise the user's profile folders.</summary>
    public static AppPaths For(string exeFolder)
    {
        if (File.Exists(Path.Combine(exeFolder, PortableMarker)))
        {
            var data = Path.Combine(exeFolder, PortableFolder);
            return new AppPaths(data, Path.Combine(data, "local"), true);
        }
        return new AppPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Magpie"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Magpie"),
            false);
    }

    public string Database => Path.Combine(Root, "mail.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Secrets => Path.Combine(Root, "secrets.json");
    public string MimeCache => Path.Combine(Root, "messages");
    public string Logs => Root;
    public string WebView2Data => Path.Combine(LocalRoot, "WebView2");
    public string RenderCache => Path.Combine(LocalRoot, "render");
    public string Updates => Path.Combine(LocalRoot, "updates");

    public string MimePath(string accountId, long messageRowId) =>
        Path.Combine(MimeCache, Sanitize(accountId), messageRowId + ".eml");

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }
}
