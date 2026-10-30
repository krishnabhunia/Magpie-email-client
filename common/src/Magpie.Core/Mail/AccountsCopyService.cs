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
    /// <summary>The last round couldn't take another PC's changes because Settings was open: they come in when it closes.</summary>
    public bool HeldBack { get; private set; }

    private AccountsCopySettings Options => _e.Config.AccountsCopy;
    public string PcName => Options.PcName.Length > 0 ? Options.PcName : Environment.MachineName;
    public Account? Home => _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == Options.HomeEmail);

    public void Start(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _e.Settings.Saving += SoonAfterChange;
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _e.Settings.Saving -= SoonAfterChange;
        _cts?.Cancel();
        _debounce?.Dispose();
    }

    /// <summary>Check now (Settings → "Update the copy now", or after a change).</summary>
    public void Poke() => _wake.Release();

    // Every save (also the quiet ones, like blocking a sender) notes what changed at once, in the same write: its time is
    // when it was made, also offline or if Magpie stops before the next round. Several changes in a row make one write.
    private void SoonAfterChange(AppSettings s)
    {
        // Rules written back by a window that loaded them earlier name accounts as they were then: put them right.
        AccountsCopy.ResolveRules(s);
        if (!s.AccountsCopy.Enabled) return;
        if (!AccountsCopy.Track(s, _e.Vault, DateTimeOffset.Now)) return;
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
            // After the round (which may follow a "moved" note first).
            try { await CleanUpAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Warn("accounts copy: clean-up: " + AccountSync.Describe(ex)); }
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, _e.Config.SyncIntervalMinutes)), ct);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(0, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Clean-ups a stop or move left undone (also while the copy is off): older copies in a mailbox, or a spare
    /// copy a move that didn't finish left. Each is tried on its own (one failing never holds up the others) and goes
    /// once done. A mailbox whose account isn't in Magpie here any more can't be reached: its entry goes (logged).</summary>
    private async Task CleanUpAsync(CancellationToken ct)
    {
        var o = Options;
        if (o.CleanUp.Count == 0 && o.Spares.Count == 0) return;
        await _busy.WaitAsync(ct);
        try
        {
            foreach (var email in o.CleanUp.ToList())
                try { await CleanOneAsync(email, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn($"accounts copy: clean-up in {email} (tried again later): {ex.Message}"); }
            foreach (var spare in o.Spares.ToList())
                try { await SettleSpareAsync(spare, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn($"accounts copy: spare copy in {spare.At} (tried again later): {ex.Message}"); }
        }
        finally { _busy.Release(); }
    }

    private async Task CleanOneAsync(string email, CancellationToken ct)
    {
        if (_e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == email) is { } acc)
        {
            using var client = await _e.Connector.OpenImapAsync(acc, ct);
            await CopyMailbox.KeepOnlyNewestAsync(client, ct);
            try { await client.DisconnectAsync(true, ct); } catch { }
            Log.Info("accounts copy: older copies cleared in " + email);
        }
        else Log.Warn($"accounts copy: older copies may be left in {email} (folder \"{AccountsCopy.FolderName}\"); that account isn't in Magpie any more");
        await RunOnUi(() => { Options.CleanUp.Remove(email); _e.Settings.Save(notify: false); });
    }

    /// <summary>A spare goes only when nothing can be using it: it is still the newest email in its mailbox, that mailbox
    /// isn't the home, and (unless known unused) the old home is read and holds no "moved" note pointing at it. The entry
    /// goes once that is settled either way; while it can't be told (no password, a mailbox not reachable), it stays.
    /// False when it is still unsettled.</summary>
    private async Task<bool> SettleSpareAsync(SpareCopy spare, CancellationToken ct)
    {
        var o = Options;
        var at = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == spare.At);
        var old = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == spare.Old);
        bool? inUse = (o.Enabled && spare.At == o.HomeEmail) ? true : at == null ? null : spare.Unused ? false : old == null ? null : await PointsAtAsync(old, spare.At, ct);
        if (inUse == null)
        {
            // Tried again later while both accounts are here and the sync password can still be typed (the copy is on).
            if (at != null && old != null && (KnowsPassword || o.Enabled)) return false;
            Log.Warn($"accounts copy: a spare copy may be left in {spare.At} (folder \"{AccountsCopy.FolderName}\"); Magpie can't check it any more");
            await RunOnUi(() => { o.Spares.Remove(spare); _e.Settings.Save(notify: false); });
            return false;   // unsettled: removing an account asks first
        }
        else if (inUse == false)
        {
            using var client = await _e.Connector.OpenImapAsync(at!, ct);
            if (await CopyMailbox.ReadAsync(client, ct) is { } file && AccountsCopy.Peek(file)?.Updated == spare.Updated)
            {
                // What the mailbox held before (an old "moved" note) comes back in its place; else the folder goes.
                if (spare.Restore.Length > 0) await CopyMailbox.WriteAsync(client, at!.Email, Convert.FromBase64String(spare.Restore), ct);
                else await CopyMailbox.DeleteAsync(client, ct);
            }
            else
            {
                // No longer the newest (a take-back put the old note back but couldn't clear it, another spare, or another
                // PC wrote there since): only that one email goes. Whatever is on top now may be in use, so it is never
                // written over or deleted here.
                if (await CopyMailbox.RemoveCopyAsync(client, spare.Updated, ct) > 0) Log.Info("accounts copy: spare copy removed from " + spare.At);
                // A full copy there now that isn't another spare of this PC: some PC uses it, maybe apart from the one in the
                // home. Never written over; the user is told.
                if (await CopyMailbox.ReadAsync(client, ct) is { } top && AccountsCopy.Peek(top) is { IsNote: false } t
                    && !o.Spares.Any(x => x != spare && x.At == spare.At && x.Updated == t.Updated))
                {
                    Log.Warn($"accounts copy: {spare.At} holds a copy written after a move that didn't finish; left as it is");
                    var where = spare.At;
                    await RunOnUi(() =>
                    {
                        o.TwoCopies = $"There may be two copies of your accounts now: a move to {where} didn't finish, and Magpie on another PC has been using the copy there since. Choose the same home account on every PC (Settings → Accounts) so they come together again.";
                        _e.Settings.Save(notify: false);
                    });
                    StatusChanged?.Invoke();
                }
            }
            try { await client.DisconnectAsync(true, ct); } catch { }
        }
        else Log.Info($"accounts copy: the copy in {spare.At} is in use; it stays");
        await RunOnUi(() => { o.Spares.Remove(spare); _e.Settings.Save(notify: false); });
        return true;
    }

    /// <summary>Does this mailbox's newest email point at <paramref name="target"/> ("moved" there)? Null when that can't
    /// be told (a "moved" note this PC can't open).</summary>
    private async Task<bool?> PointsAtAsync(Account mailbox, string target, CancellationToken ct)
    {
        byte[]? newest;
        using (var client = await _e.Connector.OpenImapAsync(mailbox, ct))
        {
            newest = await CopyMailbox.ReadAsync(client, ct);
            try { await client.DisconnectAsync(true, ct); } catch { }
        }
        if (newest == null || AccountsCopy.Peek(newest) is not { IsNote: true, IsStopped: false }) return false;
        try { return AccountsCopy.Key(OpenWithKnownPassword(newest).MovedTo) == target; }
        catch (AccountsCopy.WrongPasswordException) { return null; }
    }

    /// <summary>Before an account is removed: clears the older copies still waiting in its mailbox (a stop or move whose
    /// clean-up didn't finish) and settles spare copies that depend on it. False when that can't be done now (copies
    /// would stay there for good).</summary>
    public async Task<bool> CleanUpNowAsync(string email, CancellationToken ct)
    {
        var key = AccountsCopy.Key(email);
        var o = Options;
        await _busy.WaitAsync(ct);
        try
        {
            // Read on the UI thread, where these lists change.
            var needed = false;
            await RunOnUi(() => needed = o.CleanUp.Contains(key) || o.Spares.Any(x => x.At == key || x.Old == key));
            if (!needed) return true;
            var done = true;
            if (o.CleanUp.Contains(key))
                try { await CleanOneAsync(key, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn("accounts copy: clean-up in " + key + ": " + ex.Message); done = false; }
            foreach (var spare in o.Spares.Where(x => x.At == key || x.Old == key).ToList())
                try { done &= await SettleSpareAsync(spare, ct); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn("accounts copy: spare copy in " + spare.At + ": " + ex.Message); done = false; }
            return done;
        }
        finally { _busy.Release(); }
    }

    /// <summary>Takes back the copy a move just wrote to the new home (only while it is still the newest there): what was
    /// there before comes back, or the folder goes when it held nothing.</summary>
    private async Task TakeBackAsync(Account to, AccountsCopy.Contents built, byte[]? before)
    {
        using var undo = await _e.Connector.OpenImapAsync(to, CancellationToken.None);
        if (await CopyMailbox.ReadAsync(undo, CancellationToken.None) is { } there && AccountsCopy.Peek(there)?.Updated == built.Updated)
        {
            if (before != null) await CopyMailbox.WriteAsync(undo, to.Email, before, CancellationToken.None);
            else await CopyMailbox.DeleteAsync(undo, CancellationToken.None);
        }
        try { await undo.DisconnectAsync(true, CancellationToken.None); } catch { }
    }

    private Task NoteSpareAsync(Account at, AccountsCopy.Contents copy, Account? old, bool unused, byte[]? before) => RunOnUi(() =>
    {
        // A small note held there before is kept to put back (a full copy there before was a spare itself: it goes).
        var key = AccountsCopy.Key(at.Email);
        var restore = before != null && before.Length <= 64 * 1024 && AccountsCopy.Peek(before) is { IsNote: true } ? Convert.ToBase64String(before) : "";
        // A spare noted there earlier keeps the note that mailbox held first (the one before was that spare itself).
        if (restore.Length == 0) restore = Options.Spares.LastOrDefault(x => x.At == key && x.Restore.Length > 0)?.Restore ?? "";
        Options.Spares.Add(new SpareCopy { At = key, Updated = copy.Updated, Old = old != null ? AccountsCopy.Key(old.Email) : "", Unused = unused, Restore = restore });
        _e.Settings.Save(notify: false);
    });

    /// <summary>A clean-up that didn't finish now is tried again at every check (<see cref="CleanUpAsync"/>).</summary>
    private Task CleanUpLaterAsync(string email) => RunOnUi(() =>
    {
        var key = AccountsCopy.Key(email);
        if (!Options.CleanUp.Contains(key)) Options.CleanUp.Add(key);
        _e.Settings.Save(notify: false);
    });

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

            using var client = await _e.Connector.OpenImapAsync(home, ct);
            var file = await CopyMailbox.ReadAsync(client, ct);
            var readTime = file != null ? AccountsCopy.Peek(file)?.Updated : null;   // what this round read (checked again before writing)
            AccountsCopy.Contents? copy = null;
            // The "stopped" note found when the copy was turned on here again is written over, never followed (exactly that
            // one: any other note is another PC's).
            if (file != null && IsNoteToOverwrite(file)) file = null;
            // Stopped (another PC, or this one whose stop didn't finish): followed without the password, the note says so.
            if (file != null && AccountsCopy.Peek(file) is { IsStopped: true } stoppedNote)
            {
                await StopHereAsync(stoppedNote.Pc, stoppedNote.Updated, home, client, ct);
                return;
            }
            if (string.IsNullOrEmpty(password)) { AskForPassword("Type the sync password to keep the copy up to date."); return; }
            if (file != null)
            {
                try
                {
                    copy = OpenWithKnownPassword(file);
                    (password, newPassword) = (_e.Vault.Get(AccountsCopy.VaultPassword)!, _e.Vault.Get(AccountsCopy.VaultNewPassword));
                }
                catch (AccountsCopy.WrongPasswordException)
                {
                    // Another PC chose a new sync password: ask for it once; nothing is written meanwhile.
                    if (!string.IsNullOrEmpty(newPassword))
                    {
                        ClearPendingIf(newPassword);
                        AskForPassword("Another PC changed the sync password meanwhile. Type that one on this PC; you can change it again afterwards.");
                    }
                    else AskForPassword("The copy has a new sync password. Type it once on this PC.");
                    return;
                }
            }
            NeedsPassword = false;

            if (copy is { Stopped: true })
            {
                await StopHereAsync(copy.FromPc, copy.Updated, home, client, ct);
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
            HeldBack = copy != null && !await TakeAsync(copy);

            var now = DateTimeOffset.Now;
            // A PC that just turned the copy on (or joined one) writes once at once, so the newest copy there is never one
            // another PC could take for unused (a spare) while this PC uses it.
            var write = newPassword != null || Options.CopyUpdated == null;
            await RunOnUi(() => write |= AccountsCopy.NeedsWrite(_e.Config, _e.Vault, copy, PcName, now));
            if (write)
            {
                AccountsCopy.Contents? built = null;
                await RunOnUi(() => built = AccountsCopy.Build(_e.Config, _e.Vault, copy, PcName, AppVersion, now));
                var lockWith = newPassword ?? password;
                var locked = await Task.Run(() => AccountsCopy.Lock(built!, lockWith), ct);
                // The new password is the sync password as soon as the copy locked with it is in the mailbox (even if
                // removing the old copies fails after that).
                try
                {
                    await CopyMailbox.WriteAsync(client, home.Email, locked, ct, appended: () => { if (newPassword != null) Promote(newPassword); },
                        stillSame: f => (f != null ? AccountsCopy.Peek(f)?.Updated : null) == readTime);
                }
                catch (CopyMailbox.ChangedMeanwhileException)
                {
                    // Another PC wrote (or left a note) while this round worked: look again before writing anything.
                    Log.Info("accounts copy: changed on another PC meanwhile; looking again");
                    Poke();
                    return;
                }
                if (newPassword != null) Log.Info("accounts copy: locked with the new sync password");
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
                    AccountsCopy.Settle(_e.Config, copy);
                }
                if (file != null) o.CopySize = file.Length;
                if (write) o.OverwriteNote = null;   // written over now
                _e.Settings.Save(notify: false);
            });
            Problem = "";
            StatusChanged?.Invoke();
        }
        finally { _busy.Release(); }
    }

    /// <summary>The copy opened with the sync password kept here, or with the new one chosen here (an earlier round
    /// already locked it with that one: it becomes the sync password).</summary>
    private AccountsCopy.Contents OpenWithKnownPassword(byte[] file)
    {
        var password = _e.Vault.Get(AccountsCopy.VaultPassword);
        var newPassword = _e.Vault.Get(AccountsCopy.VaultNewPassword);
        if (!string.IsNullOrEmpty(password))
            try { return AccountsCopy.Unlock(file, password); }
            catch (AccountsCopy.WrongPasswordException) when (!string.IsNullOrEmpty(newPassword)) { }
        if (string.IsNullOrEmpty(newPassword)) throw new AccountsCopy.WrongPasswordException();
        var copy = AccountsCopy.Unlock(file, newPassword);
        Promote(newPassword);
        return copy;
    }

    /// <summary>The new password chosen here locks the copy now: it is the sync password. A newer choice made meanwhile
    /// stays waiting for the next write.</summary>
    private void Promote(string newPassword)
    {
        _e.Vault.Set(AccountsCopy.VaultPassword, newPassword);
        ClearPendingIf(newPassword);
    }

    private void ClearPendingIf(string tried)
    {
        if (_e.Vault.Get(AccountsCopy.VaultNewPassword) == tried) _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
    }

    /// <summary>Exactly the "stopped" note found when the copy was turned on here again (by its time, as written in it).</summary>
    private bool IsNoteToOverwrite(byte[] file) =>
        Options.OverwriteNote is { } at && AccountsCopy.Peek(file) is { IsNote: true } p && p.Updated == at;

    private void AskForPassword(string why)
    {
        NeedsPassword = true;
        Fail(why);
        PasswordNeeded?.Invoke();
    }

    /// <summary>Takes the copy's newer changes; false when they wait (Settings is open: checked on the UI thread, right
    /// where they are applied, so a window opened meanwhile can't write its older values back over them).</summary>
    private async Task<bool> TakeAsync(AccountsCopy.Contents copy)
    {
        AccountsCopy.Plan? plan = null;
        var changed = new List<Account>();
        var added = new List<Account>();
        var paused = false;
        await RunOnUi(() =>
        {
            if (Paused()) { paused = true; return; }
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
        if (paused) return false;
        if (plan == null) return true;
        if (added.Count > 0) { Log.Info($"accounts copy: added {added.Count} account(s) from {copy.FromPc}"); AccountsAdded?.Invoke(added); }
        Available = plan.Available;
        if (plan.Available.Count > 0) AccountsAvailable?.Invoke(plan.Available);
        foreach (var (a, r) in plan.RemovedElsewhere) RemovedElsewhere?.Invoke(a, r);
        return true;
    }

    private async Task StopHereAsync(string byPc, DateTimeOffset noteTime, Account home, MailKit.Net.Imap.ImapClient client, CancellationToken ct)
    {
        var mine = Options.MyStopNote == noteTime;
        // This PC's own stop whose clean-up didn't finish: the older copies (with the sign-ins) go now, or at a later check.
        if (mine)
            try { await CopyMailbox.KeepOnlyNewestAsync(client, ct, newestIsNote: true); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn("accounts copy: clean-up after stopping: " + ex.Message); await CleanUpLaterAsync(home.Email); }
        await RunOnUi(() => TurnOff());
        Log.Info("accounts copy: stopped on " + byPc);
        Problem = "";
        StatusChanged?.Invoke();
        if (!mine) StoppedElsewhere?.Invoke(byPc);
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
        o.Adopt = new();
        o.OverwriteNote = null;
        Available = Array.Empty<AccountsCopy.CopiedAccount>();
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
        DateTimeOffset? overwrite = null;
        if (file != null && AccountsCopy.Peek(file) is { IsNote: true } note)
        {
            AccountsCopy.Contents? opened = null;
            try { opened = AccountsCopy.Unlock(file, password); } catch (AccountsCopy.WrongPasswordException) { }
            if (opened == null && !note.IsStopped)
                throw new InvalidOperationException(note.IsMoved
                    ? $"The copy moved from {home.Email} to another of your accounts. Type the sync password your other PCs use to see where."
                    : $"{home.Email} has a note from Magpie on another PC, locked with another password. Type that password.");
            // "Moved": the copy lives in another account now. Written over only when the copy there was stopped or is gone
            // (the note is left over); otherwise that account is the one to choose.
            if (opened is { MovedTo.Length: > 0 } && AccountsCopy.Key(opened.MovedTo) != AccountsCopy.Key(home.Email))
            {
                var moved = AccountsCopy.Key(opened.MovedTo);
                var target = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == moved)
                             ?? throw new InvalidOperationException($"The copy moved from {home.Email} to {moved}. Add {moved} here, then choose it as the home account.");
                var there = await FindAsync(target, null, ct);
                if (there == null)
                    throw new InvalidOperationException($"The copy moved from {home.Email} to {moved}, and it is missing there. Choose {moved} as the home account to make it again there (your other PCs use that one).");
                if (AccountsCopy.Peek(there) is not { IsStopped: true })
                    throw new InvalidOperationException($"The copy moved from {home.Email} to {moved}. Choose {moved} as the home account to join it.");
            }
            overwrite = note.Updated;   // stopped (or a left-over pointer): starting again writes over it
        }
        else if (file != null)
        {
            try { existing = AccountsCopy.Unlock(file, password); }
            catch (AccountsCopy.WrongPasswordException)
            {
                throw new InvalidOperationException($"{home.Email} already has a copy made on another PC, locked with another password. Type that password to join it.");
            }
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
                o.OverwriteNote = overwrite;
                Available = Array.Empty<AccountsCopy.CopiedAccount>();
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

    /// <summary>The home account is being removed (or changed): the copy moves to another account. The new home gets the
    /// full copy first; only then the old mailbox keeps a short note "moved to …" (nothing else in it), so the other PCs
    /// follow. When anything fails, nothing has moved: the copy stays where it was (the error says why).</summary>
    public async Task MoveHomeAsync(string newHomeEmail, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var to = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == AccountsCopy.Key(newHomeEmail))
                     ?? throw new ArgumentException("Choose an account that is in Magpie on this PC.");
            if (!KnowsPassword) throw new InvalidOperationException("Type the sync password first (Settings → Accounts), then try again.");
            var old = Home;
            AccountsCopy.Contents? current = null;
            for (var hop = 0; old != null; hop++)
            {
                byte[]? file;
                using (var client = await _e.Connector.OpenImapAsync(old, ct))
                {
                    file = await CopyMailbox.ReadAsync(client, ct);
                    try { await client.DisconnectAsync(true, ct); } catch { }
                }
                if (file != null && IsNoteToOverwrite(file)) { file = null; break; }
                if (file == null)
                {
                    // Gone from the mailbox: moving now would make a copy from this PC alone (other PCs' accounts lost).
                    if (Options.CopyUpdated != null)
                    {
                        Missing = true;
                        StatusChanged?.Invoke();
                        throw new InvalidOperationException("The copy is missing from the home mailbox. Use \"Make a new copy from this PC\" (Settings → Accounts) first, then try again.");
                    }
                    break;
                }
                if (AccountsCopy.Peek(file) is { IsStopped: true } p) { Poke(); throw new InvalidOperationException(Stopped(p.Pc)); }
                current = OpenOrRefuse(file, "move it");
                if (current.Stopped) { Poke(); throw new InvalidOperationException(Stopped(current.FromPc)); }
                if (current.MovedTo.Length == 0) break;
                // Another PC moved it already: this PC follows (and moves it on from there if another account was chosen).
                var target = AccountsCopy.Key(current.MovedTo);
                var next = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == target)
                           ?? throw new InvalidOperationException($"The copy moved to {target} on another PC. Add {target} here to keep in step, or stop the copy on this PC only.");
                if (hop >= 3) throw new InvalidOperationException("The copy moved too many times in a row. Try again in a minute.");
                await RunOnUi(() => { Options.HomeEmail = target; _e.Settings.Save(notify: false); });
                Log.Info("accounts copy: followed the copy to " + target);
                current = null;
                if (target == AccountsCopy.Key(to.Email)) return;   // it is there already
                old = next;
            }
            var password = _e.Vault.Get(AccountsCopy.VaultPassword) ?? "";
            var newPassword = _e.Vault.Get(AccountsCopy.VaultNewPassword);
            var lockWith = string.IsNullOrEmpty(newPassword) ? password : newPassword;
            // 1. The new home starts from the full copy that was in the old one (other PCs' accounts and removals stay).
            AccountsCopy.Contents? built = null;
            var now = DateTimeOffset.Now;
            await RunOnUi(() => built = AccountsCopy.Build(_e.Config, _e.Vault, current, PcName, AppVersion, now));
            var locked = await Task.Run(() => AccountsCopy.Lock(built!, lockWith), ct);
            // What the new home held before (an old "moved" note other PCs may still follow) comes back if the move is taken back.
            byte[]? before = null;
            var writing = false;
            try
            {
                using var client = await _e.Connector.OpenImapAsync(to, ct);
                before = await CopyMailbox.ReadAsync(client, ct);
                writing = true;
                await CopyMailbox.WriteAsync(client, to.Email, locked, ct);
                try { await client.DisconnectAsync(true, ct); } catch { }
            }
            catch (Exception ex)
            {
                // It may be in the new home anyway (its answer lost, or a later step failed): taken back, since no note
                // points there yet; if that fails too, it goes at a later check. Nothing was sent before the write began.
                if (writing)
                    try { await TakeBackAsync(to, built!, before); }
                    catch (Exception undoEx)
                    {
                        Log.Warn("accounts copy: couldn't check the new home after a failed write: " + undoEx.Message);
                        await NoteSpareAsync(to, built!, old, unused: true, before);
                    }
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                throw new InvalidOperationException(Connector.Friendly(ex) + $" The copy couldn't be written to {to.Email}; nothing has moved.", ex);
            }
            // 2. Only now the old mailbox points there. If the note never got there, the copy just written goes again
            // (nothing moved); once it is there, the move is done even if clearing the old copies failed.
            if (old != null && AccountsCopy.Key(old.Email) != AccountsCopy.Key(to.Email))
            {
                var noteIn = false;
                var note = new AccountsCopy.Contents { Updated = DateTimeOffset.Now, FromPc = PcName, AppVersion = AppVersion, MovedTo = AccountsCopy.Key(to.Email) };
                try
                {
                    var noteFile = await Task.Run(() => AccountsCopy.Lock(note, lockWith), ct);
                    using var client = await _e.Connector.OpenImapAsync(old, ct);
                    await CopyMailbox.WriteAsync(client, old.Email, noteFile, ct, appended: () => noteIn = true);
                    await CopyMailbox.KeepOnlyNewestAsync(client, ct, newestIsNote: true);   // also leftovers of earlier rounds (it may be removed next)
                    try { await client.DisconnectAsync(true, ct); } catch { }
                }
                catch (Exception ex) when (!noteIn)
                {
                    // The server may have kept the note although its answer was lost: look before taking anything back.
                    bool? arrived = null;
                    try
                    {
                        using var check = await _e.Connector.OpenImapAsync(old, CancellationToken.None);
                        arrived = await CopyMailbox.ReadAsync(check, CancellationToken.None) is { } newest && AccountsCopy.Peek(newest) is { IsMoved: true } p && p.Updated == note.Updated;
                        // Moved after all: the older copies there go now (that mailbox may be removed from Magpie next).
                        if (arrived == true)
                            try { await CopyMailbox.KeepOnlyNewestAsync(check, CancellationToken.None, newestIsNote: true); }
                            catch (Exception cleanEx) { Log.Warn("accounts copy: moved; clearing the old copies failed: " + cleanEx.Message); await CleanUpLaterAsync(old.Email); }
                        try { await check.DisconnectAsync(true, CancellationToken.None); } catch { }
                    }
                    catch (Exception checkEx) { Log.Warn("accounts copy: couldn't check the old mailbox: " + checkEx.Message); }
                    if (arrived == true) Log.Info("accounts copy: the moved note arrived (its answer was lost); moved");   // done: on below
                    else if (arrived == null)
                    {
                        // Can't tell: both copies stay (nothing is deleted on a guess). The one written to the new home goes
                        // later only once the old home is seen not to point at it.
                        await NoteSpareAsync(to, built!, old, unused: false, before);
                        throw new InvalidOperationException(Connector.Friendly(ex) + $" Magpie couldn't check {old.Email} afterwards, so the copy stays there; the one just written to {to.Email} goes at a later check if nothing uses it (keep {old.Email} in Magpie until then).", ex);
                    }
                    else
                    {
                        try { await TakeBackAsync(to, built!, before); }
                        catch (Exception undoEx)
                        {
                            Log.Warn("accounts copy: couldn't take back the copy written to " + to.Email + ": " + undoEx.Message);
                            await NoteSpareAsync(to, built!, old, unused: true, before);   // the note isn't there: nothing points at it
                            throw new InvalidOperationException(Connector.Friendly(ex) + $" A spare copy is left in {to.Email}'s mailbox for now; Magpie removes it at a later check.", ex);
                        }
                        throw;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"accounts copy: moved; clearing the old copies in {old.Email} failed: {ex.Message}");
                    await CleanUpLaterAsync(old.Email);
                }
            }
            if (!string.IsNullOrEmpty(newPassword)) Promote(newPassword);
            await RunOnUi(() =>
            {
                var o = Options;
                o.HomeEmail = AccountsCopy.Key(to.Email);
                o.CopyUpdated = built!.Updated;
                o.CopyAccounts = built.Accounts.Count;
                o.CopySize = locked.Length;
                o.OverwriteNote = null;
                AccountsCopy.Settle(_e.Config, built);
                _e.Settings.Save(notify: false);
            });
        }
        finally { _busy.Release(); }
        Poke();
    }

    /// <summary>"Stop and delete the copy": the accounts and sign-ins leave the mailbox, replaced by a short "stopped" note
    /// (nothing else in it) so the other PCs stop too instead of writing it back; their accounts stay. The note is locked
    /// with the password that opens the copy there now (another PC may have changed it: then it must be typed first).
    /// When another PC already stopped or moved the copy, nothing is written here. With <paramref name="deleteCopy"/>
    /// false it only stops on this PC.</summary>
    public async Task DisableAsync(bool deleteCopy, CancellationToken ct)
    {
        await _busy.WaitAsync(ct);
        try
        {
            var source = Home;
            for (var hop = 0; deleteCopy && source != null; hop++)
            {
                using var client = await _e.Connector.OpenImapAsync(source, ct);
                var file = await CopyMailbox.ReadAsync(client, ct);
                if (file != null && IsNoteToOverwrite(file)) file = null;   // only the old note this PC was going to write over
                var peek = file != null ? AccountsCopy.Peek(file) : null;
                AccountsCopy.Contents? current = null;
                if (peek is { IsStopped: true }) { }   // stopped already (by another PC, or this one's stop didn't finish)
                else if (file != null && !KnowsPassword)
                {
                    if (peek is { IsMoved: true })
                        throw new InvalidOperationException("The copy moved to another of your accounts on another PC. Type the sync password to stop it there, or stop the copy on this PC only.");
                    if (peek is not { IsNote: true })
                    {
                        AskForPassword("Type the sync password to keep the copy up to date.");
                        throw new InvalidOperationException("Type the sync password first (Settings → Accounts), then stop the copy; until then it stays as it is.");
                    }
                }
                else if (file != null) current = OpenOrRefuse(file, "stop it");
                // Moved: the copy (with the sign-ins) is in another mailbox; it is stopped there.
                if (current is { MovedTo.Length: > 0 } && !current.Stopped)
                {
                    var target = AccountsCopy.Key(current.MovedTo);
                    var next = _e.Accounts.FirstOrDefault(a => AccountsCopy.Key(a.Email) == target)
                               ?? throw new InvalidOperationException($"The copy moved to {target} on another PC. Add {target} here to stop it there, or stop the copy on this PC only.");
                    if (hop >= 3) throw new InvalidOperationException("The copy moved too many times in a row. Try again in a minute.");
                    source = next;
                    continue;
                }
                if (peek is { IsStopped: true } || current is { Stopped: true })
                {
                    // Nothing to write; older copies a stop left behind go now.
                    try { await CopyMailbox.KeepOnlyNewestAsync(client, ct, newestIsNote: true); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Warn("accounts copy: clean-up after stopping: " + ex.Message); await CleanUpLaterAsync(source.Email); }
                }
                else if (KnowsPassword)
                {
                    var password = _e.Vault.Get(AccountsCopy.VaultPassword);
                    var lockWith = string.IsNullOrEmpty(password) ? _e.Vault.Get(AccountsCopy.VaultNewPassword)! : password;
                    var stopped = new AccountsCopy.Contents { Updated = DateTimeOffset.Now, FromPc = PcName, AppVersion = AppVersion, Stopped = true };
                    var locked = await Task.Run(() => AccountsCopy.Lock(stopped, lockWith), ct);
                    // The clean-up is noted first: if the note's answer is lost, the older copies still go later (harmless
                    // if the note never got there: the newest email always stays).
                    await RunOnUi(() => { Options.MyStopNote = stopped.Updated; _e.Settings.Save(notify: false); });
                    await CleanUpLaterAsync(source.Email);
                    // Once the note is in, the stop is done; clearing the older copies is tried again later if it fails now.
                    var noteIn = false;
                    try
                    {
                        await CopyMailbox.WriteAsync(client, source.Email, locked, ct, appended: () => noteIn = true);
                        await CopyMailbox.KeepOnlyNewestAsync(client, ct, newestIsNote: true);   // also leftovers of earlier rounds
                        await RunOnUi(() => { Options.CleanUp.Remove(AccountsCopy.Key(source.Email)); _e.Settings.Save(notify: false); });
                    }
                    catch (Exception ex) when (noteIn && !ct.IsCancellationRequested)
                    {
                        Log.Warn("accounts copy: stopped; clearing the older copies failed (tried again later): " + ex.Message);
                    }
                    catch (Exception) when (!ct.IsCancellationRequested)
                    {
                        // The note's answer may have been lost: look. There → stopped; not there → nothing to clear later.
                        bool? arrived = null;
                        try
                        {
                            using var check = await _e.Connector.OpenImapAsync(source, CancellationToken.None);
                            arrived = await CopyMailbox.ReadAsync(check, CancellationToken.None) is { } newest && AccountsCopy.Peek(newest) is { IsStopped: true } p && p.Updated == stopped.Updated;
                            if (arrived == true)
                            {
                                await CopyMailbox.KeepOnlyNewestAsync(check, CancellationToken.None, newestIsNote: true);
                                await RunOnUi(() => { Options.CleanUp.Remove(AccountsCopy.Key(source.Email)); _e.Settings.Save(notify: false); });
                            }
                            try { await check.DisconnectAsync(true, CancellationToken.None); } catch { }
                        }
                        catch (Exception checkEx) { Log.Warn("accounts copy: couldn't check the mailbox after stopping: " + checkEx.Message); }
                        if (arrived == false) await RunOnUi(() => { Options.CleanUp.Remove(AccountsCopy.Key(source.Email)); _e.Settings.Save(notify: false); });
                        if (arrived != true) throw;
                        Log.Info("accounts copy: the stopped note arrived (its answer was lost); stopped");
                    }
                }
                else if (file == null) await CopyMailbox.DeleteAsync(client, ct);
                try { await client.DisconnectAsync(true, ct); } catch { }
                break;
            }
            await RunOnUi(TurnOff);
        }
        finally { _busy.Release(); }
        Problem = "";
        Missing = false;
        NeedsPassword = false;
        HeldBack = false;
        StatusChanged?.Invoke();
    }

    private bool KnowsPassword =>
        !string.IsNullOrEmpty(_e.Vault.Get(AccountsCopy.VaultPassword)) || !string.IsNullOrEmpty(_e.Vault.Get(AccountsCopy.VaultNewPassword));

    /// <summary>The copy opened with the password kept here; when another PC changed it, asks for it and refuses
    /// (writing now would lock the other PCs out or lose their accounts).</summary>
    private AccountsCopy.Contents OpenOrRefuse(byte[] file, string what)
    {
        try { return OpenWithKnownPassword(file); }
        catch (AccountsCopy.WrongPasswordException)
        {
            AskForPassword("The copy has a new sync password. Type it once on this PC.");
            throw new InvalidOperationException($"Another PC changed the sync password. Type the new one first (Settings → Accounts), then {what}; until then the copy stays as it is.");
        }
    }

    private static string Stopped(string byPc) =>
        $"Magpie on {(byPc.Length > 0 ? byPc : "another PC")} stopped the copy, so there is nothing to move; this PC stops too at its next check.";

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
        o.OverwriteNote = null;
        o.CopyUpdated = null;
        _e.Vault.Set(AccountsCopy.VaultPassword, password);
        _e.Vault.Set(AccountsCopy.VaultNewPassword, null);
        // Accounts this PC already has keep its own version until either side changes them (as when joining in Settings).
        AccountsCopy.Join(_e.Config, _e.Vault, copy, DateTimeOffset.Now);
        o.ChoicesChanged = copy.ChoicesChanged;
        // Accounts not ticked stay in the copy and wait (Settings → Accounts lists them).
        foreach (var c in copy.Accounts.Where(c => c.Email != homeKey && !wanted.Contains(c.Email) && !o.NotHere.Contains(c.Email))) o.NotHere.Add(c.Email);

        // How this PC signs in to the home account stays as it was just set up here.
        if (copy.Accounts.FirstOrDefault(c => c.Email == homeKey) is { } homeCopy && _e.AccountById(home.Id) is { } local)
            _e.RestartAccount(AccountsCopy.MergeKeepingSignIn(_e.Config, _e.Vault, homeCopy, local));
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

    /// <summary>An account added by hand that waits in the copy (another PC has it, maybe changed after it was removed
    /// here): it takes the copy's version instead of overwriting it, keeping the sign-in just made here.</summary>
    internal Account AdoptWaiting(Account added)
    {
        if (!Options.Enabled) return added;
        var key = AccountsCopy.Key(added.Email);
        if (Available.FirstOrDefault(x => x.Email == key) is not { } c)
        {
            // Not known yet (no round since start, offline, Settings open): settled at the next look at the copy.
            if (!Options.Adopt.Contains(key)) Options.Adopt.Add(key);
            return added;
        }
        Available = Available.Where(x => x.Email != key).ToList();
        return AccountsCopy.MergeKeepingSignIn(_e.Config, _e.Vault, c, added);
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

    /// <summary>"Remove it here too" (true) or "Keep it" (false) after another PC removed the account. Before "Remove it
    /// here too" the caller runs <see cref="CleanUpNowAsync"/> (and asks when that can't be done).</summary>
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
