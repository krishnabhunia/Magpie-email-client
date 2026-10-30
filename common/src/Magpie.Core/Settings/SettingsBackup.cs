using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpie.Core.Security;

namespace Magpie.Core.Settings;

/// <summary>
/// Design EX1: one file with every setting — settings.json (accounts, rules, signatures, quick replies, templates,
/// tags, Gatekeeper lists, look, layout, updates …), every secret (passwords, sign-ins, AI key) and where the mail is
/// kept — locked with a password (PBKDF2-SHA256 → AES-256-GCM). Emails are not in it.
/// Restoring is done at the next start (<see cref="StagePending"/> / <see cref="ApplyPending"/>), before anything is
/// loaded, so nothing running can write the old settings back.
/// </summary>
public static class SettingsBackup
{
    public const string Extension = ".magpie-backup";
    public const string Format = "magpie-settings-backup";
    public const int MinPasswordLength = 8;
    private const int Iterations = 600_000;
    /// <summary>A restore waiting for the next start: the unlocked backup, protected for this Windows user (DPAPI).</summary>
    public const string PendingFile = "restore-pending.bin";
    public const string RestoreJournal = "restore-transaction.json";

    public sealed class Contents
    {
        public string SettingsJson { get; set; } = "{}";
        public Dictionary<string, string> Secrets { get; set; } = new();
        /// <summary>The chosen mail folder, or "" for the default one.</summary>
        public string MailFolder { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public DateTimeOffset Created { get; set; }
    }

    public sealed class WrongPasswordException : Exception
    {
        public WrongPasswordException() : base("That password doesn't open this backup.") { }
    }

    /// <summary>What a backup holds, in plain words ("3 accounts · 12 rules …") for the restore dialog.</summary>
    public static List<string> Describe(Contents c)
    {
        var lines = new List<string>();
        try
        {
            var s = JsonSerializer.Deserialize<AppSettings>(c.SettingsJson, AppSettings.Json) ?? new AppSettings();
            if (s.Accounts?.Count > 0) lines.Add(string.Join(" · ", s.Accounts.Select(a => a.Email)));
            string N(int n, string one, string many) => n + " " + (n == 1 ? one : many);
            var bits = new List<string>();
            if (s.Rules?.Count > 0) bits.Add(N(s.Rules.Count, "rule", "rules"));
            if (s.QuickReplies?.Count > 0) bits.Add(N(s.QuickReplies.Count, "quick reply", "quick replies"));
            if (s.Templates?.Count > 0) bits.Add(N(s.Templates.Count, "template", "templates"));
            if (s.Tags?.Count > 0) bits.Add(N(s.Tags.Count, "tag", "tags"));
            var sigs = s.Accounts?.Count(a => !string.IsNullOrWhiteSpace(a.SignatureHtml) || !string.IsNullOrWhiteSpace(a.Signature)) ?? 0;
            if (sigs > 0) bits.Add(N(sigs, "signature", "signatures"));
            if (bits.Count > 0) lines.Add(string.Join(" · ", bits));
        }
        catch { lines.Add("Settings (couldn't list them)"); }
        lines.Add(c.MailFolder.Length > 0 ? "Mail folder: " + c.MailFolder : "Mail folder: the usual one");
        return lines;
    }

    /// <summary>Everything Magpie has set up now, from its files (call after the settings were saved).</summary>
    public static Contents Collect(AppPaths paths, SecretVault vault, string appVersion) => new()
    {
        SettingsJson = File.Exists(paths.Settings) ? File.ReadAllText(paths.Settings) : "{}",
        Secrets = vault.ExportAll(),
        MailFolder = paths.CustomMailRoot ? paths.MailRoot : "",
        AppVersion = appVersion,
        Created = DateTimeOffset.Now,
    };

    public static byte[] Lock(Contents c, string password)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException($"Use at least {MinPasswordLength} characters.");
        return LockBytes(JsonSerializer.SerializeToUtf8Bytes(c), password, Format, c.AppVersion, c.Created);
    }

    public static Contents Unlock(byte[] file, string password) =>
        JsonSerializer.Deserialize<Contents>(UnlockBytes(file, password, Format, "This isn't a Magpie settings backup.",
            "This backup was made by a newer Magpie. Update Magpie, then restore it.", () => new WrongPasswordException()))
        ?? throw new InvalidDataException("The backup is empty.");

    /// <summary>The EX1 lock (PBKDF2-SHA256 600 000 rounds → AES-256-GCM, the format name as associated data);
    /// also used by the accounts copy (design SY1). <paramref name="extra"/> goes into the file unlocked.</summary>
    internal static byte[] LockBytes(byte[] plain, string password, string format, string appVersion, DateTimeOffset created, JsonObject? extra = null)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(format));
        CryptographicOperations.ZeroMemory(key);
        var file = new JsonObject
        {
            ["format"] = format,
            ["version"] = 1,
            ["app"] = appVersion,
            ["created"] = created.ToString("o"),
        };
        if (extra != null) foreach (var (k, v) in extra.ToList()) { extra.Remove(k); file[k] = v; }
        file["kdf"] = new JsonObject { ["name"] = "pbkdf2-sha256", ["iterations"] = Iterations, ["salt"] = Convert.ToBase64String(salt) };
        file["nonce"] = Convert.ToBase64String(nonce);
        file["tag"] = Convert.ToBase64String(tag);
        file["data"] = Convert.ToBase64String(cipher);
        return Encoding.UTF8.GetBytes(file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The unlocked bytes of a file made by <see cref="LockBytes"/>, or the given exceptions.</summary>
    internal static byte[] UnlockBytes(byte[] file, string password, string format, string notThisFormat, string newer, Func<Exception> wrongPassword)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(file); } catch { n = null; }
        if (n is not JsonObject || n["format"]?.GetValue<string>() != format) throw new InvalidDataException(notThisFormat);
        if ((n["version"]?.GetValue<int>() ?? 0) != 1) throw new InvalidDataException(newer);
        var kdf = n["kdf"]!;
        var salt = Convert.FromBase64String(kdf["salt"]!.GetValue<string>());
        var iterations = kdf["iterations"]!.GetValue<int>();
        var nonce = Convert.FromBase64String(n["nonce"]!.GetValue<string>());
        var tag = Convert.FromBase64String(n["tag"]!.GetValue<string>());
        var cipher = Convert.FromBase64String(n["data"]!.GetValue<string>());
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(format));
        }
        catch (AuthenticationTagMismatchException) { throw wrongPassword(); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return plain;
    }

    /// <summary>Keeps an unlocked backup for the next start, protected for this Windows user.</summary>
    public static void StagePending(AppPaths paths, Contents c, ISecretProtector protector) =>
        File.WriteAllBytes(Path.Combine(paths.Root, PendingFile), protector.Protect(JsonSerializer.SerializeToUtf8Bytes(c)));

    /// <summary>
    /// At start, before settings and sign-ins are loaded: puts a staged backup in place (settings.json, secrets,
    /// mail folder). Failed restores retain the staged backup and restore the previous files.
    /// </summary>
    public static bool ApplyPending(AppPaths paths, ISecretProtector protector)
    {
        var f = Path.Combine(paths.Root, PendingFile);
        try
        {
            RecoverRestore(paths);
            if (!File.Exists(f)) return false;
            var c = JsonSerializer.Deserialize<Contents>(protector.Unprotect(File.ReadAllBytes(f)));
            if (c == null) throw new InvalidDataException("The backup is empty.");
            _ = JsonSerializer.Deserialize<AppSettings>(c.SettingsJson, AppSettings.Json)
                ?? throw new InvalidDataException("The settings in this backup are empty.");
            var destinations = RestoreFiles(paths);
            var stages = destinations.Select(p => p + ".restore-next").ToArray();
            File.WriteAllText(stages[0], c.SettingsJson);
            new SecretVault(stages[1], protector).ReplaceAll(c.Secrets);
            var folder = c.MailFolder.Length == 0 ? "" : Path.GetFullPath(c.MailFolder);
            File.WriteAllText(stages[2], folder);
            var existed = destinations.Select(File.Exists).ToArray();
            for (var index = 0; index < destinations.Length; index++)
                if (existed[index]) File.Copy(destinations[index], destinations[index] + ".before-restore", true);
            // Retain a recovery journal until every file is installed, including across a process crash.
            var journal = Path.Combine(paths.Root, RestoreJournal);
            var nextJournal = journal + ".next";
            using (var stream = new FileStream(nextJournal, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(existed));
                stream.Flush(flushToDisk: true);
            }
            File.Move(nextJournal, journal, true);
            for (var index = 0; index < destinations.Length; index++) File.Move(stages[index], destinations[index], true);
            File.Delete(Path.Combine(paths.Root, RestoreJournal));
            paths.RefreshMailRoot();
            File.Delete(f);
            Log.Info("settings restored from a backup made by Magpie " + c.AppVersion + " on " + c.Created.ToString("d MMM yyyy HH:mm"));
            return true;
        }
        catch (Exception ex)
        {
            try { RecoverRestore(paths); }
            catch (Exception rollback) { Log.Error("could not recover the previous settings; restore journal retained", rollback); }
            Log.Error("restoring the backup failed", ex);
            return false;
        }
        finally
        {
            foreach (var path in RestoreFiles(paths))
                try { File.Delete(path + ".restore-next"); } catch { }
            try { File.Delete(Path.Combine(paths.Root, RestoreJournal) + ".next"); } catch { }
        }
    }

    private static string[] RestoreFiles(AppPaths paths) => [paths.Settings, paths.Secrets, Path.Combine(paths.Root, AppPaths.MailFolderFile)];

    private static void RecoverRestore(AppPaths paths)
    {
        var journal = Path.Combine(paths.Root, RestoreJournal);
        if (!File.Exists(journal)) return;
        var existed = JsonSerializer.Deserialize<bool[]>(File.ReadAllText(journal));
        var destinations = RestoreFiles(paths);
        if (existed == null || existed.Length != destinations.Length) throw new InvalidDataException("The restore recovery record is incomplete.");
        for (var index = 0; index < destinations.Length; index++)
            if (existed[index]) File.Copy(destinations[index] + ".before-restore", destinations[index], true);
            else File.Delete(destinations[index]);
        File.Delete(journal);
        paths.RefreshMailRoot();
    }
}
