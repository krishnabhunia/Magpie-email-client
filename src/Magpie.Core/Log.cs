using System.Text;

namespace Magpie.Core;

/// <summary>Tiny append-only file log in %APPDATA%\Magpie\magpie.log (rotated at 2 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; private set; }

    public static void Init(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "magpie.log");
        try
        {
            var fi = new FileInfo(FilePath);
            if (fi.Exists && fi.Length > 2 * 1024 * 1024)
            {
                var old = FilePath + ".1";
                if (File.Exists(old)) File.Delete(old);
                File.Move(FilePath, old);
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        for (var e = ex; e != null; e = e.InnerException)
        {
            sb.Append("\n  ").Append(e.GetType().FullName).Append(": ").Append(e.Message);
            if (e.StackTrace != null) sb.Append('\n').Append(e.StackTrace);
        }
        Write("ERROR", sb.ToString());
    }

    /// <summary>Removes secrets that might appear in protocol errors.</summary>
    public static string Redact(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)(bearer\s+|access_token=|refresh_token=|api[-_]?key[""':= ]+|sk-[a-z0-9_-]{4})[A-Za-z0-9._\-]+", "$1***");
        return s;
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {Redact(message)}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        if (FilePath == null) return;
        lock (Gate)
        {
            try { File.AppendAllText(FilePath, line); } catch { /* ignore */ }
        }
    }
}
