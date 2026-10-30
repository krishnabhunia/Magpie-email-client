using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Security;

namespace Magpie.Core.Mail;

/// <summary>
/// Design SY1 (#71, approved A + SA1): keeps the accounts copy in the home mailbox in step with this PC. At each mail
/// check (and within a minute of a change here) it reads the copy, takes what another PC changed more recently, adds
/// accounts from other PCs (when chosen), asks about removals, and writes this PC's newer changes back.
/// Settings are only touched through <see cref="RunOnUi"/>, so the app can keep them on its UI thread.
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
    /// <summary>"Magpie 11.0.0" in the copy; set by the app.</summary>
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

    /// <summary>Plain-words problem, or "" when the last round went well.</summary>
    public string Problem { get; private set; } = "";
    public bool NeedsPassword { get; private set; }
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

    // Several saves in a row (Settings → Apply) make one write, within a minute of the first.
    private void SoonAfterChange()
    {
        if (!Options.Enabled) return;
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

    /// <summary>One round: read, take the newer changes, add / ask, write back what this PC has newer.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!Options.Enabled) return;
        await _busy.WaitAsync(ct);
        try
        {
            var home = Home;
            if (home == null) { Fail("The home account isn't in Magpie on this PC any more. Choose another one or stop the copy."); return; }
            var password = _e.Vault.Get(AccountsCopy.VaultPassword);
            if (string.IsNullOrEmpty(password)) { NeedsPassword = true; Fail("Type the sync password to keep the copy up to date."); PasswordNeeded?.Invoke(); return; }

            using var client = await _e.Connector.OpenImapAsync(home, ct);
            var file = await CopyMailbox.ReadAsync(client, ct);
            AccountsCopy.Contents? copy = null;
            if (file != null)
            {
                try { copy = AccountsCopy.Unlock(file, password); }
                catch (AccountsCopy.WrongPasswordException)
                {
                    // Another PC chose a new sync password: ask for it once; nothing is written meanwhile.
                    NeedsPassword = true;
                    Fail("The copy has a new sync password. Type it once on this PC.");
                    PasswordNeeded?.Invoke();
                    return;
                }
            }
            NeedsPassword = false;
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
            if (copy != null) await TakeAsync(copy);

            var now = DateTimeOffset.Now;
            var write = false;
            await RunOnUi(() => write = AccountsCopy.NeedsWrite(_e.Config, _e.Vault, copy, PcName, now));
            if (write)
            {
                byte[] locked = Array.Empty<byte>();
                AccountsCopy.Contents? built = null;
                await RunOnUi(() => built = AccountsCopy.Build(_e.Config, _e.Vault, copy, PcName, AppVersion, now));
                locked = await Task.Run(() => AccountsCopy.Lock(built!, password), ct);
                await CopyMailbox.WriteAsync(client, home.Email, locked, ct);
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
                }
                if (file != null) o.CopySize = file.Length;
                // Written ones are in the copy now; the copy remembers them from here on.
                if (copy != null) foreach (var k in o.RemovedHere.Keys.Where(k => copy.Removed.ContainsKey(k)).ToList()) o.RemovedHere.Remove(k);
                _e.Settings.Save(notify: false);
            });
            Problem = "";
            StatusChanged?.Invoke();
        }
        finally { _busy.Release(); }
    }

    private async Task TakeAsync(AccountsCopy.Contents copy)
    {
        AccountsCopy.Plan? plan = null;
        var changed = new List<Account>();
        var added = new List<Account>();
        await RunOnUi(() =>
        {
            var now = DateTimeOffset.Now;
            plan = AccountsCopy.Compare(_e.Config, _e.Vault, copy, now);
            if (!plan.Any) return;
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
            added = _e.AddAccounts(toAdd);
            if (changed.Count > 0 || plan.SettingsNewer) Log.Info($"accounts copy: took {changed.Count} account change(s){(plan.SettingsNewer ? " and settings" : "")} from {copy.FromPc}");
        });
        if (plan == null) return;
        if (added.Count > 0) { Log.Info($"accounts copy: added {added.Count} account(s) from {copy.FromPc}"); AccountsAdded?.Invoke(added); }
        Available = plan.Available;
        if (plan.Available.Count > 0) AccountsAvailable?.Invoke(plan.Available);
        foreach (var (a, r) in plan.RemovedElsewhere) RemovedElsewhere?.Invoke(a, r);
    }

    // ───────────────────────── what the user does ─────────────────────────

    /// <summary>Settings → "Bring my accounts to other PCs" turned on (or its choices changed).</summary>
    public void Enable(string homeEmail, string password, bool includeSettings, bool includeAi, bool autoAdd)
    {
        if (password.Length < AccountsCopy.MinPasswordLength) throw new ArgumentException($"Use at least {AccountsCopy.MinPasswordLength} characters.");
        var o = Options;
        o.Enabled = true;
        o.HomeEmail = AccountsCopy.Key(homeEmail);
        o.IncludeSettings = includeSettings;
        o.IncludeAiKeys = includeAi;
        o.AutoAdd = autoAdd;
        _e.Vault.Set(AccountsCopy.VaultPassword, password);
        AccountsCopy.StartHere(_e.Config, _e.Vault, DateTimeOffset.Now);
        NeedsPassword = false;
        _e.Settings.Save(notify: false);
        Poke();
    }

    /// <summary>The sync password typed again (after another PC changed it), or changed here: the next write uses it.</summary>
    public void SetPassword(string password)
    {
        _e.Vault.Set(AccountsCopy.VaultPassword, password);
        NeedsPassword = false;
        Poke();
    }

    /// <summary>The home account is being removed (or changed): the copy moves to another account. The old mailbox keeps a
    /// last note "moved to …", so the other PCs follow; the new home gets the full copy at the next round.</summary>
    public async Task MoveHomeAsync(string newHomeEmail, CancellationToken ct)
    {
        var old = Home;
        var password = _e.Vault.Get(AccountsCopy.VaultPassword);
        if (old != null && !string.IsNullOrEmpty(password))
        {
            AccountsCopy.Contents? note = null;
            var now = DateTimeOffset.Now;
            await RunOnUi(() => note = AccountsCopy.Build(_e.Config, _e.Vault, null, PcName, AppVersion, now));
            note!.MovedTo = AccountsCopy.Key(newHomeEmail);
            note.Accounts.Clear();   // only the note: the accounts go into the new home's copy
            var locked = await Task.Run(() => AccountsCopy.Lock(note, password), ct);
            using var client = await _e.Connector.OpenImapAsync(old, ct);
            await CopyMailbox.WriteAsync(client, old.Email, locked, ct);
            try { await client.DisconnectAsync(true, ct); } catch { }
        }
        await RunOnUi(() => { Options.HomeEmail = AccountsCopy.Key(newHomeEmail); _e.Settings.Save(notify: false); });
        Poke();
    }

    /// <summary>"Stop and delete the copy" (or only stop on this PC when <paramref name="deleteCopy"/> is false).</summary>
    public async Task DisableAsync(bool deleteCopy, CancellationToken ct)
    {
        var home = Home;
        if (deleteCopy && home != null)
        {
            using var client = await _e.Connector.OpenImapAsync(home, ct);
            await CopyMailbox.DeleteAsync(client, ct);
            try { await client.DisconnectAsync(true, ct); } catch { }
        }
        await RunOnUi(() =>
        {
            var o = Options;
            o.Enabled = false;
            o.CopyUpdated = null;
            o.CopyAccounts = 0;
            o.CopySize = 0;
            o.UsedBy = new();
            _e.Vault.Set(AccountsCopy.VaultPassword, null);
            _e.Settings.Save(notify: false);
        });
        Problem = "";
        StatusChanged?.Invoke();
    }

    /// <summary>The new-PC step (design SY1·3): after signing in to an account, is there a copy in its mailbox?
    /// Null when there is none. The account doesn't have to be added yet.</summary>
    public async Task<byte[]?> FindAsync(Account account, string? password, CancellationToken ct)
    {
        using var client = await _e.Connector.OpenImapAsync(account, ct, password);
        try { return await CopyMailbox.ReadAsync(client, ct); }
        finally { try { await client.DisconnectAsync(true, ct); } catch { } }
    }

    /// <summary>
    /// The new-PC step (design SY1·3/4): the home account was just added here; adds the ticked accounts from the copy in
    /// one step, takes the copy's settings, and keeps the copy in step from now on. The home account keeps this PC's own
    /// sign-in. Returns the accounts added.
    /// </summary>
    public List<Account> AddFromCopy(AccountsCopy.Contents copy, Account home, string password, IEnumerable<string> emails)
    {
        var o = Options;
        var homeKey = AccountsCopy.Key(home.Email);
        var wanted = emails.Select(AccountsCopy.Key).ToHashSet();
        o.Enabled = true;
        o.HomeEmail = homeKey;
        o.IncludeSettings = copy.Settings != null;
        o.IncludeAiKeys = copy.Ai != null;
        _e.Vault.Set(AccountsCopy.VaultPassword, password);
        // Accounts not ticked stay in the copy and wait (Settings → Accounts lists them).
        foreach (var c in copy.Accounts.Where(c => c.Email != homeKey && !wanted.Contains(c.Email) && !o.NotHere.Contains(c.Email))) o.NotHere.Add(c.Email);

        // The home account: the copy's settings (signature, colour…), this PC's own sign-in.
        if (copy.Accounts.FirstOrDefault(c => c.Email == homeKey) is { } homeCopy)
        {
            var i = _e.Config.Accounts.FindIndex(a => a.Id == home.Id);
            if (i >= 0)
            {
                var merged = AccountsCopy.ForAdd(_e.Config, homeCopy);
                merged.Id = home.Id;
                _e.Config.Accounts[i] = merged;
                var local = new AccountsCopy.CopiedAccount { Account = merged, SignedIn = homeCopy.SignedIn, Hash = homeCopy.Hash, Changed = homeCopy.Changed };
                AccountsCopy.Remember(_e.Config, _e.Vault, merged, local);
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
        var added = _e.AddAccounts(toAdd);
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
        var added = _e.AddAccounts(toAdd);
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
        Options.Stamps[key] = new CopyStamp { Hash = Options.Stamps.TryGetValue(key, out var st) ? st.Hash : "", Changed = DateTimeOffset.Now };
        _e.Settings.Save(notify: false);
        Poke();
    }
}
