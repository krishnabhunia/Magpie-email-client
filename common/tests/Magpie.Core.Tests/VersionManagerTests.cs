using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using Microsoft.Data.Sqlite;
using Magpie.Core.Updates;
using Xunit;

namespace Magpie.Core.Tests;

public sealed class VersionManagerTests
{
    private static AppVersion V(string version) => AppVersion.TryParse(version)!;
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ReleaseInfo Release(string version = "11.0.0", bool mac = false)
    {
        var name = mac ? $"Magpie_{version}.dmg" : "Magpie.exe";
        var root = $"https://github.com/{UpdateClient.Repo}/releases/download/v{version}/";
        return new ReleaseInfo { Version = V(version), Tag = "v" + version, AssetName = name, ExeUrl = root + name,
            ShaUrl = root + name + ".sha256", ManifestUrl = root + ReleaseCompatibility.AssetName,
            ManifestShaUrl = root + ReleaseCompatibility.AssetName + ".sha256" };
    }
    private static ReleaseCompatibility Contract(ReleaseInfo release, byte[]? payload = null) => new()
    {
        FormatVersion = 1, Version = release.Version.ToString(), DatabaseSchemaMin = 11, DatabaseSchemaMax = 11,
        SettingsSchemaMin = 1, SettingsSchemaMax = 1,
        InstallKinds = [UpdateInstallKinds.WindowsPortable, UpdateInstallKinds.WindowsPerUser, UpdateInstallKinds.MacApp],
        AssetSha256 = new() { [release.AssetName] = Hash(payload ?? [1, 2, 3]) },
    };

    [Theory]
    [InlineData("11.0.0", "11.1.0-beta.1")]
    [InlineData("11.0.0-beta.2", "11.0.0")]
    [InlineData("11.0.0-beta.2", "11.0.0-beta.3")]
    [InlineData("11.0.0", "11.0.0-beta.2")]
    public void Stable_beta_upgrade_and_rollback_choices_require_a_matching_data_contract(string current, string target)
    {
        var release = Release(target);
        var selected = UpdateClient.SelectVersions([(release, release.Version.IsPrerelease)], V(current), V(target).CompareTo(V(current)) < 0, true);
        Assert.Same(release, Assert.Single(selected).Info);
        Assert.True(UpdateCompatibility.Evaluate(release, Contract(release), new(11, 1), UpdateInstallKinds.WindowsPortable).CanInstall);
        Assert.False(UpdateCompatibility.Evaluate(release, null, new(11, 1), UpdateInstallKinds.WindowsPortable).CanInstall);
    }

    [Theory]
    [InlineData(12, 1, UpdateInstallKinds.WindowsPortable, "mail database")]
    [InlineData(10, 1, UpdateInstallKinds.WindowsPortable, "mail database")]
    [InlineData(11, 2, UpdateInstallKinds.WindowsPortable, "settings")]
    [InlineData(11, 1, UpdateInstallKinds.WindowsAllUsers, "installation")]
    public void Unsafe_schema_settings_and_installer_transitions_are_blocked(int database, int settings, string kind, string reason)
    {
        var release = Release();
        var result = UpdateCompatibility.Evaluate(release, Contract(release), new(database, settings), kind);
        Assert.False(result.CanInstall);
        Assert.Contains(reason, result.Reason);
    }

    [Fact]
    public void Catalog_keeps_missing_platform_assets_visible_and_blocked()
    {
        const string json = """[{"tag_name":"v10.0.0","assets":[]},{"tag_name":"v9.0.0","draft":true,"assets":[]}]""";
        Assert.Empty(UpdateClient.ParseReleases(json));
        var release = Assert.Single(UpdateClient.ParseReleases(json, UpdateAsset.WindowsExe, includeUnavailable: true)).Info;
        Assert.Equal("10.0.0", release.Version.ToString());
        Assert.False(release.AssetAvailable);
        Assert.Contains("missing", UpdateCompatibility.Evaluate(release, null, new(11, 1), UpdateInstallKinds.WindowsPerUser).Reason);
    }

    [Fact]
    public void Profile_inspection_does_not_migrate_a_future_database()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.Settings, "{\"SchemaVersion\":3}");
        Sql(paths.Database, "PRAGMA user_version=42");
        Assert.Equal(new UpdateProfile(42, 3), UpdateCompatibility.ReadProfile(paths));
        Assert.Equal(42L, Scalar(paths.Database, "PRAGMA user_version"));
    }

    [Fact]
    public async Task Manifest_is_hash_verified_version_bound_and_cannot_redirect_to_another_repository()
    {
        var release = Release();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Contract(release), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var fake = new FilesHttp();
        fake.Files[release.ManifestUrl] = bytes;
        fake.Files[release.ManifestShaUrl] = Encoding.ASCII.GetBytes(Hash(bytes));
        var client = new UpdateClient(new HttpClient(fake));
        Assert.Equal("11.0.0", (await client.LoadCompatibilityAsync(release, default))!.Version);
        fake.Files[release.ManifestShaUrl] = Encoding.ASCII.GetBytes(new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LoadCompatibilityAsync(release, default));
        var wrong = new ReleaseInfo { Version = V("11.0.0"), Tag = "v11.0.0", ManifestUrl = "https://example.org/Magpie.update.json", ManifestShaUrl = release.ManifestShaUrl };
        await Assert.ThrowsAsync<InvalidDataException>(() => client.LoadCompatibilityAsync(wrong, default));
        Assert.Throws<InvalidDataException>(() => ReleaseCompatibility.Parse(Encoding.UTF8.GetString(bytes), V("10.0.0")));
    }

    [Fact]
    public async Task Application_checksum_must_match_its_compatibility_contract()
    {
        using var dir = new TempDir();
        var release = Release();
        var fake = new FilesHttp();
        fake.Files[release.ShaUrl] = Encoding.ASCII.GetBytes(new string('0', 64));
        var client = new UpdateClient(new HttpClient(fake));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadCheckedAsync(release, dir.Path, null, default, Contract(release)));
        Assert.DoesNotContain(release.ExeUrl, fake.Requests);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Interrupted_download_does_not_leave_a_partial_installable_file()
    {
        using var dir = new TempDir();
        var release = Release();
        var fake = new FilesHttp { InterruptedUrl = release.ExeUrl };
        fake.Files[release.ShaUrl] = Encoding.ASCII.GetBytes(Hash([1, 2, 3]));
        var client = new UpdateClient(new HttpClient(fake));
        await Assert.ThrowsAsync<IOException>(() => client.DownloadCheckedAsync(release, dir.Path, null, default, Contract(release)));
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Snapshot_restores_exact_database_drafts_settings_secrets_and_cache_only_after_matching_app_starts()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe");
        File.WriteAllText(exe, "old application bytes");
        var snapshot = VersionBackup.Create(paths, V("11.0.0-beta.1"), exe);
        Assert.Equal("old application bytes", File.ReadAllText(snapshot.ApplicationPath));
        Assert.Equal("encrypted-secret-payload", File.ReadAllText(Path.Combine(snapshot.Folder, "profile", "secrets.json")));
        File.WriteAllText(paths.Settings, "{\"SchemaVersion\":2,\"SyncIntervalMinutes\":42}");
        File.WriteAllText(paths.Secrets, "new secret payload");
        File.WriteAllText(Path.Combine(paths.MimeCache, "message.eml"), "new MIME");
        File.WriteAllText(Path.Combine(paths.MimeCache, "added.eml"), "added MIME");
        Sql(paths.Database, "UPDATE drafts SET text='new draft'; PRAGMA user_version=12");
        VersionBackup.StageRestore(paths, snapshot.Id, UpdateInstallKinds.WindowsPerUser);
        Assert.Throws<InvalidDataException>(() => VersionBackup.ApplyPending(paths, V("11.0.0")));
        Assert.Equal("new draft", Scalar(paths.Database, "SELECT text FROM drafts"));
        Assert.True(VersionBackup.ApplyPending(paths, V("11.0.0-beta.1")));
        Assert.Equal("original draft", Scalar(paths.Database, "SELECT text FROM drafts"));
        Assert.Equal(11L, Scalar(paths.Database, "PRAGMA user_version"));
        Assert.Equal("encrypted-secret-payload", File.ReadAllText(paths.Secrets));
        Assert.Contains("\"SyncIntervalMinutes\":5", File.ReadAllText(paths.Settings));
        Assert.Equal("original MIME", File.ReadAllText(Path.Combine(paths.MimeCache, "message.eml")));
        Assert.False(File.Exists(Path.Combine(paths.MimeCache, "added.eml")));
        Assert.False(VersionBackup.ApplyPending(paths, V("11.0.0-beta.1")));
        VersionBackup.Validate(VersionBackup.Read(paths, snapshot.Id));
    }

    [Fact]
    public void Corrupt_snapshot_and_cross_install_restore_are_rejected_without_touching_live_profile()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        Assert.Throws<InvalidDataException>(() => VersionBackup.StageRestore(paths, snapshot.Id, UpdateInstallKinds.MacApp));
        File.WriteAllText(Path.Combine(snapshot.Folder, "profile", "secrets.json"), "tampered");
        Assert.Throws<InvalidDataException>(() => VersionBackup.StageRestore(paths, snapshot.Id));
        Assert.Equal("encrypted-secret-payload", File.ReadAllText(paths.Secrets));
        Assert.False(VersionBackup.ApplyPending(paths, V("11.0.0")));
    }

    [Fact]
    public void Failed_restore_recovers_files_already_changed_and_keeps_pending_request()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        File.WriteAllText(paths.Settings, "{\"SchemaVersion\":1,\"SyncIntervalMinutes\":42}");
        File.Delete(paths.Secrets);
        Directory.CreateDirectory(paths.Secrets); // reject the second replacement, after settings was changed
        VersionBackup.StageRestore(paths, snapshot.Id);
        Assert.ThrowsAny<IOException>(() => VersionBackup.ApplyPending(paths, V("11.0.0")));
        Assert.Contains("42", File.ReadAllText(paths.Settings));
        Assert.Equal("original draft", Scalar(paths.Database, "SELECT text FROM drafts"));
        Directory.Delete(paths.Secrets);
        Assert.True(VersionBackup.ApplyPending(paths, V("11.0.0")));
        Assert.Equal("encrypted-secret-payload", File.ReadAllText(paths.Secrets));
    }

    [Fact]
    public void Settings_and_version_restore_cannot_silently_overwrite_each_other()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        File.WriteAllText(Path.Combine(paths.Root, SettingsBackup.PendingFile), "pending");
        Assert.Throws<InvalidOperationException>(() => VersionBackup.StageRestore(paths, snapshot.Id));
        File.Delete(Path.Combine(paths.Root, SettingsBackup.PendingFile));
        VersionBackup.StageRestore(paths, snapshot.Id);
        File.WriteAllText(Path.Combine(paths.Root, SettingsBackup.PendingFile), "later pending");
        Assert.Throws<InvalidOperationException>(() => VersionBackup.ApplyPending(paths, V("11.0.0")));
        VersionBackup.CancelPending(paths);
        File.Delete(Path.Combine(paths.Root, SettingsBackup.PendingFile));
        Assert.False(VersionBackup.ApplyPending(paths, V("11.0.0")));
    }

    [Fact]
    public void Additional_application_files_cannot_be_injected_into_a_saved_bundle()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var app = dir.File("Magpie.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "Magpie"), "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), app);
        File.WriteAllText(Path.Combine(snapshot.ApplicationPath, "injected-library.dylib"), "extra");
        Assert.Throws<InvalidDataException>(() => VersionBackup.StageRestore(paths, snapshot.Id, UpdateInstallKinds.MacApp));
    }

    [Fact]
    public void Custom_mail_folder_restores_to_its_original_location_without_touching_a_new_folder()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var custom = dir.File("separate-mail");
        Directory.CreateDirectory(custom);
        File.Move(paths.Database, Path.Combine(custom, "mail.db"));
        Directory.Move(paths.MimeCache, Path.Combine(custom, "messages"));
        paths.SetMailRoot(custom);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        Sql(paths.Database, "UPDATE drafts SET text='changed'");
        var other = dir.File("other-mail"); Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep.txt"), "keep");
        paths.SetMailRoot(other);
        VersionBackup.StageRestore(paths, snapshot.Id);
        Assert.True(VersionBackup.ApplyPending(paths, V("11.0.0")));
        Assert.Equal(custom, paths.MailRoot);
        Assert.Equal("original draft", Scalar(paths.Database, "SELECT text FROM drafts"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(other, "keep.txt")));
    }

    [Fact]
    public void Mac_bundle_backups_keep_executable_modes_and_internal_framework_links()
    {
        if (OperatingSystem.IsWindows()) return; // symlink privileges are not required of a Windows test account
        using var dir = new TempDir();
        var paths = Profile(dir);
        var app = dir.File("Magpie.app");
        var executable = Path.Combine(app, "Contents", "MacOS", "Magpie");
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "app");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
        File.SetUnixFileMode(executable, mode);
        File.CreateSymbolicLink(Path.Combine(app, "Contents", "app-link"), "MacOS/Magpie");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), app);
        Assert.Equal(mode, File.GetUnixFileMode(Path.Combine(snapshot.ApplicationPath, "Contents", "MacOS", "Magpie")));
        Assert.Equal("MacOS/Magpie", new FileInfo(Path.Combine(snapshot.ApplicationPath, "Contents", "app-link")).LinkTarget);
        VersionBackup.Validate(snapshot);
    }

    [Fact]
    public void History_marks_success_only_after_the_selected_version_starts()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var success = UpdateHistory.Record(paths, "11.0.0-beta.1", "11.0.0", "Update", UpdateHistory.Installing, "Restarting", "snapshot");
        var interrupted = UpdateHistory.Record(paths, "11.0.0-beta.1", "12.0.0", "Update", UpdateHistory.Downloading, "Downloading");
        UpdateHistory.CompleteStartup(paths, V("11.0.0"));
        var records = UpdateHistory.List(paths).ToDictionary(e => e.Id);
        Assert.Equal(UpdateHistory.Succeeded, records[success].Status);
        Assert.Equal(UpdateHistory.Failed, records[interrupted].Status);
        Assert.Equal("snapshot", records[success].SnapshotId);
    }

    [Fact]
    public async Task Version_stop_drains_engine_workers_and_prevents_starting_new_work()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await engine.StopForVersionChangeAsync(timeout.Token);
        Assert.True(engine.IsStoppedForVersionChange);
        Assert.Throws<OperationCanceledException>(engine.Start);
        await Assert.ThrowsAsync<OperationCanceledException>(() => engine.ProcessOutboxAsync(default));
        Assert.False(engine.SendNow(123));
        await engine.StopForVersionChangeAsync(timeout.Token);
        Assert.True(File.Exists(engine.Paths.Settings));
    }

    [Fact]
    public async Task Late_draft_autosave_and_send_callbacks_cannot_change_a_stopped_profile()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        engine.Config.Accounts.Add(new Magpie.Core.Models.Account { Id = "a", Email = "me@example.test", Enabled = false });
        var before = new Magpie.Core.Mail.Draft { AccountId = "a", To = "you@example.test", Subject = "Saved", Html = "Keep this draft" };
        engine.SaveLocalDraft(before, pendingUpload: false);
        await engine.StopForVersionChangeAsync();
        var late = new Magpie.Core.Mail.Draft { AccountId = "a", To = "you@example.test", Subject = "Too late", Html = "Do not save" };
        Assert.Throws<OperationCanceledException>(() => engine.SaveLocalDraft(late, pendingUpload: false));
        Assert.Throws<OperationCanceledException>(() => engine.QueueSend(late, DateTimeOffset.Now, null));
        Assert.Single(engine.LocalDrafts());
        Assert.Equal("Saved", engine.LocalDrafts()[0].Subject);
        Assert.Empty(engine.Outbox());
    }

    [Fact]
    public async Task Version_stop_waits_for_admitted_ai_connection_test_and_rejects_late_tests()
    {
        using var dir = new TempDir();
        var handler = new DelayedAiHttp();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(), handler);
        var candidate = new AiSettings
        {
            Provider = AiProviderKind.Custom, Endpoint = "https://ai.example.test/v1", Model = "test-model",
        };
        var test = engine.TestAiAsync(candidate, null);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = engine.StopForVersionChangeAsync(timeout.Token);
        try
        {
            Assert.False(stopped.IsCompleted);
            await Assert.ThrowsAsync<OperationCanceledException>(() => engine.TestAiAsync(candidate, null));
            Assert.Equal(1, handler.Requests);
        }
        finally { handler.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => test);
        await stopped;
        Assert.True(engine.IsStoppedForVersionChange);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Profile_workflow_is_cancelled_and_drained_before_version_snapshot()
    {
        using var dir = new TempDir();
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workflow = engine.WithProfileOperationAsync<int>(async token =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException)
            {
                cancellationSeen.TrySetResult();
                await finishCleanup.Task;
            }
            return 42;
        });
        await entered.Task;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopped = engine.StopForVersionChangeAsync(timeout.Token);
        await cancellationSeen.Task.WaitAsync(timeout.Token);
        try
        {
            Assert.False(stopped.IsCompleted);
            await Assert.ThrowsAsync<OperationCanceledException>(() => engine.WithProfileOperationAsync(_ => Task.CompletedTask));
        }
        finally { finishCleanup.TrySetResult(); }
        Assert.Equal(42, await workflow);
        await stopped;
        Assert.True(engine.IsStoppedForVersionChange);
    }

    private sealed class DelayedAiHttp : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            Entered.TrySetResult();
            // A slow handler may finish after cancellation. The engine must wait for this admitted call to settle.
            await Release.Task;
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}") };
        }
    }

    [Fact]
    public async Task Version_operation_gate_waits_for_inflight_work_and_rejects_late_work()
    {
        var operations = new VersionChangeOperations();
        var active = operations.Enter();
        var stopped = operations.StopAsync(default);
        Assert.False(stopped.IsCompleted);
        using (operations.Enter()) { } // cleanup nested in the admitted action may still finish
        active.Dispose();
        await stopped.WaitAsync(TimeSpan.FromSeconds(1));
        active.Dispose(); // a double release cannot allow a later operation
        Assert.Throws<OperationCanceledException>(() => operations.Enter());
    }

    [Fact]
    public async Task Cancelled_wait_for_version_stop_does_not_allow_an_incomplete_snapshot()
    {
        var operations = new VersionChangeOperations();
        using var active = operations.Enter();
        using var cancellation = new CancellationTokenSource();
        var stopped = operations.StopAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopped);
        var waitAgain = operations.StopAsync(default);
        Assert.False(waitAgain.IsCompleted);
        active.Dispose();
        await waitAgain.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Admitted_async_action_can_finish_nested_cleanup_but_unrelated_work_is_blocked()
    {
        var operations = new VersionChangeOperations();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = false;
        var action = Task.Run(async () =>
        {
            using var admission = operations.Enter();
            entered.SetResult();
            await finish.Task;
            using var cleanup = operations.Enter();
            cleaned = true;
        });
        await entered.Task;
        var stopped = operations.StopAsync(default);
        Assert.False(stopped.IsCompleted);
        Assert.Throws<OperationCanceledException>(() => operations.Enter());
        finish.SetResult();
        await action;
        await stopped;
        Assert.True(cleaned);
        Assert.Throws<OperationCanceledException>(() => operations.Enter());
    }

    [Fact]
    public void Corrupt_recovery_copy_is_rejected_before_any_live_file_is_overwritten()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        var suffix = ".version-restore-" + snapshot.Id;
        var destinations = new[] { paths.Settings, paths.Secrets, Path.Combine(paths.Root, AppPaths.MailFolderFile),
            Path.Combine(paths.Root, "version-pin.txt"), paths.Database, paths.Database + "-wal", paths.Database + "-shm", paths.MimeCache };
        var original = File.ReadAllBytes(paths.Settings);
        File.WriteAllText(paths.Settings + suffix + ".before", "corrupt recovery copy");
        var items = destinations.Select(d => new
        {
            Destination = d, Source = (string?)null, Directory = d == paths.MimeCache,
            Existed = d == paths.Settings, Backup = d + suffix + ".before", Staged = d + suffix + ".next",
            BackupHash = d == paths.Settings ? Hash(original) : "", StagedHash = "",
        });
        var journal = Path.Combine(paths.Root, "version-restore-journal.json");
        File.WriteAllText(journal, JsonSerializer.Serialize(new { SnapshotId = snapshot.Id, ProfileRoot = paths.Root, MailRoot = paths.MailRoot, Items = items }));
        Assert.Throws<InvalidDataException>(() => VersionBackup.ApplyPending(paths, V("11.0.0")));
        Assert.Equal(original, File.ReadAllBytes(paths.Settings));
        Assert.True(File.Exists(journal));
        Assert.Equal("original draft", Scalar(paths.Database, "SELECT text FROM drafts"));
    }

    [Fact]
    public void Database_cannot_be_omitted_from_the_snapshot_hash_inventory()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        snapshot.Files.RemoveAll(f => f.Path == "mail/mail.db");
        Assert.Throws<InvalidDataException>(() => VersionBackup.Validate(snapshot));
    }

    [Fact]
    public void Missing_active_database_cannot_be_recorded_as_an_empty_profile_snapshot()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "current application");
        var settings = File.ReadAllBytes(paths.Settings);
        File.Delete(paths.Database);
        Assert.Throws<FileNotFoundException>(() => VersionBackup.Create(paths, V("11.0.0"), exe));
        Assert.Empty(VersionBackup.List(paths));
        Assert.Equal(settings, File.ReadAllBytes(paths.Settings));
        Assert.Equal("current application", File.ReadAllText(exe));
    }

    [Fact]
    public void Disconnected_custom_mail_folder_stops_backup_without_losing_the_saved_mail()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var custom = dir.File("mail-drive"); Directory.CreateDirectory(custom);
        File.Move(paths.Database, Path.Combine(custom, "mail.db"));
        Directory.Move(paths.MimeCache, Path.Combine(custom, "messages"));
        paths.SetMailRoot(custom);
        var offline = custom + "-disconnected";
        Directory.Move(custom, offline);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "current application");
        Assert.Throws<DirectoryNotFoundException>(() => VersionBackup.Create(paths, V("11.0.0"), exe));
        Assert.Empty(VersionBackup.List(paths));
        Assert.Equal("original draft", Scalar(Path.Combine(offline, "mail.db"), "SELECT text FROM drafts"));
        Directory.Move(offline, custom);
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        Assert.Contains(snapshot.Files, file => file.Path == "mail/mail.db" && file.Kind == "file" && file.Sha256.Length == 64);
    }

    [Fact]
    public void New_profile_with_no_accounts_still_has_a_complete_restorable_database()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.File("new-profile"));
        using var engine = new MailEngine(paths, new FakeProtector());
        engine.Settings.Save();
        Assert.Empty(engine.Accounts);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "current application");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        VersionBackup.Validate(snapshot);
        Assert.Equal((long)MailStore.SchemaVersion, Scalar(Path.Combine(snapshot.Folder, "mail", "mail.db"), "PRAGMA user_version"));
    }

    [Fact]
    public void Snapshot_with_database_removed_from_both_files_and_inventory_is_never_restorable()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "current application");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        snapshot.Files.RemoveAll(file => file.Path == "mail/mail.db");
        File.Delete(Path.Combine(snapshot.Folder, "mail", "mail.db"));
        File.WriteAllText(Path.Combine(snapshot.Folder, "snapshot.json"), JsonSerializer.Serialize(snapshot));
        Assert.Throws<InvalidDataException>(() => VersionBackup.StageRestore(paths, snapshot.Id));
        Assert.Null(VersionBackup.PendingVersion(paths));
        Assert.Equal("original draft", Scalar(paths.Database, "SELECT text FROM drafts"));
    }

    [Fact]
    public void Live_wal_database_is_backed_up_consistently_with_committed_drafts()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString());
        writer.Open();
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; INSERT INTO drafts VALUES('committed WAL draft');";
        command.ExecuteNonQuery();
        Assert.True(File.Exists(paths.Database + "-wal"));
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0"), exe);
        Assert.Equal(2L, Scalar(Path.Combine(snapshot.Folder, "mail", "mail.db"), "SELECT COUNT(*) FROM drafts"));
        VersionBackup.Validate(snapshot);
    }

    [Fact]
    public void Corrupt_history_row_does_not_block_valid_startup_and_recovery_history()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var valid = UpdateHistory.Record(paths, "10.0.0", "11.0.0", "Update", UpdateHistory.Installing, "Starting");
        var invalid = Path.Combine(paths.Root, "version-history", "events", Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(invalid, "{broken");
        UpdateHistory.CompleteStartup(paths, V("11.0.0"));
        var rows = UpdateHistory.List(paths);
        Assert.Equal(2, rows.Count);
        Assert.Equal(UpdateHistory.Succeeded, rows.Single(r => r.Id == valid).Status);
        Assert.Contains(rows, r => r.Action == "History record" && r.Status == UpdateHistory.Failed);
    }

    [Fact]
    public void Pending_restore_cancellation_requires_the_exact_interrupted_target()
    {
        using var dir = new TempDir();
        var paths = Profile(dir);
        var exe = dir.File("Magpie.exe"); File.WriteAllText(exe, "app");
        var snapshot = VersionBackup.Create(paths, V("11.0.0-beta.2"), exe);
        VersionBackup.StageRestore(paths, snapshot.Id);
        Assert.Equal("11.0.0-beta.2", VersionBackup.PendingVersion(paths));
        Assert.False(VersionBackup.CancelPendingForVersion(paths, "11.0.0"));
        Assert.Equal("11.0.0-beta.2", VersionBackup.PendingVersion(paths));
        Assert.True(VersionBackup.CancelPendingForVersion(paths, "11.0.0-beta.2"));
        Assert.Null(VersionBackup.PendingVersion(paths));
    }

    private static AppPaths Profile(TempDir dir)
    {
        var paths = new AppPaths(dir.File("profile"));
        File.WriteAllText(paths.Settings, "{\"SchemaVersion\":1,\"SyncIntervalMinutes\":5}");
        File.WriteAllText(paths.Secrets, "encrypted-secret-payload");
        File.WriteAllText(Path.Combine(paths.MimeCache, "message.eml"), "original MIME");
        Sql(paths.Database, "CREATE TABLE drafts(text TEXT); INSERT INTO drafts VALUES('original draft'); PRAGMA user_version=11;");
        return paths;
    }

    private static void Sql(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static object? Scalar(string path, string sql)
    {
        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        c.Open(); using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }
    private sealed class FilesHttp : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new();
        public List<string> Requests { get; } = [];
        public string? InterruptedUrl { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString(); Requests.Add(url);
            if (url == InterruptedUrl) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream()) });
            return Task.FromResult(Files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
    private sealed class InterruptedStream : MemoryStream
    {
        public InterruptedStream() : base([1, 2, 3]) { }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position > 0) throw new IOException("The simulated connection was interrupted.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Fact]
    public void VersionHistorySeparatesNewerOlderStableAndBeta()
    {
        static (ReleaseInfo Info, bool Prerelease) Version(string version, bool beta) =>
            (new ReleaseInfo { Version = AppVersion.TryParse(version)! }, beta);
        var versions = new[]
        {
            Version("7.0.0", false),
            Version("7.1.0-beta.2", true),
            Version("7.1.0", false),
            Version("6.9.0-beta.1", true),
            Version("6.8.0", false),
        };
        var current = AppVersion.TryParse("7.0.0")!;

        Assert.Equal(new[] { "7.1.0", "7.1.0-beta.2" },
            UpdateClient.SelectVersions(versions, current, older: false, includePrerelease: true)
                .Select(x => x.Info.Version.ToString()));
        Assert.Equal(new[] { "7.1.0" },
            UpdateClient.SelectVersions(versions, current, older: false, includePrerelease: false)
                .Select(x => x.Info.Version.ToString()));
        Assert.Equal(new[] { "6.9.0-beta.1", "6.8.0" },
            UpdateClient.SelectVersions(versions, current, older: true, includePrerelease: true)
                .Select(x => x.Info.Version.ToString()));
    }
}
