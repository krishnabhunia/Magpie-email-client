using System.Net;
using System.Security.Cryptography;
using System.Text;
using Magpie.Core.Auth;
using Magpie.Core.Mail;
using Magpie.Core.Security;
using Magpie.Core.Updates;

namespace Magpie.Core.Tests;

/// <summary>8.0.0: Magpie for Mac (plan PX1 part B, queue #74) — the Core parts it needs: Mac data folders, secrets in
/// the Keychain, updates from the .dmg, the update rules and the bundle swap, the shared editor and reader scripts.</summary>
public class Release800Tests
{
    // ── Data folders ──

    [Fact]
    public void On_a_Mac_data_lives_in_Library_Application_Support_and_caches_in_Library_Caches()
    {
        using var home = new TempDir();
        var p = AppPaths.For("/Applications/Magpie.app/Contents/MacOS", "Magpie", home.Path);
        Assert.Equal(Path.Combine(home.Path, "Library", "Application Support", "Magpie"), p.Root);
        Assert.Equal(Path.Combine(home.Path, "Library", "Caches", "Magpie"), p.LocalRoot);
        Assert.False(p.IsPortable);
        Assert.Equal(Path.Combine(p.Root, "mail.db"), p.Database);
        Assert.Equal(Path.Combine(p.Root, "secrets.json"), p.Secrets);
        Assert.Equal(Path.Combine(p.LocalRoot, "updates"), p.Updates);
        Assert.Equal(Path.Combine(p.LocalRoot, "WebView"), p.WebViewData);
        Assert.True(Directory.Exists(p.Root));
    }

    [Fact]
    public void A_Mac_copy_is_never_portable_even_named_like_the_portable_exe()
    {
        using var home = new TempDir();
        using var folder = new TempDir();
        File.WriteAllText(Path.Combine(folder.Path, AppPaths.PortableMarker), "portable");
        var p = AppPaths.For(folder.Path, "Magpie_8.0.0.exe", home.Path);
        Assert.False(p.IsPortable);
        Assert.StartsWith(home.Path, p.Root);
        // Windows (no Mac home) keeps the portable rule exactly as before.
        Assert.True(AppPaths.For(folder.Path, "Magpie_8.0.0.exe", null).IsPortable);
    }

    // ── Secrets: AES-GCM with a key in the Keychain ──

    private sealed class FakeKeyStore : IKeyStore
    {
        public byte[]? Key;
        public int Loads, Saves;
        public byte[]? Load() { Loads++; return Key; }
        public void Save(byte[] key) { Saves++; Key = key; }
    }

    [Fact]
    public void Keychain_protector_round_trips_and_makes_its_key_once()
    {
        var store = new FakeKeyStore();
        var p = new KeychainProtector(store);
        var plain = Encoding.UTF8.GetBytes("app password · ünïcødé");
        var blob = p.Protect(plain);
        Assert.Equal(1, store.Saves);
        Assert.Equal(32, store.Key!.Length);
        Assert.NotEqual(plain, blob[^plain.Length..]);
        Assert.Equal(plain, p.Unprotect(blob));
        Assert.NotEqual(blob, p.Protect(plain));                        // a new nonce every time

        // A later start reads the same key from the Keychain and opens what the first one saved.
        var again = new KeychainProtector(new FakeKeyStore { Key = store.Key });
        Assert.Equal(plain, again.Unprotect(blob));
    }

    [Fact]
    public void Keychain_protector_with_another_key_or_a_damaged_blob_fails()
    {
        var blob = new KeychainProtector(new FakeKeyStore()).Protect(Encoding.UTF8.GetBytes("secret"));
        var other = new KeychainProtector(new FakeKeyStore());
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(blob));
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(new byte[] { 1, 2, 3 }));
        var dpapiLike = (byte[])blob.Clone();
        dpapiLike[0] = 0x01 ^ 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => other.Unprotect(dpapiLike));
    }

    [Fact]
    public void The_vault_on_a_Mac_keeps_secrets_unreadable_and_a_lost_key_means_signing_in_again()
    {
        using var dir = new TempDir();
        var store = new FakeKeyStore();
        var vault = new SecretVault(dir.File("secrets.json"), new KeychainProtector(store));
        vault.Set(SecretVault.PasswordKey("a1"), "hunter2");
        Assert.DoesNotContain("hunter2", File.ReadAllText(dir.File("secrets.json")));
        Assert.Equal("hunter2", new SecretVault(dir.File("secrets.json"), new KeychainProtector(new FakeKeyStore { Key = store.Key })).Get(SecretVault.PasswordKey("a1")));
        Assert.Null(new SecretVault(dir.File("secrets.json"), new KeychainProtector(new FakeKeyStore())).Get(SecretVault.PasswordKey("a1")));
    }

    // ── Updates from the .dmg ──

    private const string ReleasesJson = """
        [
          { "tag_name": "v8.0.1-beta.97", "prerelease": true, "body": "beta", "html_url": "https://github.com/x/releases/tag/v8.0.1-beta.97",
            "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/b/Magpie.exe", "size": 10 },
                        { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/b/Magpie.exe.sha256" },
                        { "name": "Magpie_8.0.1-beta.97.dmg", "browser_download_url": "https://dl/b/Magpie_8.0.1-beta.97.dmg", "size": 60 },
                        { "name": "Magpie_8.0.1-beta.97.dmg.sha256", "browser_download_url": "https://dl/b/Magpie_8.0.1-beta.97.dmg.sha256" } ] },
          { "tag_name": "v8.0.0", "prerelease": false, "body": "## 8.0.0\n- Mac", "html_url": "https://github.com/x/releases/tag/v8.0.0",
            "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/a/Magpie.exe", "size": 85000000 },
                        { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/a/Magpie.exe.sha256" },
                        { "name": "Magpie_8.0.0.dmg", "browser_download_url": "https://dl/a/Magpie_8.0.0.dmg", "size": 70000000 },
                        { "name": "Magpie_8.0.0.dmg.sha256", "browser_download_url": "https://dl/a/Magpie_8.0.0.dmg.sha256" },
                        { "name": "Magpie_8.0.0.zip", "browser_download_url": "https://dl/a/Magpie_8.0.0.zip" } ] },
          { "tag_name": "v7.2.0", "prerelease": false, "body": "## 7.2.0",
            "assets": [ { "name": "Magpie.exe", "browser_download_url": "https://dl/c/Magpie.exe", "size": 84000000 },
                        { "name": "Magpie.exe.sha256", "browser_download_url": "https://dl/c/Magpie.exe.sha256" } ] },
          { "tag_name": "v7.1.0", "assets": [ { "name": "Magpie_7.1.0.dmg", "browser_download_url": "https://dl/d/Magpie_7.1.0.dmg" } ] }
        ]
        """;

    [Fact]
    public void The_Mac_app_only_sees_releases_with_a_dmg_and_its_checksum()
    {
        var mac = UpdateClient.ParseReleases(ReleasesJson, UpdateAsset.MacDmg);
        Assert.Equal(new[] { "v8.0.1-beta.97", "v8.0.0" }, mac.Select(r => r.Info.Tag));   // 7.2.0 has no dmg, 7.1.0 no checksum
        var stable = UpdateClient.PickNewest(mac, includePrerelease: false)!;
        Assert.Equal("https://dl/a/Magpie_8.0.0.dmg", stable.ExeUrl);
        Assert.Equal("https://dl/a/Magpie_8.0.0.dmg.sha256", stable.ShaUrl);
        Assert.Equal(70000000, stable.ExeSize);
        Assert.Equal("https://dl/b/Magpie_8.0.1-beta.97.dmg", UpdateClient.PickNewest(mac, includePrerelease: true)!.ExeUrl);
    }

    [Fact]
    public void Windows_still_updates_from_Magpie_exe_exactly_as_before()
    {
        var win = UpdateClient.ParseReleases(ReleasesJson);
        Assert.Equal(new[] { "v8.0.1-beta.97", "v8.0.0", "v7.2.0" }, win.Select(r => r.Info.Tag));
        Assert.Equal("https://dl/a/Magpie.exe", UpdateClient.PickNewest(win, false)!.ExeUrl);
        Assert.Equal(win.Select(r => r.Info.ExeUrl), UpdateClient.ParseReleases(ReleasesJson, UpdateAsset.WindowsExe).Select(r => r.Info.ExeUrl));
        Assert.Equal("Magpie.exe", UpdateAsset.WindowsExe.FileFor("8.0.0"));
        Assert.Equal(UpdateClient.ShaAsset, UpdateAsset.WindowsExe.ShaFor("8.0.0"));
        Assert.Equal("Magpie-8.0.0.exe", UpdateAsset.WindowsExe.LocalFileFor(AppVersion.TryParse("8.0.0")!));
        Assert.Equal("Magpie_8.0.0-beta.5.dmg", UpdateAsset.MacDmg.FileFor("8.0.0-beta.5"));
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("api.github.com"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesJson) });
            return Task.FromResult(Files.TryGetValue(url, out var b)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(b) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task The_Mac_app_downloads_a_verified_dmg()
    {
        using var dir = new TempDir();
        var dmg = Encoding.ASCII.GetBytes("koly pretend this is Magpie_8.0.0.dmg");
        var hub = new FakeGitHub();
        hub.Files["https://dl/a/Magpie_8.0.0.dmg"] = dmg;
        hub.Files["https://dl/a/Magpie_8.0.0.dmg.sha256"] = Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(dmg)) + "\n");
        var client = new UpdateClient(new HttpClient(hub), UpdateAsset.MacDmg);
        var rel = await client.FindNewerAsync(AppVersion.TryParse("7.2.0")!, false, default);
        Assert.Equal("8.0.0", rel!.Version.ToString());
        var path = await client.DownloadAsync(rel, dir.Path, null, default);
        Assert.EndsWith("Magpie_8.0.0.dmg", path);
        Assert.Equal(dmg, File.ReadAllBytes(path));
        Assert.Null(await client.FindNewerAsync(AppVersion.TryParse("8.0.0")!, false, default));
    }

    // ── The update rules ──

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 9, 0, 0, TimeSpan.FromHours(5.5));

    [Theory]
    [InlineData(false, false)]   // every start checks, Auto update off …
    [InlineData(false, true)]    // … or on
    public void Every_start_checks_for_a_new_version(bool startCheckDone, bool autoUpdate) =>
        Assert.True(UpdatePolicy.ShouldAutoCheck(startCheckDone, autoUpdate, Now.AddMinutes(-5), Now, DateTimeOffset.MinValue, busy: false, offering: false));

    [Fact]
    public void After_the_start_check_only_Auto_update_checks_again_and_only_once_a_day()
    {
        Assert.False(UpdatePolicy.ShouldAutoCheck(true, false, Now.AddDays(-3), Now, DateTimeOffset.MinValue, false, false));
        Assert.False(UpdatePolicy.ShouldAutoCheck(true, true, Now.AddHours(-2), Now, DateTimeOffset.MinValue, false, false));
        Assert.True(UpdatePolicy.ShouldAutoCheck(true, true, Now.AddHours(-23), Now, DateTimeOffset.MinValue, false, false));
        Assert.True(UpdatePolicy.ShouldAutoCheck(true, true, null, Now, DateTimeOffset.MinValue, false, false));
        Assert.False(UpdatePolicy.ShouldAutoCheck(true, true, null, Now, Now.AddHours(1), false, false));   // "Later"
        Assert.False(UpdatePolicy.ShouldAutoCheck(false, true, null, Now, DateTimeOffset.MinValue, busy: true, offering: false));
        Assert.False(UpdatePolicy.ShouldAutoCheck(false, true, null, Now, DateTimeOffset.MinValue, busy: false, offering: true));
    }

    [Fact]
    public void A_skipped_version_is_offered_only_when_asked_and_the_button_names_the_version()
    {
        var current = AppVersion.TryParse("8.0.0")!;
        var found = AppVersion.TryParse("8.0.1")!;
        Assert.True(UpdatePolicy.ShouldOffer(found, current, automatic: true, skippedVersion: ""));
        Assert.False(UpdatePolicy.ShouldOffer(found, current, automatic: true, skippedVersion: "8.0.1"));
        Assert.True(UpdatePolicy.ShouldOffer(found, current, automatic: false, skippedVersion: "8.0.1"));
        Assert.False(UpdatePolicy.ShouldOffer(current, current, automatic: false, skippedVersion: ""));
        Assert.True(UpdatePolicy.DownloadsByItself(autoUpdate: true));
        Assert.False(UpdatePolicy.DownloadsByItself(autoUpdate: false));
        Assert.Equal("Update to v8.0.1", UpdatePolicy.ButtonText(found, current));
        Assert.Equal("", UpdatePolicy.ButtonText(current, current));
        Assert.Equal("", UpdatePolicy.ButtonText(null, current));
    }

    // ── Replacing the .app bundle ──

    [Theory]
    [InlineData("/Applications/Magpie.app/Contents/MacOS/Magpie", "/Applications/Magpie.app")]
    [InlineData("/Users/k/Apps/Magpie 8.app/Contents/MacOS/Magpie", "/Users/k/Apps/Magpie 8.app")]
    [InlineData("/home/k/magpie/bin/Magpie", null)]
    [InlineData("/Applications/Magpie.app/Contents/Resources/Magpie", null)]
    [InlineData(null, null)]
    public void The_running_bundle_is_found_from_the_executable(string? exe, string? app) =>
        Assert.Equal(app, MacBundle.FindBundle(exe));

    private static string MakeApp(string folder, string name, string marker)
    {
        var app = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "Magpie"), marker);
        return app;
    }

    private static string Marker(string app) => File.ReadAllText(Path.Combine(app, "Contents", "MacOS", "Magpie"));

    [Fact]
    public void The_new_bundle_takes_the_old_ones_place_and_the_old_one_is_kept_for_going_back()
    {
        using var dir = new TempDir();
        var app = MakeApp(dir.Path, "Magpie.app", "8.0.0");
        MakeApp(dir.Path, MacBundle.BackupName, "7.0.0");                    // an older backup is replaced
        var staged = MakeApp(dir.Path, MacBundle.StagingName, "8.0.1");
        Assert.Equal(staged, MacBundle.StagingPathFor(app));
        Assert.True(MacBundle.CanReplace(app));

        MacBundle.Swap(app, staged);
        Assert.Equal("8.0.1", Marker(app));
        Assert.Equal("8.0.0", Marker(MacBundle.BackupPathFor(app)));
        Assert.False(Directory.Exists(staged));

        Assert.True(MacBundle.Rollback(app));
        Assert.Equal("8.0.0", Marker(app));
        Assert.False(Directory.Exists(MacBundle.BackupPathFor(app)));
        Assert.False(MacBundle.Rollback(app));                                // nothing left to go back to
    }

    [Fact]
    public void An_incomplete_new_bundle_leaves_the_app_as_it_was()
    {
        using var dir = new TempDir();
        var app = MakeApp(dir.Path, "Magpie.app", "8.0.0");
        var staged = Path.Combine(dir.Path, MacBundle.StagingName);
        Directory.CreateDirectory(staged);
        Assert.Throws<InvalidOperationException>(() => MacBundle.Swap(app, staged));
        Assert.Equal("8.0.0", Marker(app));
        Assert.False(Directory.Exists(MacBundle.BackupPathFor(app)));
    }

    [Theory]
    [InlineData("/private/var/folders/x/T/AppTranslocation/1234/d/Magpie.app", true)]
    [InlineData("/Volumes/Magpie 8.0.0/Magpie.app", true)]
    [InlineData("/Applications/Magpie.app", false)]
    public void An_app_started_from_the_disk_image_or_Downloads_is_noticed(string app, bool translocated) =>
        Assert.Equal(translocated, MacBundle.IsTranslocated(app));

    // ── Shared pages and settings ──

    [Fact]
    public void The_Mac_editor_sends_with_Command_Return_and_Windows_keeps_Ctrl()
    {
        Assert.Contains("e.ctrlKey && e.key === 'Enter'", EditorPage.Html);
        Assert.DoesNotContain("metaKey", EditorPage.Html);
        Assert.Contains("(e.ctrlKey || e.metaKey) && e.key === 'Enter'", EditorPage.ForMac);
        Assert.Contains("(e.ctrlKey || e.metaKey) && (e.key === 'k'", EditorPage.ForMac);
    }

    [Fact]
    public void The_reader_payload_sends_the_page_once_and_then_only_its_key()
    {
        var page = new ReaderPage("a\nt1", "FP", "<html><style>p{}</style><body><p>Hi</p></body></html>", 0, "ctx",
            ReadStates: new Dictionary<long, bool> { [7] = true });
        var full = ReaderShell.PresentScript(page, 3, reuse: false, hoverDelayMs: 5000);
        Assert.StartsWith("magpiePresent({", full);
        Assert.Contains("\"body\":\"\\u003Cp\\u003EHi\\u003C/p\\u003E\"", full);
        Assert.Contains("\"hoverDelay\":1000", full);
        Assert.Contains("\"read\":{\"7\":true}", full);
        var reuse = ReaderShell.PresentScript(page, 4, reuse: true, hoverDelayMs: 600);
        Assert.Contains("\"body\":\"\"", reuse);
        Assert.Contains("\"reuse\":true", reuse);
        Assert.Contains("function magpiePresent(", ReaderShell.Build(dark: false));
    }

    [Fact]
    public void A_Google_client_file_fills_the_id_and_secret()
    {
        var parsed = GoogleClientFile.Parse("""{"installed":{"client_id":"123.apps.googleusercontent.com","client_secret":"GOCSPX-x"}}""", out var err);
        Assert.Null(err);
        Assert.Equal(("123.apps.googleusercontent.com", "GOCSPX-x"), parsed!.Value);
        Assert.Equal("1.apps", GoogleClientFile.Parse("""{"web":{"client_id":"1.apps"}}""", out _)!.Value.Id);
        Assert.Null(GoogleClientFile.Parse("""{"installed":{}}""", out err));
        Assert.Contains("no client_id", err);
        Assert.Null(GoogleClientFile.Parse("not json", out err));
        Assert.StartsWith("That file isn't a Google OAuth client file", err);
    }
}
