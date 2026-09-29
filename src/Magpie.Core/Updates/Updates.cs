using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Magpie.Core.Updates;

/// <summary>A version like 1.1.0 or 1.2.0-beta.2 (SemVer order: a pre-release comes before its release).</summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    /// <summary>"" for a release, e.g. "beta.2" for a pre-release.</summary>
    public string Pre { get; }

    private AppVersion(int major, int minor, int patch, string pre) { Major = major; Minor = minor; Patch = patch; Pre = pre; }

    public bool IsPrerelease => Pre.Length > 0;

    /// <summary>Accepts "v1.1.0", "1.1", "1.1.0-beta.2", "1.1.0+abc123" (build metadata ignored).</summary>
    public static AppVersion? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text.Trim(), @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.\d+)?(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$");
        if (!m.Success) return null;
        return new AppVersion(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0, m.Groups[4].Success ? m.Groups[4].Value : "");
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre.Length == 0 || other.Pre.Length == 0) return (Pre.Length == 0 ? 1 : 0) - (other.Pre.Length == 0 ? 1 : 0);
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], out var ai);
            var bn = int.TryParse(b[i], out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre.Length > 0 ? "-" + Pre : "");
    public override bool Equals(object? obj) => obj is AppVersion v && CompareTo(v) == 0;
    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Pre);
}

/// <summary>One GitHub release that carries Magpie.exe and its SHA-256 file.</summary>
public sealed class ReleaseInfo
{
    public AppVersion Version { get; init; } = AppVersion.TryParse("0.0.0")!;
    public string Tag { get; init; } = "";
    public string Notes { get; init; } = "";
    public string PageUrl { get; init; } = "";
    public string ExeUrl { get; init; } = "";
    public string ShaUrl { get; init; } = "";
    public long ExeSize { get; init; }
    public DateTimeOffset? Published { get; init; }
}

/// <summary>Reads GitHub Releases and downloads a verified Magpie.exe (design U1).</summary>
public sealed class UpdateClient
{
    public const string Repo = "krishnabhunia/Magpie-email-client";
    public const string ExeAsset = "Magpie.exe";
    public const string ShaAsset = "Magpie.exe.sha256";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    private readonly HttpClient _http;

    public UpdateClient(HttpClient http) { _http = http; }

    /// <summary>The newest release newer than <paramref name="current"/>, or null when up to date.</summary>
    public async Task<ReleaseInfo?> FindNewerAsync(AppVersion current, bool includePrerelease, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=20");
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Magpie", current.ToString()));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var res = await _http.SendAsync(req, ct);
        if ((int)res.StatusCode == 403 || (int)res.StatusCode == 429)
            throw new InvalidOperationException("GitHub is limiting requests right now — Magpie will try again later.");
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        var best = PickNewest(ParseReleases(json), includePrerelease);
        return best != null && best.Version.CompareTo(current) > 0 ? best : null;
    }

    /// <summary>Releases (not drafts) that have both Magpie.exe and Magpie.exe.sha256 attached.</summary>
    public static List<(ReleaseInfo Info, bool Prerelease)> ParseReleases(string json)
    {
        var list = new List<(ReleaseInfo, bool)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (r.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var ver = AppVersion.TryParse(tag);
            if (ver == null) continue;
            string exe = "", sha = "";
            long size = 0;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                    if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) { exe = url; size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0; }
                    else if (string.Equals(name, ShaAsset, StringComparison.OrdinalIgnoreCase)) sha = url;
                }
            if (exe.Length == 0 || sha.Length == 0) continue;
            var pre = (r.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) || ver.IsPrerelease;
            list.Add((new ReleaseInfo
            {
                Version = ver, Tag = tag, ExeUrl = exe, ShaUrl = sha, ExeSize = size,
                Notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                PageUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage,
                Published = r.TryGetProperty("published_at", out var pa) && pa.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(pa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd) ? pd : null,
            }, pre));
        }
        return list;
    }

    public static ReleaseInfo? PickNewest(IEnumerable<(ReleaseInfo Info, bool Prerelease)> releases, bool includePrerelease) =>
        releases.Where(r => includePrerelease || !r.Prerelease).Select(r => r.Info).OrderByDescending(r => r.Version).FirstOrDefault();

    /// <summary>The 64-hex-digit hash in a .sha256 file ("HASH", "HASH  Magpie.exe", any case).</summary>
    public static string? ParseSha256(string text)
    {
        var m = Regex.Match(text ?? "", @"\b([0-9a-fA-F]{64})\b");
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>Downloads Magpie.exe into <paramref name="folder"/> and checks it against the published SHA-256.
    /// Returns the verified file's path; a file that fails the check is deleted.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, string folder, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var expected = ParseSha256(await _http.GetStringAsync(release.ShaUrl, ct))
                       ?? throw new InvalidOperationException("The release's checksum file is unreadable.");
        var final = Path.Combine(folder, $"Magpie-{release.Version}.exe");
        if (File.Exists(final) && await HashFileAsync(final, ct) == expected) { progress?.Report(1); return final; }
        var part = final + ".part";
        using (var res = await _http.GetAsync(release.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            var total = res.Content.Headers.ContentLength ?? release.ExeSize;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
        }
        var actual = await HashFileAsync(part, ct);
        if (actual != expected)
        {
            try { File.Delete(part); } catch { }
            throw new InvalidOperationException("The download didn't match its checksum and was thrown away. Try again later.");
        }
        File.Move(part, final, true);
        progress?.Report(1);
        return final;
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>
/// Replaces the running Magpie.exe with a downloaded one (design U1). Windows lets a running EXE be renamed
/// but not overwritten, so: rename the running file to Magpie.previous.exe (kept as the backup), copy the new
/// one into its place. <see cref="Rollback"/> puts the previous version back.
/// </summary>
/// <summary>
/// "Installed in the background" (design A1): the new EXE is already in place while the old one keeps running.
/// The first start of the new version finds this note, treats itself as just updated (the previous version is offered
/// back if it fails to start) and then removes it.
/// </summary>
public sealed record InstalledUpdate(string From, string To)
{
    public const string FileName = "installed.json";

    public static void Write(string folder, string from, string to)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(new InstalledUpdate(from, to)));
    }

    public static InstalledUpdate? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<InstalledUpdate>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static void Clear(string folder)
    {
        try { File.Delete(Path.Combine(folder, FileName)); } catch { }
    }
}

public static class SelfUpdate
{
    public const string BackupName = "Magpie.previous.exe";

    public static string BackupPathFor(string currentExe) => Path.Combine(Path.GetDirectoryName(currentExe)!, BackupName);

    /// <summary>True when Magpie can write next to its own EXE (not the case for an all-users install in Program Files).</summary>
    public static bool CanReplace(string currentExe)
    {
        try
        {
            var probe = Path.Combine(Path.GetDirectoryName(currentExe)!, $".magpie-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Swaps the files. On any failure the original EXE is back in place and the error is thrown.</summary>
    public static void Swap(string currentExe, string newExe)
    {
        var backup = BackupPathFor(currentExe);
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(currentExe, backup);
        try
        {
            File.Copy(newExe, currentExe, overwrite: false);
        }
        catch
        {
            try { if (File.Exists(currentExe)) File.Delete(currentExe); } catch { }
            File.Move(backup, currentExe);
            throw;
        }
    }

    /// <summary>Puts Magpie.previous.exe back as Magpie.exe (the failed new EXE is removed).</summary>
    public static bool Rollback(string currentExe)
    {
        var backup = BackupPathFor(currentExe);
        if (!File.Exists(backup)) return false;
        var failed = currentExe + ".failed";
        if (File.Exists(failed)) File.Delete(failed);
        var moved = false;
        if (File.Exists(currentExe)) { File.Move(currentExe, failed); moved = true; }   // may still be running: rename, don't delete
        try { File.Move(backup, currentExe); }
        catch
        {
            if (moved) try { File.Move(failed, currentExe); } catch { }   // never leave no Magpie.exe at all
            throw;
        }
        try { File.Delete(failed); } catch { }
        return true;
    }
}
