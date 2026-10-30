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
    public const string Subject = "Magpie accounts copy: please don't delete";
    /// <summary>The sync password, kept for this Windows user (DPAPI) so the copy can be updated without asking.</summary>
    public const string VaultPassword = "sync:password";
    /// <summary>A new sync password chosen here, used for the next write once the copy has been opened with the old one.</summary>
    public const string VaultNewPassword = "sync:password-new";
    internal const string SettingsStamp = "\u0001settings";
    internal const string AiStamp = "\u0001ai";
    /// <summary>Saves can come from any thread (the copy notes changes on each one): one of these steps at a time.</summary>
    private static readonly object Gate = new();

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
        /// <summary>What comes along, for every PC (the newest choice wins): settings, AI connections and keys.</summary>
        public bool ShareSettings { get; set; } = true;
        public bool ShareAi { get; set; }
        public DateTimeOffset ChoicesChanged { get; set; }
        /// <summary>PCs that used the copy (name → last time).</summary>
        public Dictionary<string, DateTimeOffset> UsedBy { get; set; } = new();
        /// <summary>The home account was changed on some PC: the copy now lives in this mailbox (a last note left in the old one).</summary>
        public string MovedTo { get; set; } = "";
        /// <summary>"Stop and delete the copy" on <see cref="FromPc"/>: the other PCs stop too (their accounts stay).</summary>
        public bool Stopped { get; set; }
    }

    public sealed class CopiedAccount
    {
        public Account Account { get; set; } = new();
        public string Password { get; set; } = "";
        public string Refresh { get; set; } = "";
        /// <summary>The sign-in app (client ID) that issued <see cref="Refresh"/>; a refresh token only works with that app.</summary>
        public string Client { get; set; } = "";
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
    /// templates, tags, Gatekeeper lists, look, and the smart-inbox choices. Rules name their account by email here.</summary>
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

    /// <summary>The sign-in apps set in Settings on the PC that wrote the copy (never taken over by itself: tokens already
    /// on a PC belong to its own app).</summary>
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
        /// <summary>Another PC chose more recently what comes along.</summary>
        public bool ChoicesNewer { get; set; }
        /// <summary>Accounts added here by hand wait to take the copy's version (<see cref="AccountsCopySettings.Adopt"/>).</summary>
        public bool Adopting { get; set; }
        /// <summary>Applying the plan changes something here (so settings are saved and the window told).</summary>
        public bool Changes => Add.Count + Update.Count > 0 || SettingsNewer || AiNewer || ChoicesNewer || Adopting;
    }

    public static string Key(string email) => (email ?? "").Trim().ToLowerInvariant();

    private static readonly JsonSerializerOptions Json = AppSettings.Json;
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;
    private static string Sha(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>An account's fingerprint: its settings (not its id on this PC) and when it last signed in again or got a
    /// new password (MailEngine.UpdateAccount notes that). Neither the password nor the refresh token is in it:
    /// settings.json keeps the fingerprint unlocked, and Microsoft hands out a new refresh token every hour.</summary>
    internal static string AccountHash(Account a, DateTimeOffset? signedIn)
    {
        var c = a.Clone();
        c.Id = "";
        return Sha(JsonSerializer.Serialize(c, Json) + "\n" + (signedIn?.ToUnixTimeMilliseconds() ?? 0));
    }

    private static string StampFree<T>(T value, Action<T> clear) { var c = Copy(value); clear(c); return Sha(JsonSerializer.Serialize(c, Json)); }

    public static SharedSettings SharedOf(AppSettings s)
    {
        var shared = Copy(new SharedSettings
        {
            Rules = s.Rules, QuickReplies = s.QuickReplies, Templates = s.Templates, Tags = s.Tags, Gatekeeper = s.Gatekeeper,
            Appearance = s.Appearance, Typography = s.Typography, SenderCategories = new Dictionary<string, Category>(s.SenderCategories),
        });
        // A rule for one account names it by email in the copy: the account has another id on every PC.
        var emails = s.Accounts.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => Key(g.First().Email));
        foreach (var r in shared.Rules) if (r.AccountId.Length > 0 && emails.TryGetValue(r.AccountId, out var e)) r.AccountId = "@" + e;
        shared.Hash = StampFree(shared, c => { c.Hash = ""; c.Changed = default; });
        return shared;
    }

    public static void ApplyShared(AppSettings s, SharedSettings shared)
    {
        var c = Copy(shared);
        s.Rules = c.Rules;
        ResolveRules(s); s.QuickReplies = c.QuickReplies; s.Templates = c.Templates; s.Tags = c.Tags; s.Gatekeeper = c.Gatekeeper;
        s.Appearance = c.Appearance; s.Typography = c.Typography;
        s.SenderCategories = new Dictionary<string, Category>(c.SenderCategories, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Rules that name their account by email ("@email": from the copy, or the account was removed here) point at
    /// that account's id on this PC once it is here. The rest keep the email until it is added.</summary>
    public static void ResolveRules(AppSettings s)
    {
        var ids = s.Accounts.GroupBy(a => Key(a.Email)).ToDictionary(g => g.Key, g => g.First().Id);
        var here = s.Accounts.Select(a => a.Id).ToHashSet();
        foreach (var r in s.Rules)
        {
            // Still the id of an account removed here: by its email again.
            if (r.AccountId.Length > 0 && !here.Contains(r.AccountId) && s.AccountsCopy.FormerIds.TryGetValue(r.AccountId, out var email)) r.AccountId = "@" + email;
            if (r.AccountId.StartsWith('@') && ids.TryGetValue(r.AccountId[1..], out var id)) r.AccountId = id;
        }
    }

    public static SharedAi AiOf(AppSettings s, SecretVault vault)
    {
        var ai = new SharedAi { Settings = s.Ai.Clone() };
        ai.Settings.Consents = new();   // consent is asked again on each PC
        // What each PC learns by itself in the background (the provider's model list, refused settings, the notice it
        // dismissed) isn't part of it: those change daily and would make an old key win.
        foreach (var conn in ai.Settings.Connections) { conn.Models = new(); conn.ModelsCheckedAt = null; conn.Unsupported = new(); conn.DismissedNewModel = ""; }
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

    private static string HashOf(Account a, AccountsCopySettings o) => AccountHash(a, SignedIn(o, Key(a.Email)));

    private static CopyStamp Fresh(string hash, DateTimeOffset now) => new() { Hash = hash, Local = hash, Changed = now };

    /// <summary>Notes what changed on this PC since the last look (fingerprints and times). True when anything did.
    /// A fingerprint taken over from the copy counts as unchanged while this PC's own one stays the same: another Magpie
    /// version may write an account a little differently, and that must not make two PCs overwrite each other.</summary>
    public static bool Track(AppSettings s, SecretVault vault, DateTimeOffset now)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            var changed = false;
            void Stamp(string key, string hash)
            {
                if (o.Stamps.TryGetValue(key, out var st) && (st.Local.Length > 0 ? st.Local == hash : st.Hash == hash)) return;
                // Added by hand, the copy not looked at yet: older than any version in the copy, so the copy's wins.
                if (st == null && o.Adopt.Contains(key)) { o.Stamps[key] = new CopyStamp { Hash = hash, Local = hash, Changed = DateTimeOffset.MinValue }; changed = true; return; }
                o.Stamps[key] = Fresh(hash, now);
                changed = true;
            }
            foreach (var a in s.Accounts) Stamp(Key(a.Email), HashOf(a, o));
            // Settings count as changed here only once this PC has started them (StartHere) or taken them from the copy:
            // a new PC's untouched settings must not win over the ones in the copy.
            if (o.IncludeSettings && o.Stamps.ContainsKey(SettingsStamp)) Stamp(SettingsStamp, SharedOf(s).Hash);
            if (o.IncludeAiKeys && o.Stamps.ContainsKey(AiStamp)) Stamp(AiStamp, AiOf(s, vault).Hash);
            return changed;
        }
    }

    /// <summary>The copy was turned on here and there was none yet: this PC's settings, AI block and choices go into it.</summary>
    public static void StartHere(AppSettings s, SecretVault vault, DateTimeOffset now)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            if (o.IncludeSettings && !o.Stamps.ContainsKey(SettingsStamp)) o.Stamps[SettingsStamp] = Fresh(SharedOf(s).Hash, now);
            if (o.IncludeAiKeys && !o.Stamps.ContainsKey(AiStamp)) o.Stamps[AiStamp] = Fresh(AiOf(s, vault).Hash, now);
            o.ChoicesChanged = now;
            Track(s, vault, now);
        }
    }

    /// <summary>The copy was turned on here and the home mailbox already has one (another PC made it): join it. Accounts
    /// both have keep this PC's version until either side changes them; the copy's settings and choices win.</summary>
    public static void Join(AppSettings s, SecretVault vault, Contents copy, DateTimeOffset now)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            o.Stamps.Remove(SettingsStamp);
            o.Stamps.Remove(AiStamp);
            o.ChoicesChanged = default;
            var inCopy = copy.Accounts.ToDictionary(a => a.Email);
            foreach (var a in s.Accounts)
                if (inCopy.TryGetValue(Key(a.Email), out var c))
                    o.Stamps[c.Email] = new CopyStamp { Hash = c.Hash, Local = HashOf(a, o), Changed = c.Changed };
            Track(s, vault, now);
        }
    }

    /// <summary>This PC's sign-in app for an account kind (Settings, else the built-in one); set by MailEngine.</summary>
    public static Func<AccountKind, string> ClientFor { get; set; } = _ => "";
    /// <summary>True when an account's sign-in no longer works here (by its id): its token isn't put in the copy.</summary>
    public static Func<string, bool> SignInBroken { get; set; } = _ => false;

    /// <summary>The copy to write: the one read before, with this PC's newer changes and removals laid over it. Accounts
    /// only another PC has stay in it (this PC may have chosen not to add them).</summary>
    public static Contents Build(AppSettings s, SecretVault vault, Contents? previous, string pc, string appVersion, DateTimeOffset now)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            Track(s, vault, now);
            var c = new Contents { Updated = now, FromPc = pc, AppVersion = appVersion };
            // What comes along: the newest choice of any PC.
            if (previous == null || o.ChoicesChanged > previous.ChoicesChanged)
                (c.ShareSettings, c.ShareAi, c.ChoicesChanged) = (o.IncludeSettings, o.IncludeAiKeys, o.ChoicesChanged);
            else (c.ShareSettings, c.ShareAi, c.ChoicesChanged) = (previous.ShareSettings, previous.ShareAi, previous.ChoicesChanged);

            var accounts = new Dictionary<string, CopiedAccount>();
            foreach (var x in previous?.Accounts ?? new()) accounts[x.Email] = x;
            foreach (var a in s.Accounts)
            {
                var key = Key(a.Email);
                if (o.Adopt.Contains(key)) continue;   // takes the copy's version first (next look at the copy)
                var stamp = o.Stamps[key];
                // A token that no longer works here never replaces the one in the copy (another PC's may still work).
                var refresh = SignInBroken(a.Id) ? "" : vault.Get(SecretVault.RefreshKey(a.Id)) ?? "";
                var client = a.Auth == AuthMethod.OAuth2 ? ClientFor(a.Kind) : "";
                if (accounts.TryGetValue(key, out var old) && old.Changed >= stamp.Changed)
                {
                    // Not newer here, but this PC's working sign-in keeps the copy's one fresh (Microsoft tokens expire when
                    // unused), when it is from the same sign-in app and no newer sign-in elsewhere replaced it.
                    if (refresh.Length > 0 && old.Client == client && (old.SignedIn ?? default) <= (SignedIn(o, key) ?? default)) old.Refresh = refresh;
                    continue;
                }
                accounts[key] = new CopiedAccount
                {
                    Account = Copy(a), Password = vault.Get(SecretVault.PasswordKey(a.Id)) ?? "",
                    Refresh = refresh.Length == 0 && old != null && old.Client == client ? old.Refresh : refresh, Client = client,
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

            c.Settings = c.ShareSettings ? previous?.Settings : null;
            if (c.ShareSettings && o.IncludeSettings && o.Stamps.TryGetValue(SettingsStamp, out var ss) && (c.Settings == null || c.Settings.Changed < ss.Changed))
            {
                c.Settings = SharedOf(s);
                c.Settings.Changed = ss.Changed;
            }
            // Unticked (the newest choice of any PC): the AI keys leave the copy.
            c.Ai = c.ShareAi ? previous?.Ai : null;
            if (c.ShareAi && o.IncludeAiKeys && o.Stamps.TryGetValue(AiStamp, out var ai) && (c.Ai == null || c.Ai.Changed < ai.Changed))
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
    }

    /// <summary>True when the copy lacks something this PC has newer (or this PC hasn't been noted in it today).</summary>
    public static bool NeedsWrite(AppSettings s, SecretVault vault, Contents? copy, string pc, DateTimeOffset now)
    {
        lock (Gate)
        {
            if (copy == null) return true;
            var o = s.AccountsCopy;
            Track(s, vault, now);
            if (o.ChoicesChanged > copy.ChoicesChanged) return true;
            var inCopy = copy.Accounts.ToDictionary(a => a.Email);
            foreach (var a in s.Accounts)
            {
                var key = Key(a.Email);
                if (o.Adopt.Contains(key)) continue;
                var st = o.Stamps[key];
                if (copy.Removed.TryGetValue(key, out var r) && r.When >= st.Changed && !o.RemovedHere.ContainsKey(key)) continue;   // waits for the user's answer
                if (!inCopy.TryGetValue(key, out var c) || (c.Changed < st.Changed && c.Hash != st.Hash)) return true;
            }
            foreach (var (k, when) in o.RemovedHere)
            {
                // The copy has it changed after the removal here (another PC kept it): nothing to write, it waits here.
                if (inCopy.TryGetValue(k, out var c)) { if (c.Changed <= when) return true; continue; }
                if (!copy.Removed.TryGetValue(k, out var r) || r.When < when) return true;
            }
            if (copy.ShareSettings && o.IncludeSettings && o.Stamps.TryGetValue(SettingsStamp, out var ss) && (copy.Settings == null || (copy.Settings.Changed < ss.Changed && copy.Settings.Hash != ss.Hash))) return true;
            if (copy.ShareAi && o.IncludeAiKeys && o.Stamps.TryGetValue(AiStamp, out var ai) && (copy.Ai == null || (copy.Ai.Changed < ai.Changed && copy.Ai.Hash != ai.Hash))) return true;
            if (!copy.ShareAi && copy.Ai != null) return true;
            if (copy.Apps.IsEmpty && !AppsOf(s).IsEmpty) return true;
            return !copy.UsedBy.TryGetValue(pc, out var seen) || now - seen > TimeSpan.FromDays(1);
        }
    }

    /// <summary>After a round: removals here that the copy holds now, or that another PC overrode (it kept or changed the
    /// account later), are the copy's from now on. The overridden ones wait here like any account from another PC.</summary>
    public static void Settle(AppSettings s, Contents copy)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            var inCopy = copy.Accounts.ToDictionary(a => a.Email);
            foreach (var (k, when) in o.RemovedHere.ToList())
                if (copy.Removed.ContainsKey(k) || (inCopy.TryGetValue(k, out var c) && c.Changed > when)) o.RemovedHere.Remove(k);
        }
    }

    public static Plan Compare(AppSettings s, SecretVault vault, Contents copy, DateTimeOffset now)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            Track(s, vault, now);
            var plan = new Plan { ChoicesNewer = copy.ChoicesChanged > o.ChoicesChanged, Adopting = o.Adopt.Count > 0 };
            var shareSettings = plan.ChoicesNewer ? copy.ShareSettings : o.IncludeSettings;
            var shareAi = plan.ChoicesNewer ? copy.ShareAi : o.IncludeAiKeys;
            var local = s.Accounts.GroupBy(a => Key(a.Email)).ToDictionary(g => g.Key, g => g.First());
            foreach (var c in copy.Accounts)
            {
                if (local.ContainsKey(c.Email))
                {
                    var st = o.Stamps[c.Email];
                    if (c.Changed > st.Changed && c.Hash != st.Hash) plan.Update.Add(c);
                }
                else if (o.RemovedHere.ContainsKey(c.Email)) continue;   // removed here; the copy learns it at the next write
                else if (o.NotHere.Contains(c.Email) || !o.AutoAdd) plan.Available.Add(c);
                else plan.Add.Add(c);
            }
            foreach (var (key, a) in local)
                if (copy.Removed.TryGetValue(key, out var r) && r.When > o.Stamps[key].Changed && !o.RemovedHere.ContainsKey(key) && !o.Adopt.Contains(key))
                    plan.RemovedElsewhere.Add((a, r));
            if (shareSettings && copy.Settings is { } cs)
                plan.SettingsNewer = !o.Stamps.TryGetValue(SettingsStamp, out var ss) || (cs.Changed > ss.Changed && cs.Hash != ss.Hash);
            if (shareAi && copy.Ai is { } ca)
                plan.AiNewer = !o.Stamps.TryGetValue(AiStamp, out var st2) || (ca.Changed > st2.Changed && ca.Hash != st2.Hash);
            return plan;
        }
    }

    /// <summary>Takes the newer accounts, settings and AI block from the copy (adding and removing are the caller's,
    /// so it can start and stop syncs). Returns the accounts changed here, to be restarted.</summary>
    public static List<Account> ApplyUpdates(AppSettings s, SecretVault vault, Contents copy, Plan plan)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            if (plan.ChoicesNewer)
            {
                o.IncludeSettings = copy.ShareSettings;
                o.IncludeAiKeys = copy.ShareAi;
                o.ChoicesChanged = copy.ChoicesChanged;
            }
            var changed = new List<Account>();
            foreach (var c in plan.Update)
            {
                var i = s.Accounts.FindIndex(a => Key(a.Email) == c.Email);
                if (i < 0) continue;
                // Added here by hand: the copy's version, keeping how this PC signs in to it.
                if (o.Adopt.Remove(c.Email)) { changed.Add(MergeKeepingSignIn(s, vault, c, s.Accounts[i])); continue; }
                var a = Copy(c.Account);
                a.Id = s.Accounts[i].Id;
                s.Accounts[i] = a;
                Remember(s, vault, a, c);
                changed.Add(a);
            }
            // Added by hand and not in the copy (or the same there): it is this PC's own from now on.
            foreach (var key in o.Adopt.ToList())
            {
                o.Adopt.Remove(key);
                if (o.Stamps.TryGetValue(key, out var st) && st.Changed == DateTimeOffset.MinValue) st.Changed = DateTimeOffset.Now;
            }
            if (plan.SettingsNewer && copy.Settings is { } cs)
            {
                ApplyShared(s, cs);
                o.Stamps[SettingsStamp] = new CopyStamp { Hash = cs.Hash, Local = SharedOf(s).Hash, Changed = cs.Changed };
            }
            if (plan.AiNewer && copy.Ai is { } ca)
            {
                var consents = s.Ai.Consents;
                var learned = s.Ai.Connections.ToDictionary(x => x.Id);
                s.Ai = Copy(ca.Settings);
                s.Ai.Consents = consents ?? new();
                // Keep what this PC learned by itself about a connection it already had.
                foreach (var conn in s.Ai.Connections)
                    if (learned.TryGetValue(conn.Id, out var old) && old.Provider == conn.Provider && old.Endpoint == conn.Endpoint)
                        (conn.Models, conn.ModelsCheckedAt, conn.Unsupported, conn.DismissedNewModel) = (old.Models, old.ModelsCheckedAt, old.Unsupported, old.DismissedNewModel);
                s.Ai.NormaliseConnections();
                foreach (var (id, key) in ca.Keys) vault.Set(SecretVault.AiKeyFor(id), key);
                o.Stamps[AiStamp] = new CopyStamp { Hash = ca.Hash, Local = AiOf(s, vault).Hash, Changed = ca.Changed };
            }
            return changed;
        }
    }

    /// <summary>An account from the copy, ready to add here: its own id on this PC when that one is taken.</summary>
    public static Account ForAdd(AppSettings s, CopiedAccount c)
    {
        var a = Copy(c.Account);
        if (string.IsNullOrEmpty(a.Id) || s.Accounts.Any(x => x.Id == a.Id)) a.Id = Guid.NewGuid().ToString("N");
        return a;
    }

    /// <summary>After adding or updating an account from the copy: its secrets here, and the copy's fingerprint and time,
    /// so it isn't written back as a change of this PC. A copied sign-in replaces this PC's only when it is newer, and a
    /// refresh token only when it is from this PC's sign-in app (it doesn't work with another one).</summary>
    public static void Remember(AppSettings s, SecretVault vault, Account a, CopiedAccount c, bool keepSecrets = false)
    {
        lock (Gate)
        {
            var o = s.AccountsCopy;
            var localSignIn = SignedIn(o, c.Email);
            // Nothing to sign in with here yet (a new account): the copy's sign-in is the one to use.
            var noSecret = string.IsNullOrEmpty(vault.Get(SecretVault.PasswordKey(a.Id))) && string.IsNullOrEmpty(vault.Get(SecretVault.RefreshKey(a.Id)));
            var newer = localSignIn == null || (c.SignedIn ?? default) >= localSignIn || (noSecret && !keepSecrets);
            if (!keepSecrets && newer)
            {
                if (c.Password.Length > 0) vault.Set(SecretVault.PasswordKey(a.Id), c.Password);
                if (c.Refresh.Length > 0 && !OtherApp(c)) vault.Set(SecretVault.RefreshKey(a.Id), c.Refresh);
                if (c.SignedIn is { } t) o.SignedIn[c.Email] = t; else o.SignedIn.Remove(c.Email);
            }
            var local = HashOf(a, o);
            // This PC kept its own newer sign-in: the account (the copy's settings, this PC's sign-in) counts as changed here
            // just after the copy's version, so the next write brings the newer sign-in to the other PCs.
            if (!newer)
                o.Stamps[c.Email] = new CopyStamp { Hash = local, Local = local, Changed = c.Changed.AddTicks(1) };
            else o.Stamps[c.Email] = new CopyStamp { Hash = c.Hash, Local = local, Changed = c.Changed };
            o.NotHere.Remove(c.Email);
            o.RemovedHere.Remove(c.Email);
        }
    }

    /// <summary>An account from the copy that this PC has just signed in to itself (the new-PC home account, or one added
    /// by hand while it waited in the copy): it takes the copy's version (signature, name, colour…) but keeps how this
    /// PC signs in to it and its sign-in here. Replaces the account in the list; returns the merged one.</summary>
    public static Account MergeKeepingSignIn(AppSettings s, SecretVault vault, CopiedAccount c, Account local)
    {
        lock (Gate)
        {
            var merged = ForAdd(s, c);
            merged.Id = local.Id;
            merged.Kind = local.Kind; merged.Auth = local.Auth; merged.UserName = local.UserName;
            merged.ImapHost = local.ImapHost; merged.ImapPort = local.ImapPort; merged.ImapSecurity = local.ImapSecurity;
            merged.SmtpHost = local.SmtpHost; merged.SmtpPort = local.SmtpPort; merged.SmtpSecurity = local.SmtpSecurity;
            merged.ServerSavesSent = local.ServerSavesSent;
            var i = s.Accounts.FindIndex(a => a.Id == local.Id);
            if (i >= 0) s.Accounts[i] = merged;
            Remember(s, vault, merged, c, keepSecrets: true);
            return merged;
        }
    }

    /// <summary>A copied Google / Microsoft sign-in from another sign-in app than this PC uses: it can't be used here, so
    /// that account asks to sign in again (and the token isn't copied over this PC's own).</summary>
    public static bool OtherApp(CopiedAccount c) => c.Account.Auth == AuthMethod.OAuth2 && c.Client.Length > 0 && c.Client != ClientFor(c.Account.Kind);

    // ───────────────────────── the locked file ─────────────────────────

    public static byte[] Lock(Contents c, string password)
    {
        if (password.Length < MinPasswordLength) throw new ArgumentException($"Use at least {MinPasswordLength} characters.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(c, Json);
        var extra = new JsonObject { ["pc"] = c.FromPc, ["accounts"] = c.Accounts.Count, ["updated"] = c.Updated.ToString("o") };
        // What kind of note it is (never where it moved to), so a PC without the password still knows.
        if (c.Stopped) extra["note"] = NoteStopped;
        else if (c.MovedTo.Length > 0) extra["note"] = NoteMoved;
        var file = SettingsBackup.LockBytes(plain, password, Format, c.AppVersion, c.Updated, extra);
        if (file.Length > MaxBytes)
            throw new InvalidOperationException($"The copy would be {file.Length / 1024 / 1024} MB; the limit is {MaxBytes / 1024 / 1024} MB. Pictures in signatures are the usual reason.");
        return file;
    }

    public static Contents Unlock(byte[] file, string password)
    {
        if (file.Length > MaxBytes * 2) throw new InvalidDataException("This copy is too big to be a Magpie accounts copy.");
        var plain = SettingsBackup.UnlockBytes(file, password, Format, "This isn't a Magpie accounts copy.",
            "This copy was made by a newer Magpie. Update Magpie, then try again.", () => new WrongPasswordException());
        Contents c;
        try { c = JsonSerializer.Deserialize<Contents>(plain, Json) ?? throw new InvalidDataException("The copy is empty."); }
        catch (JsonException ex) { throw new InvalidDataException("The copy can't be read: " + ex.Message); }
        c.Accounts = (c.Accounts ?? new()).Where(a => a?.Account != null && Key(a.Account.Email).Contains('@')).ToList();
        foreach (var a in c.Accounts) { a.Password ??= ""; a.Refresh ??= ""; a.Client ??= ""; a.Hash ??= ""; }
        c.Removed = (c.Removed ?? new()).Where(kv => kv.Value != null).ToDictionary(kv => Key(kv.Key), kv => kv.Value);
        c.UsedBy ??= new();
        c.Apps ??= new();
        c.MovedTo ??= "";
        c.FromPc ??= "";
        return c;
    }

    /// <summary>What the copy says about itself without its password (shown before asking for it).</summary>
    public sealed record Summary(string Pc, int Accounts, DateTimeOffset Updated, string Note = "")
    {
        /// <summary>A "stopped" or "moved" note: no accounts in it.</summary>
        public bool IsNote => Accounts == 0;
        public bool IsMoved => Note == NoteMoved;
        /// <summary>A "stopped" note (it says so in its open part).</summary>
        public bool IsStopped => IsNote && Note == NoteStopped;
    }

    public const string NoteStopped = "stopped";
    public const string NoteMoved = "moved";

    public static Summary? Peek(byte[] file)
    {
        try
        {
            var n = JsonNode.Parse(file);
            if (n?["format"]?.GetValue<string>() != Format) return null;
            return new Summary(n["pc"]?.GetValue<string>() ?? "", n["accounts"]?.GetValue<int>() ?? 0,
                DateTimeOffset.TryParse(n["updated"]?.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t : default,
                n["note"]?.GetValue<string>() ?? "");
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
