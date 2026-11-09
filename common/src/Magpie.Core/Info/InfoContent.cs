using System.Reflection;
using System.Text.RegularExpressions;
using Magpie.Core.Updates;

namespace Magpie.Core.Info;

/// <summary>One row of docs/FEATURES.md (design IN1). <see cref="Windows"/> / <see cref="Mac"/> are "Yes" or "Not yet".</summary>
public sealed record FeatureRow(int SrNo, string Area, string What, string Since, string Windows, string Mac)
{
    public bool OnWindows => Windows.Equals("Yes", StringComparison.OrdinalIgnoreCase);
    public bool OnMac => Mac.Equals("Yes", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The filter chips of "What's implemented".</summary>
public enum FeatureFilter { All, NewInThisVersion, WindowsOnly, AlsoOnMac }

/// <summary>docs/FEATURES.md, built into Magpie.Core at build time. Same rules as common/scripts/features_check.py.</summary>
public static class FeatureList
{
    public static readonly string[] Header = ["SrNo.", "Area", "What it does", "Since", "Windows", "Mac"];

    /// <summary>The table's rows; rows that don't fit the table are skipped (CI keeps the file right).</summary>
    public static IReadOnlyList<FeatureRow> Parse(string? markdown)
    {
        var rows = new List<FeatureRow>();
        var inTable = false;
        foreach (var raw in (markdown ?? "").Split('\n'))
        {
            var cells = Cells(raw);
            if (cells is null) { if (inTable && raw.Trim().Length > 0) break; continue; }
            if (!inTable) { inTable = cells.SequenceEqual(Header); continue; }
            if (cells.Length != Header.Length || cells.All(c => Regex.IsMatch(c, "^:?-{3,}:?$"))) continue;
            if (!int.TryParse(cells[0], out var n)) continue;
            rows.Add(new FeatureRow(n, Plain(cells[1]), Plain(cells[2]), cells[3], cells[4], cells[5]));
        }
        return rows;
    }

    /// <summary>Rows matching the search words (all of them, any column) and the chip.</summary>
    public static IReadOnlyList<FeatureRow> Filter(IEnumerable<FeatureRow> rows, string? search, FeatureFilter filter, string currentCore)
    {
        var words = (search ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return rows.Where(r => filter switch
            {
                FeatureFilter.NewInThisVersion => r.Since == currentCore,
                FeatureFilter.WindowsOnly => r.OnWindows && !r.OnMac,
                FeatureFilter.AlsoOnMac => r.OnMac,
                _ => true,
            })
            .Where(r => words.All(w => $"{r.Area} {r.What} {r.Since}".Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>"New in 15.0.0" for the chip.</summary>
    public static string NewInLabel(string core) => "New in " + core;

    private static string[]? Cells(string line)
    {
        var s = line.Trim();
        if (s.Length < 2 || s[0] != '|' || s[^1] != '|') return null;
        return s[1..^1].Split('|').Select(c => c.Trim()).ToArray();
    }

    /// <summary>Markdown marks out: **bold**, `code`.</summary>
    internal static string Plain(string text) => text.Replace("**", "").Replace("`", "");
}

/// <summary>One CHANGELOG section for "What's new": its heading and the lines under New / Changed / Fixed.</summary>
public sealed record ChangelogSection(string Version, string Date, IReadOnlyList<ChangelogGroup> Groups)
{
    public bool IsEmpty => Groups.All(g => g.Lines.Count == 0);
}

/// <summary>"New" with its lines; <see cref="Kind"/> is "Changes" for old sections without headings.</summary>
public sealed record ChangelogGroup(string Kind, IReadOnlyList<string> Lines);

/// <summary>CHANGELOG.md, built into Magpie.Core at build time.</summary>
public static class ChangelogNotes
{
    /// <summary>
    /// The section of <paramref name="core"/> ("## 15.0.0 (5 Nov 2026)"), or else the "Next version (not released
    /// yet)" section, or null. Nested lines ("  - …") are kept, indented with "· ". HTML comments are dropped.
    /// </summary>
    public static ChangelogSection? Section(string? changelog, string core)
    {
        var text = Regex.Replace((changelog ?? "").Replace("\r", ""), "<!--.*?-->", "", RegexOptions.Singleline);
        var lines = text.Split('\n');
        int start = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^## " + Regex.Escape(core) + @"( |$)"));
        if (start < 0) start = Array.FindIndex(lines, l => l.StartsWith("## Next version", StringComparison.Ordinal));
        if (start < 0) return null;
        var head = Regex.Match(lines[start], @"^## (\S+)(?:.*\((.*)\))?");
        var version = head.Groups[1].Value == "Next" ? core : head.Groups[1].Value;
        var date = head.Groups[2].Success && !head.Groups[2].Value.Contains("not released", StringComparison.Ordinal) ? head.Groups[2].Value : "";
        var groups = new List<(string Kind, List<string> Lines)>();
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            var line = lines[i];
            var kind = Regex.Match(line, @"^###\s+(.+?)\s*$");
            if (kind.Success) { groups.Add((kind.Groups[1].Value, new List<string>())); continue; }
            var item = Regex.Match(line, @"^(\s*)[-*]\s+(.*)$");
            if (item.Success)
            {
                if (groups.Count == 0) groups.Add(("Changes", new List<string>()));
                var nested = item.Groups[1].Value.Length >= 2;
                groups[^1].Lines.Add((nested ? "· " : "") + FeatureList.Plain(item.Groups[2].Value.Trim()));
            }
            else if (line.Trim().Length > 0 && groups.Count > 0 && groups[^1].Lines.Count > 0)
                groups[^1].Lines[^1] += " " + FeatureList.Plain(line.Trim());   // a wrapped line
        }
        return new ChangelogSection(version, date, groups.Select(g => new ChangelogGroup(g.Kind, g.Lines)).ToList());
    }

    /// <summary>"15.0.0 · beta 007 · being tested" for a test version, "15.0.0 · released 5 Nov 2026" for a release.</summary>
    public static string Heading(BuildInfo build, ChangelogSection? section)
    {
        if (build.IsBeta)
            return build.BetaNumber is { } n ? $"{build.Core} · beta {n:000} · being tested" : $"{build.Core} · test version · being tested";
        return section is { Date.Length: > 0 } ? $"{build.Core} · released {section.Date}" : build.Core;
    }
}

/// <summary>The two documents built into Magpie.Core (docs/FEATURES.md, CHANGELOG.md).</summary>
public static class InfoContent
{
    public static string Features => Read("Magpie.FEATURES.md");
    public static string Changelog => Read("Magpie.CHANGELOG.md");

    private static string Read(string name)
    {
        using var stream = typeof(InfoContent).Assembly.GetManifestResourceStream(name);
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>"Under the hood": what this copy is made with. The apps pass what only they know.</summary>
public static class UnderTheHood
{
    /// <param name="mac">The Mac app (Avalonia, WKWebView, Keychain) instead of Windows (WPF, WebView2, DPAPI).</param>
    /// <param name="uiLibraries">e.g. "WPF · CommunityToolkit.Mvvm 8.4" — the app reads its own assemblies.</param>
    /// <param name="dataFolder">Where mail, settings and the log are.</param>
    /// <param name="testVersions">Settings → Updates → Include test versions.</param>
    /// <param name="theme">"Match Windows (dark now)" and the like.</param>
    /// <param name="fonts">Bundled fonts, or null when the app uses the system's.</param>
    public static IReadOnlyList<InfoRow> Rows(bool mac, string uiLibraries, string dataFolder, bool testVersions, string theme, string? fonts)
    {
        var runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        return new List<InfoRow>
        {
            new("App", $"C# · {runtime} · shared mail engine Magpie.Core (.NET 8)"),
            new("Window toolkit", uiLibraries),
            new("Showing emails", mac ? "WKWebView, the Mac's own web view (NativeWebView), scripts removed (HtmlSanitizer "
                + Ver(typeof(Ganss.Xss.HtmlSanitizer)) + ")" : "Microsoft Edge WebView2, scripts removed (HtmlSanitizer " + Ver(typeof(Ganss.Xss.HtmlSanitizer)) + ")"),
            new("Mail", "MailKit " + Ver(typeof(MailKit.Net.Imap.ImapClient)) + " (IMAP, SMTP, Google and Microsoft sign-in)"),
            new("Storage", "SQLite with full-text search (Microsoft.Data.Sqlite " + Ver(typeof(Microsoft.Data.Sqlite.SqliteConnection))
                + "), kept on this " + (mac ? "Mac" : "PC") + ": " + dataFolder),
            new("Sync", "Local-first: changes are saved here at once and sent to the server in the background"),
            new("Passwords and keys", mac ? "Encrypted (AES-256-GCM) with a key in your login Keychain" : "Encrypted with your Windows account (DPAPI)"),
            new("Updates", "GitHub Releases (" + UpdateClient.Repo + ") · test versions " + (testVersions ? "on" : "off"), BuildInfo.ReleasesUrl),
            new("Design / theme", theme),
            new("Fonts", fonts ?? "The system's own fonts"),
        };
    }

    private static string Ver(Type t)
    {
        var info = t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var parsed = AppVersion.TryParse(info);
        return parsed?.ToString() ?? t.Assembly.GetName().Version?.ToString(3) ?? "";
    }
}
