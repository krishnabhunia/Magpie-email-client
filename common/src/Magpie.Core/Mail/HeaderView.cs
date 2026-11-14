using System.Globalization;
using System.Text.RegularExpressions;
using Magpie.Core.Models;
using MimeKit;
using MimeKit.Utils;

namespace Magpie.Core.Mail;

/// <summary>One header line of an email, as it is shown: its name and its value on one line.</summary>
public sealed record HeaderLine(string Name, string Value);

/// <summary>One check the receiving mail server wrote into the email (SPF, DKIM or DMARC) and what it found.</summary>
public sealed record HeaderCheck(string Name, string Result)
{
    public bool Passed => Result.Equals("pass", StringComparison.OrdinalIgnoreCase);

    /// <summary>"SPF passed", "DKIM failed", or the server's own word for anything in between ("SPF: softfail").</summary>
    public string Label => Passed ? Name + " passed"
        : Result.Equals("fail", StringComparison.OrdinalIgnoreCase) ? Name + " failed"
        : $"{Name}: {Result}";
}

/// <summary>
/// Design HD1 (option A): everything the Headers window shows and says — the lines, the find box, the plain text,
/// the mail server's checks and how the email travelled. The window only lays it out.
/// </summary>
public static partial class HeaderView
{
    public const string Title = "Email headers";
    public const string Loading = "Getting the headers…";
    public const string Failed = "Could not get the headers. Check the connection and try again.";
    public const string Gone = "This email is no longer on the mail server, so its headers cannot be read.";
    public const string ChecksLabel = "Checked by your mail server:";
    public const string Copied = "Copied all headers";

    /// <summary>The header lines in the order the email carries them; a folded value becomes one line.</summary>
    public static List<HeaderLine> Lines(IEnumerable<Header> headers) =>
        headers.Select(h => new HeaderLine(h.Field, OneLine(h.Value))).Where(l => l.Name.Length > 0).ToList();

    /// <summary>
    /// The header block of a message file, complete and in its own order. Read straight from the file: a loaded
    /// <see cref="MimeMessage"/> keeps the Content-* lines on its body, so its own header list would miss them.
    /// </summary>
    public static async Task<List<HeaderLine>> ReadFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return Lines(await new MimeParser(stream, MimeFormat.Entity).ParseHeadersAsync(ct));
    }

    private static string OneLine(string? value) => Spaces().Replace(value ?? "", " ").Trim();

    /// <summary>"Name: value", one header per line (joined with "\n"; <see cref="AddressCopy.ForClipboard"/> for Windows).</summary>
    public static string PlainText(IEnumerable<HeaderLine> lines) => string.Join("\n", lines.Select(l => $"{l.Name}: {l.Value}"));

    /// <summary>The lines whose name or value holds the text, in any letter case; every line for an empty box.</summary>
    public static List<HeaderLine> Filter(IReadOnlyList<HeaderLine> lines, string? text)
    {
        var find = (text ?? "").Trim();
        if (find.Length == 0) return lines.ToList();
        return lines.Where(l => l.Name.Contains(find, StringComparison.OrdinalIgnoreCase) || l.Value.Contains(find, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// SPF, DKIM and DMARC as the receiving server recorded them: only the topmost Authentication-Results line counts,
    /// because lines further down were written before the email reached that server and can be made up by the sender.
    /// </summary>
    public static List<HeaderCheck> Checks(IEnumerable<HeaderLine> lines)
    {
        var first = lines.FirstOrDefault(l => l.Name.Equals("Authentication-Results", StringComparison.OrdinalIgnoreCase));
        var found = new List<HeaderCheck>();
        if (first == null) return found;
        foreach (var name in new[] { "SPF", "DKIM", "DMARC" })
        {
            var m = Regex.Match(first.Value, $@"(?<![\w.-]){name}\s*=\s*([a-z]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (m.Success) found.Add(new HeaderCheck(name, m.Groups[1].Value.ToLowerInvariant()));
        }
        return found;
    }

    /// <summary>How many mail servers handled the email: one Received line each.</summary>
    public static int Hops(IEnumerable<HeaderLine> lines) => lines.Count(l => l.Name.Equals("Received", StringComparison.OrdinalIgnoreCase));

    /// <summary>From the first server's time stamp to the last one's; null when fewer than two can be read.</summary>
    public static TimeSpan? TravelTime(IEnumerable<HeaderLine> lines)
    {
        var times = new List<DateTimeOffset>();
        foreach (var l in lines.Where(l => l.Name.Equals("Received", StringComparison.OrdinalIgnoreCase)))
        {
            var at = l.Value.LastIndexOf(';');
            if (at >= 0 && DateUtils.TryParse(l.Value[(at + 1)..], out var when)) times.Add(when);
        }
        if (times.Count < 2) return null;
        var took = times.Max() - times.Min();
        return took < TimeSpan.Zero ? null : took;
    }

    /// <summary>"Passed through 3 servers in 7 seconds"; "" when the email names no server.</summary>
    public static string HopsLine(IReadOnlyList<HeaderLine> lines)
    {
        var hops = Hops(lines);
        if (hops == 0) return "";
        var text = $"Passed through {hops} server{(hops == 1 ? "" : "s")}";
        return TravelTime(lines) is { } took ? $"{text} in {Duration(took)}" : text;
    }

    internal static string Duration(TimeSpan t)
    {
        static string Unit(long n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";
        if (t.TotalSeconds < 1) return "under a second";
        if (t.TotalSeconds < 60) return Unit((long)t.TotalSeconds, "second");
        if (t.TotalMinutes < 60) return Unit((long)t.TotalMinutes, "minute");
        if (t.TotalHours < 48) return Unit((long)t.TotalHours, "hour");
        return Unit((long)t.TotalDays, "day");
    }

    /// <summary>The window's first line: the subject.</summary>
    public static string Subject(MessageRow row) => string.IsNullOrWhiteSpace(row.Subject) ? "(no subject)" : row.Subject.Trim();

    /// <summary>"From Rajesh Mehta · Friday, 13 November 2026, 21:04".</summary>
    public static string SubTitle(MessageRow row)
    {
        var from = !string.IsNullOrWhiteSpace(row.FromName) ? row.FromName.Trim() : row.FromAddress;
        var date = row.Date.ToLocalTime().ToString("dddd, d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(from) ? date : $"From {from} · {date}";
    }

    /// <summary>"15 header lines", or "3 of 15 header lines" while the find box narrows them.</summary>
    public static string CountLine(int shown, int total) =>
        shown == total ? $"{total} header line{(total == 1 ? "" : "s")}" : $"{shown} of {total} header lines";

    /// <summary>A file name for Save: "Headers - Handover schedule.txt", without characters Windows refuses.</summary>
    public static string FileName(MessageRow row)
    {
        var subject = string.Concat(Subject(row).Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? ' ' : c));
        subject = Spaces().Replace(subject, " ").Trim().TrimEnd('.');
        if (subject.Length > 80) subject = subject[..80].Trim();
        return subject.Length == 0 ? "Headers.txt" : $"Headers - {subject}.txt";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
