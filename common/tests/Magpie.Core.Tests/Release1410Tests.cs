using Magpie.Core.Updates;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>14.1.0 — version picker look (design VP1, Q90): confirm screen texts and versions kept on this PC.</summary>
public sealed class Release1410Tests
{
    private static AppVersion V(string version) => AppVersion.TryParse(version)!;

    private static VersionChoice Choice(string version, bool compatible = true, bool available = true)
    {
        var parsed = V(version);
        var prefix = $"https://github.com/{UpdateClient.Repo}/releases/download/v{version}/";
        var release = new ReleaseInfo
        {
            Version = parsed,
            Tag = "v" + version,
            ExeUrl = available ? prefix + "Magpie.exe" : "",
            ShaUrl = available ? prefix + "Magpie.exe.sha256" : "",
        };
        return new VersionChoice(release, parsed.IsPrerelease, new CompatibilityResult(compatible && available, compatible ? "Compatible." : "Blocked."));
    }

    private static readonly VersionChoice[] Catalogue =
    [
        Choice("15.0.0-beta.2"), Choice("14.2.0"), Choice("14.1.1", compatible: false), Choice("14.1.0"),
        Choice("14.1.0-beta.4"), Choice("13.0.2"), Choice("13.0.0"),
    ];

    private static VersionChoice Pick(string version) => Catalogue.Single(c => c.Release.Version.ToString() == version);

    // ───────────── confirm screen ─────────────

    [Fact]
    public void A_beta_that_passes_a_newer_stable_says_which_stable_it_skips()
    {
        var skipped = VersionPicker.SkippedStable(Pick("15.0.0-beta.2"), Catalogue, V("14.0.0"));
        Assert.Equal("14.2.0", skipped!.Version.ToString());
        Assert.Contains("skips the latest stable (v14.2.0)", VersionPicker.Warning(Pick("15.0.0-beta.2"), Catalogue, V("14.0.0")));
    }

    [Fact]
    public void An_incompatible_stable_is_not_counted_as_skipped()
    {
        Assert.Equal("14.1.0", VersionPicker.SkippedStable(Pick("15.0.0-beta.2"), Catalogue.Where(c => c.Release.Version.ToString() != "14.2.0"), V("14.0.0"))!.Version.ToString());
        Assert.Null(VersionPicker.SkippedStable(Pick("14.1.0-beta.4"), Catalogue, V("14.0.0"))); // no stable between 14.0.0 and that beta
    }

    [Fact]
    public void A_stable_update_needs_no_warning_and_a_roll_back_always_gets_one()
    {
        Assert.Null(VersionPicker.Warning(Pick("14.2.0"), Catalogue, V("14.0.0")));
        Assert.Contains("going back to an older version", VersionPicker.Warning(Pick("13.0.2"), Catalogue, V("14.0.0")));
        Assert.StartsWith("You are moving to a beta. Beta builds", VersionPicker.Warning(Pick("14.1.0-beta.4"), Catalogue, V("14.0.0")));
    }

    [Fact]
    public void Steps_follow_the_real_order_and_name_the_exact_versions()
    {
        var steps = VersionPicker.Steps(V("14.0.0"), Pick("13.0.2"), onThisPc: false);
        Assert.Equal(5, steps.Count);
        Assert.Contains("v13.0.2 works with your mail", steps[0]);
        Assert.StartsWith("Download v13.0.2", steps[1]);
        Assert.Contains("recovery copy of v14.0.0", steps[2]);
        Assert.Contains("install exactly v13.0.2", steps[3]);
        Assert.StartsWith("Pin v13.0.2", steps[4]);
        Assert.Contains("saved on this PC", VersionPicker.Steps(V("14.0.0"), Pick("13.0.2"), onThisPc: true)[1]);
    }

    [Theory]
    [InlineData("14.0.0", "14.2.0", "Update to v14.2.0")]
    [InlineData("14.0.0", "13.0.2", "Roll back to v13.0.2")]
    [InlineData("14.1.0", "14.1.0-beta.4", "Roll back to v14.1.0-beta.4")]
    public void The_action_button_names_the_direction_and_version(string current, string target, string text) =>
        Assert.Equal(text, VersionPicker.ActionText(V(current), V(target)));

    // ───────────── versions kept on this PC ─────────────

    private static AppPaths Profile(TempDir dir)
    {
        var paths = new AppPaths(dir.File("profile"));
        File.WriteAllText(paths.Settings, "{\"SchemaVersion\":1}");
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString());
        c.Open();
        using var command = c.CreateCommand();
        command.CommandText = "CREATE TABLE drafts(text TEXT); PRAGMA user_version=11;";
        command.ExecuteNonQuery();
        return paths;
    }

    [Fact]
    public void Kept_versions_are_found_for_this_installation_type_only()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe");
        File.WriteAllText(exe, "13.0.2 bytes");
        VersionBackup.Create(paths, V("13.0.2"), exe, default, UpdateInstallKinds.WindowsPerUser);
        File.WriteAllText(exe, "14.1.0 beta bytes");
        VersionBackup.Create(paths, V("14.1.0-beta.4"), exe, default, UpdateInstallKinds.WindowsPortable);

        Assert.Equal(new[] { "13.0.2" }, KeptVersions.Versions(paths, UpdateInstallKinds.WindowsPerUser).ToArray());
        Assert.Equal("13.0.2 bytes", File.ReadAllText(KeptVersions.Find(paths, V("13.0.2"), UpdateInstallKinds.WindowsPerUser)!));
        Assert.Null(KeptVersions.Find(paths, V("14.1.0-beta.4"), UpdateInstallKinds.WindowsPerUser));
        Assert.Null(KeptVersions.Find(paths, V("14.1.0"), UpdateInstallKinds.WindowsPortable)); // the beta is not its stable
        Assert.NotNull(KeptVersions.Find(paths, V("14.1.0-beta.4"), UpdateInstallKinds.WindowsPortable));
    }

    [Fact]
    public void An_unreadable_backup_record_leaves_nothing_kept_instead_of_failing()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe");
        File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("13.0.2"), exe, default, UpdateInstallKinds.WindowsPerUser);
        foreach (var json in Directory.GetFiles(snapshot.Folder, "*.json")) File.WriteAllText(json, "{ broken");

        Assert.Empty(KeptVersions.Versions(paths, UpdateInstallKinds.WindowsPerUser));
        Assert.Null(KeptVersions.Find(paths, V("13.0.2"), UpdateInstallKinds.WindowsPerUser));
    }

    [Fact]
    public void Seeding_puts_the_kept_copy_where_the_checked_download_looks_and_never_overwrites()
    {
        using var dir = new TempDir();
        var kept = dir.File("kept.exe");
        File.WriteAllText(kept, "kept bytes");
        var target = Path.Combine(dir.Path, "updates", "Magpie-13.0.2.exe");

        Assert.True(KeptVersions.Seed(kept, target));
        Assert.Equal("kept bytes", File.ReadAllText(target));

        File.WriteAllText(target, "a download already here");
        Assert.False(KeptVersions.Seed(kept, target));
        Assert.Equal("a download already here", File.ReadAllText(target));
        Assert.False(KeptVersions.Seed(null, target));
        Assert.False(KeptVersions.Seed(dir.File("missing.exe"), dir.File("other.exe")));
    }

    [Fact]
    public async Task A_seeded_copy_that_does_not_match_the_release_is_downloaded_again()
    {
        using var dir = new TempDir();
        var good = System.Text.Encoding.UTF8.GetBytes("the real 13.0.2");
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(good)).ToLowerInvariant();
        var release = Pick("13.0.2").Release;
        var http = new HttpClient(new Files(new()
        {
            [release.ShaUrl] = System.Text.Encoding.UTF8.GetBytes(sha + "  Magpie.exe"),
            [release.ExeUrl] = good,
        }));
        var client = new UpdateClient(http);
        var folder = Path.Combine(dir.Path, "updates");
        var kept = dir.File("kept.exe");
        File.WriteAllText(kept, "an older build of something else");
        KeptVersions.Seed(kept, Path.Combine(folder, client.Asset.LocalFileFor(release.Version)));

        var (path, checkedSha) = await client.DownloadCheckedAsync(release, folder, null, CancellationToken.None);

        Assert.Equal(sha, checkedSha);
        Assert.Equal(good, File.ReadAllBytes(path));
    }

    private sealed class Files(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }
}
