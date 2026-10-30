using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpie.Core.Models;
using Magpie.Core.Security;

namespace Magpie.Core.Settings;

/// <summary>
/// Design SY1 (#71, approved 30 Oct 2026: A + SA1): "Bring my accounts to other PCs". One locked file (the EX1 lock,
/// format <see cref="Format"/>) holding every account with its sign-in and, if chosen, the shared settings, kept as one
/// email in a hidden "Magpie Sync" folder of the home account's mailbox. Emails never go into it.
/// Accounts are matched between PCs by email address. Each account, the settings and the AI block carry a fingerprint
/// and when they last changed; the newer change wins. Removing an account is written into the copy, but never removes
/// it elsewhere by itself: the other PCs ask.
/// </summary>
public static class AccountsCopy
{
    public const string Format = "magpie-accounts-copy";
    public const int MinPasswordLength = 10;
    public const int MaxBytes = 10 * 1024 * 1024;
    public const string FolderName = "Magpie Sync";
    public const string Header = "X-Magpie-Accounts-Copy";
    public const string FileName = "accounts.magpie-copy";
    /// <summary>The sync password, kept for this Windows user (DPAPI) so the copy can be updated without asking.</summary>
    public const string VaultPassword = "sync:password";
    internal const string SettingsStamp = "\u0001settings";
    internal const string AiStamp = "\u0001ai";

    public sealed class Contents
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset Updated { get; set; }
        /// <summary>The PC that wrote this copy last.</summary>
        public string FromPc { get; set; } = "";
        public string AppVersion { get; set; } = "";
        public List<CopiedAccount> Accounts { get; set; } = new();
        /// <summary>Accounts removed on some PC (email → when): the other PCs ask before removing them.</summary>
        public Dictionary<string, RemovedAccount> Removed { get; set; } = new();
        public SharedSettings? Settings { get; set; }
        public SharedAi? Ai { get; set; }
        public SignInApps Apps { get; set; } = new();
        /// <summary>PCs that used the copy (name → last time).</summary>
        public Dictionary<string, DateTimeOffset> UsedBy { get; set; } = new();
    }

    public sealed class CopiedAccount
    {
        public Account Account { get; set; } = new();
        public string Password { get; set; } = "";
        public string Refresh { get; set; } = "";
        public DateTimeOffset? SignedIn { get; set; }
        public string Hash { get; set; } = "";
        public DateTimeOffset Changed { get; set; }
        public string Email => Key(Account.Email);
    }

    public sealed class RemovedAccount
    {
        public DateTimeOffset When { get; set; }
        public string ByPc { get; set; } = "";
    }

    /// <summary>The settings that come along (design SY1·1): rules, signatures (they are on the accounts), quick replies,
    /// templates, tags, Gatekeeper lists, look, and the smart-inbox choices.</summary>
    public sealed class SharedSettings
    {
        public List<Mail.MailRule> Rules { get; set; } = new();
        public List<string> QuickReplies { get; set; } = new();
        public List<QuickTemplate> Templates { get; set; } = new();
        public List<TagDef> Tags { get; set; } = new();
        public GatekeeperSettings Gatekeeper { get; set; } = new();
        public Appearance Appearance { get; set; } = new();
        public TypographySettings Typography { get; set; } = new();
        public Dictionary<string, Category> SenderCategories { get; set; } = new();
        public string Hash { get; set; } = "";
        public DateTimeOffset Changed { get; set; }
    }

    /// <summary>The AI connections and their keys (only when "AI keys" is ticked).</summary>
    public sealed class SharedAi
    {
        public AiSettings Settings { get; set; } = new();
        public Dictionary<string, string> Keys { get; set; } = new();
        public string Hash { get; set; } = "";
        public DateTimeOffset Changed { get; set; }
    }

    /// <summary>The sign-in apps the copied sign-ins belong to: a refresh token only works with the app that issued it.</summary>
    public sealed class SignInApps
    {
        public string GoogleClientId { get; set; } = "";
        public string GoogleClientSecret { get; set; } = "";
        public string MicrosoftClientId { get; set; } = "";
        public bool IsEmpty => GoogleClientId.Length == 0 && MicrosoftClientId.Length == 0;
    }

    public sealed class WrongPasswordException : Exception
    {
        public WrongPasswordException() : base("That password doesn't open this copy.") { }
    }

    /// <summary>What comparing the copy with this PC found (design SY1·3/4).</summary>
    public sealed class Plan
    {
        /// <summary>Accounts to add here by themselves ("Add accounts from my other PCs here by themselves").</summary>
        public List<CopiedAccount> Add { get; } = new();
        /// <summary>Accounts in the copy not on this PC that wait for the user (Settings → Accounts, or the new-PC list).</summary>
        public List<CopiedAccount> Available { get; } = new();
        /// <summary>Accounts changed on another PC more recently than here.</summary>
        public List<CopiedAccount> Update { get; } = new();
        /// <summary>Accounts removed on another PC after their last change here: ask "Remove it here too / Keep it".</summary>
        public List<(Account Account, RemovedAccount Removed)> RemovedElsewhere { get; } = new();
        public bool SettingsNewer { get; set; }
        public bool AiNewer { get; set; }
        public bool AdoptApps { get; set; }
        public bool Any => Add.Count + Update.Count + RemovedElsewhere.Count > 0 || SettingsNewer || AiNewer || AdoptApps;
    }

    public static string Key(string email) => (email ?? "").Trim().ToLowerInvariant();

    private static readonly JsonSerializerOptions Json = AppSettings.Json;
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>An account's fingerprint: its settings (not its id on this PC), password, and when it last signed in again.
    /// The refresh token itself is left out: Microsoft hands out a new one every hour.</summary>
    internal static string AccountHash(Account a, string? password, DateTimeOffset? signedIn)
    {
        var c = a.Clone();
        c.Id = "";
        return Sha(JsonSerializer.Serialize(c, Json) + "\n" + (password ?? "") + "\n" + (signedIn?.ToUnixTimeMilliseconds() ?? 0));
    }

    private static string StampFree<T>(T value, Action<T> clear) { var c = Copy(value); clear(c); return Sha(JsonSerializer.Serialize(c, Json)); }

    public static SharedSettings SharedOf(AppSettings s)
    {
        var shared = Copy(new SharedSettings
        {
            Rules = s.Rules, QuickReplies = s.QuickReplies, Templates = s.Templates, Tags = s.Tags, Gatekeeper = s.Gatekeeper,
            Appearance = s.Appearance, Typography = s.Typography, SenderCategories = new Dictionary<string, Category>(s.SenderCategories),
        });
        shared.Hash = StampFree(shared, c => { c.Hash = ""; c.Changed = default; });
        return shared;
    }

    public static void ApplyShared(AppSettings s, SharedSettings shared)
    {
        var c = Copy(shared);
        s.Rules = c.Rules; s.QuickReplies = c.QuickReplies; s.Templates = c.Templates; s.Tags = c.Tags; s.Gatekeeper = c.Gatekeeper;
        s.Appearance = c.Appearance; s.Typography = c.Typography;
        s.SenderCategories = new Dictionary<string, Category>(c.SenderCategories, StringComparer.OrdinalIgnoreCase);
    }

    public static SharedAi AiOf(AppSettings s, SecretVault vault)
    {
        var ai = new SharedAi { Settings = s.Ai.Clone() };
        ai.Settings.Consents = new();   // consent is asked again on each PC
        foreach (var c in ai.Settings.Connections)
            if (vault.Get(SecretVault.AiKeyFor(c.Id)) is { Length: > 0 } key) ai.Keys[c.Id] = key;
        ai.Hash = StampFree(ai, c => { c.Hash = ""; c.Changed = default; });
        return ai;
    }

    private static SignInApps AppsOf(AppSettings s) => new()
    {
        GoogleClientId = s.GoogleClientId ?? "", GoogleClientSecret = s.GoogleClientSecret ?? "", MicrosoftClientId = s.MicrosoftClientId ?? "",
    };

    private static DateTimeOffset? SignedIn(AccountsCopySettings o, string key) => o.SignedIn.TryGetValue(key, out var t) ? t : null;

    private static string HashOf(Account a, SecretVault v, AccountsCopySettings o) =>
        AccountHash(a, v.Get(SecretVault.PasswordKey(a.Id)), SignedIn(o, Key(a.Email)));

    /// <summary>Notes on this PC what changed since the last look (fingerprints and times). True when anything did.</summary>
    public static bool Track(AppSettings s, SecretVault vault, DateTimeOffset now)
    {
        var o = s.AccountsCopy;
        var changed = false;
        void Stamp(string key, string hash)
        {
            if (o.Stamps.TryGetValue(key, out var st) && st.Hash == hash) return;
            o.Stamps[key] = new CopyStamp { Hash = hash, Changed = now };
            changed = true;
        }
        foreach (var a in s.Accounts) Stamp(Key(a.Email), HashOf(a, vault, o));
        // Settings count as changed here only once this PC has started them (StartHere) or taken them from the copy:
        // a new PC's untouched settings must not win over the ones in the copy.
        if (o.IncludeSettings && o.Stamps.ContainsKey(SettingsStamp)) Stamp(SettingsStamp, SharedOf(s).Hash);
        if (o.IncludeAiKeys && o.Stamps.ContainsKey(AiStamp)) Stamp(AiStamp, AiOf(s, vault).Hash);
        return changed;
    }

    /// <summary>The copy was turned on here (Settings → Accounts): this PC's settings and AI block go into it.</summary>
    public static void StartHere(AppSettings s, SecretVault vault, DateTimeOffset now)
    {
        var o = s.AccountsCopy;
        if (o.IncludeSettings && !o.Stamps.ContainsKey(SettingsStamp)) o.Stamps[SettingsStamp] = new CopyStamp { Hash = SharedOf(s).Hash, Changed = now };
        if (o.IncludeAiKeys && !o.Stamps.ContainsKey(AiStamp)) o.Stamps[AiStamp] = new CopyStamp { Hash = AiOf(s, vault).Hash, Changed = now };
        Track(s, vault, now);
    }

    /// <summary>The copy to write: the one read before, with this PC's newer changes and removals laid over it. Accounts
    /// only another PC has stay in it (this PC may have chosen not to add them).</summary>
    public static Contents Build(AppSettings s, SecretVault vault, Contents? previous, string pc, string appVersion, DateTimeOffset now)
    {
        var o = s.AccountsCopy;
        Track(s, vault, now);
        var c = new Contents { Updated = now, FromPc = pc, AppVersion = appVersion };
        var accounts = new Dictionary<string, CopiedAccount>();
        foreach (var x in previous?.Accounts ?? new()) accounts[x.Email] = x;
        foreach (var a in s.Accounts)
        {
            var key = Key(a.Email);
            var stamp = o.Stamps[key];
            if (accounts.TryGetValue(key, out var old) && old.Changed >= stamp.Changed) continue;
            accounts[key] = new CopiedAccount
            {
                Account = Copy(a), Password = vault.Get(SecretVault.PasswordKey(a.Id)) ?? "", Refresh = vault.Get(SecretVault.RefreshKey(a.Id)) ?? "",
                SignedIn = SignedIn(o, key), Hash = stamp.Hash, Changed = stamp.Changed,
            };
        }
        var removed = new Dictionary<string, RemovedAccount>();
        foreach (var (k, r) in previous?.Removed ?? new()) removed[Key(k)] = r;
        foreach (var (k, when) in o.RemovedHere)
            if (!removed.TryGetValue(k, out var r) || r.When < when) removed[k] = new RemovedAccount { When = when, ByPc = pc };
        foreach (var (k, r) in removed.ToList())
        {
            if (!accounts.TryGetValue(k, out var acc)) continue;
            if (acc.Changed > r.When) removed.Remove(k);   // kept (or added again) after it was removed somewhere
            else accounts.Remove(k);
        }
        c.Accounts = accounts.Values.OrderBy(x => x.Email, StringComparer.Ordinal).ToList();
        c.Removed = removed;

        c.Settings = previous?.Settings;
        if (o.IncludeSettings && o.Stamps.TryGetValue(SettingsStamp, out var ss) && (c.Settings == null || c.Settings.Changed < ss.Changed))
        {
            c.Settings = SharedOf(s);
            c.Settings.Changed = ss.Changed;
        }
        c.Ai = previous?.Ai;
        if (o.IncludeAiKeys && o.Stamps.TryGetValue(AiStamp, out var ai) && (c.Ai == null || c.Ai.Changed < ai.Changed))
        {
            c.Ai = AiOf(s, vault);
            c.Ai.Changed = ai.Changed;
        }
        var apps = AppsOf(s);
        c.Apps = apps.IsEmpty ? previous?.Apps ?? new() : apps;
        c.UsedBy = new Dictionary<string, DateTimeOffset>(previous?.UsedBy ?? new());
        foreach (var k in c.UsedBy.Where(u => now - u.Value > TimeSpan.FromDays(90)).Select(u => u.Key).ToList()) c.UsedBy.Remove(k);
        c.UsedBy[pc] = now;
        return c;
    }

    /// <summary>True when the copy lacks something this PC has newer (or this PC hasn't been noted in it today).</summary>
    public static bool NeedsWrite(AppSettings s, SecretVault vault, Contents? copy, string pc, DateTimeOffset now)
    {
        if (copy == null) return true;
        var o = s.AccountsCopy;
        Track(s, vault, now);
        var inCopy = copy.Accounts.ToDictionary(a => a.Email);
        foreach (var a in s.Accounts)
        {
            var key = Key(a.Email);
            var st = o.Stamps[key];
            if (copy.Removed.TryGetValue(key, out var r) && r.When >= st.Changed) continue;   // waits for the user's answer
            if (!inCopy.TryGetValue(key, out var c) || (c.Changed < st.Changed && c.Hash != st.Hash)) return true;
        }
        foreach (var (k, when) in o.RemovedHere)
            if (inCopy.ContainsKey(k) || !copy.Removed.TryGetValue(k, out var r) || r.When < when) return true;
        if (o.IncludeSettings && o.Stamps.TryGetValue(SettingsStamp, out var ss) && (copy.Settings == null || (copy.Settings.Changed < ss.Changed && copy.Settings.Hash != ss.Hash))) return true;
        if (o.IncludeAiKeys && o.Stamps.TryGetValue(AiStamp, out var ai) && (copy.Ai == null || (copy.Ai.Changed < ai.Changed && copy.Ai.Hash != ai.Hash))) return true;
        if (copy.Apps.IsEmpty && !AppsOf(s).IsEmpty) return true;
        return !copy.UsedBy.TryGetValue(pc, out var seen) || now - seen > TimeSpan.FromDays(1);
    }

    public static Plan Compare(AppSettings s, SecretVault vault, Contents copy, DateTimeOffset now)
    {
        var o = s.AccountsCopy;
        Track(s, vault, now);
        var plan = new Plan();
        var local = s.Accounts.GroupBy(a => Key(a.Email)).ToDictionary(g => g.Key, g => g.First());
        foreach (var c in copy.Accounts)
        {
            if (local.TryGetValue(c.Email, out _))
            {
                var st = o.Stamps[c.Email];
                if (c.Changed > st.Changed && c.Hash != st.Hash) plan.Update.Add(c);
            }
            else if (o.NotHere.Contains(c.Email) || !o.AutoAdd) plan.Available.Add(c);
            else plan.Add.Add(c);
        }
        foreach (var (key, a) in local)
            if (copy.Removed.TryGetValue(key, out var r) && r.When > o.Stamps[key].Changed && !o.RemovedHere.ContainsKey(key))
                plan.RemovedElsewhere.Add((a, r));
        if (o.IncludeSettings && copy.Settings is { } cs)
            plan.SettingsNewer = !o.Stamps.TryGetValue(SettingsStamp, out var ss) || (cs.Changed > ss.Changed && cs.Hash != ss.Hash);
        if (o.IncludeAiKeys && copy.Ai is { } ca)
            plan.AiNewer = !o.Stamps.TryGetValue(AiStamp, out var st2) || (ca.Changed > st2.Changed && ca.Hash != st2.Hash);
        plan.AdoptApps = AppsOf(s).IsEmpty && !copy.Apps.IsEmpty;
        return plan;
    }

    /// <summary>Takes the newer accounts, settings and AI block from the copy (adding and removing are the caller's,
    /// so it can start and stop syncs). Returns the accounts changed here, to be restarted.</summary>
    public static List<Account> ApplyUpdates(AppSettings s, SecretVault vault, Contents copy, Plan plan)
    {
        var o = s.AccountsCopy;
        var changed = new List<Account>();
        foreach (var c in plan.Update)
        {
            var i = s.Accounts.FindIndex(a => Key(a.Email) == c.Email);
            if (i < 0) continue;
            var a = Copy(c.Account);
            a.Id = s.Accounts[i].Id;
            s.Accounts[i] = a;
            Remember(s, vault, a, c);
            changed.Add(a);
        }
        if (plan.SettingsNewer && copy.Settings is { } cs)
        {
            ApplyShared(s, cs);
            o.Stamps[SettingsStamp] = new CopyStamp { Hash = cs.Hash, Changed = cs.Changed };
        }
        if (plan.AiNewer && copy.Ai is { } ca)
        {
            var consents = s.Ai.Consents;
            s.Ai = Copy(ca.Settings);
            s.Ai.Consents = consents ?? new();
            foreach (var (id, key) in ca.Keys) vault.Set(SecretVault.AiKeyFor(id), key);
            o.Stamps[AiStamp] = new CopyStamp { Hash = ca.Hash, Changed = ca.Changed };
        }
        if (plan.AdoptApps)
        {
            s.GoogleClientId = copy.Apps.GoogleClientId;
            s.GoogleClientSecret = copy.Apps.GoogleClientSecret;
            s.MicrosoftClientId = copy.Apps.MicrosoftClientId;
        }
        return changed;
    }

    /// <summary>An account from the copy, ready to add here: its own id on this PC when that one is taken.</summary>
    public static Account ForAdd(AppSettings s, CopiedAccount c)
    {
        var a = Copy(c.Account);
        if (string.IsNullOrEmpty(a.Id) || s.Accounts.Any(x => x.Id == a.Id)) a.Id = Guid.NewGuid().ToString("N");
        return a;
    }

    /// <summary>After adding or updating an account from the copy: its secrets here, and the copy's fingerprint and time,
    /// so it isn't written back as a change of this PC.</summary>
    public static void Remember(AppSettings s, SecretVault vault, Account a, CopiedAccount c)
    {
        var o = s.AccountsCopy;
        if (c.Password.Length > 0) vault.Set(SecretVault.PasswordKey(a.Id), c.Password);
        if (c.Refresh.Length > 0) vault.Set(SecretVault.RefreshKey(a.Id), c.Refresh);
        if (c.SignedIn is { } t) o.SignedIn[c.Email] = t; else o.SignedIn.Remove(c.Email);
        o.Stamps[c.Email] = new CopyStamp { Hash = c.Hash, Changed = c.Changed };
        o.NotHere.Remove(c.Email);
        o.RemovedHere.Remove(c.Email);
    }

    // ───────────────────────── the locked file ─────────────────────────

    public static byte[] Lock(Contents c, string password)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException($"Use at least {MinPasswordLength} characters.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(c, Json);
        var extra = new JsonObject { ["pc"] = c.FromPc, ["accounts"] = c.Accounts.Count, ["updated"] = c.Updated.ToString("o") };
        var file = SettingsBackup.LockBytes(plain, password, Format, c.AppVersion, c.Updated, extra);
        if (file.Length > MaxBytes)
            throw new InvalidOperationException($"The copy would be {file.Length / 1024 / 1024} MB; the limit is {MaxBytes / 1024 / 1024} MB. Pictures in signatures are the usual reason.");
        return file;
    }

    public static Contents Unlock(byte[] file, string password)
    {
        var plain = SettingsBackup.UnlockBytes(file, password, Format, "This isn't a Magpie accounts copy.",
            "This copy was made by a newer Magpie. Update Magpie, then try again.", () => new WrongPasswordException());
        var c = JsonSerializer.Deserialize<Contents>(plain, Json) ?? throw new InvalidDataException("The copy is empty.");
        c.Accounts ??= new();
        c.Removed = (c.Removed ?? new()).ToDictionary(kv => Key(kv.Key), kv => kv.Value);
        c.UsedBy ??= new();
        c.Apps ??= new();
        return c;
    }

    /// <summary>What the copy says about itself without its password (shown before asking for it).</summary>
    public sealed record Summary(string Pc, int Accounts, DateTimeOffset Updated);

    public static Summary? Peek(byte[] file)
    {
        try
        {
            var n = JsonNode.Parse(file);
            if (n?["format"]?.GetValue<string>() != Format) return null;
            return new Summary(n["pc"]?.GetValue<string>() ?? "", n["accounts"]?.GetValue<int>() ?? 0,
                DateTimeOffset.TryParse(n["updated"]?.GetValue<string>(), out var t) ? t : default);
        }
        catch { return null; }
    }

    /// <summary>The strength line under the sync password: 0 (too short) – 4 (strong), in plain words.</summary>
    public static (int Score, string Text) Strength(string password)
    {
        if (password.Length < MinPasswordLength) return (0, $"At least {MinPasswordLength} characters.");
        var kinds = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(ch => !char.IsLetterOrDigit(ch)) }.Count(b => b);
        var distinct = password.Distinct().Count();
        var score = Math.Clamp((password.Length >= 16 ? 2 : 1) + (kinds >= 3 ? 1 : 0) + (distinct >= 8 ? 1 : 0) - (distinct < 5 ? 2 : 0), 1, 4);
        return (score, score switch
        {
            1 => "Weak: someone with the mailbox could guess it. Use a longer phrase.",
            2 => "Fair. A few more words make it much harder to guess.",
            3 => "Good.",
            _ => "Strong.",
        });
    }
}
