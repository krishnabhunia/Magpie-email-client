namespace Magpie.Core.Updates;

/// <summary>
/// The words on the version picker's confirm screen (design VP1, Q90) — kept here so they can be tested without WPF.
/// The steps follow the order the Windows app really uses: check, download, save a recovery copy, install, pin.
/// </summary>
public static class VersionPicker
{
    /// <summary>
    /// Choosing a beta while a compatible stable newer than <paramref name="current"/> (and older than the beta) exists
    /// skips that stable: the newest such stable, or null.
    /// </summary>
    public static ReleaseInfo? SkippedStable(VersionChoice target, IEnumerable<VersionChoice> choices, AppVersion current)
    {
        if (!target.Prerelease || target.Release.Version.CompareTo(current) <= 0) return null;
        return choices
            .Where(c => !c.Prerelease && c.Release.AssetAvailable && c.Compatibility.CanInstall
                        && c.Release.Version.CompareTo(current) > 0 && c.Release.Version.CompareTo(target.Release.Version) < 0)
            .Select(c => c.Release)
            .OrderByDescending(r => r.Version)
            .FirstOrDefault();
    }

    /// <summary>The amber note on the confirm screen, or null when there is nothing to warn about.</summary>
    public static string? Warning(VersionChoice target, IEnumerable<VersionChoice> choices, AppVersion current)
    {
        var to = target.Release.Version;
        if (to.CompareTo(current) < 0)
            return $"You are going back to an older version. Features added after v{to} will be missing until you update again.";
        if (!target.Prerelease) return null;
        return SkippedStable(target, choices, current) is { } skipped
            ? $"You are moving to a beta. This skips the latest stable (v{skipped.Version}). Beta builds can be unstable — you can roll back from Version history at any time."
            : "You are moving to a beta. Beta builds can be unstable — you can roll back from Version history at any time.";
    }

    /// <summary>The numbered "What will happen" list (without numbers; the screen adds them).</summary>
    public static IReadOnlyList<string> Steps(AppVersion current, VersionChoice target, bool onThisPc)
    {
        var to = target.Release.Version;
        return
        [
            $"Check that v{to} works with your mail, settings and installation",
            onThisPc
                ? $"Use the copy of v{to} saved on this PC (checked against the release's SHA-256; downloaded only if it no longer matches)"
                : $"Download v{to} from GitHub and check its SHA-256",
            $"Save a recovery copy of v{current} and your local data",
            $"Close Magpie, install exactly v{to} and start it",
            $"Pin v{to} — newer versions are not offered until you clear the pin",
        ];
    }

    /// <summary>"Update to v14.1.0" or "Roll back to v13.0.0".</summary>
    public static string ActionText(AppVersion current, AppVersion target) =>
        (target.CompareTo(current) < 0 ? "Roll back to v" : "Update to v") + target;
}

/// <summary>
/// Versions whose application is kept in this profile's version backups (design VP1 "On this PC", Q90). A kept copy
/// only saves the download: <see cref="UpdateClient.DownloadCheckedAsync"/> still checks it against the release's
/// published SHA-256 and downloads the release when it does not match.
/// </summary>
public static class KeptVersions
{
    /// <summary>The newest kept application file of <paramref name="version"/> for this installation type, or null.</summary>
    public static string? Find(AppPaths paths, AppVersion version, string installKind) =>
        Snapshots(paths)
            .Where(s => s.InstallKind == installKind && s.ApplicationKind == "file"
                        && AppVersion.TryParse(s.Version) is { } v && v.CompareTo(version) == 0)
            .Select(s => s.ApplicationPath)
            .FirstOrDefault(File.Exists);

    /// <summary>Every version with a kept application file for this installation type ("13.0.2", "14.0.0-beta.3").</summary>
    public static IReadOnlySet<string> Versions(AppPaths paths, string installKind) =>
        Snapshots(paths)
            .Where(s => s.InstallKind == installKind && s.ApplicationKind == "file" && File.Exists(s.ApplicationPath))
            .Select(s => AppVersion.TryParse(s.Version)?.ToString())
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the kept copy where the checked download looks first (<paramref name="downloadPath"/>), unless a file is
    /// already there. True when it was copied.
    /// </summary>
    public static bool Seed(string? keptApplication, string downloadPath)
    {
        if (keptApplication is null || !File.Exists(keptApplication) || File.Exists(downloadPath)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
        var part = downloadPath + ".kept";
        File.Copy(keptApplication, part, overwrite: true);
        File.Move(part, downloadPath, overwrite: false);
        return true;
    }

    private static IEnumerable<VersionSnapshot> Snapshots(AppPaths paths)
    {
        try { return VersionBackup.List(paths); }
        catch { return []; } // an unreadable backup record never hides the list or blocks a change
    }
}
