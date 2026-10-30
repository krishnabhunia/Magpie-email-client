using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>
/// Design SY1 (#71, approved A + SA1): keeps the accounts copy in the home mailbox in step with this PC. At each mail
/// check (and soon after a change here) it reads the copy, takes what another PC changed more recently, adds accounts
/// from other PCs (when chosen), asks about removals, and writes this PC's newer changes back.
/// Settings are only touched through <see cref="RunOnUi"/>, so the app can keep them on its UI thread; one round runs at
/// a time, and stopping or moving the copy waits for it.
/// </summary>
public sealed class AccountsCopyService
{
    private readonly MailEngine _e;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _busy = new(1, 1);
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _debounce;

    public AccountsCopyService(MailEngine engine) { _e = engine; }

    /// <summary>Runs settings changes where the app wants them (the WPF app: its UI thread). Default: right away.</summary>
    public Func<Action, Task> RunOnUi { get; set; } = a => { a(); return Task.CompletedTask; };
    /// <summary>True while changes from the copy must wait (the Settings window is open and would write its older values
    /// back over them). The copy is still written.</summary>
    public Func<bool> Paused { get; set; } = () => false;
    /// <summary>"11.0.0" in the copy; set by the app.</summary>
    public string AppVersion { get; set; } = "";

    /// <summary>The status line or the reason it stopped changed (Settings → Accounts).</summary>
    public event Action? StatusChanged;
    /// <summary>Accounts in the copy that are not on this PC and wait for the user.</summary>
    public event Action<IReadOnlyList<AccountsCopy.CopiedAccount>>? AccountsAvailable;
    /// <summary>Another PC removed this account: "Remove it here too / Keep it".</summary>
    public event Action<Account, AccountsCopy.RemovedAccount>? RemovedElsewhere;
    /// <summary>The copy has a new sync password (or none is kept here): ask once.</summary>
    public event Action? PasswordNeeded;
    /// <summary>Accounts were just added from the copy (design SY1·4: the adding window shows their progress).</summary>
    public event Action<IReadOnlyList<Account>>? AccountsAdded;
    /// <summary>Another PC stopped the copy (its name): this PC stopped too, its accounts stay.</summary>
    public event Action<string>? StoppedElsewhere;

    /// <summary>Plain-words problem, or "" when the last round went well.</summary>
    public string Problem { get; private set; } = "";
    public bool NeedsPassword { get; private set; }
    /// <summary>The copy that was there is missing from the home mailbox: nothing is written until "Update the copy now".</summary>
    public bool Missing { get; private set; }
    public IReadOnlyList<AccountsCopy.CopiedAccount> Available { get; private set; } = Array.Empty<AccountsCopy.CopiedAccount>();

    private AccountsCopySettings Options => _e.Config.AccountsCopy;
    public string PcName => Options.PcName.Length > 0 ? Options.PcName : Environment.MachineName;
    public Account? Home => _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == Options.HomeEmail);

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _e.Settings.Changed += SoonAfterChange;
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _e.Settings.Changed -= SoonAfterChange;
        _cts?.Cancel();
        _debounce?.Dispose();
    }

    /// <summary>Check now (Settings → "Update the copy now", or after a change).</summary>
    public void Poke() => _wake.Release();

    // A change is noted at once (so its time is when it was made, also offline); several saves in a row make one write.
    private void SoonAfterChange()
    {
        if (!Options.Enabled) return;
        try { AccountsCopy.Track(_e.Config, _e.Vault, DateTimeOffset.Now); } catch (Exception ex) { Log.Warn("accounts copy: " + ex.Message); }
        _debounce ??= new System.Threading.Timer(_ => Poke());
        _debounce.Change(TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Fail(Connector.Friendly(ex)); Log.Warn("accounts copy: " + AccountSync.Describe(ex)); }
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, _e.Config.SyncIntervalMinutes)), ct);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(0, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Fail(string problem)
    {
        Problem = problem;
        StatusChanged?.Invoke();
    }

    /// <summary>One round: read, take the newer changes, add / ask, write back what this PC has newer.
    /// <paramref name="rebuild"/> writes a new copy when the one that was there has gone ("Update the copy now").</summary>
    public async Task RunOnceAsync(CancellationToken ct, bool rebuild = false)
    {
        if (!Options.Enabled) return;
        await _busy.WaitAsync(ct);
        try
        {
            if (!Options.Enabled) return;
            var home = Home;
            if (home == null) { Fail("The home account isn't in Magpie on this PC any more. Stop the copy here, or add that account again."); return; }
            var password = _e.Vault.Get(AccountsCopy.VaultPassword);
            var newPassword = _e.Vault.Get(AccountsCopy.VaultNewPassword);
            if (string.IsNullOrEmpty(password)) { AskForPassword("Type the sync password to keep the copy up to date."); return; }

            using var client = await _e.Connector.OpenImapAsync(home, ct);
            var file = await CopyMailbox.ReadAsync(client, ct);
            AccountsCopy.Contents? copy = null;
            if (file != null)
            {
                try { copy = AccountsCopy.Unlock(file, password); }
                catch (AccountsCopy.WrongPasswordException) when (!string.IsNullOrEmpty(newPassword))
                {
                    // The new password chosen here already locks it (written by an earlier round that didn't finish).
                    copy = AccountsCopy.Unlock(file, newPassword);
                    _e.Vault.Set(AccountsCopy.VaultPassword, newPassword);
                    _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
                    (password, newPassword) = (newPassword, null);
                }
                catch (AccountsCopy.WrongPasswordException)
                {
                    // Another PC chose a new sync password: ask for it once; nothing is written meanwhile.
                    AskForPassword("The copy has a new sync password. Type it once on this PC.");
                    return;
                }
            }
            NeedsPassword = false;

            if (copy is { Stopped: true })
            {
                await StopHereAsync(copy.FromPc);
                return;
            }
            // The home account was changed on another PC: follow it there (when this PC has that account).
            if (copy is { MovedTo.Length: > 0 } && AccountsCopy.Key(copy.MovedTo) != Options.HomeEmail)
            {
                var to = AccountsCopy.Key(copy.MovedTo);
                if (_e.Accounts.All(a => AccountsCopy.Key(a.Email) != to)) { Fail($"The copy moved to {to}. Add that account here to keep in step."); return; }
                await RunOnUi(() => { Options.HomeEmail = to; _e.Settings.Save(notify: false); });
                Log.Info("accounts copy: followed the copy to " + to);
                Poke();
                return;
            }
            // A copy this PC used before has gone: don't make a new one from this PC alone (other PCs' accounts and
            // removals would be lost) unless asked to.
            if (file == null && Options.CopyUpdated != null && !rebuild)
            {
                Missing = true;
                Fail("The copy is missing from the home mailbox. Use \"Update the copy now\" to make a new one from this PC.");
                return;
            }
            Missing = false;
            if (copy != null && !Paused()) await TakeAsync(copy);

            var now = DateTimeOffset.Now;
            var write = newPassword != null;
            await RunOnUi(() => write |= AccountsCopy.NeedsWrite(_e.Config, _e.Vault, copy, PcName, now));
            if (write)
            {
                AccountsCopy.Contents? built = null;
                await RunOnUi(() => built = AccountsCopy.Build(_e.Config, _e.Vault, copy, PcName, AppVersion, now));
                var lockWith = newPassword ?? password;
                var locked = await Task.Run(() => AccountsCopy.Lock(built!, lockWith), ct);
                await CopyMailbox.WriteAsync(client, home.Email, locked, ct);
                if (newPassword != null)
                {
                    _e.Vault.Set(AccountsCopy.VaultPassword, newPassword);
                    _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
                    Log.Info("accounts copy: locked with the new sync password");
                }
                copy = built;
                file = locked;
                Log.Info($"accounts copy written: {built!.Accounts.Count} account(s), {locked.Length / 1024} KB");
            }
            try { await client.DisconnectAsync(true, ct); } catch { }

            await RunOnUi(() =>
            {
                var o = Options;
                o.LastChecked = now;
                if (copy != null)
                {
                    o.CopyUpdated = copy.Updated;
                    o.CopyAccounts = copy.Accounts.Count;
                    o.UsedBy = copy.UsedBy.OrderByDescending(u => u.Value).Select(u => u.Key).ToList();
                    // Written ones are in the copy now; the copy remembers them from here on.
                    foreach (var k in o.RemovedHere.Keys.Where(k => copy.Removed.ContainsKey(k)).ToList()) o.RemovedHere.Remove(k);
                }
                if (file != null) o.CopySize = file.Length;
                _e.Settings.Save(notify: false);
            });
            Problem = "";
            StatusChanged?.Invoke();
        }
        finally { _busy.Release(); }
    }

    private void AskForPassword(string why)
    {
        NeedsPassword = true;
        Fail(why);
        PasswordNeeded?.Invoke();
    }

    private async Task TakeAsync(AccountsCopy.Contents copy)
    {
        AccountsCopy.Plan? plan = null;
        var changed = new List<Account>();
        var added = new List<Account>();
        await RunOnUi(() =>
        {
            plan = AccountsCopy.Compare(_e.Config, _e.Vault, copy, DateTimeOffset.Now);
            if (!plan.Changes) return;
            changed = AccountsCopy.ApplyUpdates(_e.Config, _e.Vault, copy, plan);
            var toAdd = new List<Account>();
            foreach (var c in plan.Add)
            {
                var a = AccountsCopy.ForAdd(_e.Config, c);
                AccountsCopy.Remember(_e.Config, _e.Vault, a, c);
                toAdd.Add(a);
            }
            _e.Settings.Save();
            foreach (var a in changed) _e.RestartAccount(a);
            added = _e.AddAccounts(toAdd, fromCopy: true);
            if (changed.Count > 0 || plan.SettingsNewer) Log.Info($"accounts copy: took {changed.Count} account change(s){(plan.SettingsNewer ? " and settings" : "")} from {copy.FromPc}");
        });
        if (plan == null) return;
        if (added.Count > 0) { Log.Info($"accounts copy: added {added.Count} account(s) from {copy.FromPc}"); AccountsAdded?.Invoke(added); }
        Available = plan.Available;
        if (plan.Available.Count > 0) AccountsAvailable?.Invoke(plan.Available);
        foreach (var (a, r) in plan.RemovedElsewhere) RemovedElsewhere?.Invoke(a, r);
    }

    private async Task StopHereAsync(string byPc)
    {
        await RunOnUi(() => TurnOff());
        Log.Info("accounts copy: stopped on " + byPc);
        Problem = "";
        StatusChanged?.Invoke();
        StoppedElsewhere?.Invoke(byPc);
    }

    private void TurnOff()
    {
        var o = Options;
        o.Enabled = false;
        o.CopyUpdated = null;
        o.CopyAccounts = 0;
        o.CopySize = 0;
        o.UsedBy = new();
        o.Stamps = new();
        o.RemovedHere = new();
        _e.Vault.Set(AccountsCopy.VaultPassword, null);
        _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
        _e.Settings.Save(notify: false);
    }

    // ───────────────────────── what the user does ─────────────────────────

    /// <summary>Settings → "Bring my accounts to other PCs" → Turn on. When the home mailbox already has a copy (another
    /// PC made it), this PC joins it: the password must open it, and the copy's settings win. Otherwise this PC's
    /// accounts and settings start it.</summary>
    public async Task EnableAsync(string homeEmail, string password, bool includeSettings, bool includeAi, bool autoAdd, CancellationToken ct)
    {
        if (password.Length < AccountsCopy.MinPasswordLength) throw new ArgumentException($"Use at least {AccountsCopy.MinPasswordLength} characters.");
        var home = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == AccountsCopy.Key(homeEmail)) ?? throw new ArgumentException("Choose a home account.");
        var file = await FindAsync(home, null, ct);
        AccountsCopy.Contents? existing = null;
        if (file != null)
        {
            try { existing = AccountsCopy.Unlock(file, password); }
            catch (AccountsCopy.WrongPasswordException)
            {
                throw new InvalidOperationException($"{home.Email} already has a copy made on another PC, locked with another password. Type that password to join it.");
            }
            if (existing.Stopped || existing.MovedTo.Length > 0) existing = null;   // a note only: start a new copy
        }
        await _busy.WaitAsync(ct);
        try
        {
            await RunOnUi(() =>
            {
                var o = Options;
                o.Enabled = true;
                o.HomeEmail = AccountsCopy.Key(homeEmail);
                o.AutoAdd = autoAdd;
                o.IncludeSettings = includeSettings;
                o.IncludeAiKeys = includeAi;
                o.CopyUpdated = null;
                _e.Vault.Set(AccountsCopy.VaultPassword, password);
                _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
                if (existing != null) AccountsCopy.Join(_e.Config, _e.Vault, existing, DateTimeOffset.Now);
                else AccountsCopy.StartHere(_e.Config, _e.Vault, DateTimeOffset.Now);
                NeedsPassword = false;
                _e.Settings.Save(notify: false);
            });
        }
        finally { _busy.Release(); }
        Poke();
    }

    /// <summary>The choices changed while the copy is on (what comes along, adding by themselves): every PC follows.</summary>
    public void SetChoices(bool includeSettings, bool includeAi, bool autoAdd)
    {
        var o = Options;
        var share = o.IncludeSettings != includeSettings || o.IncludeAiKeys != includeAi;
        o.AutoAdd = autoAdd;
        o.IncludeSettings = includeSettings;
        o.IncludeAiKeys = includeAi;
        if (share) AccountsCopy.StartHere(_e.Config, _e.Vault, DateTimeOffset.Now);   // newly ticked blocks start from this PC
        _e.Settings.Save(notify: false);
        Poke();
    }

    /// <summary>"Change sync password": the copy is opened with the old one and locked with the new one at the next round;
    /// the other PCs ask for it once.</summary>
    public void ChangePassword(string newPassword)
    {
        if (newPassword.Length < AccountsCopy.MinPasswordLength) throw new ArgumentException($"Use at least {AccountsCopy.MinPasswordLength} characters.");
        _e.Vault.Set(AccountsCopy.VaultNewPassword, newPassword);
        Poke();
    }

    /// <summary>The sync password typed again after another PC changed it.</summary>
    public void SetPassword(string password)
    {
        if (password.Length < AccountsCopy.MinPasswordLength) throw new ArgumentException($"Use at least {AccountsCopy.MinPasswordLength} characters.");
        _e.Vault.Set(AccountsCopy.VaultPassword, password);
        _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
        NeedsPassword = false;
        Poke();
    }

    /// <summary>The home account is being removed (or changed): the copy moves to another account. The old mailbox keeps a
    /// short note "moved to …" (nothing else in it), so the other PCs follow; the new home gets the full copy at once.</summary>
    public async Task MoveHomeAsync(string newHomeEmail, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var old = Home;
            var password = _e.Vault.Get(AccountsCopy.VaultPassword);
            AccountsCopy.Contents? current = null;
            if (old != null && !string.IsNullOrEmpty(password))
            {
                using var client = await _e.Connector.OpenImapAsync(old, ct);
                if (await CopyMailbox.ReadAsync(client, ct) is { } file)
                    try { current = AccountsCopy.Unlock(file, password); } catch (AccountsCopy.WrongPasswordException) { }
                var note = new AccountsCopy.Contents { Updated = DateTimeOffset.Now, FromPc = PcName, AppVersion = AppVersion, MovedTo = AccountsCopy.Key(newHomeEmail) };
                var locked = await Task.Run(() => AccountsCopy.Lock(note, password), ct);
                await CopyMailbox.WriteAsync(client, old.Email, locked, ct);
                try { await client.DisconnectAsync(true, ct); } catch { }
            }
            // The new home starts from the full copy that was in the old one (other PCs' accounts and removals stay).
            var to = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == AccountsCopy.Key(newHomeEmail));
            if (to != null && !string.IsNullOrEmpty(password))
            {
                AccountsCopy.Contents? built = null;
                var now = DateTimeOffset.Now;
                await RunOnUi(() => built = AccountsCopy.Build(_e.Config, _e.Vault, current, PcName, AppVersion, now));
                var locked = await Task.Run(() => AccountsCopy.Lock(built!, password), ct);
                using var client = await _e.Connector.OpenImapAsync(to, ct);
                await CopyMailbox.WriteAsync(client, to.Email, locked, ct);
                try { await client.DisconnectAsync(true, ct); } catch { }
            }
            await RunOnUi(() => { Options.HomeEmail = AccountsCopy.Key(newHomeEmail); _e.Settings.Save(notify: false); });
        }
        finally { _busy.Release(); }
        Poke();
    }

    /// <summary>"Stop and delete the copy": the accounts and sign-ins leave the mailbox, replaced by a short "stopped" note
    /// (nothing else in it) so the other PCs stop too instead of writing it back; their accounts stay. With
    /// <paramref name="deleteCopy"/> false it only stops on this PC.</summary>
    public async Task DisableAsync(bool deleteCopy, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var home = Home;
            var password = _e.Vault.Get(AccountsCopy.VaultPassword);
            if (deleteCopy && home != null)
            {
                using var client = await _e.Connector.OpenImapAsync(home, ct);
                if (!string.IsNullOrEmpty(password))
                {
                    var note = new AccountsCopy.Contents { Updated = DateTimeOffset.Now, FromPc = PcName, AppVersion = AppVersion, Stopped = true };
                    var locked = await Task.Run(() => AccountsCopy.Lock(note, password), ct);
                    await CopyMailbox.WriteAsync(client, home.Email, locked, ct);
                }
                else await CopyMailbox.DeleteAsync(client, ct);
                try { await client.DisconnectAsync(true, ct); } catch { }
            }
            await RunOnUi(TurnOff);
        }
        finally { _busy.Release(); }
        Problem = "";
        Missing = false;
        StatusChanged?.Invoke();
    }

    /// <summary>The new-PC step (design SY1·3): after signing in to an account, is there a copy in its mailbox?
    /// Null when there is none. A "stopped" / "moved" note lists no accounts (<see cref="AccountsCopy.Peek"/>).
    /// The account doesn't have to be added yet.</summary>
    public async Task<byte[]?> FindAsync(Account account, string? password, CancellationToken ct)
    {
        using var client = await _e.Connector.OpenImapAsync(account, ct, password);
        try { return await CopyMailbox.ReadAsync(client, ct); }
        finally { try { await client.DisconnectAsync(true, ct); } catch { } }
    }

    /// <summary>
    /// The new-PC step (design SY1·3/4): the home account was just added here; adds the ticked accounts from the copy in
    /// one step, takes the copy's settings, and keeps the copy in step from now on. The home account keeps this PC's own
    /// sign-in and server settings (only its signature, name and colour come from the copy). Returns the accounts added.
    /// </summary>
    public List<Account> AddFromCopy(AccountsCopy.Contents copy, Account home, string password, IEnumerable<string> emails)
    {
        var o = Options;
        var homeKey = AccountsCopy.Key(home.Email);
        var wanted = emails.Select(AccountsCopy.Key).ToHashSet();
        o.Enabled = true;
        o.HomeEmail = homeKey;
        o.IncludeSettings = copy.ShareSettings;
        o.IncludeAiKeys = copy.ShareAi;
        o.ChoicesChanged = copy.ChoicesChanged;
        _e.Vault.Set(AccountsCopy.VaultPassword, password);
        // Accounts not ticked stay in the copy and wait (Settings → Accounts lists them).
        foreach (var c in copy.Accounts.Where(c => c.Email != homeKey && !wanted.Contains(c.Email) && !o.NotHere.Contains(c.Email))) o.NotHere.Add(c.Email);

        if (copy.Accounts.FirstOrDefault(c => c.Email == homeKey) is { } homeCopy)
        {
            var i = _e.Config.Accounts.FindIndex(a => a.Id == home.Id);
            if (i >= 0)
            {
                var local = _e.Config.Accounts[i];
                var merged = AccountsCopy.ForAdd(_e.Config, homeCopy);
                merged.Id = local.Id;
                // How this PC signs in to it stays as it was just set up here.
                merged.Kind = local.Kind; merged.Auth = local.Auth; merged.UserName = local.UserName;
                merged.ImapHost = local.ImapHost; merged.ImapPort = local.ImapPort; merged.ImapSecurity = local.ImapSecurity;
                merged.SmtpHost = local.SmtpHost; merged.SmtpPort = local.SmtpPort; merged.SmtpSecurity = local.SmtpSecurity;
                merged.ServerSavesSent = local.ServerSavesSent;
                _e.Config.Accounts[i] = merged;
                AccountsCopy.Remember(_e.Config, _e.Vault, merged, homeCopy, keepSecrets: true);
                _e.RestartAccount(merged);
            }
        }
        var plan = AccountsCopy.Compare(_e.Config, _e.Vault, copy, DateTimeOffset.Now);
        plan.Update.Clear();
        AccountsCopy.ApplyUpdates(_e.Config, _e.Vault, copy, plan);
        var toAdd = new List<Account>();
        foreach (var c in copy.Accounts.Where(c => wanted.Contains(c.Email) && c.Email != homeKey))
        {
            var a = AccountsCopy.ForAdd(_e.Config, c);
            AccountsCopy.Remember(_e.Config, _e.Vault, a, c);
            toAdd.Add(a);
        }
        _e.Settings.Save();
        var added = _e.AddAccounts(toAdd, fromCopy: true);
        Log.Info($"accounts copy: added {added.Count} account(s) on a new PC from {copy.FromPc}");
        if (added.Count > 0) AccountsAdded?.Invoke(added);
        Poke();
        return added;
    }

    /// <summary>Settings → Accounts: add accounts that wait in the copy (not added by themselves, or "Not now" before).</summary>
    public List<Account> AddWaiting(IEnumerable<AccountsCopy.CopiedAccount> chosen)
    {
        var toAdd = new List<Account>();
        foreach (var c in chosen)
        {
            var a = AccountsCopy.ForAdd(_e.Config, c);
            AccountsCopy.Remember(_e.Config, _e.Vault, a, c);
            toAdd.Add(a);
        }
        _e.Settings.Save();
        var added = _e.AddAccounts(toAdd, fromCopy: true);
        Available = Available.Where(x => !added.Any(a => AccountsCopy.Key(a.Email) == x.Email)).ToList();
        if (added.Count > 0) AccountsAdded?.Invoke(added);
        return added;
    }

    /// <summary>"Remove it here too" (true) or "Keep it" (false) after another PC removed the account.</summary>
    public void AnswerRemoved(Account account, bool removeHere)
    {
        var key = AccountsCopy.Key(account.Email);
        if (removeHere) { _e.RemoveAccount(account.Id); return; }
        // Kept: it counts as changed here now, so the copy gets it back (and its removal note goes).
        if (Options.Stamps.TryGetValue(key, out var st)) st.Changed = DateTimeOffset.Now;
        _e.Settings.Save(notify: false);
        Poke();
    }
}
