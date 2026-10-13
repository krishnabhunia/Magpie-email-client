namespace Magpie.Core;

/// <summary>
/// Where Magpie keeps its data. Everything is local: %APPDATA%\Magpie (mail, settings, sign-ins, log) and
/// %LOCALAPPDATA%\Magpie (reading-pane cache, update downloads) by default.
/// Portable mode (design Z1): the portable copy keeps all of it in <see cref="PortableFolder"/> next to the EXE instead,
/// e.g. on a USB stick. A copy is portable when the EXE is named like the release zip's portable/Magpie_x.y.z.exe
/// (Krishna's workflow rule: one file per folder) and wasn't put there by the installer, or when an older
/// <see cref="PortableMarker"/> sits next to it.
/// </summary>
public sealed class AppPaths
{
    public const string PortableMarker = "portable.txt";
    /// <summary>The portable EXE's name: Magpie_7.2.0.exe (release_prep.py APP + "_").</summary>
    public const string PortableExePrefix = "Magpie_";
    /// <summary>Inno Setup's uninstaller sits next to an installed Magpie: that copy is never portable.</summary>
    public const string InstallerUninstaller = "unins000.exe";
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
    public static AppPaths Default() => _default ??= For(ExeFolder(), Path.GetFileName(Environment.ProcessPath));

    public static string ExeFolder() =>
        Path.GetDirectoryName(Environment.ProcessPath) is { Length: > 0 } d ? d : AppContext.BaseDirectory;

    /// <summary>True for the portable copy: Magpie_x.y.z.exe not installed by the installer, or portable.txt next to it.</summary>
    public static bool IsPortableCopy(string exeFolder, string? exeName) =>
        File.Exists(Path.Combine(exeFolder, PortableMarker))
        || (exeName is { } n && PortableExeName.IsMatch(n)
            && !File.Exists(Path.Combine(exeFolder, InstallerUninstaller)));

    /// <summary>The download's own name, <c>Magpie_&lt;version&gt;.exe</c> (x.y.z or x.y.z-beta.N; a browser's " (1)" allowed).
    /// Any other name — e.g. a copy renamed to <c>Magpie_backup.exe</c> — keeps using the profile folders.</summary>
    private static readonly System.Text.RegularExpressions.Regex PortableExeName = new(
        @"^Magpie_\d+\.\d+\.\d+(-[A-Za-z]+\.\d+)?( \(\d+\))?\.exe$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Portable (see <see cref="IsPortableCopy"/>); otherwise the user's profile folders.</summary>
    public static AppPaths For(string exeFolder, string? exeName = null)
    {
        if (IsPortableCopy(exeFolder, exeName))
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
