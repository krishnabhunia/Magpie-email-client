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
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
        if (to.StartsWith(from.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(Path.Combine(from, MessagesFolder))
            && to.StartsWith(Path.Combine(from, MessagesFolder), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Choose a folder outside Magpie's own message folder.");
        if (HasMail(to) && !replace) throw new InvalidOperationException("That folder already has Magpie mail.");
        Directory.CreateDirectory(to);

        var files = new List<(string src, string dst)>();
        foreach (var n in DbFiles)
            if (File.Exists(Path.Combine(from, n))) files.Add((Path.Combine(from, n), Path.Combine(to, n)));
        var msgFrom = Path.Combine(from, MessagesFolder);
        if (Directory.Exists(msgFrom))
            foreach (var f in Directory.EnumerateFiles(msgFrom, "*", SearchOption.AllDirectories))
                files.Add((f, Path.Combine(to, MessagesFolder, Path.GetRelativePath(msgFrom, f))));
        var total = files.Sum(f => new FileInfo(f.src).Length);

        if (replace)
        {
            foreach (var n in DbFiles) { var f = Path.Combine(to, n); if (File.Exists(f)) File.Delete(f); }
            var m = Path.Combine(to, MessagesFolder);
            if (Directory.Exists(m)) Directory.Delete(m, true);
        }

        long done = 0;
        var copied = new List<string>();
        try
        {
            foreach (var (src, dst) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
                copied.Add(dst);
                if (new FileInfo(dst).Length != new FileInfo(src).Length) throw new IOException("A file did not copy completely: " + dst);
                done += new FileInfo(src).Length;
                progress?.Report((done, total));
            }
        }
        catch
        {
            foreach (var c in copied) try { File.Delete(c); } catch { }
            throw;
        }

        foreach (var (src, _) in files) try { File.Delete(src); } catch (Exception ex) { Log.Warn("mail move: old file kept: " + ex.Message); }
        try { if (Directory.Exists(msgFrom)) Directory.Delete(msgFrom, true); } catch { }
    }
}
