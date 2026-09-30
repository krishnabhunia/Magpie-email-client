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
        var plain = JsonSerializer.SerializeToUtf8Bytes(c);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, Encoding.ASCII.GetBytes(Format));
        CryptographicOperations.ZeroMemory(key);
        var file = new JsonObject
        {
            ["format"] = Format,
            ["version"] = 1,
            ["app"] = c.AppVersion,
            ["created"] = c.Created.ToString("o"),
            ["kdf"] = new JsonObject { ["name"] = "pbkdf2-sha256", ["iterations"] = Iterations, ["salt"] = Convert.ToBase64String(salt) },
            ["nonce"] = Convert.ToBase64String(nonce),
            ["tag"] = Convert.ToBase64String(tag),
            ["data"] = Convert.ToBase64String(cipher),
        };
        return Encoding.UTF8.GetBytes(file.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Contents Unlock(byte[] file, string password)
    {
        JsonNode? n;
        try { n = JsonNode.Parse(file); } catch { n = null; }
        if (n?["format"]?.GetValue<string>() != Format) throw new InvalidDataException("This isn't a Magpie settings backup.");
        if ((n["version"]?.GetValue<int>() ?? 0) != 1) throw new InvalidDataException("This backup was made by a newer Magpie. Update Magpie, then restore it.");
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
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.ASCII.GetBytes(Format));
        }
        catch (AuthenticationTagMismatchException) { throw new WrongPasswordException(); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return JsonSerializer.Deserialize<Contents>(plain) ?? throw new InvalidDataException("The backup is empty.");
    }

    /// <summary>Keeps an unlocked backup for the next start, protected for this Windows user.</summary>
    public static void StagePending(AppPaths paths, Contents c, ISecretProtector protector) =>
        File.WriteAllBytes(Path.Combine(paths.Root, PendingFile), protector.Protect(JsonSerializer.SerializeToUtf8Bytes(c)));

    /// <summary>
    /// At start, before settings and sign-ins are loaded: puts a staged backup in place (settings.json, secrets,
    /// mail folder). Returns true when one was applied. The staged file is removed either way.
    /// </summary>
    public static bool ApplyPending(AppPaths paths, ISecretProtector protector)
    {
        var f = Path.Combine(paths.Root, PendingFile);
        if (!File.Exists(f)) return false;
        try
        {
            var c = JsonSerializer.Deserialize<Contents>(protector.Unprotect(File.ReadAllBytes(f)));
            if (c == null) return false;
            if (File.Exists(paths.Settings)) File.Copy(paths.Settings, paths.Settings + ".before-restore", true);
            File.WriteAllText(paths.Settings, c.SettingsJson);
            new SecretVault(paths.Secrets, protector).ReplaceAll(c.Secrets);
            paths.SetMailRoot(c.MailFolder.Length > 0 ? c.MailFolder : null);
            Log.Info("settings restored from a backup made by Magpie " + c.AppVersion + " on " + c.Created.ToString("d MMM yyyy HH:mm"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("restoring the backup failed", ex);
            return false;
        }
        finally { try { File.Delete(f); } catch { } }
    }
}
