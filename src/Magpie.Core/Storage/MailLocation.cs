using System.Security.Cryptography;

namespace Magpie.Core.Storage;

/// <summary>
/// Design DL1: moving the mail (database + message files) to a folder the user chooses, e.g. on another or an
/// encrypted drive. The move is asked for while Magpie runs (<see cref="RequestMove"/>) and done at the next start,
/// before the mail database is opened (<see cref="PendingMove"/>, <see cref="Move"/>).
/// </summary>
public static class MailLocation
{
    public const string PendingMoveFile = "mail-move.txt";
    /// <summary>The database and its journal files; the message files are the "messages" folder.</summary>
    private static readonly string[] DbFiles = { "mail.db", "mail.db-wal", "mail.db-shm" };
    private const string MessagesFolder = "messages";

    public static void RequestMove(AppPaths paths, string target) =>
        File.WriteAllText(Path.Combine(paths.Root, PendingMoveFile), Path.GetFullPath(target));

    public static string? PendingMove(AppPaths paths)
    {
        var f = Path.Combine(paths.Root, PendingMoveFile);
        try { return File.Exists(f) ? File.ReadAllText(f).Trim() is { Length: > 0 } t ? t : null : null; }
        catch { return null; }
    }

    public static void ClearPendingMove(AppPaths paths)
    {
        try { File.Delete(Path.Combine(paths.Root, PendingMoveFile)); } catch { }
    }

    /// <summary>True when <paramref name="folder"/> already holds Magpie mail (e.g. kept there across a reinstall).</summary>
    public static bool HasMail(string folder) => File.Exists(Path.Combine(folder, "mail.db"));

    /// <summary>Bytes of mail in <paramref name="folder"/> (database + message files).</summary>
    public static long Size(string folder)
    {
        long total = 0;
        try
        {
            foreach (var n in DbFiles) { var f = Path.Combine(folder, n); if (File.Exists(f)) total += new FileInfo(f).Length; }
            var m = Path.Combine(folder, MessagesFolder);
            if (Directory.Exists(m)) total += Directory.EnumerateFiles(m, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch { }
        return total;
    }

    /// <summary>
    /// Copies the mail from <paramref name="from"/> to <paramref name="to"/>, checks every file arrived whole, and only
    /// then removes it from <paramref name="from"/>. <paramref name="replace"/>: mail already in <paramref name="to"/> is
    /// overwritten; otherwise such a folder is refused (use it as it is with <see cref="AppPaths.SetMailRoot"/>).
    /// A failed copy leaves <paramref name="from"/> untouched and removes what it copied.
    /// </summary>
    public static void Move(string from, string to, bool replace, IProgress<(long done, long total)>? progress = null)
    {
        from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(from));
        to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(to));
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        if (IsInside(from, to) || IsInside(to, from))
            throw new InvalidOperationException("Choose a mail folder that does not contain, or sit inside, the current mail folder.");
        RejectLinkedParents(from);
        RejectLinkedParents(to);
        if (!HasMail(from)) throw new InvalidOperationException("The current folder has no mail database to move.");
        if (HasMail(to) && !replace) throw new InvalidOperationException("That folder already has Magpie mail.");
        Directory.CreateDirectory(to);
        var staging = Path.Combine(to, ".magpie-move-" + Guid.NewGuid().ToString("N"));
        var backup = Path.Combine(to, ".magpie-move-backup-" + Guid.NewGuid().ToString("N"));
        var files = new List<(string src, string dst)>();
        foreach (var n in DbFiles)
            if (File.Exists(Path.Combine(from, n))) files.Add((Path.Combine(from, n), Path.Combine(staging, n)));
        var msgFrom = Path.Combine(from, MessagesFolder);
        if (Directory.Exists(msgFrom))
        {
            foreach (var dir in Directory.EnumerateDirectories(msgFrom, "*", SearchOption.AllDirectories).Prepend(msgFrom))
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Mail cannot be moved through a linked folder.");
            foreach (var f in Directory.EnumerateFiles(msgFrom, "*", SearchOption.AllDirectories))
                files.Add((f, Path.Combine(staging, MessagesFolder, Path.GetRelativePath(msgFrom, f))));
        }
        foreach (var (src, _) in files)
            if ((File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Mail cannot be moved through a linked file.");
        var total = files.Sum(f => new FileInfo(f.src).Length);
        var installed = new List<string>();
        var previous = new List<string>();
        var committed = false;
        var restored = false;
        long done = 0;
        try
        {
            foreach (var (src, dst) in files)
            {
                if ((File.GetAttributes(src) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Mail files cannot be symbolic links.");
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
                using var original = File.OpenRead(src);
                using var copy = File.OpenRead(dst);
                if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copy))) throw new IOException("A file did not copy completely: " + dst);
                done += new FileInfo(src).Length;
                progress?.Report((done, total));
            }
            Directory.CreateDirectory(backup);
            foreach (var name in DbFiles.Append(MessagesFolder))
            {
                var old = Path.Combine(to, name);
                var saved = Path.Combine(backup, name);
                if (Directory.Exists(old)) { Directory.Move(old, saved); previous.Add(name); }
                else if (File.Exists(old)) { File.Move(old, saved); previous.Add(name); }
                var ready = Path.Combine(staging, name);
                if (Directory.Exists(ready)) { Directory.Move(ready, old); installed.Add(name); }
                else if (File.Exists(ready)) { File.Move(ready, old); installed.Add(name); }
            }
            committed = true;
        }
        catch
        {
            foreach (var name in installed.AsEnumerable().Reverse()) DeleteOwned(Path.Combine(to, name), to);
            foreach (var name in previous.AsEnumerable().Reverse())
            {
                var saved = Path.Combine(backup, name);
                var old = Path.Combine(to, name);
                if (Directory.Exists(saved)) Directory.Move(saved, old);
                else if (File.Exists(saved)) File.Move(saved, old);
            }
            restored = true;
            throw;
        }
        finally
        {
            try { DeleteOwned(staging, to); } catch (Exception ex) { Log.Warn("mail move: temporary files kept: " + ex.Message); }
            if (committed || restored)
                try { DeleteOwned(backup, to); } catch (Exception ex) { Log.Warn("mail move: backup kept: " + ex.Message); }
        }

        foreach (var (src, _) in files) try { File.Delete(src); } catch (Exception ex) { Log.Warn("mail move: old file kept: " + ex.Message); }
        // Remove only empty source directories; failed deletions retain the old files.
        if (Directory.Exists(msgFrom))
            foreach (var dir in Directory.EnumerateDirectories(msgFrom, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length).Append(msgFrom))
                try { Directory.Delete(dir, false); } catch { }
    }

    private static bool IsInside(string path, string parent) =>
        path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLinkedParents(string path)
    {
        for (var dir = new DirectoryInfo(path); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Choose a mail folder outside linked folders.");
    }

    private static void DeleteOwned(string path, string parent)
    {
        var absolute = Path.GetFullPath(path);
        if (!IsInside(absolute, Path.GetFullPath(parent))) throw new IOException("Invalid mail move cleanup path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
        else if (File.Exists(absolute)) File.Delete(absolute);
    }
}
