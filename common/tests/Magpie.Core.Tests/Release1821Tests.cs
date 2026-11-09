using System.Net;
using System.Text;
using Magpie.Core.Settings;
using Magpie.Core.Updates;

namespace Magpie.Core.Tests;

/// <summary>18.2.1: updating to a newer version keeps update checks running; the Mac finds a stable release behind many betas.</summary>
public sealed class Release1821Tests
{
    private static AppVersion V(string v) => AppVersion.TryParse(v)!;

    private static SettingsStore Store(string path, bool autoUpdate)
    {
        var settings = new SettingsStore(path);
        settings.Load();
        settings.Current.Updates.AutoUpdate = settings.Current.Updates.AutoCheck = settings.Current.Updates.AutoDownload = autoUpdate;
        settings.Save(notify: false);
        return settings;
    }

    private static bool? SavedAutoUpdate(string path) => new SettingsStore(path).Load().Updates.AutoUpdate;

    private static UpdateHistoryEntry Entry(string action, string from, string to, string status, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Action = action, From = from, To = to, Status = status,
        Created = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), Updated = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };

    [Theory]
    [InlineData("17.0.0", "16.1.0", true)]           // rollback
    [InlineData("17.0.0", "17.0.0-beta003", true)]   // a beta of the same version is older
    [InlineData("16.1.0", "17.0.0", false)]          // update
    [InlineData("17.0.0-beta002", "17.0.0", false)]
    public void Only_a_rollback_pins_the_selected_version(string current, string selected, bool pins) =>
        Assert.Equal(pins, VersionPin.PinsAfterChange(V(current), V(selected)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Updating_to_a_newer_version_keeps_the_users_auto_update_choice_and_writes_no_pin(bool autoUpdate)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = Store(path, autoUpdate);
        using (var handoff = VersionPin.PrepareForward(settings, pin)) handoff.Commit();
        Assert.False(File.Exists(pin));
        Assert.Equal(autoUpdate, SavedAutoUpdate(path));
    }

    [Fact]
    public void Updating_after_a_rollback_ends_the_rollback_pin_and_its_paused_checks()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "16.1.0");
        var settings = Store(path, autoUpdate: false);
        using (var handoff = VersionPin.PrepareForward(settings, pin))
        {
            Assert.False(File.Exists(pin));
            Assert.True(SavedAutoUpdate(path));
            handoff.Commit();
        }
        Assert.False(File.Exists(pin));
        Assert.True(SavedAutoUpdate(path));
    }

    [Fact]
    public void A_failed_forward_handoff_puts_the_rollback_pin_and_switches_back()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "16.1.0");
        var settings = Store(path, autoUpdate: false);
        using (VersionPin.PrepareForward(settings, pin)) { }   // not committed: the new version never started
        Assert.Equal("16.1.0", File.ReadAllText(pin));
        Assert.False(SavedAutoUpdate(path));
        var s = new SettingsStore(path).Load().Updates;
        Assert.False(s.AutoCheck);
        Assert.False(s.AutoDownload);
    }

    [Fact]
    public void A_copy_left_pinned_by_an_earlier_update_gets_its_checks_back_once()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "17.0.0");
        var settings = Store(path, autoUpdate: false);   // what 17.0.0 and older left after "Update to v17.0.0"
        var history = new[]
        {
            Entry("Rollback", "16.1.0", "15.0.0", UpdateHistory.Succeeded, minutesAgo: 90),
            Entry("Update", "16.1.0", "17.0.0", UpdateHistory.Succeeded, minutesAgo: 5),
        };
        Assert.True(VersionPin.ReleaseAfterForwardMove(settings, pin, V("17.0.0"), history));
        Assert.False(File.Exists(pin));
        var s = new SettingsStore(path).Load().Updates;
        Assert.True(s.AutoUpdate);
        Assert.True(s.AutoCheck);
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("17.0.0"), history));   // nothing left to do
    }

    [Theory]
    [InlineData("Rollback", "17.0.0", UpdateHistory.Succeeded)]   // went back on purpose
    [InlineData("Restore", "17.0.0", UpdateHistory.Restored)]     // a restored backup
    [InlineData("Update", "16.1.0", UpdateHistory.Failed)]        // never started
    [InlineData("Update", "16.1.0", UpdateHistory.Installing)]    // not started yet
    public void A_pin_from_a_rollback_or_restore_is_kept(string action, string from, string status)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "16.1.0");
        var settings = Store(path, autoUpdate: false);
        var history = new[] { Entry(action, from, "16.1.0", status) };
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("16.1.0"), history));
        Assert.Equal("16.1.0", File.ReadAllText(pin));
        Assert.False(SavedAutoUpdate(path));
    }

    [Fact]
    public void The_newest_transition_into_the_running_version_decides()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "16.1.0");
        var settings = Store(path, autoUpdate: false);
        var history = new[]
        {
            Entry("Update", "16.0.0", "16.1.0", UpdateHistory.Succeeded, minutesAgo: 60),
            Entry("Rollback", "17.0.0", "16.1.0", UpdateHistory.Succeeded, minutesAgo: 2),
        };
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("16.1.0"), history));
        Assert.True(File.Exists(pin));
    }

    [Fact]
    public void A_rollback_not_yet_marked_successful_still_beats_an_older_update()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "17.0.1");
        var settings = Store(path, autoUpdate: false);
        var history = new[]
        {
            Entry("Update", "17.0.0", "17.0.1", UpdateHistory.Succeeded, minutesAgo: 600),
            Entry("Update", "17.0.1", "17.2.0", UpdateHistory.Succeeded, minutesAgo: 300),
            Entry("Rollback", "17.2.0", "17.0.1", UpdateHistory.Installing, minutesAgo: 1),   // its status update failed
        };
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("17.0.1"), history));
        Assert.Equal("17.0.1", File.ReadAllText(pin));
        Assert.False(SavedAutoUpdate(path));
    }

    [Fact]
    public void An_unreadable_history_entry_leaves_the_pin_alone()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "17.0.0");
        var settings = Store(path, autoUpdate: false);
        var history = new[]
        {
            Entry("Update", "16.1.0", "17.0.0", UpdateHistory.Succeeded, minutesAgo: 60),
            Entry("History record", "", "", UpdateHistory.Failed),   // what UpdateHistory.List returns for a broken file
        };
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("17.0.0"), history));
        Assert.True(File.Exists(pin));
    }

    [Fact]
    public void A_pin_older_than_the_running_version_is_stale()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        File.WriteAllText(pin, "15.0.0");   // rolled back to 15.0.0, then the 17.0.0 installer was run
        var settings = Store(path, autoUpdate: false);
        Assert.True(VersionPin.ReleaseAfterForwardMove(settings, pin, V("17.0.0"), []));
        Assert.False(File.Exists(pin));
        Assert.True(SavedAutoUpdate(path));
    }

    [Fact]
    public void A_pin_newer_than_the_running_version_or_no_pin_changes_nothing()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = Store(path, autoUpdate: false);
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("17.0.0"), []));
        File.WriteAllText(pin, "17.0.0");
        Assert.False(VersionPin.ReleaseAfterForwardMove(settings, pin, V("16.1.0"), []));
        Assert.Equal("17.0.0", File.ReadAllText(pin));
        Assert.False(SavedAutoUpdate(path));
    }

    // ── Magpie for Mac: a stable release behind many test versions ──

    private static string Release(string tag, bool pre) => $$"""
        { "tag_name": "v{{tag}}", "prerelease": {{(pre ? "true" : "false")}}, "body": "", "html_url": "https://github.com/x/releases/tag/v{{tag}}",
          "assets": [ { "name": "Magpie_{{tag}}.dmg", "browser_download_url": "https://dl/{{tag}}/Magpie_{{tag}}.dmg", "size": 10 },
                      { "name": "Magpie_{{tag}}.dmg.sha256", "browser_download_url": "https://dl/{{tag}}/Magpie_{{tag}}.dmg.sha256" } ] }
        """;

    private sealed class PagedGitHub : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            // Page 1: 100 test versions published after the stable one; page 2: the stable release.
            var json = url.EndsWith("&page=1", StringComparison.Ordinal)
                ? "[" + string.Join(",", Enumerable.Range(1, 100).Select(i => Release($"17.1.0-beta{i:000}", true))) + "]"
                : "[" + Release("17.0.1", false) + "]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task The_Mac_finds_a_stable_release_listed_behind_a_hundred_betas()
    {
        var hub = new PagedGitHub();
        var client = new UpdateClient(new HttpClient(hub), UpdateAsset.MacDmg);
        var stable = await client.FindNewerAsync(V("17.0.0"), includePrerelease: false, default);
        Assert.Equal("17.0.1", stable?.Version.ToString());
        Assert.Equal("https://dl/17.0.1/Magpie_17.0.1.dmg", stable!.ExeUrl);
        Assert.Contains(hub.Urls, u => u.EndsWith("page=2", StringComparison.Ordinal));
        var beta = await client.FindNewerAsync(V("17.0.0"), includePrerelease: true, default);
        Assert.Equal("17.1.0-beta100", beta?.Version.ToString());
        Assert.Null(await client.FindNewerAsync(V("17.0.1"), includePrerelease: false, default));
    }
}
