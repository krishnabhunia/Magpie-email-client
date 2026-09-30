using System.Collections.Concurrent;
using System.Net;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.Core.Calendar;

/// <summary>
/// Design B2: keeps the Google calendars of every Google account on this PC (every 5 minutes, and at once after a change
/// made in Magpie), and makes changes local-first: saved here straight away, sent to Google when it can be reached.
/// </summary>
public sealed class CalendarService
{
    private readonly MailStore _store;
    private readonly HttpClient _http;
    private readonly Func<IReadOnlyList<Account>> _accounts;
    private readonly Func<Account, CancellationToken, Task<string>> _token;
    private readonly Func<string, string> _grantedScopes;
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CalendarProblem> _problems = new();

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>The window kept on this PC: two months back, thirteen months ahead.</summary>
    public static TimeSpan Before { get; set; } = TimeSpan.FromDays(60);
    public static TimeSpan After { get; set; } = TimeSpan.FromDays(400);

    /// <summary>Calendars or events changed (after a sync or a change made here).</summary>
    public event Action? Changed;
    public DateTimeOffset? LastSync { get; private set; }

    public CalendarService(MailStore store, HttpClient http, Func<IReadOnlyList<Account>> accounts,
        Func<Account, CancellationToken, Task<string>> token, Func<string, string> grantedScopes)
    {
        _store = store;
        _http = http;
        _accounts = accounts;
        _token = token;
        _grantedScopes = grantedScopes;
    }

    /// <summary>Google accounts (signed in with Google): the only ones with a calendar here.</summary>
    public IReadOnlyList<Account> GoogleAccounts => _accounts().Where(a => a.Enabled && a.Kind == AccountKind.Gmail && a.Auth == AuthMethod.OAuth2).ToList();

    /// <summary>Why an account's calendar can't be shown or updated right now (account id → problem).</summary>
    public IReadOnlyDictionary<string, CalendarProblem> Problems => _problems;

    public void Start(CancellationToken ct) => _ = Task.Run(() => LoopAsync(ct));

    /// <summary>Check Google now (after a change, a new sign-in, or a calendar ticked on).</summary>
    public void Poke() => _wake.Release();

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await SyncNowAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error("calendar sync", ex); }
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
            var first = true;
            foreach (var a in GoogleAccounts)
            {
                try
                {
                    await SyncAccountAsync(a, holidays: first, ct);
                    _problems.TryRemove(a.Id, out _);
                }
                catch (CalendarProblem p) { _problems[a.Id] = p; Log.Warn($"[{a.Email}] calendar: {p.Message}"); }
                catch (ReauthRequiredException) { _problems[a.Id] = new CalendarProblem("Sign in to this Google account again to show its calendar here.", needsSignIn: true); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warn($"[{a.Email}] calendar sync failed (kept what is on this PC): {ex.Message}"); }
                first = false;
            }
            foreach (var gone in _problems.Keys.Where(id => GoogleAccounts.All(a => a.Id != id)).ToList()) _problems.TryRemove(gone, out _);
            LastSync = DateTimeOffset.Now;
        }
        finally { _syncGate.Release(); }
        Changed?.Invoke();
    }

    private async Task SyncAccountAsync(Account a, bool holidays, CancellationToken ct)
    {
        await _token(a, ct);   // fails with ReauthRequired when the sign-in is gone
        var granted = _grantedScopes(a.Id);
        if (granted.Length > 0 && !OAuthService.Allows(granted, OAuthService.GoogleCalendarRead))
            throw new CalendarProblem("Sign in to this Google account again to show its calendar here (Magpie now asks for the calendar too).", needsSignIn: true);
        var client = new GoogleCalendarClient(_http, c => _token(a, c));
        await PushPendingAsync(a, client, ct);

        var calendars = await client.ListCalendarsAsync(a.Id, ct);
        if (holidays && calendars.All(c => c.Id != GoogleCalendarClient.IndiaHolidays))
            calendars.Add(new CalendarInfo { AccountId = a.Id, Id = GoogleCalendarClient.IndiaHolidays, Name = "Holidays in India", Color = "#B45309" });
        _store.SaveCalendars(a.Id, calendars);

        var from = DateTimeOffset.Now.Date - Before;
        var to = DateTimeOffset.Now.Date + After;
        foreach (var cal in _store.GetCalendars(a.Id).Where(c => c.Selected))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var events = await client.ListEventsAsync(a.Id, cal.Id, a.Email, from, to, ct);
                _store.ReplaceEvents(a.Id, cal.Id, from, to, events);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                Log.Warn($"[{a.Email}] calendar \"{cal.Name}\" can't be read: {ex.Message}");
            }
        }
    }

    /// <summary>Sends what was changed on this PC, oldest first. Stops (keeping the rest) when Google can't be reached.</summary>
    private async Task PushPendingAsync(Account a, GoogleCalendarClient client, CancellationToken ct)
    {
        foreach (var e in _store.PendingEvents(a.Id))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                CalendarEvent? saved = null;
                switch (e.Pending)
                {
                    case PendingEventOp.Create: saved = await client.InsertAsync(e, a.Email, ct); break;
                    case PendingEventOp.Update: saved = await client.UpdateAsync(e, a.Email, ct); break;
                    case PendingEventOp.Respond: saved = await client.RespondAsync(e, a.Email, ct); break;
                    case PendingEventOp.Delete:
                        if (e.EventId.Length > 0) await client.DeleteAsync(e, ct);
                        _store.DeleteEventRow(e.Id);
                        continue;
                }
                if (saved == null) continue;
                saved.Id = e.Id;
                saved.Pending = PendingEventOp.None;
                _store.SaveLocalEvent(saved);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                Log.Warn($"[{a.Email}] event \"{e.Title}\" is gone from Google; dropped here too");
                _store.DeleteEventRow(e.Id);
            }
        }
    }

    // ───────────────────────── changes made in Magpie (local-first) ─────────────────────────

    public List<CalendarInfo> Calendars() => _store.GetCalendars();

    public List<CalendarEvent> Between(DateTimeOffset from, DateTimeOffset to) => _store.EventsBetween(from, to);

    public void SetSelected(CalendarInfo cal, bool selected)
    {
        _store.SetCalendarSelected(cal.AccountId, cal.Id, selected);
        Changed?.Invoke();
        if (selected) Poke();   // download its events
    }

    /// <summary>Adds or changes an event: saved on this PC at once, sent to Google in the background.</summary>
    public CalendarEvent Save(CalendarEvent e)
    {
        e.Pending = e.Id == 0 || e.EventId.Length == 0 ? PendingEventOp.Create : PendingEventOp.Update;
        e.Updated = DateTimeOffset.Now;
        _store.SaveLocalEvent(e);
        Changed?.Invoke();
        Poke();
        return e;
    }

    public void Delete(CalendarEvent e)
    {
        if (e.EventId.Length == 0) _store.DeleteEventRow(e.Id);
        else
        {
            e.Pending = PendingEventOp.Delete;
            _store.SaveLocalEvent(e);
        }
        Changed?.Invoke();
        Poke();
    }

    /// <summary>Accept / Maybe / Decline an invite in the calendar; the organiser is told when Google gets it.</summary>
    public void Respond(CalendarEvent e, EventAnswer answer)
    {
        e.MyAnswer = answer;
        foreach (var at in e.Attendees.Where(x => x.Self)) at.Answer = answer;
        if (e.Pending is not (PendingEventOp.Create or PendingEventOp.Update)) e.Pending = PendingEventOp.Respond;
        _store.SaveLocalEvent(e);
        Changed?.Invoke();
        Poke();
    }
}
