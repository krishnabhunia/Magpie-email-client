using System.Diagnostics;
using Magpie.Core;
using Magpie.Core.Updates;

namespace Magpie.Mac.Services;

/// <summary>
/// Puts a downloaded, checked Magpie_&lt;version&gt;.dmg in place of the running Magpie.app: mount the image
/// (<c>hdiutil attach -nobrowse</c>), copy its Magpie.app next to the running one (<c>ditto</c>, which keeps the
/// bundle's links, permissions and signature), unmount, then Core's <see cref="MacBundle.Swap"/> (the old bundle is
/// kept in ~/Library/Caches/Magpie/previous/Magpie.app, outside Applications). Restarting is <see cref="RelaunchAfterExit"/>.
/// </summary>
public static class MacInstaller
{
    public sealed record InstallPlan(string? App, string? Problem, bool OpenImageInstead);

    /// <summary>Can this copy replace itself? If not, says why in plain words (and whether to open the image instead).</summary>
    public static InstallPlan Plan()
    {
        if (!OperatingSystem.IsMacOS()) return new(null, "Updating needs macOS.", false);
        var app = MacBundle.FindBundle(Environment.ProcessPath);
        if (app == null)
            return new(null, "This copy of Magpie isn't running from Magpie.app, so it can't replace itself. The new disk image opens instead: drag Magpie to Applications.", true);
        if (MacBundle.IsTranslocated(app))
            return new(null, "Magpie is running straight from the disk image or the Downloads folder, where macOS doesn't let it change itself. Drag Magpie to your Applications folder, open it from there, and updates will install by themselves. The new disk image opens now.", true);
        if (!MacBundle.CanReplace(app))
            return new(null, $"Magpie can't write to {Path.GetDirectoryName(app)} (your Mac account may not be an administrator). The new disk image opens instead: drag Magpie to Applications and choose Replace.", true);
        return new(app, null, false);
    }

    /// <summary>Where the previous version is kept (outside Applications, so Launchpad doesn't show two Magpies).</summary>
    public static string BackupPath() =>
        MacBundle.BackupPathIn(AppPaths.MacFolders(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).LocalRoot);

    /// <summary>ditto keeps a bundle's links, permissions and signature; used when a move has to cross volumes.</summary>
    private static void Ditto(string from, string to) =>
        Check(Shell.Run("/usr/bin/ditto", new[] { from, to }, TimeSpan.FromMinutes(5)), "copy the app");

    /// <summary>Installs <paramref name="dmg"/> over <paramref name="app"/>, after checking once more that the disk image
    /// is still the one that matched the published SHA-256. Throws (leaving the app as it was) on failure.</summary>
    public static void Install(string dmg, string? expectedSha256, string app)
    {
        if (!UpdateClient.MatchesSha256(dmg, expectedSha256))
        {
            try { File.Delete(dmg); } catch { }
            throw new InvalidOperationException("The downloaded disk image no longer matches its published checksum, so it wasn't installed. Magpie will download it again.");
        }
        var mount = Path.Combine(Path.GetTempPath(), "magpie-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mount);
        var staged = MacBundle.StagingPathFor(app);
        Check(Shell.Run("/usr/bin/hdiutil", new[] { "attach", "-nobrowse", "-readonly", "-noautoopen", "-quiet", "-mountpoint", mount, dmg }, TimeSpan.FromMinutes(3)), "open the disk image");
        try
        {
            var source = Path.Combine(mount, "Magpie.app");
            if (!Directory.Exists(source))
                source = Directory.GetDirectories(mount, "*.app").FirstOrDefault() ?? throw new InvalidOperationException("The disk image has no Magpie.app in it.");
            if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
            Check(Shell.Run("/usr/bin/ditto", new[] { source, staged }, TimeSpan.FromMinutes(5)), "copy the new version");
        }
        finally
        {
            var (code, _, _) = Shell.Run("/usr/bin/hdiutil", new[] { "detach", mount, "-quiet" }, TimeSpan.FromMinutes(1));
            if (code != 0) Shell.Run("/usr/bin/hdiutil", new[] { "detach", mount, "-force", "-quiet" }, TimeSpan.FromMinutes(1));
            try { Directory.Delete(mount); } catch { }
        }
        // Downloaded by Magpie itself, so normally not quarantined; make sure Gatekeeper doesn't ask again anyway.
        try { Shell.Run("/usr/bin/xattr", new[] { "-dr", "com.apple.quarantine", staged }, TimeSpan.FromMinutes(1)); } catch { }
        var backup = BackupPath();
        bool kept;
        try { kept = MacBundle.Swap(app, staged, backup, Ditto); }
        catch
        {
            try { if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true); } catch { }
            throw;
        }
        Log.Info($"installed {Path.GetFileName(dmg)} over {app}" + (kept ? $" (previous version kept in {backup})" : " (the previous version couldn't be kept)"));
    }

    private static void Check((int Code, string Output, string Error) r, string what)
    {
        if (r.Code != 0) throw new InvalidOperationException($"Couldn't {what} (code {r.Code}): {r.Error.Trim()}");
    }

    /// <summary>Starts the (new) app once this process has exited, telling it which version it replaced.</summary>
    public static void RelaunchAfterExit(string app, string fromVersion, bool afterUpdate = true)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(afterUpdate
                ? "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$2\" --args --after-update \"$3\""
                : "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$2\"");
            psi.ArgumentList.Add("magpie-relaunch");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            psi.ArgumentList.Add(app);
            psi.ArgumentList.Add(fromVersion);
            Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { Log.Error("relaunch failed", ex); }
    }

    /// <summary>
    /// The new version failed to start: ask (with a plain macOS dialog — Magpie's own windows may not work) whether to
    /// go back to the kept previous version, and if so put it back and start it. True when it was put back.
    /// </summary>
    public static bool OfferRollback(Exception ex, string fromVersion)
    {
        try
        {
            var app = MacBundle.FindBundle(Environment.ProcessPath);
            var backup = BackupPath();
            if (app == null || !Directory.Exists(backup)) return false;
            var text = $"Magpie {AppServices.Current} could not start: {ex.Message}\n\nGo back to the previous version ({fromVersion})?";
            var script = $"display dialog {AppleScriptString(text)} buttons {{\"Keep this version\", \"Go back\"}} default button \"Go back\" with icon caution with title \"Magpie\"";
            var (code, output, _) = Shell.Run("/usr/bin/osascript", new[] { "-e", script }, TimeSpan.FromMinutes(10));
            if (code != 0 || !output.Contains("Go back", StringComparison.Ordinal)) return false;
            MacBundle.Rollback(app, backup, Ditto);
            RelaunchAfterExit(app, fromVersion, afterUpdate: false);
            Log.Info($"rolled back to {fromVersion}");
            return true;
        }
        catch (Exception rex) { Log.Error("rollback failed", rex); return false; }
    }

    private static string AppleScriptString(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
