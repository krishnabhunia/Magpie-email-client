using Magpie.Core.Info;
using Magpie.Core.Updates;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>
/// 18.0.0 (written for 15.0.0, which main released with other work) — short test-version names (design BT1 option A, BT2) and the Info window (design IN1, Q95): beta parsing
/// and order, portable names, build details, Copy build info, docs/FEATURES.md and the CHANGELOG section.
/// </summary>
public sealed class Release1800Tests
{
    private static AppVersion V(string version) => AppVersion.TryParse(version)!;

    [Theory]
    [InlineData("15.0.0-beta007", "15.0.0")]                                 // below its release
    [InlineData("2.16.0-beta.18.29", "2.16.0-beta001")]                      // above the old long betas
    [InlineData("15.0.0-beta.1793625068.gaaaaaaaa", "15.0.0-beta001")]
    [InlineData("15.0.0-beta.95", "15.0.0-beta001")]
    [InlineData("15.0.0-beta009", "15.0.0-beta010")]                         // fixed width sorts 001 … 999
    [InlineData("15.0.0-beta099", "15.0.0-beta999")]
    [InlineData("14.1.0", "15.0.0-beta001")]                                 // above the previous release
    [InlineData("15.0.0", "15.0.1-beta001")]
    public void Short_test_versions_sort_where_the_design_says(string older, string newer)
    {
        Assert.True(V(older).CompareTo(V(newer)) < 0, $"{older} < {newer}");
        Assert.True(V(newer).CompareTo(V(older)) > 0, $"{newer} > {older}");
    }

    [Fact]
    public void Short_test_versions_parse_and_print_unchanged()
    {
        var v = V("v15.0.0-beta007");
        Assert.True(v.IsPrerelease);
        Assert.Equal("beta007", v.Pre);
        Assert.Equal("15.0.0-beta007", v.ToString());
        Assert.Equal(7, BetaName.Number(v));
        Assert.Equal("15.0.0", BetaName.Core(v));
        Assert.Null(BetaName.Number(V("15.0.0-beta.1793625068.gaaaaaaaa")));
        Assert.Null(BetaName.Number(V("15.0.0")));
        Assert.Null(BetaName.Number(V("15.0.0-beta000")));
        Assert.Equal("Magpie_15.0.0-beta007.dmg", UpdateAsset.MacDmg.FileFor("15.0.0-beta007"));
    }

    private const string Releases = """
        [
          { "tag_name": "v15.0.0-beta002", "prerelease": true, "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/c/Magpie.exe" }, { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/c/s" } ] },
          { "tag_name": "v15.0.0-beta.1793625068.gaaaaaaaa", "prerelease": true, "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/b/Magpie.exe" }, { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/b/s" } ] },
          { "tag_name": "v15.0.0-beta001", "prerelease": true, "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/a/Magpie.exe" }, { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/a/s" } ] },
          { "tag_name": "v14.1.0", "prerelease": false, "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/s/Magpie.exe" }, { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/s/s" } ] }
        ]
        """;

    [Fact]
    public void Old_and_new_test_versions_are_both_listed_and_the_short_one_is_newest()
    {
        var all = UpdateClient.ParseReleases(Releases);
        Assert.Equal(4, all.Count);
        Assert.Equal("15.0.0-beta002", UpdateClient.PickNewest(all, includePrerelease: true)!.Version.ToString());
        Assert.Equal("14.1.0", UpdateClient.PickNewest(all, includePrerelease: false)!.Version.ToString());
        Assert.Equal(new[] { "15.0.0-beta002", "15.0.0-beta001", "15.0.0-beta.1793625068.gaaaaaaaa", "14.1.0" },
            all.Select(r => r.Info.Version).OrderByDescending(v => v).Select(v => v.ToString()));
    }

    [Theory]
    [InlineData("Magpie_15.0.0-beta007.exe", true)]
    [InlineData("magpie_15.0.0-BETA007.EXE", true)]
    [InlineData("Magpie_15.0.0-beta007 (1).exe", true)]
    [InlineData("Magpie_15.0.0-beta.1793625068.gaaaaaaaa.exe", true)]   // the old long test names too
    [InlineData("Magpie_7.2.0-beta.5.exe", true)]
    [InlineData("Magpie_15.0.0-.exe", false)]
    [InlineData("Magpie_15.0.0-beta007.dll", false)]
    [InlineData("Magpie_backup.exe", false)]
    public void Portable_copies_of_test_versions_stay_portable(string name, bool portable)
    {
        using var dir = new TempDir();
        Assert.Equal(portable, AppPaths.IsPortableCopy(dir.Path, name));
    }

    private static readonly DateTimeOffset Built = new(2026, 11, 4, 14, 25, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");

    private static BuildInfo Beta() => BuildInfo.From("15.0.0-beta001+4e68c2a", key => key switch
    {
        "SourcePr" => "35",
        "SourceCommit" => "4E68C2A0B1C2D3E4F5061728394A5B6C7D8E9F00",
        "BuildTimeUtc" => "2026-11-04T14:25:00Z",
        "ReleaseTag" => "v15.0.0-beta001",
        _ => null,
    });

    [Fact]
    public void A_test_build_names_its_pr_commit_time_and_tag()
    {
        var b = Beta();
        Assert.True(b.IsBeta);
        Assert.False(b.IsLocal);
        Assert.Equal("Beta", b.Channel);
        Assert.Equal("v15.0.0-beta001", b.VersionLabel);
        Assert.Equal("4e68c2a", b.ShortCommit);
        Assert.Equal("Magpie_15.0.0-beta001.zip", b.DownloadName);
        Assert.Equal("Version 15.0.0-beta001 · Beta · Windows", b.Subtitle("Windows"));
        Assert.Equal("This is a test build. It is beta 001 of Magpie 15.0.0, made from a pull request.", b.Banner);
        Assert.Equal("04-Nov-2026, 19:55", b.BuiltOn(India));
        Assert.Equal("https://github.com/krishnabhunia/Magpie-email-client/pull/35", b.PrUrl);

        var rows = b.Rows("installer, per-user", India);
        Assert.Equal(new[] { "Version", "Channel", "Made from", "Commit", "Built on", "GitHub tag", "Download", "Installed as" },
            rows.Select(r => r.Item));
        Assert.Equal("PR #35", rows[2].Value);
        Assert.Equal(b.PrUrl, rows[2].Link);
        Assert.Equal("v15.0.0-beta001", rows[5].Value);
    }

    [Fact]
    public void Copy_build_info_gives_the_two_lines_of_the_design()
    {
        var system = SystemInfo.Line(SystemInfo.WindowsName("Windows 10 Pro", "24H2", 26100),
            SystemInfo.Arch(System.Runtime.InteropServices.Architecture.X64),
            SystemInfo.InstalledAs(UpdateInstallKinds.WindowsPerUser), new Version(8, 0, 11));
        Assert.Equal("Windows 11 Pro 24H2 x64 · installer, per-user · .NET 8", system);
        Assert.Equal("Magpie 15.0.0-beta001 (Beta) · PR #35 · 4e68c2a · built 2026-11-04 14:25 UTC" + Environment.NewLine + system,
            Beta().CopyText(system));
        Assert.Equal("Windows 10 Home 22H2", SystemInfo.WindowsName("Windows 10 Home", "22H2", 19045));
        Assert.Equal("portable", SystemInfo.InstalledAs(UpdateInstallKinds.WindowsPortable));
        Assert.Equal("installer, all users", SystemInfo.InstalledAs(UpdateInstallKinds.WindowsAllUsers));
    }

    [Fact]
    public void A_local_build_says_local_build_and_has_no_pr_row()
    {
        var b = BuildInfo.From("15.0.0", _ => "");
        Assert.True(b.IsLocal);
        Assert.False(b.IsBeta);
        Assert.Null(b.Banner);
        Assert.Equal("Stable", b.Channel);
        var rows = b.Rows(null);
        Assert.DoesNotContain(rows, r => r.Item is "Made from" or "Installed as");
        Assert.Equal(BuildInfo.LocalBuild, rows.Single(r => r.Item == "Commit").Value);
        Assert.Equal(BuildInfo.LocalBuild, rows.Single(r => r.Item == "Built on").Value);
        Assert.Equal("Magpie 15.0.0 (Stable) · local build", b.SummaryLine());
    }

    [Fact]
    public void An_old_long_test_version_has_a_banner_without_a_number()
    {
        var b = BuildInfo.From("14.0.0-beta.1793625068.gaaaaaaaa", _ => null);
        Assert.Equal("This is a test build of Magpie 14.0.0, made from a pull request.", b.Banner);
        Assert.Equal("14.0.0 · test version · being tested", ChangelogNotes.Heading(b, null));
    }

    [Fact]
    public void This_app_reads_its_own_build_details()
    {
        var b = BuildInfo.FromAssembly(typeof(BuildInfo).Assembly);
        Assert.NotEqual("0.0.0", b.Version.ToString());
    }

    private const string Features = """
        # What Magpie can do

        Intro.

        | SrNo. | Area | What it does | Since | Windows | Mac |
        |---|---|---|---|---|---|
        | 1 | Accounts | Gmail and **IMAP** | 1.0.0 | Yes | Yes |
        | 2 | Calendar | Google Calendar with `Meet` links | 3.0.0 | Yes | Not yet |
        | 3 | Info window | ⓘ next to the version | 15.0.0 | Yes | Yes |

        Text after the table.
        """;

    [Fact]
    public void Features_md_parses_and_filters()
    {
        var rows = FeatureList.Parse(Features);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.SrNo));
        Assert.Equal("Gmail and IMAP", rows[0].What);
        Assert.Equal("Google Calendar with Meet links", rows[1].What);
        Assert.Equal(new[] { "Info window" }, FeatureList.Filter(rows, "", FeatureFilter.NewInThisVersion, "15.0.0").Select(r => r.Area));
        Assert.Equal(new[] { "Calendar" }, FeatureList.Filter(rows, null, FeatureFilter.WindowsOnly, "15.0.0").Select(r => r.Area));
        Assert.Equal(new[] { "Accounts", "Info window" }, FeatureList.Filter(rows, "", FeatureFilter.AlsoOnMac, "15.0.0").Select(r => r.Area));
        Assert.Equal(new[] { "Calendar" }, FeatureList.Filter(rows, "google MEET", FeatureFilter.All, "15.0.0").Select(r => r.Area));
        Assert.Empty(FeatureList.Filter(rows, "nothing like this", FeatureFilter.All, "15.0.0"));
        Assert.Equal("New in 15.0.0", FeatureList.NewInLabel("15.0.0"));
    }

    [Fact]
    public void The_built_in_features_md_has_the_info_window_and_numbered_rows()
    {
        var rows = FeatureList.Parse(InfoContent.Features);
        Assert.True(rows.Count >= 20);
        Assert.Equal(Enumerable.Range(1, rows.Count), rows.Select(r => r.SrNo));
        Assert.Contains(rows, r => r.Area == "Info window" && r.OnWindows && r.OnMac);
        Assert.All(rows, r => Assert.NotNull(AppVersion.TryParse(r.Since)));
    }

    private const string Log = """
        # Changelog

        <!-- the rule
             spans lines -->

        ## 15.0.0 (5 Nov 2026)
        ### New
        - **Info window.** Four tabs:
          - **This build** — the version.
        - Copy build info.
        ### Changed
        - Short `beta007` names,
          wrapped onto a second line.
        <!-- closes: #138 -->
        ## 14.1.0 (4 Nov 2026)
        ### Changed
        - Confirm screen.

        ## 2.2.0 (30 Sep 2026)
        - Emails open from this PC.
        """;

    [Fact]
    public void Whats_new_reads_the_versions_changelog_section()
    {
        var s = ChangelogNotes.Section(Log, "15.0.0")!;
        Assert.Equal("15.0.0", s.Version);
        Assert.Equal("5 Nov 2026", s.Date);
        Assert.Equal(new[] { "New", "Changed" }, s.Groups.Select(g => g.Kind));
        Assert.Equal(new[] { "Info window. Four tabs:", "· This build — the version.", "Copy build info." }, s.Groups[0].Lines);
        Assert.Equal(new[] { "Short beta007 names, wrapped onto a second line." }, s.Groups[1].Lines);
        var old = ChangelogNotes.Section(Log, "2.2.0")!;
        Assert.Equal("Changes", Assert.Single(old.Groups).Kind);
        Assert.Null(ChangelogNotes.Section(Log, "99.0.0"));
        Assert.Equal("15.0.0 · released 5 Nov 2026", ChangelogNotes.Heading(BuildInfo.From("15.0.0", _ => null), s));
        Assert.Equal("15.0.0 · beta 001 · being tested", ChangelogNotes.Heading(Beta(), s));
    }

    [Fact]
    public void Whats_new_falls_back_to_the_next_version_section()
    {
        var s = ChangelogNotes.Section("# Changelog\n\n## Next version (not released yet)\n### Fixed\n- A fix.\n\n## 14.1.0 (4 Nov 2026)\n- Old.\n", "15.0.0")!;
        Assert.Equal("15.0.0", s.Version);
        Assert.Equal("", s.Date);
        Assert.Equal("A fix.", Assert.Single(Assert.Single(s.Groups).Lines));
    }

    [Fact]
    public void The_built_in_changelog_has_this_versions_section()
    {
        var core = BetaName.Core(BuildInfo.FromAssembly(typeof(BuildInfo).Assembly).Version);
        var s = ChangelogNotes.Section(InfoContent.Changelog, core);
        Assert.NotNull(s);
        Assert.False(s!.IsEmpty);
    }

    [Fact]
    public void Under_the_hood_lists_the_parts_this_copy_uses()
    {
        var rows = UnderTheHood.Rows(false, "WPF · CommunityToolkit.Mvvm 8.4.2", @"C:\Users\k\AppData\Roaming\Magpie", true, "Match Windows", "IBM Plex Sans, IBM Plex Serif");
        Assert.Equal(new[] { "App", "Window toolkit", "Showing emails", "Mail", "Storage", "Sync", "Passwords and keys", "Updates", "Design / theme", "Fonts" },
            rows.Select(r => r.Item));
        Assert.Contains("MailKit 4.", rows.Single(r => r.Item == "Mail").Value);
        Assert.Contains("test versions on", rows.Single(r => r.Item == "Updates").Value);
        Assert.Contains("DPAPI", rows.Single(r => r.Item == "Passwords and keys").Value);
        var mac = UnderTheHood.Rows(true, "Avalonia 12", "~/Library/Application Support/Magpie", false, "Follows the Mac", null);
        Assert.Contains("Keychain", mac.Single(r => r.Item == "Passwords and keys").Value);
        Assert.Equal("The system's own fonts", mac.Single(r => r.Item == "Fonts").Value);
    }
}
