using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Magpie.Core.Updates;

namespace Magpie.Core.Info;

/// <summary>
/// Short test-version names (design BT1 option A, 5 Nov 2026): <c>x.y.z-betaNNN</c>, three digits, 001–999.
/// Older test versions are <c>x.y.z-beta.&lt;time&gt;.g&lt;sha&gt;</c> or <c>x.y.z-beta.N</c>; they still parse and
/// sort (below the short ones of the same x.y.z, see <see cref="AppVersion.CompareTo"/>), they just have no number.
/// </summary>
public static class BetaName
{
    private static readonly Regex Short = new(@"^beta(\d{3})$", RegexOptions.CultureInvariant);

    /// <summary>7 for 15.0.0-beta007; null for a release or an old long test version.</summary>
    public static int? Number(AppVersion version)
    {
        var m = Short.Match(version.Pre);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>"15.0.0" — the version without its test suffix.</summary>
    public static string Core(AppVersion version) => $"{version.Major}.{version.Minor}.{version.Patch}";
}

/// <summary>
/// What this copy of Magpie is (design IN1): its version and the details CI built into it as AssemblyMetadata
/// (<c>SourcePr</c>, <c>SourceCommit</c>, <c>BuildTimeUtc</c>, <c>ReleaseTag</c> from <c>Directory.Build.props</c>).
/// A local build has none of them and says "Local build".
/// </summary>
public sealed class BuildInfo
{
    public const string App = "Magpie";
    public const string LocalBuild = "Local build";
    public const string ReleasesUrl = "https://github.com/" + UpdateClient.Repo + "/releases";

    public AppVersion Version { get; }
    public int? SourcePr { get; }
    /// <summary>The full commit SHA (lower case), or "".</summary>
    public string SourceCommit { get; }
    public DateTimeOffset? BuiltUtc { get; }
    public string ReleaseTag { get; }
    public string ReleaseDate { get; }

    public BuildInfo(AppVersion version, int? sourcePr = null, string? sourceCommit = null, DateTimeOffset? builtUtc = null,
        string? releaseTag = null, string? releaseDate = null)
    {
        Version = version;
        SourcePr = sourcePr is > 0 ? sourcePr : null;
        SourceCommit = Regex.IsMatch(sourceCommit ?? "", "^[0-9a-fA-F]{7,40}$") ? sourceCommit!.ToLowerInvariant() : "";
        BuiltUtc = builtUtc?.ToUniversalTime();
        ReleaseTag = (releaseTag ?? "").Trim();
        ReleaseDate = (releaseDate ?? "").Trim();
    }

    /// <summary>Reads an assembly's InformationalVersion and the CI metadata (pass the app's own assembly).</summary>
    public static BuildInfo FromAssembly(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var meta = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First().Value ?? "");
        return From(info ?? assembly.GetName().Version?.ToString(3), key => meta.TryGetValue(key, out var v) ? v : null);
    }

    /// <summary>The same from plain values (tests, and the Mac/Windows apps when they already know the version).</summary>
    public static BuildInfo From(string? version, Func<string, string?> metadata)
    {
        var v = AppVersion.TryParse(version) ?? AppVersion.TryParse("0.0.0")!;
        int? pr = int.TryParse(metadata("SourcePr"), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        DateTimeOffset? built = DateTimeOffset.TryParse(metadata("BuildTimeUtc"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
        return new BuildInfo(v, pr, metadata("SourceCommit"), built, metadata("ReleaseTag"), metadata("ReleaseDate"));
    }

    public bool IsBeta => Version.IsPrerelease;
    public string Channel => IsBeta ? "Beta" : "Stable";
    public int? BetaNumber => BetaName.Number(Version);
    public string Core => BetaName.Core(Version);
    /// <summary>True when CI built nothing in (a build on a developer's PC).</summary>
    public bool IsLocal => SourceCommit.Length == 0 && BuiltUtc is null && ReleaseTag.Length == 0;
    public string ShortCommit => SourceCommit.Length >= 7 ? SourceCommit[..7] : SourceCommit;
    public string? PrUrl => SourcePr is { } pr ? $"https://github.com/{UpdateClient.Repo}/pull/{pr}" : null;
    public string? CommitUrl => SourceCommit.Length == 40 ? $"https://github.com/{UpdateClient.Repo}/commit/{SourceCommit}" : null;
    public string? TagUrl => ReleaseTag.Length > 0 ? $"{ReleasesUrl}/tag/{ReleaseTag}" : null;
    /// <summary>"v15.0.0-beta007" — what the main window shows.</summary>
    public string VersionLabel => "v" + Version;
    /// <summary>The release download (Krishna's naming rule: [App]_[version]).</summary>
    public string DownloadName => $"{App}_{Version}.zip";

    /// <summary>"Version 15.0.0-beta007 · Beta · Windows".</summary>
    public string Subtitle(string platform) => $"Version {Version} · {Channel} · {platform}";

    /// <summary>The beta banner on This build, or null for a release.</summary>
    public string? Banner => !IsBeta ? null
        : BetaNumber is { } n ? $"This is a test build. It is beta {n:000} of {App} {Core}, made from a pull request."
        : $"This is a test build of {App} {Core}, made from a pull request.";

    /// <summary>"04-Nov-2026, 19:55" in the given time zone (the PC's own by default), or "Local build".</summary>
    public string BuiltOn(TimeZoneInfo? zone = null) => BuiltUtc is { } t
        ? TimeZoneInfo.ConvertTime(t, zone ?? TimeZoneInfo.Local).ToString("dd-MMM-yyyy, HH:mm", CultureInfo.InvariantCulture)
        : LocalBuild;

    /// <summary>The "This build" table (SrNo. is the row's place). PR and Installed as only when known.</summary>
    public IReadOnlyList<InfoRow> Rows(string? installedAs, TimeZoneInfo? zone = null)
    {
        var rows = new List<InfoRow>
        {
            new("Version", Version.ToString()),
            new("Channel", Channel),
        };
        if (SourcePr is { } pr) rows.Add(new("Made from", $"PR #{pr}", PrUrl));
        rows.Add(new("Commit", ShortCommit.Length > 0 ? ShortCommit : LocalBuild, CommitUrl));
        rows.Add(new("Built on", BuiltOn(zone)));
        rows.Add(new("GitHub tag", ReleaseTag.Length > 0 ? ReleaseTag : LocalBuild, TagUrl));
        rows.Add(new("Download", DownloadName));
        if (!string.IsNullOrWhiteSpace(installedAs)) rows.Add(new("Installed as", installedAs!));
        return rows;
    }

    /// <summary>
    /// "Copy build info", line 1: <c>Magpie 15.0.0-beta001 (Beta) · PR #35 · 4e68c2a · built 2026-11-04 14:25 UTC</c>.
    /// </summary>
    public string SummaryLine()
    {
        var parts = new List<string> { $"{App} {Version} ({Channel})" };
        if (SourcePr is { } pr) parts.Add($"PR #{pr}");
        if (ShortCommit.Length > 0) parts.Add(ShortCommit);
        parts.Add(BuiltUtc is { } t ? "built " + t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "local build");
        return string.Join(" · ", parts);
    }

    /// <summary>Both lines: the summary, then <paramref name="systemLine"/> (see <see cref="SystemInfo.Line"/>).</summary>
    public string CopyText(string systemLine) => SummaryLine() + Environment.NewLine + systemLine;
}

/// <summary>One row of an Info table: the item, what this copy has, and an optional link.</summary>
public sealed record InfoRow(string Item, string Value, string? Link = null);

/// <summary>The second "Copy build info" line, e.g. <c>Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8</c>.</summary>
public static class SystemInfo
{
    /// <summary>"installer, per-user" / "installer, all users" / "portable" / "app in Applications" for an install kind.</summary>
    public static string InstalledAs(string? installKind) => installKind switch
    {
        UpdateInstallKinds.WindowsPortable => "portable",
        UpdateInstallKinds.WindowsPerUser => "installer, per-user",
        UpdateInstallKinds.WindowsAllUsers => "installer, all users",
        UpdateInstallKinds.MacApp => "app (Magpie.app)",
        _ => "",
    };

    /// <summary>
    /// "Windows 11 Pro 24H2" from the registry's ProductName / DisplayVersion. Windows 11 still says "Windows 10"
    /// in ProductName; build 22000 and later is Windows 11.
    /// </summary>
    public static string WindowsName(string? productName, string? displayVersion, int build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? "Windows" : productName!.Trim();
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase)) name = "Windows 11" + name[10..];
        return string.IsNullOrWhiteSpace(displayVersion) ? name : name + " " + displayVersion!.Trim();
    }

    /// <summary>"x64", "arm64" for the process architecture.</summary>
    public static string Arch(System.Runtime.InteropServices.Architecture a) => a switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x64",
        System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
        System.Runtime.InteropServices.Architecture.X86 => "x86",
        _ => a.ToString().ToLowerInvariant(),
    };

    /// <summary>"Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8".</summary>
    public static string Line(string os, string arch, string? installedAs, Version runtime)
    {
        var parts = new List<string> { (os + " " + arch).Trim() };
        if (!string.IsNullOrWhiteSpace(installedAs)) parts.Add(installedAs!);
        parts.Add($".NET {runtime.Major}");
        return string.Join(" · ", parts);
    }
}
