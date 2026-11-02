using System.Text.Json;
using Magpie.Core.Settings;
using Magpie.Core.Updates;

namespace Magpie.Core.Tests;

public sealed class Release1100Tests
{
    [Fact]
    public void Selected_version_disables_legacy_auto_install_before_handoff_and_survives_reload()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = AutomaticUpdates(path);
        using (var handoff = VersionPin.Prepare(settings, pin, AppVersion.TryParse("10.0.0-beta.2")!))
        {
            Assert.Equal("10.0.0-beta.2", File.ReadAllText(pin));
            AssertLegacyAutoInstallDisabled(path);
            var reloaded = new SettingsStore(path);
            Assert.False(reloaded.Load().Updates.AutoUpdate);
            reloaded.Save();
            AssertLegacyAutoInstallDisabled(path);
            handoff.Commit();
        }
        Assert.Equal("10.0.0-beta.2", File.ReadAllText(pin));
        AssertLegacyAutoInstallDisabled(path);
    }

    [Theory]
    [InlineData("{\"Updates\":{\"AutoCheck\":true,\"AutoDownload\":true}}")]
    [InlineData("{\"Updates\":{\"AutoUpdate\":null,\"AutoCheck\":true,\"AutoDownload\":true}}")]
    public void Legacy_settings_with_missing_or_null_AutoUpdate_get_a_persisted_opt_out(string json)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        File.WriteAllText(path, json);
        var settings = new SettingsStore(path);
        Assert.True(settings.Load().Updates.AutoUpdate);
        using var handoff = VersionPin.Prepare(settings, dir.File("version-pin.txt"), AppVersion.TryParse("1.1.0")!);
        AssertLegacyAutoInstallDisabled(path);
        handoff.Commit();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9.0.0\n")]
    public void Failed_handoff_restores_prior_pin_and_flags_without_reverting_other_settings(string? previousPin)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = AutomaticUpdates(path);
        if (previousPin != null) File.WriteAllText(pin, previousPin);
        using (VersionPin.Prepare(settings, pin, AppVersion.TryParse("8.1.0")!))
        {
            settings.Current.SyncIntervalMinutes = 42;
            settings.Current.Updates.IncludePrerelease = true;
            settings.Save();
        }
        var restored = new SettingsStore(path).Load();
        Assert.True(restored.Updates.AutoUpdate);
        Assert.True(restored.Updates.AutoCheck);
        Assert.True(restored.Updates.AutoDownload);
        Assert.True(restored.Updates.IncludePrerelease);
        Assert.Equal(42, restored.SyncIntervalMinutes);
        if (previousPin == null) Assert.False(File.Exists(pin));
        else Assert.Equal(previousPin, File.ReadAllText(pin));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9.0.0")]
    public void Settings_write_failure_restores_previous_pin_and_in_memory_flags(string? previousPin)
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = AutomaticUpdates(path);
        if (previousPin != null) File.WriteAllText(pin, previousPin);
        // A directory at the destination rejects the atomic settings-file replacement on every platform.
        File.Delete(path);
        Directory.CreateDirectory(path);
        var error = Record.Exception(() => VersionPin.Prepare(settings, pin, AppVersion.TryParse("8.1.0")!));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.True(settings.Current.Updates.AutoUpdate);
        Assert.True(settings.Current.Updates.AutoCheck);
        Assert.True(settings.Current.Updates.AutoDownload);
        if (previousPin == null) Assert.False(File.Exists(pin));
        else Assert.Equal(previousPin, File.ReadAllText(pin));
        Assert.Empty(Directory.GetFiles(dir.Path, "version-pin.txt.next-*"));
    }

    [Fact]
    public void Pin_write_failure_aborts_without_changing_persisted_settings()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = AutomaticUpdates(path);
        var previousSettings = File.ReadAllText(path);
        Directory.CreateDirectory(pin);
        var error = Record.Exception(() => VersionPin.Prepare(settings, pin, AppVersion.TryParse("8.1.0")!));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(previousSettings, File.ReadAllText(path));
        Assert.True(settings.Current.Updates.AutoUpdate);
        Assert.True(settings.Current.Updates.AutoCheck);
        Assert.True(settings.Current.Updates.AutoDownload);
        Assert.Empty(Directory.GetFiles(dir.Path, "version-pin.txt.next-*"));
    }

    [Fact]
    public void Failed_handoff_reports_restore_failure_and_still_restores_the_pin()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        var pin = dir.File("version-pin.txt");
        var settings = AutomaticUpdates(path);
        File.WriteAllText(pin, "9.0.0");
        var handoff = VersionPin.Prepare(settings, pin, AppVersion.TryParse("8.1.0")!);
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.Throws<AggregateException>(() => handoff.Dispose());
        Assert.Equal("9.0.0", File.ReadAllText(pin));
        Assert.True(settings.Current.Updates.AutoUpdate);
    }

    private static SettingsStore AutomaticUpdates(string path)
    {
        var settings = new SettingsStore(path);
        settings.Load();
        settings.Current.Updates.AutoUpdate = true;
        settings.Save();
        return settings;
    }

    private static void AssertLegacyAutoInstallDisabled(string path)
    {
        // Inspect persisted JSON as an older executable would; do not rely only on the in-memory model.
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var updates = json.RootElement.GetProperty("Updates");
        Assert.False(updates.GetProperty("AutoUpdate").GetBoolean());
        Assert.False(updates.GetProperty("AutoCheck").GetBoolean());
        Assert.False(updates.GetProperty("AutoDownload").GetBoolean());
    }
}
