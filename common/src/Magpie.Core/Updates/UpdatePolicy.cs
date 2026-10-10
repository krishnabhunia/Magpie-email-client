namespace Magpie.Core.Updates;

/// <summary>
/// When an installed copy looks for a new version (Krishna's rule, GitHub Skills/versions_management.md): every start
/// checks GitHub, even with Auto update off; Auto update also checks once a day and downloads and installs by itself.
/// The same rules as the Windows UpdateService, kept here so Magpie for Mac follows them and they can be tested.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>Auto update checks at most this often after the check at start.</summary>
    public static readonly TimeSpan Daily = TimeSpan.FromHours(23);
    /// <summary>"Later": automatic checks leave the offer alone for this long.</summary>
    public static readonly TimeSpan LaterFor = TimeSpan.FromHours(20);

    /// <summary>
    /// Should an automatic check run now? <paramref name="startCheckDone"/> = this run of Magpie has checked once
    /// already; <paramref name="busy"/> = checking or downloading; <paramref name="offering"/> = a newer version is
    /// on offer or ready.
    /// </summary>
    public static bool ShouldAutoCheck(bool startCheckDone, bool autoUpdate, DateTimeOffset? lastCheck, DateTimeOffset now,
        DateTimeOffset laterUntil, bool busy, bool offering)
    {
        if (busy || offering) return false;
        if (!startCheckDone) return now >= laterUntil;           // the check at start: always (Auto update on or off)
        if (!autoUpdate) return false;                            // off: once per start only
        if (now < laterUntil) return false;
        return lastCheck is not { } last || now - last >= Daily;  // on: once a day
    }

    /// <summary>A found version is offered unless an automatic check finds the one the user skipped.</summary>
    public static bool ShouldOffer(AppVersion found, AppVersion current, bool automatic, string skippedVersion) =>
        found.CompareTo(current) > 0 && !(automatic && found.ToString() == skippedVersion);

    /// <summary>Auto update downloads (and installs) by itself; otherwise the title-bar button waits for a click.</summary>
    public static bool DownloadsByItself(bool autoUpdate) => autoUpdate;

    /// <summary>The title-bar button's words (design UB1): "Update to v8.0.1"; empty (hidden) without a newer version.</summary>
    public static string ButtonText(AppVersion? offered, AppVersion current) =>
        offered != null && offered.CompareTo(current) > 0 ? $"Update to v{offered}" : "";
}

/// <summary>
/// Magpie for Mac updates itself by replacing its .app bundle (the folder /Applications/Magpie.app): the new bundle,
/// copied from the downloaded disk image next to the running one, takes its place; the running one is kept as
/// Magpie.previous.app so <see cref="Rollback"/> can put it back. macOS lets a running app's bundle be renamed.
/// </summary>
public static class MacBundle
{
    public const string BackupName = "Magpie.previous.app";
    public const string StagingName = ".Magpie.incoming.app";

    /// <summary>The .app folder the running executable is in (…/Magpie.app/Contents/MacOS/Magpie), or null when
    /// Magpie isn't running from a bundle (e.g. <c>dotnet run</c>).</summary>
    public static string? FindBundle(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath)) return null;
        var macOs = Path.GetDirectoryName(processPath);
        var contents = macOs == null ? null : Path.GetDirectoryName(macOs);
        var app = contents == null ? null : Path.GetDirectoryName(contents);
        if (macOs == null || contents == null || app == null) return null;
        if (!string.Equals(Path.GetFileName(macOs), "MacOS", StringComparison.Ordinal)
            || !string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)
            || !app.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
        return app;
    }

    public static string BackupPathFor(string app) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, BackupName);
    public static string StagingPathFor(string app) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, StagingName);

    /// <summary>True when Magpie may write next to its bundle (not the case for a standard user in /Applications,
    /// or an app macOS started from a read-only "translocated" copy because it was never moved out of Downloads).</summary>
    public static bool CanReplace(string app)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, $".magpie-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>The app is running from a read-only disk image or a translocated copy: it can't update itself there.</summary>
    public static bool IsTranslocated(string app) =>
        app.Contains("/AppTranslocation/", StringComparison.Ordinal) || app.StartsWith("/Volumes/", StringComparison.Ordinal);

    /// <summary>
    /// Puts <paramref name="staged"/> (a complete Magpie.app copied next to <paramref name="app"/>) in its place and
    /// keeps the old bundle as Magpie.previous.app. On any failure the original bundle is back where it was and the
    /// error is thrown.
    /// </summary>
    public static void Swap(string app, string staged)
    {
        if (!Directory.Exists(Path.Combine(staged, "Contents", "MacOS")))
            throw new InvalidOperationException("The new version is incomplete (no Contents/MacOS in it).");
        var backup = BackupPathFor(app);
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
        Directory.Move(app, backup);
        try { Directory.Move(staged, app); }
        catch
        {
            try { if (Directory.Exists(app)) Directory.Delete(app, recursive: true); } catch { }
            Directory.Move(backup, app);
            throw;
        }
    }

    /// <summary>Puts Magpie.previous.app back as the app (the failed new bundle is removed).</summary>
    public static bool Rollback(string app)
    {
        var backup = BackupPathFor(app);
        if (!Directory.Exists(backup)) return false;
        var failed = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app))!, ".Magpie.failed.app");
        if (Directory.Exists(failed)) Directory.Delete(failed, recursive: true);
        var moved = false;
        if (Directory.Exists(app)) { Directory.Move(app, failed); moved = true; }
        try { Directory.Move(backup, app); }
        catch
        {
            if (moved) try { Directory.Move(failed, app); } catch { }   // never leave no Magpie.app at all
            throw;
        }
        try { Directory.Delete(failed, recursive: true); } catch { }
        return true;
    }
}
