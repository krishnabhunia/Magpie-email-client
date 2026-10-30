using Magpie.Core.Auth;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using MailKit;
using MimeKit;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>SY1 (queue #71, approved A + SA1): sign in once on a new PC and every account comes along.</summary>
public class Release1100Tests
{
    private const string Pwd = "correct horse battery";
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    /// <summary>One PC: its settings and its vault.</summary>
    private sealed class Pc : IDisposable
    {
        public TempDir Dir { get; } = new();
        public AppSettings S { get; } = new();
        public SecretVault V { get; }
        public string Name { get; }
        public Pc(string name) { Name = name; V = new SecretVault(Dir.File("secrets.json"), new FakeProtector()); SettingsStore.Normalise(S); }
        public Account Add(string email, string? password = "pw-x", string? refresh = null)
        {
            var a = new Account { Email = email, DisplayName = email.Split('@')[0], Kind = AccountKind.Imap, ImapHost = "imap.x.test" };
            S.Accounts.Add(a);
            if (password != null) V.Set(SecretVault.PasswordKey(a.Id), password);
            if (refresh != null) V.Set(SecretVault.RefreshKey(a.Id), refresh);
            return a;
        }
        public AccountsCopy.Contents Write(AccountsCopy.Contents? previous, DateTimeOffset now) =>
            AccountsCopy.Unlock(AccountsCopy.Lock(AccountsCopy.Build(S, V, previous, Name, "11.0.0", now), Pwd), Pwd);
        public void Dispose() => Dir.Dispose();
    }

    private static Pc FirstPc()
    {
        var pc = new Pc("HP-OMEN");
        pc.S.AccountsCopy.Enabled = true;
        pc.S.AccountsCopy.HomeEmail = "home@x.test";
        pc.Add("home@x.test");
        pc.Add("work@x.test", password: null, refresh: "refresh-work");
        pc.S.QuickReplies = new() { "On it." };
        AccountsCopy.StartHere(pc.S, pc.V, T0);
        return pc;
    }

    [Fact]
    public void The_copy_is_locked_with_the_sync_password_and_says_what_it_is_without_it()
    {
        using var pc = FirstPc();
        var c = AccountsCopy.Build(pc.S, pc.V, null, pc.Name, "11.0.0", T0);
        var file = AccountsCopy.Lock(c, Pwd);
        Assert.DoesNotContain("refresh-work", System.Text.Encoding.UTF8.GetString(file));
        Assert.DoesNotContain("work@x.test", System.Text.Encoding.UTF8.GetString(file));
        var peek = AccountsCopy.Peek(file)!;
        Assert.Equal(("HP-OMEN", 2), (peek.Pc, peek.Accounts));
        Assert.Throws<AccountsCopy.WrongPasswordException>(() => AccountsCopy.Unlock(file, "not the password"));
        Assert.Throws<InvalidDataException>(() => AccountsCopy.Unlock(SettingsBackup.Lock(new SettingsBackup.Contents(), Pwd), Pwd));
        Assert.Throws<ArgumentException>(() => AccountsCopy.Lock(c, "short"));
        var back = AccountsCopy.Unlock(file, Pwd);
        Assert.Equal(new[] { "home@x.test", "work@x.test" }, back.Accounts.Select(a => a.Email));
        Assert.Equal("refresh-work", back.Accounts[1].Refresh);
        Assert.Equal(new[] { "On it." }, back.Settings!.QuickReplies);
        Assert.Null(back.Ai);   // AI keys only when ticked
        Assert.Equal(T0, back.UsedBy["HP-OMEN"]);
    }

    [Fact]
    public void A_new_PC_gets_every_account_with_its_sign_in_and_writes_nothing_back()
    {
        using var pc1 = FirstPc();
        var copy = pc1.Write(null, T0);
        using var pc2 = new Pc("NEW-PC");
        pc2.S.AccountsCopy.Enabled = true;
        pc2.S.AccountsCopy.HomeEmail = "home@x.test";
        var plan = AccountsCopy.Compare(pc2.S, pc2.V, copy, T0.AddHours(1));
        Assert.Equal(new[] { "home@x.test", "work@x.test" }, plan.Add.Select(c => c.Email));
        Assert.True(plan.SettingsNewer);
        AccountsCopy.ApplyUpdates(pc2.S, pc2.V, copy, plan);
        foreach (var c in plan.Add)
        {
            var a = AccountsCopy.ForAdd(pc2.S, c);
            AccountsCopy.Remember(pc2.S, pc2.V, a, c);
            pc2.S.Accounts.Add(a);
        }
        Assert.Equal("refresh-work", pc2.V.Get(SecretVault.RefreshKey(pc2.S.Accounts[1].Id)));
        Assert.Equal(new[] { "On it." }, pc2.S.QuickReplies);
        // Same fingerprints as the copy: no change of this PC to write (only "used by NEW-PC" is new).
        Assert.True(AccountsCopy.NeedsWrite(pc2.S, pc2.V, copy, "NEW-PC", T0.AddHours(1)));
        var again = pc2.Write(copy, T0.AddHours(1));
        Assert.False(AccountsCopy.NeedsWrite(pc2.S, pc2.V, again, "NEW-PC", T0.AddHours(2)));
        Assert.All(again.Accounts, a => Assert.Equal(T0, a.Changed));
        // A Microsoft-style new refresh token every hour is not a change.
        pc2.V.Set(SecretVault.RefreshKey(pc2.S.Accounts[1].Id), "refresh-rotated");
        Assert.False(AccountsCopy.Track(pc2.S, pc2.V, T0.AddHours(3)));
    }

    [Fact]
    public void The_newer_change_wins_per_account_and_settings_block()
    {
        using var pc1 = FirstPc();
        using var pc2 = FirstPc();
        var copy = pc1.Write(null, T0);
        AccountsCopy.Track(pc2.S, pc2.V, T0.AddMinutes(1));   // pc2 has the same accounts from before
        // PC 1 changes the work signature later; PC 2 changes the quick replies even later.
        pc1.S.Accounts[1].SignatureHtml = "<p>New signature</p>";
        copy = pc1.Write(copy, T0.AddMinutes(10));
        pc2.S.QuickReplies = new() { "Later reply" };
        AccountsCopy.Track(pc2.S, pc2.V, T0.AddMinutes(20));
        var plan = AccountsCopy.Compare(pc2.S, pc2.V, copy, T0.AddMinutes(21));
        Assert.Equal("work@x.test", Assert.Single(plan.Update).Email);
        Assert.False(plan.SettingsNewer);   // PC 2's own change is newer
        var changed = AccountsCopy.ApplyUpdates(pc2.S, pc2.V, copy, plan);
        Assert.Equal("<p>New signature</p>", Assert.Single(changed).SignatureHtml);
        Assert.Equal(pc2.S.Accounts[1].Id, changed[0].Id);   // keeps this PC's id
        var merged = pc2.Write(copy, T0.AddMinutes(22));
        Assert.Equal(new[] { "Later reply" }, merged.Settings!.QuickReplies);
        Assert.Equal("<p>New signature</p>", merged.Accounts.Single(a => a.Email == "work@x.test").Account.SignatureHtml);
    }

    [Fact]
    public void Removing_an_account_never_removes_it_elsewhere_by_itself()
    {
        using var pc1 = FirstPc();
        var copy = pc1.Write(null, T0);
        using var pc2 = FirstPc();
        AccountsCopy.Track(pc2.S, pc2.V, T0);
        // PC 1 removes work@ (MailEngine.RemoveAccount notes it like this).
        pc1.S.Accounts.RemoveAll(a => a.Email == "work@x.test");
        pc1.S.AccountsCopy.RemovedHere["work@x.test"] = T0.AddMinutes(5);
        pc1.S.AccountsCopy.NotHere.Add("work@x.test");
        copy = pc1.Write(copy, T0.AddMinutes(5));
        Assert.DoesNotContain(copy.Accounts, a => a.Email == "work@x.test");
        Assert.Equal("HP-OMEN", copy.Removed["work@x.test"].ByPc);
        var plan = AccountsCopy.Compare(pc2.S, pc2.V, copy, T0.AddMinutes(6));
        Assert.Equal("work@x.test", Assert.Single(plan.RemovedElsewhere).Account.Email);
        Assert.Contains(pc2.S.Accounts, a => a.Email == "work@x.test");   // still here: PC 2 asks
        // "Keep it" on PC 2: it counts as changed there now, so the copy gets it back and the removal note goes.
        pc2.S.AccountsCopy.Stamps["work@x.test"].Changed = T0.AddMinutes(7);
        var kept = AccountsCopy.Build(pc2.S, pc2.V, copy, "NEW-PC", "11.0.0", T0.AddMinutes(7));
        Assert.Contains(kept.Accounts, a => a.Email == "work@x.test");
        Assert.False(kept.Removed.ContainsKey("work@x.test"));
        // PC 1 removed it on purpose: it waits there instead of coming back by itself.
        var back = AccountsCopy.Compare(pc1.S, pc1.V, kept, T0.AddMinutes(8));
        Assert.Empty(back.Add);
        Assert.Equal("work@x.test", Assert.Single(back.Available).Email);
    }

    [Fact]
    public void A_new_sign_in_travels_and_accounts_only_elsewhere_stay_in_the_copy()
    {
        using var pc1 = FirstPc();
        var copy = pc1.Write(null, T0);
        using var pc2 = new Pc("NEW-PC");
        pc2.S.AccountsCopy.Enabled = true;
        pc2.S.AccountsCopy.HomeEmail = "home@x.test";
        pc2.S.AccountsCopy.NotHere.Add("work@x.test");   // "Not now" on the new PC
        var home = AccountsCopy.ForAdd(pc2.S, copy.Accounts[0]);
        AccountsCopy.Remember(pc2.S, pc2.V, home, copy.Accounts[0]);
        pc2.S.Accounts.Add(home);
        var written = pc2.Write(copy, T0.AddMinutes(1));
        Assert.Contains(written.Accounts, a => a.Email == "work@x.test");   // not on PC 2, still in the copy
        // PC 1 signs in to work@ again (MailEngine.UpdateAccount notes the time): PC 2 isn't asked, but a third PC updates.
        pc1.V.Set(SecretVault.RefreshKey(pc1.S.Accounts[1].Id), "refresh-new");
        pc1.S.AccountsCopy.SignedIn["work@x.test"] = T0.AddMinutes(9);
        var newer = pc1.Write(written, T0.AddMinutes(10));
        using var pc3 = FirstPc();
        AccountsCopy.Track(pc3.S, pc3.V, T0);
        var plan = AccountsCopy.Compare(pc3.S, pc3.V, newer, T0.AddMinutes(11));
        Assert.Equal("work@x.test", Assert.Single(plan.Update).Email);
        AccountsCopy.ApplyUpdates(pc3.S, pc3.V, newer, plan);
        Assert.Equal("refresh-new", pc3.V.Get(SecretVault.RefreshKey(pc3.S.Accounts[1].Id)));
    }

    [Fact]
    public void AI_keys_come_along_only_when_ticked_and_consents_are_asked_again()
    {
        using var pc1 = FirstPc();
        pc1.S.AccountsCopy.IncludeAiKeys = true;
        pc1.S.Ai.Consents = new() { "summarise|main|https://api.openai.com/v1" };
        pc1.V.Set(SecretVault.AiKey, "sk-test");
        AccountsCopy.StartHere(pc1.S, pc1.V, T0);   // ticked in Settings → the copy is started again with it
        var copy = pc1.Write(null, T0);
        Assert.Equal("sk-test", copy.Ai!.Keys[AiConnection.FirstId]);
        Assert.Empty(copy.Ai.Settings.Consents);
        using var pc2 = new Pc("NEW-PC");
        pc2.S.AccountsCopy.Enabled = true;
        var plan = AccountsCopy.Compare(pc2.S, pc2.V, copy, T0);
        Assert.False(plan.AiNewer);   // not ticked here
        pc2.S.AccountsCopy.IncludeAiKeys = true;
        plan = AccountsCopy.Compare(pc2.S, pc2.V, copy, T0);
        Assert.True(plan.AiNewer);
        AccountsCopy.ApplyUpdates(pc2.S, pc2.V, copy, plan);
        Assert.Equal("sk-test", pc2.V.Get(SecretVault.AiKey));
    }

    [Fact]
    public void Sign_in_apps_travel_with_the_sign_ins_and_the_built_in_ones_stand_in()
    {
        using var pc1 = FirstPc();
        pc1.S.GoogleClientId = "own-app.apps.googleusercontent.com";
        var copy = pc1.Write(null, T0);
        using var pc2 = new Pc("NEW-PC");
        pc2.S.AccountsCopy.Enabled = true;
        var plan = AccountsCopy.Compare(pc2.S, pc2.V, copy, T0);
        Assert.True(plan.AdoptApps);
        AccountsCopy.ApplyUpdates(pc2.S, pc2.V, copy, plan);
        Assert.Equal("own-app.apps.googleusercontent.com", pc2.S.GoogleClientId);
        // Settings win; empty settings fall back to the release's built-in apps (empty in a local build).
        Assert.Equal(("mine", "s"), BuiltInSignIn.Google(" mine ", "s"));
        Assert.Equal((BuiltInSignIn.GoogleClientId, BuiltInSignIn.GoogleClientSecret), BuiltInSignIn.Google("", ""));
        Assert.Equal(BuiltInSignIn.MicrosoftClientId, BuiltInSignIn.Microsoft(""));
    }

    [Fact]
    public void The_copy_folder_and_email_stay_out_of_the_mail_lists()
    {
        Assert.True(AccountSync.IsCopyFolder("Magpie Sync", '/'));
        Assert.True(AccountSync.IsCopyFolder("INBOX.Magpie Sync", '.'));
        Assert.True(AccountSync.IsCopyFolder("magpie sync", '/'));
        Assert.False(AccountSync.IsCopyFolder("Work/Magpie Sync", '/'));
        var copyMail = new MessageSummary(0) { UniqueId = new UniqueId(1, 5), Headers = new HeaderList { { AccountsCopy.Header, "1" } } };
        var mail = new MessageSummary(1) { UniqueId = new UniqueId(1, 6), Headers = new HeaderList { { "Subject", "Hi" } } };
        Assert.False(AccountSync.IsVisibleSummary(copyMail));
        Assert.True(AccountSync.IsVisibleSummary(mail));
        var msg = CopyMailbox.BuildMessage("home@x.test", new byte[] { 1, 2, 3 }, new AccountsCopy.Summary("HP-OMEN", 2, T0));
        Assert.Equal("1", msg.Headers[AccountsCopy.Header]);
        Assert.Equal(AccountsCopy.FileName, Assert.Single(msg.Attachments).ContentDisposition.FileName);
    }

    [Fact]
    public void The_sync_password_is_long_and_its_strength_is_said_plainly()
    {
        Assert.Equal(0, AccountsCopy.Strength("short").Score);
        Assert.Equal(1, AccountsCopy.Strength("aaaaaaaaaaaa").Score);
        Assert.True(AccountsCopy.Strength("Tr0ub4dor & horse staple").Score >= 3);
        Assert.Equal(10, AccountsCopy.MinPasswordLength);
    }

    [Fact]
    public void Old_settings_load_with_the_copy_off()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AccountsCopy\":null}", AppSettings.Json)!;
        SettingsStore.Normalise(s);
        Assert.False(s.AccountsCopy.Enabled);
        Assert.True(s.AccountsCopy.AutoAdd);
        var on = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AccountsCopy\":{\"Enabled\":true,\"HomeEmail\":\"\"}}", AppSettings.Json)!;
        SettingsStore.Normalise(on);
        Assert.False(on.AccountsCopy.Enabled);   // no home account: off
    }
}
