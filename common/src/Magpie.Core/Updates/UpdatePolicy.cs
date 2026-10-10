namespace Magpie.Core.Updates;

/// <summary>
/// When Magpie for Mac looks for a new version (Krishna's rule, GitHub Skills/versions_management.md): every start
/// checks GitHub, even with Auto update off; Auto update also checks once a day and downloads and installs by itself.
/// Written for the Mac app so its rules can be tested; the Windows UpdateService keeps its own copy of these rules
/// (with Later / Skip in its flyout) and doesn't use this class.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>Auto update checks at most this often after the check at start.</summary>
    public static readonly TimeSpan Daily = TimeSpan.FromHours(23);

    /// <summary>
    /// Should an automatic check run now? <paramref name="startCheckDone"/> = this run of Magpie has checked once
    /// already; <paramref name="busy"/> = checking or downloading; <paramref name="offering"/> = a newer version is
    /// on offer or ready.
    /// </summary>
    public static bool ShouldAutoCheck(bool startCheckDone, bool autoUpdate, DateTimeOffset? lastCheck, DateTimeOffset now,
        bool busy, bool offering)
    {
        if (busy || offering) return false;
        if (!startCheckDone) return true;                         // the check at start: always (Auto update on or off)
        if (!autoUpdate) return false;                            // off: once per start only
        return lastCheck is not { } last || now - last >= Daily;  // on: once a day
    }

    /// <summary>A found version is offered unless an automatic check finds the one the user skipped.</summary>
    public static bool ShouldOffer(AppVersion found, AppVersion current, bool automatic, string skippedVersion) =>
        found.CompareTo(current) > 0 && !(automatic && found.ToString() == skippedVersion);

    /// <summary>Auto update downloads (and installs) by itself; otherwise the title-bar button waits for a click.</summary>
    public static bool DownloadsByItself(bool autoUpdate) => autoUpdate;

    /// <summary>
    /// Quitting with Auto update on installs a downloaded, checked version — unless it was installed already (the
    /// "Update to …" button installs and restarts at once; installing again would swap twice and lose the backup).
    /// </summary>
    public static bool InstallsOnQuit(bool autoUpdate, bool downloadReady, bool alreadyInstalled) =>
        autoUpdate && downloadReady && !alreadyInstalled;

    /// <summary>The title-bar button's words (design UB1): "Update to v8.0.1"; empty (hidden) without a newer version.</summary>
    public static string ButtonText(AppVersion? offered, AppVersion current) =>
        offered != null && offered.CompareTo(current) > 0 ? $"Update to v{offered}" : "";
}

/// <summary>
/// Magpie for Mac updates itself by replacing its .app bundle (the folder /Applications/Magpie.app): the new bundle,
/// copied from the downloaded disk image next to the running one, takes its place. The running one is kept outside
/// Applications — ~/Library/Caches/Magpie/previous/Magpie.app (<see cref="BackupPathIn"/>), so Launchpad and
/// Spotlight don't show a second Magpie — and <see cref="Rollback"/> can put it back. macOS lets a running app's
/// bundle be renamed. Moves between volumes (an app on another disk) fall back to the copy the caller gives
/// (<c>ditto</c> in the app, which keeps the bundle's links, permissions and signature).
/// </summary>
public static class MacBundle
{
    public const string StagingName = ".Magpie.incoming.app";
    public const string AsideName = ".Magpie.old.app";
    public const string FailedName = ".Magpie.failed.app";

    /// <summary>The .app folder the running executable is in (…/Magpie.app/Contents/MacOS/Magpie), or null when
    /// Magpie isn't running from a bundle (e.g. <c>dotnet run</c>).</summary>
    public static string? FindBundle(string? processPath)
    {
        // Mac paths always use "/"; plain string work keeps this the same on every OS (and in the Windows CI tests).
        if (string.IsNullOrEmpty(processPath)) return null;
        static string? Up(string? p) { var i = p?.LastIndexOf('/') ?? -1; return i > 0 ? p![..i] : null; }
        static string Name(string p) => p[(p.LastIndexOf('/') + 1)..];
        var macOs = Up(processPath);
        var contents = Up(macOs);
        var app = Up(contents);
        if (macOs == null || contents == null || app == null) return null;
        if (!string.Equals(Name(macOs), "MacOS", StringComparison.Ordinal)
            || !string.Equals(Name(contents), "Contents", StringComparison.Ordinal)
            || !app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
        return app;
    }

    /// <summary>Where the previous version is kept: &lt;caches&gt;/previous/Magpie.app (caches = AppPaths.LocalRoot).</summary>
    public static string BackupPathIn(string localRoot) => Path.Combine(localRoot, "previous", "Magpie.app");
    public static string StagingPathFor(string app) => Sibling(app, StagingName);
    private static string Sibling(string app, string name) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, name);

    /// <summary>True when Magpie may write next to its bundle (not the case for a standard user in /Applications,
    /// or an app macOS started from a read-only "translocated" copy because it was never moved out of Downloads).</summary>
    public static bool CanReplace(string app)
    {
        try
        {
            var probe = Sibling(app, $".magpie-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The app is running from a read-only disk image or a translocated copy: it can't update itself there.</summary>
    public static bool IsTranslocated(string app) =>
        app.Contains("/AppTranslocation/", StringComparison.Ordinal) || app.StartsWith("/Volumes/", StringComparison.Ordinal);

    /// <summary>Moves a folder; across volumes (where a rename can't) it copies with <paramref name="copyTree"/> and
    /// deletes the source. <paramref name="move"/> is the plain move (replaceable in tests).</summary>
    internal static void MoveDir(string from, string to, Action<string, string>? copyTree, Action<string, string>? move = null)
    {
        move ??= Directory.Move;
        try { move(from, to); }
        catch (IOException) when (copyTree != null)
        {
            if (Directory.Exists(to)) Directory.Delete(to, recursive: true);
            copyTree(from, to);
            Directory.Delete(from, recursive: true);
        }
    }

    /// <summary>
    /// Puts <paramref name="staged"/> (a complete Magpie.app copied next to <paramref name="app"/>) in its place, then
    /// keeps the old bundle at <paramref name="backup"/>. If the swap fails, the original bundle is back where it was
    /// and the error is thrown; if only keeping the backup fails, the new version stays installed (no going back).
    /// Returns false in that last case.
    /// </summary>
    public static bool Swap(string app, string staged, string backup, Action<string, string>? copyTree = null) =>
        Swap(app, staged, backup, copyTree, null);

    internal static bool Swap(string app, string staged, string backup, Action<string, string>? copyTree, Action<string, string>? move)
    {
        if (!Directory.Exists(Path.Combine(staged, "Contents", "MacOS")))
            throw new InvalidOperationException("The new version is incomplete (no Contents/MacOS in it).");
        var aside = Sibling(app, AsideName);
        if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
        Directory.Move(app, aside);                          // same folder: a rename
        try { Directory.Move(staged, app); }
        catch
        {
            try { if (Directory.Exists(app)) Directory.Delete(app, recursive: true); } catch { }
            Directory.Move(aside, app);
            throw;
        }
        try
        {
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backup))!);
            MoveDir(aside, backup, copyTree, move);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("the previous version couldn't be kept: " + ex.Message);
            try { if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true); } catch { }
            return false;
        }
    }

    /// <summary>Puts the kept previous version (<paramref name="backup"/>) back as the app; the failed new bundle is removed.</summary>
    public static bool Rollback(string app, string backup, Action<string, string>? copyTree = null) =>
        Rollback(app, backup, copyTree, null);

    internal static bool Rollback(string app, string backup, Action<string, string>? copyTree, Action<string, string>? move)
    {
        if (!Directory.Exists(backup)) return false;
        var failed = Sibling(app, FailedName);
        if (Directory.Exists(failed)) Directory.Delete(failed, recursive: true);
        var moved = false;
        if (Directory.Exists(app)) { Directory.Move(app, failed); moved = true; }
        try { MoveDir(backup, app, copyTree, move); }
        catch
        {
            if (moved) try { if (!Directory.Exists(app)) Directory.Move(failed, app); } catch { }   // never leave no Magpie.app at all
            throw;
        }
        try { Directory.Delete(failed, recursive: true); } catch { }
        return true;
    }
}
