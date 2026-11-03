using System.Text.Json;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

/// <summary>SP1-B (14.0.0): the start-up screen (Q89) — setting, when it shows, timing, wording, early settings read.</summary>
public class Release1400Tests
{
    [Fact]
    public void Setting_defaults_to_every_time_with_accounts()
    {
        var a = new Appearance();
        Assert.Equal(SplashMode.Always, a.Splash);
        Assert.True(a.SplashAccounts);
    }

    [Fact]
    public void Old_settings_without_the_setting_show_the_screen()
    {
        var old = JsonSerializer.Deserialize<Appearance>("""{"Theme":"Dark","Colourful":true}""", AppSettings.Json)!;
        old.Normalise();
        Assert.Equal(SplashMode.Always, old.Splash);
        Assert.True(old.SplashAccounts);
    }

    [Fact]
    public void Unknown_value_falls_back_to_every_time()
    {
        var odd = JsonSerializer.Deserialize<Appearance>("""{"Splash":9}""", AppSettings.Json)!;
        odd.Normalise();
        Assert.Equal(SplashMode.Always, odd.Splash);
    }

    [Fact]
    public void Setting_round_trips_and_clones()
    {
        var a = new Appearance { Splash = SplashMode.AfterUpdate, SplashAccounts = false };
        var back = JsonSerializer.Deserialize<Appearance>(JsonSerializer.Serialize(a, AppSettings.Json), AppSettings.Json)!;
        Assert.Equal(SplashMode.AfterUpdate, back.Splash);
        Assert.False(back.SplashAccounts);
        var c = a.Clone();
        Assert.Equal(SplashMode.AfterUpdate, c.Splash);
        Assert.False(c.SplashAccounts);
    }

    [Theory]
    [InlineData(SplashMode.Always, false, false, true)]
    [InlineData(SplashMode.Always, true, false, true)]
    [InlineData(SplashMode.AfterUpdate, false, false, false)]
    [InlineData(SplashMode.AfterUpdate, true, false, true)]
    [InlineData(SplashMode.Never, true, false, false)]
    [InlineData(SplashMode.Never, false, false, false)]
    [InlineData(SplashMode.Always, false, true, false)]   // Start with Windows is quiet
    [InlineData(SplashMode.AfterUpdate, true, true, false)]
    public void When_it_shows(SplashMode mode, bool afterUpdate, bool tray, bool shows) =>
        Assert.Equal(shows, SplashPlan.ShouldShow(mode, afterUpdate, tray));

    [Fact]
    public void Stays_at_least_0_8_seconds()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(500), SplashPlan.HoldBeforeClose(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(TimeSpan.Zero, SplashPlan.HoldBeforeClose(TimeSpan.FromMilliseconds(800)));
        Assert.Equal(TimeSpan.Zero, SplashPlan.HoldBeforeClose(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Slow_note_after_10_seconds_only_while_running()
    {
        Assert.False(SplashPlan.IsSlow(TimeSpan.FromSeconds(9.9), finished: false));
        Assert.True(SplashPlan.IsSlow(TimeSpan.FromSeconds(10), finished: false));
        Assert.False(SplashPlan.IsSlow(TimeSpan.FromSeconds(30), finished: true));
    }

    [Fact]
    public void Step_lines_name_the_accounts()
    {
        Assert.Equal(("Checking the last update", "Checked the last update"), SplashPlan.Step(0, 3));
        Assert.Equal(("Opening your mail folder", "Opened your mail folder"), SplashPlan.Step(1, 3));
        Assert.Equal(("Loading settings, look and fonts", "Loaded settings, look and fonts"), SplashPlan.Step(2, 3));
        Assert.Equal(("Starting 3 accounts", "Started 3 accounts"), SplashPlan.Step(3, 3));
        Assert.Equal(("Starting 1 account", "Started 1 account"), SplashPlan.Step(3, 1));
        Assert.Equal(("Opening Magpie", "Opened Magpie"), SplashPlan.Step(3, 0));
    }

    [Theory]
    [InlineData(42, "42 ms")]
    [InlineData(999.4, "999 ms")]
    [InlineData(1000, "1.0 s")]
    [InlineData(1490, "1.4 s")]
    [InlineData(12_300, "12 s")]
    public void Durations(double ms, string text) => Assert.Equal(text, SplashPlan.Duration(TimeSpan.FromMilliseconds(ms)));

    [Fact]
    public void Log_line_lists_every_step()
    {
        var line = SplashPlan.LogLine(new TimeSpan?[] { TimeSpan.FromMilliseconds(42), TimeSpan.FromMilliseconds(318), TimeSpan.FromMilliseconds(121), TimeSpan.FromMilliseconds(264) },
            TimeSpan.FromMilliseconds(1200));
        Assert.Equal("start-up 1.2 s (update 42 ms · mail folder 318 ms · look 121 ms · accounts 264 ms)", line);
        Assert.Equal("start-up 50 ms (update 50 ms)", SplashPlan.LogLine(new TimeSpan?[] { TimeSpan.FromMilliseconds(50), null, null, null }, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void Prefs_read_from_a_real_settings_file()
    {
        var s = new AppSettings();
        s.Accounts.Add(new Models.Account { Email = "krishna@example.com", Color = "#D93025" });
        s.Accounts.Add(new Models.Account { Email = "work@example.com", Color = "not a colour" });
        s.Accounts.Add(new Models.Account { Email = "  " });
        s.Appearance.Splash = SplashMode.AfterUpdate;
        s.Appearance.SplashAccounts = false;
        s.Appearance.Theme = ThemeMode.Dark;
        s.Window.Left = 100; s.Window.Top = 50; s.Window.Width = 1320; s.Window.Height = 840;

        var p = SplashPrefs.FromJson(JsonSerializer.Serialize(s, AppSettings.Json));
        Assert.Equal(SplashMode.AfterUpdate, p.Mode);
        Assert.False(p.ShowAccounts);
        Assert.Equal(ThemeMode.Dark, p.Theme);
        Assert.Equal(new[] { "krishna@example.com", "work@example.com" }, p.Accounts.Select(a => a.Email));
        Assert.Equal("#D93025", p.Accounts[0].Color);
        Assert.Equal("#14606E", p.Accounts[1].Color);   // a bad colour gets Magpie's teal
        Assert.Equal(100, p.WindowLeft);
        Assert.Equal(840, p.WindowHeight);
    }

    [Fact]
    public void Prefs_from_a_new_settings_file_keep_window_place_unknown()
    {
        var p = SplashPrefs.FromJson(JsonSerializer.Serialize(new AppSettings(), AppSettings.Json));
        Assert.Equal(SplashMode.Always, p.Mode);
        Assert.True(p.ShowAccounts);
        Assert.Empty(p.Accounts);
        Assert.True(double.IsNaN(p.WindowLeft));   // saved as "NaN"
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{"Appearance":"oops","Accounts":{"a":1}}""")]
    public void Broken_settings_give_the_defaults(string json)
    {
        var p = SplashPrefs.FromJson(json);
        Assert.Equal(SplashMode.Always, p.Mode);
        Assert.True(p.ShowAccounts);
        Assert.Equal(ThemeMode.MatchWindows, p.Theme);
        Assert.Empty(p.Accounts);
    }

    [Fact]
    public void Prefs_accept_numbers_and_any_case()
    {
        var p = SplashPrefs.FromJson("""{"appearance":{"splash":2,"theme":"light"},"accounts":[{"email":"a@b.c","color":"#123456"}]}""");
        Assert.Equal(SplashMode.Never, p.Mode);
        Assert.Equal(ThemeMode.Light, p.Theme);
        Assert.Single(p.Accounts);
    }

    [Fact]
    public void Missing_file_gives_the_defaults()
    {
        var p = SplashPrefs.Read(Path.Combine(Path.GetTempPath(), "magpie-no-such-" + Guid.NewGuid().ToString("N"), "settings.json"));
        Assert.Equal(SplashMode.Always, p.Mode);
        Assert.Empty(p.Accounts);
    }

    [Fact]
    public void Settings_store_keeps_the_setting()
    {
        var dir = Directory.CreateTempSubdirectory("magpie-sp1-");
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            var store = new SettingsStore(path);
            store.Load();
            store.Current.Appearance.Splash = SplashMode.Never;
            store.Save();
            var again = new SettingsStore(path);
            Assert.Equal(SplashMode.Never, again.Load().Appearance.Splash);
            Assert.Equal(SplashMode.Never, SplashPrefs.Read(path).Mode);
        }
        finally { dir.Delete(true); }
    }
}
