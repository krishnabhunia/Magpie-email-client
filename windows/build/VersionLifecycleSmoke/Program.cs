using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Storage;
using Magpie.Core.Updates;
using Microsoft.Data.Sqlite;

namespace VersionLifecycleSmoke;

/// <summary>
/// Native, offline process tests. The fixture executable is the program being swapped; every profile and named
/// Windows object belongs to a unique temporary case. No production test switch or default user profile is used.
/// </summary>
internal static class Program
{
    private static readonly AppVersion OriginalVersion = AppVersion.TryParse("11.0.0-beta.701")!;
    private static readonly AppVersion SelectedVersion = AppVersion.TryParse("11.0.0-beta.702")!;
    private const string AccountId = "native-lifecycle-fixture";
    private const string SavedSecret = "offline-fixture-secret";
    private const int OwnershipTimeoutExit = 23;
    private static int _assertions;
    private static string Fixture => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "LifecycleFixture").Value!;

    [STAThread]
    public static int Main(string[] args)
    {
        string? suiteRoot = null;
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Run this process smoke on Windows.");
            if (Option(args, "--mode") is { } mode)
            {
                var file = Option(args, "--case") ?? throw new ArgumentException("A child needs an isolated case file.");
                var test = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(file)) ?? throw new InvalidDataException("Missing case.");
                return mode switch
                {
                    "blocked" => BlockedChild(test),
                    "update-parent" => UpdateParent(test),
                    "failed-start" => FailedStartup(test),
                    "restored-start" => RestoredStartup(test),
                    _ => throw new ArgumentException("Unknown fixture mode."),
                };
            }

            Assert(Fixture == "original", "The suite starts from the original fixture");
            var selected = Path.GetFullPath(Option(args, "--selected-exe") ?? throw new ArgumentException("Supply --selected-exe."));
            Assert(File.Exists(selected), "The selected fixture was published");
            Assert(Hash(Self) != Hash(selected), "Original and selected executable payloads have different SHA-256 hashes");
            suiteRoot = Path.Combine(Path.GetTempPath(), "magpie-version-lifecycle-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(suiteRoot);
            CheckBlockedChild(TestCase.Create(suiteRoot, "blocked", selected));
            CheckUnavailableMutex(TestCase.Create(suiteRoot, "unavailable", selected));
            CheckPairedRecovery(TestCase.Create(suiteRoot, "recovery", selected));
            Console.WriteLine("VERSION_LIFECYCLE_SMOKE_PASSED assertions=" + _assertions);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("VERSION_LIFECYCLE_SMOKE_FAILED fixture=" + Fixture + " " + ex);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (suiteRoot != null)
                try { Directory.Delete(suiteRoot, recursive: true); }
                catch (Exception ex) { Console.Error.WriteLine("Temporary fixture cleanup: " + ex.Message); }
        }
    }

    private static void CheckBlockedChild(TestCase test)
    {
        using var parent = WindowsStartup.Acquire(false, false, test.MutexName, test.StartedEvent);
        Assert(parent.OwnsInstance, "The parent owns the isolated single-instance mutex");
        using var started = WindowsStartup.CreateAcknowledgement(test.StartedEvent);
        using var child = Child.Start(test.SelectedExe, "blocked", test);
        Assert(started.WaitOne(TimeSpan.FromSeconds(25)), "The child acknowledges startup while the parent still owns the mutex");
        child.ExpectExit(OwnershipTimeoutExit);
        Assert(parent.OwnsInstance, "The parent kept ownership until the child timed out");
        Assert(!Directory.Exists(test.Profile), "A child without ownership never opens or creates the profile");
        Console.WriteLine("PASS acknowledgement-before-lock-and-timeout");
    }

    private static int BlockedChild(TestCase test)
    {
        using var startup = WindowsStartup.Acquire(true, false, test.MutexName, test.StartedEvent, TimeSpan.FromSeconds(2));
        if (!startup.OwnsInstance) return OwnershipTimeoutExit;
        // Like Program.Main, profile initialization is reachable only after ownership. A broken gate makes this
        // observable even if a scheduler happens to delay the child until after its acknowledgement is received.
        _ = new AppPaths(test.Profile);
        throw new InvalidOperationException("The child acquired a mutex still held by its parent.");
    }

    private static void CheckUnavailableMutex(TestCase test)
    {
        using var collision = WindowsStartup.CreateAcknowledgement(test.MutexName);
        using var started = WindowsStartup.CreateAcknowledgement(test.StartedEvent);
        var rejected = false;
        try { using var unexpected = WindowsStartup.Acquire(true, false, test.MutexName, test.StartedEvent); }
        catch (WaitHandleCannotBeOpenedException) { rejected = true; }
        Assert(rejected, "An unavailable mutex fails closed instead of granting profile access");
        Assert(!started.WaitOne(0), "An unavailable mutex cannot acknowledge that the replacement is ready to wait");
        Assert(!Directory.Exists(test.Profile), "Synchronization failure leaves the profile unopened");
        Console.WriteLine("PASS unavailable-mutex-fails-closed");
    }

    private static void CheckPairedRecovery(TestCase test)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(test.Exe)!);
        File.Copy(Self, test.Exe);
        using (var parent = Child.Start(test.Exe, "update-parent", test)) parent.ExpectExit(0, 120);
        Assert(File.Exists(test.Report("handoff-acknowledged")), "The running original process observed the selected child's acknowledgement");
        Assert(File.Exists(test.Report("recovery-waited")), "The saved application waited for the failed process to release the profile");
        Assert(File.Exists(test.Report("restored")), "The saved application completed its paired data restore");
        Assert(Hash(test.Exe) == Hash(Self), "The installed executable is byte-for-byte the original fixture after recovery");
        var paths = new AppPaths(test.Profile);
        var history = UpdateHistory.List(paths);
        Assert(history.Count(e => e.Action == "Update" && e.Status == UpdateHistory.Failed) == 1,
            "The failed startup stays recorded as a failed transition");
        Assert(history.Count(e => e.Action == "Restore" && e.Status == UpdateHistory.Restored) == 1,
            "Recovery is completed only after the saved application loads its paired profile");
        Assert(!history.Any(e => e.Status == UpdateHistory.Succeeded), "Process acknowledgement alone never records startup success");
        Console.WriteLine("PASS running-executable-and-paired-profile-recovery");
    }

    private static int UpdateParent(TestCase test)
    {
        Assert(Fixture == "original", "The process being replaced is the original executable");
        using var ownership = WindowsStartup.Acquire(false, false, test.MutexName, test.StartedEvent);
        Assert(ownership.OwnsInstance, "The original process owns its profile before preparation");
        var paths = new AppPaths(test.Profile);
        using var http = new RejectHttp();
        using var engine = new MailEngine(paths, new DpapiProtector(), http);
        engine.Config.Accounts.Add(new Account { Id = AccountId, Email = "fixture@example.test", SignatureDefaultApplied = true });
        engine.Config.SyncIntervalMinutes = 5;
        engine.Config.Updates.AutoUpdate = engine.Config.Updates.AutoCheck = engine.Config.Updates.AutoDownload = true;
        engine.Settings.Save(notify: false);
        engine.Vault.Set(SecretVault.PasswordKey(AccountId), SavedSecret);
        engine.SaveLocalDraft(new Draft
        {
            AccountId = AccountId, To = "recipient@example.test", Subject = "Saved local draft", Html = "<p>Original unsent text</p>",
        }, pendingUpload: false);
        var inbox = engine.Store.UpsertFolder(new MailFolder { AccountId = AccountId, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        var now = DateTimeOffset.UtcNow;
        engine.Store.InsertMessages([new MessageRow
        {
            AccountId = AccountId, FolderId = inbox, Uid = 1, MessageId = "saved-message", ThreadKey = "saved-message",
            FromAddress = "sender@example.test", Subject = "Saved mail", Date = now, SortDate = now,
        }]);
        File.WriteAllText(Path.Combine(paths.MimeCache, "saved.eml"), "Subject: Saved mail\r\n\r\nOriginal cached MIME");
        File.WriteAllText(PinPath(paths), "10.0.0");
        engine.StopForVersionChangeAsync().GetAwaiter().GetResult();
        var snapshot = VersionBackup.Create(paths, OriginalVersion, Self, installKind: UpdateInstallKinds.WindowsPerUser);
        Assert(VersionBackup.MatchesApplication(snapshot, Self), "The final snapshot contains the actual running executable");
        Assert(!File.ReadAllText(Path.Combine(snapshot.Folder, "profile", "secrets.json")).Contains(SavedSecret, StringComparison.Ordinal),
            "The snapshot retains DPAPI ciphertext without exposing plaintext secrets");
        var transitionId = UpdateHistory.Record(paths, OriginalVersion.ToString(), SelectedVersion.ToString(), "Update",
            UpdateHistory.Installing, "Native offline startup fixture.", snapshot.Id);

        using var pin = VersionPin.Prepare(engine.Settings, PinPath(paths), SelectedVersion);
        InstalledUpdate.Write(paths.Updates, OriginalVersion.ToString(), SelectedVersion.ToString());
        var exe = Self;
        SelfUpdate.Swap(exe, test.SelectedExe);
        Assert(Hash(exe) == Hash(test.SelectedExe), "The running executable was renamed and the exact selected bytes installed");
        Assert(VersionBackup.MatchesApplication(snapshot, SelfUpdate.BackupPathFor(exe)), "The renamed running original remains intact");
        // A real locked previous EXE makes the recovery path use its immutable snapshot fallback. This also avoids
        // depending on how quickly the original process exits after acknowledging a successful handoff.
        using var retainedPrevious = new FileStream(SelfUpdate.BackupPathFor(exe), FileMode.Open, FileAccess.Read, FileShare.Read);
        var before = ProfileFingerprint(paths);
        using var started = WindowsStartup.CreateAcknowledgement(test.StartedEvent);
        using var selected = Child.Start(exe, "failed-start", test);
        Assert(started.WaitOne(TimeSpan.FromSeconds(25)), "Selected startup acknowledges before waiting for the original process");
        Assert(!selected.HasExited, "Acknowledgement does not mean the selected process has completed startup");
        Assert(!File.Exists(test.Report("selected-profile-opened")), "The selected process has not crossed the ownership gate");
        Assert(ProfileFingerprint(paths) == before, "Acknowledgement leaves the complete profile unchanged while the parent owns it");
        Assert(UpdateHistory.List(paths).Single(e => e.Id == transitionId).Status == UpdateHistory.Installing,
            "History remains installing until profile and application startup complete");
        AssertLegacyOptOut(paths, SelectedVersion);
        pin.Commit();
        File.WriteAllText(test.Report("handoff-acknowledged"), "acknowledged before profile access");
        // Stay alive with the renamed original EXE loaded, but relinquish all profile writers and mutex ownership.
        ownership.Dispose();
        selected.ExpectExit(0, 90);
        Assert(http.Requests == 0, "Preparing the native fixture made no network requests");
        Console.WriteLine("UPDATE_PARENT_PASSED assertions=" + _assertions);
        return 0;
    }

    private static int FailedStartup(TestCase test)
    {
        Assert(Fixture == "selected", "The selected executable really started, rather than the saved original");
        using var ownership = WindowsStartup.Acquire(true, false, test.MutexName, test.StartedEvent);
        Assert(ownership.OwnsInstance, "Selected startup opens its profile only after mutex ownership");
        File.WriteAllText(test.Report("selected-profile-opened"), "owned");
        var paths = new AppPaths(test.Profile);
        VersionBackup.RecoverInterruptedRestore(paths);
        Assert(!VersionBackup.ApplyPending(paths, SelectedVersion), "A normal selected startup has no pending data restore");
        AssertLegacyOptOut(paths, SelectedVersion);
        var transition = UpdateHistory.List(paths).Single(e => e.Action == "Update");
        var snapshot = VersionBackup.Read(paths, transition.SnapshotId!);
        Assert(!VersionBackup.MatchesApplication(snapshot, Self), "The failed candidate differs from the saved application hash");
        using (var http = new RejectHttp())
        using (var engine = new MailEngine(paths, new DpapiProtector(), http))
        {
            var draft = engine.OpenLocalDraft(engine.LocalDrafts().Single().Id)!;
            draft.Subject = "Changed by failed startup";
            draft.Html = "<p>Changed unsent text</p>";
            engine.SaveLocalDraft(draft, pendingUpload: false);
            engine.Config.SyncIntervalMinutes = 42;
            engine.Config.SchemaVersion = 2;
            engine.Config.Updates.AutoUpdate = engine.Config.Updates.AutoCheck = engine.Config.Updates.AutoDownload = true;
            engine.Settings.Save(notify: false);
            engine.Vault.Set(SecretVault.PasswordKey(AccountId), "replacement-fixture-secret");
            File.WriteAllText(Path.Combine(paths.MimeCache, "saved.eml"), "Changed cached MIME");
            File.WriteAllText(Path.Combine(paths.MimeCache, "added.eml"), "Only in failed startup");
            engine.StopForVersionChangeAsync().GetAwaiter().GetResult();
            Assert(http.Requests == 0, "The intentionally failed startup made no network requests");
        }
        SqliteConnection.ClearAllPools();
        Sql(paths.Database, "UPDATE messages SET subject='Changed mail'; CREATE TABLE failed_start_marker(value TEXT); PRAGMA user_version=12;");
        Assert(Scalar(paths.Database, "PRAGMA user_version").Equals(12L), "The failed startup changed the live database schema");
        var changedSettings = Hash(paths.Settings);
        var changedDatabase = Hash(paths.Database);
        var recoveryId = "";
        try { throw new InvalidOperationException("Intentional fixture failure after the selected application changed its local data."); }
        catch (InvalidOperationException)
        {
            // OfferRollback calls this same production helper after the user accepts its recovery dialog.
            recoveryId = WindowsVersionRecovery.StageFailedStartup(paths, SelectedVersion, Self,
                UpdateInstallKinds.WindowsPerUser, transition, snapshot);
        }
        Assert(VersionBackup.MatchesApplication(snapshot, Self), "Recovery verifies the saved executable before relaunch");
        Assert(VersionBackup.PendingVersion(paths) == OriginalVersion.ToString(), "Recovery stages the matching saved data");
        Assert(Hash(paths.Settings) == changedSettings && Hash(paths.Database) == changedDatabase,
            "Staging application recovery leaves the failed process's live profile in place until restart");
        Assert(InstalledUpdate.Read(paths.Updates) == null, "The failed installed-version notice is cleared");
        Assert(UpdateHistory.List(paths).Single(e => e.Id == recoveryId).Status == UpdateHistory.Installing,
            "A staged recovery is not recorded as completed");

        using var attempted = WindowsStartup.CreateAcknowledgement(test.RestoreAttemptEvent);
        using var restored = Child.Start(Self, "restored-start", test);
        Assert(attempted.WaitOne(TimeSpan.FromSeconds(25)), "The recovered executable reaches its restart ownership gate");
        Assert(!File.Exists(test.Report("restored-profile-opened")), "The saved application cannot restore data while the failed process owns it");
        Assert(Hash(paths.Settings) == changedSettings && Hash(paths.Database) == changedDatabase,
            "The recovering child has not touched the newer settings or database before ownership");
        File.WriteAllText(test.Report("recovery-waited"), "waited before data restore");
        ownership.Dispose();
        restored.ExpectExit(0, 45);
        Console.WriteLine("FAILED_START_RECOVERY_PASSED assertions=" + _assertions);
        return 0;
    }

    private static int RestoredStartup(TestCase test)
    {
        Assert(Fixture == "original", "Recovery launches the saved original executable bytes");
        using (var attempted = EventWaitHandle.OpenExisting(test.RestoreAttemptEvent)) attempted.Set();
        using var ownership = WindowsStartup.Acquire(false, true, test.MutexName, test.StartedEvent);
        Assert(ownership.OwnsInstance, "Recovered startup owns the profile before applying any restore");
        File.WriteAllText(test.Report("restored-profile-opened"), "owned");
        var paths = new AppPaths(test.Profile);
        var failed = UpdateHistory.List(paths).Single(e => e.Action == "Update");
        var snapshot = VersionBackup.Read(paths, failed.SnapshotId!);
        VersionBackup.RecoverInterruptedRestore(paths);
        Assert(VersionBackup.ApplyPending(paths, OriginalVersion), "The matching saved application applies the staged paired restore");
        Assert(Hash(paths.Database) == Hash(Path.Combine(snapshot.Folder, "mail", "mail.db")),
            "The restored database matches the immutable snapshot before MailEngine opens it");
        Assert(Hash(paths.Settings) == Hash(Path.Combine(snapshot.Folder, "profile", "settings.json")),
            "The original settings are recovered as part of the same restore");
        Assert(Hash(paths.Secrets) == Hash(Path.Combine(snapshot.Folder, "profile", "secrets.json")),
            "Encrypted credentials are restored byte-for-byte with the paired profile");
        Assert(Hash(Path.Combine(paths.MimeCache, "saved.eml")) == Hash(Path.Combine(snapshot.Folder, "mail", "messages", "saved.eml"))
            && !File.Exists(Path.Combine(paths.MimeCache, "added.eml")), "MIME cache recovery removes files introduced by the failed startup");
        Assert(Scalar(paths.Database, "PRAGMA user_version").Equals((long)MailStore.SchemaVersion), "The saved database schema is restored");
        Assert(Scalar(paths.Database, "SELECT COUNT(*) FROM sqlite_master WHERE name='failed_start_marker'").Equals(0L),
            "The newer schema's table cannot leak into the restored version");
        Assert(File.ReadAllText(PinPath(paths)) == "10.0.0", "The snapshot included the original pin before reapplying the recovery choice");
        using var http = new RejectHttp();
        using var engine = new MailEngine(paths, new DpapiProtector(), http);
        // This is the same post-restore pin operation used by App.OnStartup, before checks or background sync.
        using (var protection = VersionPin.Prepare(engine.Settings, PinPath(paths), OriginalVersion)) protection.Commit();
        AssertLegacyOptOut(paths, OriginalVersion);
        Assert(engine.Config.SyncIntervalMinutes == 5 && engine.Config.SchemaVersion == 1,
            "Recovery preserves unrelated saved settings while disabling legacy automatic installation");
        var draft = engine.LocalDrafts().Single();
        Assert(draft.Subject == "Saved local draft" && engine.OpenLocalDraft(draft.Id)!.Html.Contains("Original unsent text", StringComparison.Ordinal),
            "The saved unsent draft survives the failed startup and paired recovery");
        Assert(Scalar(paths.Database, "SELECT subject FROM messages").Equals("Saved mail"), "Saved mail is restored with the draft");
        Assert(engine.Vault.Get(SecretVault.PasswordKey(AccountId)) == SavedSecret, "The restored DPAPI secret still decrypts for the same Windows user");
        Assert(VersionBackup.PendingVersion(paths) == null, "Successful data restoration clears its pending marker");
        Assert(!VersionBackup.ApplyPending(paths, OriginalVersion), "A completed restore is not applied twice");
        UpdateHistory.CompleteStartup(paths, OriginalVersion);
        Assert(UpdateHistory.List(paths).Single(e => e.Action == "Restore").Status == UpdateHistory.Restored,
            "The recovered application records completion after its database and settings load");
        VersionBackup.Validate(snapshot);
        Assert(http.Requests == 0, "The recovered startup made no network requests");
        File.WriteAllText(test.Report("restored"), "saved application and paired profile verified");
        Console.WriteLine("RESTORED_START_PASSED assertions=" + _assertions);
        return 0;
    }

    private static void AssertLegacyOptOut(AppPaths paths, AppVersion version)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(paths.Settings), AppSettings.Json)!;
        Assert(settings.Updates.AutoUpdate == false && !settings.Updates.AutoCheck && !settings.Updates.AutoDownload,
            "All three persisted legacy automatic-update flags are false");
        Assert(File.ReadAllText(PinPath(paths)) == version.ToString(), "The persisted pin identifies the exact selected version");
    }

    private static string ProfileFingerprint(AppPaths paths) => string.Join("\n", Directory.GetFiles(paths.Root, "*", SearchOption.AllDirectories)
        .OrderBy(file => file, StringComparer.Ordinal).Select(file => Path.GetRelativePath(paths.Root, file) + ":" + Hash(file)));

    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Sql(string database, string sql)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static object Scalar(string database, string sql)
    {
        using var connection = Open(database);
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()!;
    }

    private static string Self => Environment.ProcessPath ?? throw new InvalidOperationException("The fixture needs an apphost executable.");
    private static string PinPath(AppPaths paths) => Path.Combine(paths.Root, "version-pin.txt");
    private static string? Option(string[] args, string key) => Array.IndexOf(args, key) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }

    private sealed record TestCase(string Root, string MutexName, string StartedEvent, string RestoreAttemptEvent, string SelectedExe)
    {
        public string File => Path.Combine(Root, "case.json");
        public string Profile => Path.Combine(Root, "profile");
        public string Exe => Path.Combine(Root, "app", "Magpie.exe");
        public string Report(string name) => Path.Combine(Root, name + ".txt");
        public static TestCase Create(string suiteRoot, string name, string selected)
        {
            var id = Guid.NewGuid().ToString("N");
            var prefix = @"Local\Magpie.VersionLifecycleSmoke." + id;
            var test = new TestCase(Path.Combine(suiteRoot, name), prefix + ".Mutex", prefix + ".Started", prefix + ".Restore", selected);
            Directory.CreateDirectory(test.Root);
            System.IO.File.WriteAllText(test.File, JsonSerializer.Serialize(test));
            return test;
        }
    }

    private sealed class Child : IDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _output;
        private readonly Task<string> _error;
        private readonly string _mode;
        private Child(Process process, string mode)
        {
            _process = process; _mode = mode;
            _output = process.StandardOutput.ReadToEndAsync();
            _error = process.StandardError.ReadToEndAsync();
        }
        public bool HasExited => _process.HasExited;
        public static Child Start(string exe, string mode, TestCase test)
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = test.Root,
            };
            start.ArgumentList.Add("--mode"); start.ArgumentList.Add(mode);
            start.ArgumentList.Add("--case"); start.ArgumentList.Add(test.File);
            start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(test.Root, "bundle");
            return new Child(Process.Start(start) ?? throw new InvalidOperationException("The fixture child did not start."), mode);
        }
        public void ExpectExit(int code, int seconds = 30)
        {
            if (!_process.WaitForExit(checked(seconds * 1000))) throw new TimeoutException("The " + _mode + " fixture did not finish.");
            var output = _output.GetAwaiter().GetResult();
            var error = _error.GetAwaiter().GetResult();
            if (output.Length > 0) Console.Write(output);
            if (error.Length > 0) Console.Error.Write(error);
            Assert(_process.ExitCode == code, _mode + " exited with " + _process.ExitCode + "; expected " + code);
        }
        public void Dispose()
        {
            try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(5000); } }
            finally { _process.Dispose(); }
        }
    }

    private sealed class RejectHttp : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            throw new InvalidOperationException("The native lifecycle fixtures must remain offline.");
        }
    }
}
