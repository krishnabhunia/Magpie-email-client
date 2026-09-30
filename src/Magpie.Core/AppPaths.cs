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
    /// <summary>Design DL1: where the mail itself lives (database and message files). <see cref="Root"/> unless the
    /// user chose another folder (named in <see cref="MailFolderFile"/>), e.g. on another or an encrypted drive.
    /// Settings, sign-ins and the log always stay in <see cref="Root"/>: they say where the mail is.</summary>
    public string MailRoot { get; private set; } = "";
    /// <summary>True when <see cref="MailRoot"/> is a folder the user chose.</summary>
    public bool CustomMailRoot => !string.Equals(MailRoot, Root, StringComparison.OrdinalIgnoreCase);
    public const string MailFolderFile = "mail-folder.txt";
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
        RefreshMailRoot();
    }

    /// <summary>Reads <see cref="MailFolderFile"/> again (after a move). The default mail folder is created here;
    /// a chosen one never is (its drive may be locked or unplugged, see <see cref="MailRootAvailable"/>).</summary>
    public void RefreshMailRoot()
    {
        string? chosen = null;
        try
        {
            var f = Path.Combine(Root, MailFolderFile);
            if (File.Exists(f)) chosen = File.ReadAllText(f).Trim();
        }
        catch { }
        MailRoot = string.IsNullOrEmpty(chosen) ? Root : chosen;
        if (!CustomMailRoot) Directory.CreateDirectory(MimeCache);
    }

    /// <summary>Points Magpie at another mail folder (null = the default one), without moving anything.</summary>
    public void SetMailRoot(string? folder)
    {
        var f = Path.Combine(Root, MailFolderFile);
        if (string.IsNullOrWhiteSpace(folder) || string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Root), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(f)) File.Delete(f);
        }
        else File.WriteAllText(f, Path.GetFullPath(folder));
        RefreshMailRoot();
    }

    /// <summary>False when the chosen mail folder can't be reached (drive locked, unplugged, or folder gone).</summary>
    public bool MailRootAvailable => Directory.Exists(MailRoot);

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

    public string Database => Path.Combine(MailRoot, "mail.db");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Secrets => Path.Combine(Root, "secrets.json");
    public string MimeCache => Path.Combine(MailRoot, "messages");
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
