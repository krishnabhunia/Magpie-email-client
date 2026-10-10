using System.Collections.Concurrent;
using System.Net;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Contacts;

/// <summary>
/// Design B4: keeps the Google contacts of every Google account on this PC (every 30 minutes, and at once after a
/// change made in Magpie). Changes are local-first: saved here straight away, sent to Google when it can be reached.
/// </summary>
public sealed class ContactsService
{
    private readonly MailStore _store;
    private readonly HttpClient _http;
    private readonly Func<IReadOnlyList<Account>> _accounts;
    private readonly Func<Account, CancellationToken, Task<string>> _token;
    private readonly Func<string, string> _grantedScopes;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, ContactsProblem> _problems = new();
    private readonly ConcurrentDictionary<string, byte> _removed = new();
    private readonly object _accountGate = new();

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Contacts changed (after a sync or a change made here).</summary>
    public event Action? Changed;
    public DateTimeOffset? LastSync { get; private set; }

    public ContactsService(MailStore store, HttpClient http, Func<IReadOnlyList<Account>> accounts,
        Func<Account, CancellationToken, Task<string>> token, Func<string, string> grantedScopes)
    {
        _store = store;
        _http = http;
        _accounts = accounts;
        _token = token;
        _grantedScopes = grantedScopes;
    }

    /// <summary>Google accounts signed in with Google: the only ones whose contacts Magpie can read and save.</summary>
    public IReadOnlyList<Account> GoogleAccounts => _accounts().Where(a => !_removed.ContainsKey(a.Id) && a.Enabled && a.Kind == AccountKind.Gmail && a.Auth == AuthMethod.OAuth2).ToList();

    /// <summary>Why an account's contacts can't be shown or updated right now (account id → problem).</summary>
    public IReadOnlyDictionary<string, ContactsProblem> Problems => _problems;

    public void Start(CancellationToken ct) => _ = Task.Run(() => LoopAsync(ct));

    public void Poke() => _wake.Release();

    public void ForgetAccount(string accountId)
    {
        lock (_accountGate)
        {
            _removed[accountId] = 0;
            _store.DeleteSavedContactsForAccount(accountId);
            _problems.TryRemove(accountId, out _);
        }
        Changed?.Invoke();
    }

    private bool ForActiveAccount(string accountId, Action work)
    {
        lock (_accountGate)
        {
            if (_removed.ContainsKey(accountId)) return false;
            work();
            return true;
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SyncNowAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error("contacts sync", ex); }
            try
            {
                await _wake.WaitAsync(PollInterval, ct);
                while (_wake.CurrentCount > 0) await _wake.WaitAsync(0, ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public async Task SyncNowAsync(CancellationToken ct)
    {
        await _syncGate.WaitAsync(ct);
        try
        {
            foreach (var a in GoogleAccounts)
            {
                try
                {
                    await SyncAccountAsync(a, ct);
                    _problems.TryRemove(a.Id, out _);
                }
                catch (ContactsProblem p) { _problems[a.Id] = p; Log.Warn($"[{a.Email}] contacts: {p.Message}"); }
                catch (ReauthRequiredException) { _problems[a.Id] = new ContactsProblem("Sign in to this Google account again to show its contacts here.", needsSignIn: true); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn($"[{a.Email}] contacts sync failed (kept what is on this PC): {ex.Message}"); }
            }
            foreach (var gone in _problems.Keys.Where(id => GoogleAccounts.All(a => a.Id != id)).ToList()) _problems.TryRemove(gone, out _);
            LastSync = DateTimeOffset.Now;
        }
        finally { _syncGate.Release(); }
        Changed?.Invoke();
    }

    private async Task SyncAccountAsync(Account a, CancellationToken ct)
    {
        await _token(a, ct);   // fails with ReauthRequired when the sign-in is gone
        var granted = _grantedScopes(a.Id);
        if (granted.Length > 0 && !OAuthService.Allows(granted, OAuthService.GoogleContacts))
            throw new ContactsProblem("Sign in to this Google account again to show its contacts here (Magpie now asks for contacts too).", needsSignIn: true);
        var client = new GooglePeopleClient(_http, c => _token(a, c));
        await PushPendingAsync(a, client, ct);
        var people = await client.ListAsync(a.Id, ct);
        ForActiveAccount(a.Id, () => _store.ReplaceSavedContacts(a.Id, people));
    }

    /// <summary>Sends what was changed on this PC, oldest first. Stops (keeping the rest) when Google can't be reached.</summary>
    private async Task PushPendingAsync(Account a, GooglePeopleClient client, CancellationToken ct)
    {
        foreach (var c in _store.PendingContacts(a.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (_removed.ContainsKey(a.Id)) return;
            try
            {
                switch (c.Pending)
                {
                    case PendingContactOp.Create:
                    case PendingContactOp.Update when c.Resource.Length == 0:
                        var created = await client.CreateAsync(c, ct);
                        ForActiveAccount(a.Id, () => _store.CompleteContactUpload(c, created));
                        break;
                    case PendingContactOp.Update:
                        var updated = await client.UpdateAsync(c, ct);
                        ForActiveAccount(a.Id, () => _store.CompleteContactUpload(c, updated));
                        break;
                    case PendingContactOp.Delete:
                        if (c.Resource.Length > 0) await client.DeleteAsync(c, ct);
                        ForActiveAccount(a.Id, () => _store.DeleteSavedContactRow(c.Id));
                        break;
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                Log.Warn($"[{a.Email}] contact \"{c.Display}\" is gone from Google; dropped here too");
                ForActiveAccount(a.Id, () => _store.DeleteSavedContactRow(c.Id));
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.BadRequest)
            {
                // Changed on another device since (etag no longer current) or refused: Google's copy wins at the next list.
                Log.Warn($"[{a.Email}] contact \"{c.Display}\" not saved to Google: {ex.Message}");
                ForActiveAccount(a.Id, () => { c.Pending = PendingContactOp.None; _store.SaveLocalContact(c); });
            }
        }
    }

    // ───────────────────────── changes made in Magpie (local-first) ─────────────────────────

    public List<SavedContact> All() => _store.GetSavedContacts();

    /// <summary>Adds or changes a contact: saved on this PC at once, sent to Google in the background.</summary>
    public SavedContact Save(SavedContact c)
    {
        Clean(c);
        if (!ForActiveAccount(c.AccountId, () =>
        {
            var current = c.Id == 0 ? null : _store.GetSavedContact(c.Id);
            if (current != null) { c.Resource = current.Resource; c.Etag = current.Etag; c.Groups = current.Groups; }
            c.Pending = c.Resource.Length == 0 ? PendingContactOp.Create : PendingContactOp.Update;
            c.Updated = DateTimeOffset.Now;
            _store.SaveLocalContact(c);
        })) throw new InvalidOperationException("This contacts account was removed.");
        Changed?.Invoke();
        Poke();
        return c;
    }

    public void Delete(SavedContact c)
    {
        ForActiveAccount(c.AccountId, () =>
        {
            var current = _store.GetSavedContact(c.Id);
            if (current == null) return;
            if (current.Resource.Length == 0) { _store.DeleteSavedContactRow(c.Id); return; }   // never reached Google
            current.Pending = PendingContactOp.Delete;
            current.Updated = DateTimeOffset.Now;
            _store.SaveLocalContact(current);
        });
        Changed?.Invoke();
        Poke();
    }

    /// <summary>Trims the fields; the display name follows the given and family names when it isn't typed.</summary>
    public static void Clean(SavedContact c)
    {
        c.GivenName = c.GivenName.Trim();
        c.FamilyName = c.FamilyName.Trim();
        c.Name = (c.GivenName + " " + c.FamilyName).Trim();
        c.Emails = c.Emails.Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        c.Phones = c.Phones.Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToList();
        c.Company = c.Company.Trim();
        c.Title = c.Title.Trim();
        c.Notes = c.Notes.Trim();
    }

    /// <summary>Recipient suggestions for what is typed (design B4), from saved contacts and people seen in mail.</summary>
    public List<ContactSuggestion> Suggest(string typed, int limit = 8) =>
        ContactSuggest.Rank(typed, _store.GetSavedContacts(), _store.MailPeople(), limit);
}
