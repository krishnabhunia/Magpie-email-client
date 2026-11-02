using Magpie.Core;
using Magpie.Core.Updates;

namespace Magpie.App.Services;

/// <summary>File and history operations used after the user accepts recovery from a failed Windows startup.</summary>
public static class WindowsVersionRecovery
{
    /// <summary>
    /// Restores the verified saved executable and stages its matching data for the next process. The caller owns
    /// the single-instance mutex throughout; the restarted process must acquire it before applying the restore.
    /// </summary>
    public static string StageFailedStartup(AppPaths paths, AppVersion failedVersion, string exe, string installKind,
        UpdateHistoryEntry transition, VersionSnapshot snapshot)
    {
        if (transition.Status != UpdateHistory.Installing || transition.To != failedVersion.ToString()
            || transition.SnapshotId != snapshot.Id)
            throw new InvalidDataException("The failed transition does not match this recovery point.");
        VersionBackup.Validate(snapshot);
        // An unrecovered journal deliberately blocks cancellation and a second restore.
        if (VersionBackup.PendingVersion(paths) is { } pendingVersion)
            VersionBackup.CancelPendingForVersion(paths, pendingVersion);
        VersionBackup.StageRestore(paths, snapshot.Id, installKind);
        // Windows permits renaming the loaded failed EXE, but not overwriting it in place. Keep the pending-data
        // marker on failure so an older application cannot silently open newer data.
        try { SelfUpdate.Swap(exe, snapshot.ApplicationPath); }
        catch
        {
            if (!RestoreApplicationSnapshot(exe, snapshot)) throw;
        }
        if (!VersionBackup.MatchesApplication(snapshot, exe))
            throw new InvalidDataException("The restored application could not be verified.");
        UpdateHistory.SetStatus(paths, transition.Id, UpdateHistory.Failed,
            "Startup failed; recovering the previous application and paired local data.");
        var recoveryId = UpdateHistory.Record(paths, failedVersion.ToString(), snapshot.Version, "Restore", UpdateHistory.Installing,
            "Restoring the last working application and its saved local data.");
        InstalledUpdate.Clear(paths.Updates);
        return recoveryId;
    }

    public static bool RestoreApplicationSnapshot(string exe, VersionSnapshot snapshot)
    {
        VersionBackup.Validate(snapshot);
        var staged = exe + ".recover-" + Guid.NewGuid().ToString("N");
        var failed = exe + ".failed-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(snapshot.ApplicationPath, staged, false);
            if (!VersionBackup.MatchesApplication(snapshot, staged))
                throw new InvalidDataException("The recovery application did not match its saved checksum.");
            if (File.Exists(exe)) File.Move(exe, failed);
            try { File.Move(staged, exe); }
            catch
            {
                if (!File.Exists(exe) && File.Exists(failed)) File.Move(failed, exe);
                throw;
            }
            return VersionBackup.MatchesApplication(snapshot, exe);
        }
        finally
        {
            try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            try { if (File.Exists(failed)) File.Delete(failed); } catch { }
        }
    }
}
