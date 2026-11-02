using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magpie.Core.Settings;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Updates;

public sealed record VersionBackupFile(string Path, string Kind, string Sha256, string? LinkTarget = null);

public sealed class VersionSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public string ProfileRoot { get; init; } = "";
    public string MailRoot { get; init; } = "";
    public string ApplicationKind { get; init; } = "";
    public string InstallKind { get; init; } = "";
    public string ApplicationName { get; init; } = "";
    public List<VersionBackupFile> Files { get; init; } = [];
    [JsonIgnore] public string Folder { get; internal set; } = "";
    [JsonIgnore] public string ApplicationPath => Path.Combine(Folder, "application", ApplicationName);
}

/// <summary>
/// A same-device, private snapshot of the application and its local profile. Secrets are copied in their existing
/// encrypted form. Restores are staged, verified and journalled before any mail engine is allowed to open the files.
/// </summary>
public static class VersionBackup
{
    private const string Metadata = "snapshot.json";
    private const string Pending = "version-restore-pending.json";
    private const string Journal = "version-restore-journal.json";
    private static readonly string[] ProfileFiles = ["settings.json", "secrets.json", AppPaths.MailFolderFile, "version-pin.txt"];
    private static readonly object Gate = new();

    private sealed record PendingRestore(string SnapshotId, string Version, string InstallKind);
    private sealed record RestoreItem(string Destination, string? Source, bool Directory, bool Existed, string Backup, string Staged,
        string BackupHash = "", string StagedHash = "");
    private sealed record RestoreJournal(string SnapshotId, string ProfileRoot, string MailRoot, List<RestoreItem> Items);

    public static VersionSnapshot Create(AppPaths paths, AppVersion current, string applicationPath,
        CancellationToken ct = default, string? installKind = null)
    {
        RequireNoSettingsRestore(paths);
        if (File.Exists(Path.Combine(paths.Root, Pending)) || File.Exists(Path.Combine(paths.Root, Journal)))
            throw new InvalidOperationException("Finish the pending version restore before starting another version change.");
        if (!paths.MailRootAvailable)
            throw new DirectoryNotFoundException("The mail folder is unavailable. Reconnect it before changing application versions.");
        // Even a new profile has an empty SQLite database after MailEngine starts. An absent database means the
        // live profile cannot be captured, not that a future restore should delete the user's mail and drafts.
        if (!File.Exists(paths.Database))
            throw new FileNotFoundException("The active mail database is unavailable. No version change can start until it can be backed up.");
        applicationPath = Path.GetFullPath(applicationPath);
        var isDirectory = Directory.Exists(applicationPath);
        if (!isDirectory && !File.Exists(applicationPath)) throw new FileNotFoundException("The running application could not be backed up.");
        if (isDirectory && (!applicationPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.Combine(applicationPath, "Contents", "MacOS"))))
            throw new InvalidDataException("Only a complete Mac application bundle can be backed up as an application folder.");
        var kind = installKind ?? (isDirectory ? UpdateInstallKinds.MacApp
            : paths.IsPortable ? UpdateInstallKinds.WindowsPortable : UpdateInstallKinds.WindowsPerUser);
        if (!UpdateInstallKinds.All.Contains(kind) || (kind == UpdateInstallKinds.MacApp) != isDirectory)
            throw new InvalidDataException("The application does not match this installation type.");
        var id = Guid.NewGuid().ToString("N");
        var final = SnapshotFolder(paths, id);
        var staging = final + ".incomplete";
        PrivateDirectory(staging);
        var entries = new List<VersionBackupFile>();
        try
        {
            foreach (var name in ProfileFiles)
            {
                ct.ThrowIfCancellationRequested();
                var source = Path.Combine(paths.Root, name);
                if (File.Exists(source)) CopyFile(source, Path.Combine(staging, "profile", name), "profile/" + name, entries, ct);
            }
            if (!File.Exists(paths.Settings)) throw new InvalidDataException("Save your settings before creating a version backup.");
            {
                var target = Path.Combine(staging, "mail", "mail.db");
                PrivateDirectory(Path.GetDirectoryName(target)!);
                using (var source = OpenDatabase(paths.Database, readOnly: true))
                using (var destination = OpenDatabase(target, readOnly: false))
                {
                    source.BackupDatabase(destination);
                    // The source uses WAL. A self-contained immutable snapshot must not depend on sidecars or
                    // create them during later integrity reads, so checkpoint the destination into one file.
                    using var command = destination.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=DELETE";
                    if (!string.Equals(command.ExecuteScalar()?.ToString(), "delete", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The mail snapshot could not be made into a self-contained database.");
                }
                CheckDatabase(target);
                entries.Add(new("mail/mail.db", "file", Hash(target)));
                MakePrivate(target);
            }
            if (Directory.Exists(paths.MimeCache)) CopyTree(paths.MimeCache, Path.Combine(staging, "mail", "messages"),
                "mail/messages", entries, ct, allowLinks: false);
            var applicationName = isDirectory ? "Magpie.app" : "Magpie.exe";
            var applicationTarget = Path.Combine(staging, "application", applicationName);
            if (isDirectory) CopyTree(applicationPath, applicationTarget, "application/" + applicationName, entries, ct, allowLinks: true);
            else CopyFile(applicationPath, applicationTarget, "application/" + applicationName, entries, ct);
            var snapshot = new VersionSnapshot
            {
                Id = id, Version = current.ToString(), Created = DateTimeOffset.UtcNow,
                ProfileRoot = Path.GetFullPath(paths.Root), MailRoot = Path.GetFullPath(paths.MailRoot),
                ApplicationKind = isDirectory ? "directory" : "file", InstallKind = kind,
                ApplicationName = applicationName, Files = entries, Folder = staging,
            };
            Validate(snapshot);
            AtomicJson(Path.Combine(staging, Metadata), snapshot);
            Directory.Move(staging, final);
            snapshot.Folder = final;
            return snapshot;
        }
        catch
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
            throw;
        }
    }

    public static IReadOnlyList<VersionSnapshot> List(AppPaths paths)
    {
        var folder = SnapshotsFolder(paths);
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateDirectories(folder).Where(d => Guid.TryParseExact(Path.GetFileName(d), "N", out _))
            .Select(d => Read(paths, Path.GetFileName(d))).OrderByDescending(s => s.Created).ToList();
    }

    public static VersionSnapshot Read(AppPaths paths, string id)
    {
        var folder = SnapshotFolder(paths, id);
        var snapshot = JsonSerializer.Deserialize<VersionSnapshot>(File.ReadAllText(Path.Combine(folder, Metadata)))
                       ?? throw new InvalidDataException("The version backup record is incomplete.");
        snapshot.Folder = folder;
        if (snapshot.Id != id || !SamePath(snapshot.ProfileRoot, paths.Root))
            throw new InvalidDataException("This backup belongs to a different Magpie profile.");
        ValidateMetadata(snapshot);
        return snapshot;
    }

    public static void Validate(VersionSnapshot snapshot)
    {
        ValidateMetadata(snapshot);
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in snapshot.Files)
        {
            if (!expected.Add(entry.Path)) throw new InvalidDataException("The version backup contains duplicate files.");
            var file = SafeFile(snapshot.Folder, entry.Path);
            if (entry.Kind == "directory")
            {
                if (!Directory.Exists(file) || new DirectoryInfo(file).LinkTarget != null)
                    throw new InvalidDataException("A folder is missing from the version backup.");
            }
            else if (entry.Kind == "symlink")
            {
                ValidateLink(entry.Path, entry.LinkTarget);
                var actual = new FileInfo(file).LinkTarget ?? new DirectoryInfo(file).LinkTarget;
                if (actual != entry.LinkTarget) throw new InvalidDataException("An application link in the version backup changed.");
            }
            else if (entry.Kind != "file" || !File.Exists(file) || new FileInfo(file).LinkTarget != null
                     || !string.Equals(Hash(file), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A file in the version backup is missing or did not match its checksum.");
        }
        if (!expected.Contains("profile/settings.json") || !expected.Contains("mail/mail.db")
            || !expected.Contains("application/" + snapshot.ApplicationName))
            throw new InvalidDataException("The application, settings or mail database are missing from the version backup.");
        var allowed = new HashSet<string>(expected, StringComparer.Ordinal) { Metadata };
        foreach (var name in expected)
        {
            var parent = name;
            while (parent.LastIndexOf('/') is var slash && slash > 0)
            {
                parent = parent[..slash];
                allowed.Add(parent);
            }
        }
        CheckInventory(snapshot.Folder, "", allowed);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(snapshot.Folder, "profile", "settings.json")));
        if (settings.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("The backup's settings are unreadable.");
        var database = Path.Combine(snapshot.Folder, "mail", "mail.db");
        CheckDatabase(database);
    }

    /// <summary>Checks the current app against the immutable saved application before restoring legacy preferences.</summary>
    public static bool MatchesApplication(VersionSnapshot snapshot, string applicationPath)
    {
        try
        {
            Validate(snapshot);
            var prefix = "application/" + snapshot.ApplicationName;
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in snapshot.Files.Where(e => e.Path == prefix || e.Path.StartsWith(prefix + "/", StringComparison.Ordinal)))
            {
                var relative = entry.Path.Length == prefix.Length ? "" : entry.Path[(prefix.Length + 1)..];
                var actual = relative.Length == 0 ? applicationPath : Path.Combine(applicationPath, relative.Replace('/', Path.DirectorySeparatorChar));
                allowed.Add(relative);
                if (entry.Kind == "directory")
                {
                    if (!Directory.Exists(actual) || new DirectoryInfo(actual).LinkTarget != null) return false;
                }
                else if (entry.Kind == "symlink")
                {
                    if ((new FileInfo(actual).LinkTarget ?? new DirectoryInfo(actual).LinkTarget) != entry.LinkTarget) return false;
                }
                else if (!File.Exists(actual) || new FileInfo(actual).LinkTarget != null
                         || !string.Equals(Hash(actual), entry.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (snapshot.ApplicationKind == "directory") CheckInventory(applicationPath, "", allowed);
            return true;
        }
        catch { return false; }
    }

    public static void StageRestore(AppPaths paths, string id, string? expectedInstallKind = null)
    {
        lock (Gate)
        {
            if (File.Exists(Path.Combine(paths.Root, Journal)))
                throw new InvalidOperationException("An interrupted restore must be recovered before another can start.");
            if (File.Exists(Path.Combine(paths.Root, Pending)))
                throw new InvalidOperationException("A version restore is already waiting for a restart.");
            RequireNoSettingsRestore(paths);
            var snapshot = Read(paths, id);
            Validate(snapshot);
            if (expectedInstallKind != null && expectedInstallKind != snapshot.InstallKind)
                throw new InvalidDataException("The backup was made by a different installation type.");
            AtomicJson(Path.Combine(paths.Root, Pending), new PendingRestore(id, snapshot.Version, snapshot.InstallKind));
        }
    }

    public static void CancelPending(AppPaths paths)
    {
        lock (Gate)
        {
            if (File.Exists(Path.Combine(paths.Root, Journal)))
                throw new InvalidOperationException("The restore has started and must be recovered before cancellation.");
            File.Delete(Path.Combine(paths.Root, Pending));
        }
    }

    public static string? PendingVersion(AppPaths paths)
    {
        lock (Gate)
        {
            var file = Path.Combine(paths.Root, Pending);
            if (!File.Exists(file)) return null;
            var pending = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(file))
                          ?? throw new InvalidDataException("The pending version restore is incomplete.");
            if (AppVersion.TryParse(pending.Version) == null || !Guid.TryParseExact(pending.SnapshotId, "N", out _))
                throw new InvalidDataException("The pending version restore is invalid.");
            return pending.Version;
        }
    }

    public static bool CancelPendingForVersion(AppPaths paths, string version)
    {
        lock (Gate)
        {
            if (PendingVersion(paths) != version) return false;
            CancelPending(paths);
            return true;
        }
    }

    /// <summary>Call before startup interruption handling and before opening any profile files.</summary>
    public static void RecoverInterruptedRestore(AppPaths paths)
    {
        lock (Gate) Recover(paths);
    }

    /// <summary>
    /// Run before SettingsBackup.ApplyPending and before constructing any settings store, vault or mail engine.
    /// The platform must already have put the matching saved app in place and restarted it. A failed/partial restore
    /// throws and prevents startup; its recovery journal is retained until the previous profile is safely recovered.
    /// </summary>
    public static bool ApplyPending(AppPaths paths, AppVersion running)
    {
        lock (Gate)
        {
            Recover(paths);
            var pendingPath = Path.Combine(paths.Root, Pending);
            if (!File.Exists(pendingPath)) return false;
            RequireNoSettingsRestore(paths);
            var pending = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(pendingPath))
                          ?? throw new InvalidDataException("The pending version restore is incomplete.");
            var snapshot = Read(paths, pending.SnapshotId);
            if (pending.Version != running.ToString() || snapshot.Version != pending.Version
                || snapshot.InstallKind != pending.InstallKind)
                throw new InvalidDataException("The saved application must be restored before its mail and settings. No profile files were changed.");
            Validate(snapshot);
            var plan = BuildRestorePlan(paths, snapshot);
            try
            {
                PreparePlan(plan);
                var journal = new RestoreJournal(snapshot.Id, Path.GetFullPath(paths.Root), snapshot.MailRoot, plan);
                AtomicJson(Path.Combine(paths.Root, Journal), journal);
                foreach (var item in plan) ApplyItem(item);
                // Once this marker is gone the restored profile is committed. A leftover pending marker is safe:
                // applying the same immutable snapshot again is idempotent and the current version gate still holds.
                File.Delete(Path.Combine(paths.Root, Journal));
                File.Delete(pendingPath);
                foreach (var item in plan) CleanItem(item);
                paths.RefreshMailRoot();
                return true;
            }
            catch
            {
                Recover(paths);  // failure to recover propagates; the app must not continue with a partial profile
                foreach (var item in plan) CleanStage(item);
                throw;
            }
        }
    }

    private static List<RestoreItem> BuildRestorePlan(AppPaths paths, VersionSnapshot snapshot)
    {
        // Restoring into another profile/device is not supported. The original custom mail folder must still exist.
        if (!Directory.Exists(snapshot.MailRoot)) throw new DirectoryNotFoundException("The original mail folder is unavailable. Reconnect it before restoring.");
        var marker = Path.Combine(snapshot.Folder, "profile", AppPaths.MailFolderFile);
        var indicated = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
        if (!SamePath(indicated.Length == 0 ? paths.Root : indicated, snapshot.MailRoot))
            throw new InvalidDataException("The backup's mail folder does not match its saved profile.");
        var suffix = ".version-restore-" + snapshot.Id;
        var plan = new List<RestoreItem>();
        void Add(string destination, string? source, bool directory)
        {
            var exists = directory ? Directory.Exists(destination) : File.Exists(destination);
            if (new FileInfo(destination).LinkTarget != null || new DirectoryInfo(destination).LinkTarget != null)
                throw new InvalidDataException("A profile restore cannot replace a linked destination.");
            plan.Add(new(destination, source, directory, exists, destination + suffix + ".before", destination + suffix + ".next"));
        }
        foreach (var name in ProfileFiles)
        {
            var source = Path.Combine(snapshot.Folder, "profile", name);
            Add(Path.Combine(paths.Root, name), File.Exists(source) ? source : null, false);
        }
        var db = Path.Combine(snapshot.MailRoot, "mail.db");
        // A complete checkpoint is required before moving the database and its sidecars. The platform still owns
        // process/profile exclusivity: a SQLite transaction alone cannot exclude every WAL reader between files.
        if (File.Exists(db))
        {
            using var check = OpenDatabase(db, readOnly: false);
            using var command = check.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=1000;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using (var result = command.ExecuteReader())
            {
                if (!result.Read() || result.GetInt32(0) != 0 || result.GetInt32(1) != result.GetInt32(2))
                    throw new IOException("The mail database is still in use. Close other copies of Magpie before restoring.");
            }
            command.CommandText = "BEGIN EXCLUSIVE; ROLLBACK;";
            command.ExecuteNonQuery();
        }
        var dbSource = Path.Combine(snapshot.Folder, "mail", "mail.db");
        if (!File.Exists(dbSource)) throw new InvalidDataException("The saved mail database is missing. No live mail files were changed.");
        Add(db, dbSource, false);
        Add(db + "-wal", null, false);
        Add(db + "-shm", null, false);
        var messages = Path.Combine(snapshot.Folder, "mail", "messages");
        Add(Path.Combine(snapshot.MailRoot, "messages"), Directory.Exists(messages) ? messages : null, true);
        return plan;
    }

    private static void PreparePlan(List<RestoreItem> plan)
    {
        for (var index = 0; index < plan.Count; index++)
        {
            var item = plan[index];
            CleanStage(item);
            if (item.Existed)
            {
                if (Exists(item.Backup, item.Directory)) Remove(item.Backup, item.Directory);
                Copy(item.Destination, item.Backup, item.Directory);
            }
            if (item.Source != null) Copy(item.Source, item.Staged, item.Directory);
            plan[index] = item with
            {
                BackupHash = item.Existed ? Fingerprint(item.Backup, item.Directory) : "",
                StagedHash = item.Source != null ? Fingerprint(item.Staged, item.Directory) : "",
            };
        }
    }

    private static void ApplyItem(RestoreItem item)
    {
        if (item.Source != null && (item.StagedHash.Length == 0 || Fingerprint(item.Staged, item.Directory) != item.StagedHash))
            throw new InvalidDataException("A staged restore file changed. The previous profile will be recovered.");
        if (Exists(item.Destination, item.Directory)) Remove(item.Destination, item.Directory);
        if (item.Source != null)
        {
            if (item.Directory) Directory.Move(item.Staged, item.Destination);
            else File.Move(item.Staged, item.Destination);
        }
    }

    private static void Recover(AppPaths paths)
    {
        var journalPath = Path.Combine(paths.Root, Journal);
        if (!File.Exists(journalPath)) return;
        var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(journalPath))
                      ?? throw new InvalidDataException("The restore recovery record is incomplete.");
        var snapshot = Read(paths, journal.SnapshotId);
        if (!SamePath(journal.ProfileRoot, paths.Root) || !SamePath(journal.MailRoot, snapshot.MailRoot))
            throw new InvalidDataException("The restore recovery record belongs to another profile.");
        var permitted = ProfileFiles.Select(n => Path.Combine(paths.Root, n))
            .Concat(new[] { "mail.db", "mail.db-wal", "mail.db-shm", "messages" }.Select(n => Path.Combine(snapshot.MailRoot, n)))
            .Select(Path.GetFullPath).ToHashSet(PathComparer);
        if (journal.Items.Count != permitted.Count || journal.Items.Select(i => Path.GetFullPath(i.Destination)).Distinct(PathComparer).Count() != permitted.Count)
            throw new InvalidDataException("The restore recovery record has an invalid file list.");
        foreach (var item in journal.Items)
        {
            var suffix = ".version-restore-" + snapshot.Id;
            if (!permitted.Contains(Path.GetFullPath(item.Destination))
                || item.Backup != item.Destination + suffix + ".before" || item.Staged != item.Destination + suffix + ".next"
                || item.Directory != SamePath(item.Destination, Path.Combine(snapshot.MailRoot, "messages")))
                throw new InvalidDataException("The restore recovery record has an invalid destination.");
            if (item.Existed && !Exists(item.Backup, item.Directory))
                throw new InvalidDataException("A file required to recover an interrupted restore is missing. The journal was retained.");
            if (item.Existed && (item.BackupHash.Length == 0 || Fingerprint(item.Backup, item.Directory) != item.BackupHash))
                throw new InvalidDataException("A file required to recover an interrupted restore did not match its checksum. No recovery files were changed; the journal was retained.");
        }
        foreach (var item in journal.Items)
        {
            if (Exists(item.Destination, item.Directory)) Remove(item.Destination, item.Directory);
            if (item.Existed) Copy(item.Backup, item.Destination, item.Directory);
        }
        File.Delete(journalPath);
        foreach (var item in journal.Items) CleanItem(item);
        paths.RefreshMailRoot();
    }

    private static void ValidateMetadata(VersionSnapshot snapshot)
    {
        if (snapshot.FormatVersion != 1 || !Guid.TryParseExact(snapshot.Id, "N", out _) || AppVersion.TryParse(snapshot.Version) == null
            || !Path.IsPathFullyQualified(snapshot.ProfileRoot) || !Path.IsPathFullyQualified(snapshot.MailRoot)
            || snapshot.Files == null || snapshot.Files.Count == 0
            || !snapshot.Files.Any(e => e.Path == "mail/mail.db" && e.Kind == "file" && e.Sha256.Length == 64)
            || !UpdateInstallKinds.All.Contains(snapshot.InstallKind)
            || (snapshot.ApplicationKind == "directory" ? snapshot.ApplicationName != "Magpie.app" || snapshot.InstallKind != UpdateInstallKinds.MacApp
                : snapshot.ApplicationKind != "file" || snapshot.ApplicationName != "Magpie.exe" || snapshot.InstallKind == UpdateInstallKinds.MacApp))
            throw new InvalidDataException("The version backup record is invalid.");
        foreach (var entry in snapshot.Files)
        {
            _ = SafeFile(snapshot.Folder, entry.Path);
            if (!(entry.Path.StartsWith("application/", StringComparison.Ordinal)
                  || entry.Path.StartsWith("mail/", StringComparison.Ordinal)
                  || ProfileFiles.Any(n => entry.Path == "profile/" + n)))
                throw new InvalidDataException("The version backup contains an unexpected file.");
            if ((entry.Path == "profile/settings.json" || entry.Path == "mail/mail.db") && entry.Kind != "file")
                throw new InvalidDataException("The backup's settings and database must be ordinary files.");
            if (entry.Path == "application/" + snapshot.ApplicationName && entry.Kind != snapshot.ApplicationKind)
                throw new InvalidDataException("The backup's application does not match its installation type.");
        }
        if (snapshot.ApplicationKind == "directory" && !Directory.Exists(Path.Combine(snapshot.ApplicationPath, "Contents", "MacOS")))
            throw new InvalidDataException("The saved Mac application is incomplete.");
    }

    private static void CheckInventory(string folder, string relative, HashSet<string> allowed)
    {
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            var path = relative.Length == 0 ? item.Name : relative + "/" + item.Name;
            if (!allowed.Contains(path)) throw new InvalidDataException("An unexpected file was added to the version backup.");
            if (item.LinkTarget == null && item is DirectoryInfo) CheckInventory(item.FullName, path, allowed);
        }
    }

    private static void CopyTree(string source, string target, string relative, List<VersionBackupFile> entries,
        CancellationToken ct, bool allowLinks)
    {
        ct.ThrowIfCancellationRequested();
        if (new DirectoryInfo(source).LinkTarget != null) throw new InvalidDataException("A backup root cannot be a symbolic link.");
        PrivateDirectory(target);
        entries.Add(new(relative, "directory", ""));
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, item.Name);
            var child = relative + "/" + item.Name;
            if (item.LinkTarget is { } link)
            {
                if (!allowLinks) throw new InvalidDataException("The mail cache contains a symbolic link and cannot be backed up safely.");
                ValidateLink(child, link);
                if ((item.Attributes & FileAttributes.Directory) != 0) Directory.CreateSymbolicLink(destination, link);
                else File.CreateSymbolicLink(destination, link);
                entries.Add(new(child, "symlink", "", link));
            }
            else if (item is DirectoryInfo) CopyTree(item.FullName, destination, child, entries, ct, allowLinks);
            else CopyFile(item.FullName, destination, child, entries, ct);
        }
        if (relative.StartsWith("application/", StringComparison.Ordinal)) PreserveMode(source, target);
    }

    private static void CopyFile(string source, string target, string relative, List<VersionBackupFile> entries, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (new FileInfo(source).LinkTarget != null) throw new InvalidDataException("A backup file cannot be a symbolic link.");
        PrivateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, false);
        if (relative.StartsWith("application/", StringComparison.Ordinal)) PreserveMode(source, target);
        else MakePrivate(target);
        entries.Add(new(relative, "file", Hash(target)));
    }

    private static void ValidateLink(string relative, string? target)
    {
        if (!relative.StartsWith("application/Magpie.app/", StringComparison.Ordinal) || string.IsNullOrEmpty(target)
            || Path.IsPathRooted(target) || target.Contains('\\'))
            throw new InvalidDataException("The application backup contains an unsafe symbolic link.");
        var root = Path.Combine(Path.GetTempPath(), "magpie-link-boundary");
        var resolved = Path.GetFullPath(Path.Combine(root, Path.GetDirectoryName(relative.Replace('/', Path.DirectorySeparatorChar))!, target));
        var app = Path.Combine(root, "application", "Magpie.app") + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(app, PathComparison)) throw new InvalidDataException("An application link points outside its bundle.");
    }

    private static string SafeFile(string folder, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Split('/').Any(p => p is "" or "." or "..") || Path.IsPathRooted(relative))
            throw new InvalidDataException("The version backup contains an unsafe file name.");
        var path = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, PathComparison))
            throw new InvalidDataException("The version backup contains an unsafe file name.");
        return path;
    }

    private static void Copy(string source, string target, bool directory)
    {
        if (directory) CopyTree(source, target, "mail/messages", [], default, allowLinks: false);
        else { File.Copy(source, target, true); MakePrivate(target); }
    }
    private static bool Exists(string path, bool directory) => directory ? Directory.Exists(path) : File.Exists(path);
    private static void Remove(string path, bool directory) { if (directory) Directory.Delete(path, true); else File.Delete(path); }
    private static void CleanStage(RestoreItem item) { if (Exists(item.Staged, item.Directory)) Remove(item.Staged, item.Directory); }
    private static void CleanItem(RestoreItem item)
    {
        CleanStage(item);
        if (Exists(item.Backup, item.Directory)) Remove(item.Backup, item.Directory);
    }
    private static string SnapshotsFolder(AppPaths paths) => Path.Combine(paths.Root, "version-history", "snapshots");
    private static void RequireNoSettingsRestore(AppPaths paths)
    {
        if (File.Exists(Path.Combine(paths.Root, SettingsBackup.PendingFile))
            || File.Exists(Path.Combine(paths.Root, SettingsBackup.RestoreJournal)))
            throw new InvalidOperationException("A settings restore is also pending. Finish it before restoring an application and its data together.");
    }
    private static string SnapshotFolder(AppPaths paths, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid version backup identifier.", nameof(id));
        return Path.Combine(SnapshotsFolder(paths), id);
    }
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), PathComparison);
    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Fingerprint(string path, bool directory)
    {
        if (!directory)
        {
            if (new FileInfo(path).LinkTarget != null) throw new InvalidDataException("A restore recovery file cannot be a symbolic link.");
            return Hash(path);
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Visit(string folder, string relative)
        {
            foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
            {
                if (item.LinkTarget != null) throw new InvalidDataException("A restore recovery folder cannot contain symbolic links.");
                var name = relative + "/" + item.Name;
                var childDirectory = item is DirectoryInfo;
                hash.AppendData(System.Text.Encoding.UTF8.GetBytes((childDirectory ? "d" : "f") + "\0" + name + "\0"));
                if (childDirectory) Visit(item.FullName, name);
                else hash.AppendData(System.Text.Encoding.ASCII.GetBytes(Hash(item.FullName)));
            }
        }
        Visit(path, "");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static SqliteConnection OpenDatabase(string path, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void CheckDatabase(string path)
    {
        using var connection = OpenDatabase(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("The mail database backup failed its integrity check.");
    }
    private static void PrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    private static void MakePrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private static void PreserveMode(string from, string to)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(to, File.GetUnixFileMode(from));
    }
    private static void AtomicJson<T>(string path, T value)
    {
        PrivateDirectory(Path.GetDirectoryName(path)!);
        var next = path + ".next";
        try
        {
            using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(flushToDisk: true);
            }
            MakePrivate(next);
            File.Move(next, path, true);
        }
        finally { if (File.Exists(next)) File.Delete(next); }
    }
}
