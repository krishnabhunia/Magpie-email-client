using System.Text.Json;

namespace Magpie.Core.Updates;

public sealed record UpdateHistoryEntry
{
    public string Id { get; init; } = "";
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Action { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? SnapshotId { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Updated { get; init; }
}

/// <summary>One atomic file per transition, kept in the private profile rather than the disposable download cache.</summary>
public static class UpdateHistory
{
    public const string Preparing = "Preparing";
    public const string Downloading = "Downloading";
    public const string Installing = "Installing";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Restored = "Restored";
    private static readonly object Gate = new();

    public static string Record(AppPaths paths, string from, string to, string action, string status, string detail,
        string? snapshotId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new UpdateHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"), From = from, To = to, Action = action, Status = status, Detail = detail,
            SnapshotId = snapshotId, Created = now, Updated = now,
        };
        lock (Gate) Write(paths, item);
        return item.Id;
    }

    public static IReadOnlyList<UpdateHistoryEntry> List(AppPaths paths)
    {
        var folder = Folder(paths);
        if (!Directory.Exists(folder)) return [];
        lock (Gate)
            return Directory.EnumerateFiles(folder, "*.json")
                .Select(ReadEntry)
                .OrderByDescending(item => item.Created).ToList();
    }

    private static UpdateHistoryEntry ReadEntry(string file)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<UpdateHistoryEntry>(File.ReadAllText(file));
            if (entry == null || entry.Id != Path.GetFileNameWithoutExtension(file)
                || !Guid.TryParseExact(entry.Id, "N", out _)) throw new InvalidDataException();
            return entry;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new UpdateHistoryEntry
            {
                Id = Path.GetFileNameWithoutExtension(file), Action = "History record", Status = Failed,
                Detail = "This history record is unreadable. Other history entries and their backups remain available.",
                Created = File.GetLastWriteTimeUtc(file), Updated = File.GetLastWriteTimeUtc(file),
            };
        }
    }

    public static void SetStatus(AppPaths paths, string id, string status, string detail)
    {
        lock (Gate)
        {
            var file = FileFor(paths, id);
            var item = JsonSerializer.Deserialize<UpdateHistoryEntry>(File.ReadAllText(file))
                       ?? throw new InvalidDataException("The update history record is incomplete.");
            Write(paths, item with { Status = status, Detail = detail, Updated = DateTimeOffset.UtcNow });
        }
    }

    public static void AttachSnapshot(AppPaths paths, string id, string snapshotId)
    {
        lock (Gate)
        {
            var item = JsonSerializer.Deserialize<UpdateHistoryEntry>(File.ReadAllText(FileFor(paths, id)))
                       ?? throw new InvalidDataException("The update history record is incomplete.");
            Write(paths, item with { SnapshotId = snapshotId, Updated = DateTimeOffset.UtcNow });
        }
    }

    /// <summary>Call only after the new app has loaded its settings/database and main window successfully.</summary>
    public static void CompleteStartup(AppPaths paths, AppVersion running)
    {
        foreach (var item in List(paths).Where(x => x.Status is Preparing or Downloading or Installing))
        {
            if (item.Status == Installing && item.To == running.ToString())
                SetStatus(paths, item.Id, item.Action == "Restore" ? Restored : Succeeded,
                    "This version started successfully with the saved mail and settings.");
            else SetStatus(paths, item.Id, Failed, "The transition was interrupted before a successful startup. Its backup is available in version history.");
        }
    }

    private static string Folder(AppPaths paths) => Path.Combine(paths.Root, "version-history", "events");
    private static string FileFor(AppPaths paths, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid update history identifier.", nameof(id));
        return Path.Combine(Folder(paths), id + ".json");
    }

    private static void Write(AppPaths paths, UpdateHistoryEntry item)
    {
        Directory.CreateDirectory(Folder(paths));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Folder(paths), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var file = FileFor(paths, item.Id);
        var next = file + ".next";
        try
        {
            using (var stream = new FileStream(next, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, item);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(next, file, true);
        }
        finally { if (File.Exists(next)) File.Delete(next); }
    }
}
