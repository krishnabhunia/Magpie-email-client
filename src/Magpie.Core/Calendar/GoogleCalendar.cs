using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpie.Core.Models;

namespace Magpie.Core.Calendar;

/// <summary>Something the calendar can't do right now, in plain words (shown on the calendar page).</summary>
public sealed class CalendarProblem : Exception
{
    /// <summary>The account must sign in again (to allow the calendar) — the page offers "Sign in again".</summary>
    public bool NeedsSignIn { get; }
    public CalendarProblem(string message, bool needsSignIn = false) : base(message) => NeedsSignIn = needsSignIn;
}

/// <summary>
/// Google Calendar API v3 (design B2): the account's calendars, the events of a time window (repeating events come as
/// their single occurrences), and adding / changing / deleting / answering events. Needs the Calendar API turned on in
/// the Google Cloud project Magpie signs in with, and a sign-in that allows the calendar.
/// </summary>
public sealed class GoogleCalendarClient
{
    public const string Api = "https://www.googleapis.com/calendar/v3/";
    /// <summary>Google's public calendar of Indian holidays (design B2: "Holidays in India").</summary>
    public const string IndiaHolidays = "en.indian#holiday@group.v.calendar.google.com";

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<string>> _token;

    public GoogleCalendarClient(HttpClient http, Func<CancellationToken, Task<string>> token)
    {
        _http = http;
        _token = token;
    }

    public async Task<List<CalendarInfo>> ListCalendarsAsync(string accountId, CancellationToken ct)
    {
        var list = new List<CalendarInfo>();
        string? page = null;
        do
        {
            var root = await SendAsync(HttpMethod.Get, "users/me/calendarList?maxResults=250" + (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, ct);
            foreach (var item in Items(root))
            {
                var role = Str(item, "accessRole");
                list.Add(new CalendarInfo
                {
                    AccountId = accountId,
                    Id = Str(item, "id"),
                    Name = Str(item, "summaryOverride") is { Length: > 0 } o ? o : Str(item, "summary"),
                    Color = Str(item, "backgroundColor") is { Length: 7 } c ? c.ToUpperInvariant() : "#14606E",
                    Primary = item.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True,
                    CanEdit = role is "owner" or "writer",
                    Selected = !item.TryGetProperty("selected", out var s) || s.ValueKind != JsonValueKind.False,
                });
            }
            page = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        } while (page != null);
        return list;
    }

    /// <summary>Events overlapping [from, to), repeating ones as their occurrences.</summary>
    public async Task<List<CalendarEvent>> ListEventsAsync(string accountId, string calendarId, string myEmail, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var list = new List<CalendarEvent>();
        string? page = null;
        do
        {
            var path = $"calendars/{Uri.EscapeDataString(calendarId)}/events?singleEvents=true&orderBy=startTime&maxResults=2500" +
                       "&timeMin=" + Uri.EscapeDataString(from.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture)) +
                       "&timeMax=" + Uri.EscapeDataString(to.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture)) +
                       (page == null ? "" : "&pageToken=" + Uri.EscapeDataString(page));
            var root = await SendAsync(HttpMethod.Get, path, null, ct);
            foreach (var item in Items(root))
                if (Parse(item, accountId, calendarId, myEmail) is { } e) list.Add(e);
            page = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        } while (page != null);
        return list;
    }

    /// <summary>Adds an event; returns it as Google saved it (with its id and Meet link).</summary>
    public async Task<CalendarEvent?> InsertAsync(CalendarEvent e, string myEmail, CancellationToken ct)
    {
        var root = await SendAsync(HttpMethod.Post, $"calendars/{Uri.EscapeDataString(e.CalendarId)}/events?conferenceDataVersion=1&sendUpdates=all", ToJson(e, TimeZoneId()), ct);
        return Parse(root, e.AccountId, e.CalendarId, myEmail);
    }

    public async Task<CalendarEvent?> UpdateAsync(CalendarEvent e, string myEmail, CancellationToken ct)
    {
        var root = await SendAsync(HttpMethod.Patch, $"calendars/{Uri.EscapeDataString(e.CalendarId)}/events/{Uri.EscapeDataString(e.EventId)}?conferenceDataVersion=1&sendUpdates=all",
            ToJson(e, TimeZoneId()), ct);
        return Parse(root, e.AccountId, e.CalendarId, myEmail);
    }

    /// <summary>Accept / Maybe / Decline an invite: the account's own attendee entry changes; the organiser is told.</summary>
    public async Task<CalendarEvent?> RespondAsync(CalendarEvent e, string myEmail, CancellationToken ct)
    {
        var attendees = new JsonArray();
        foreach (var a in e.Attendees)
            attendees.Add(new JsonObject
            {
                ["email"] = a.Email,
                ["responseStatus"] = AnswerText(a.Self || a.Email.Equals(myEmail, StringComparison.OrdinalIgnoreCase) ? e.MyAnswer : a.Answer),
            });
        var body = new JsonObject { ["attendees"] = attendees };
        var root = await SendAsync(HttpMethod.Patch, $"calendars/{Uri.EscapeDataString(e.CalendarId)}/events/{Uri.EscapeDataString(e.EventId)}?sendUpdates=all", body, ct);
        return Parse(root, e.AccountId, e.CalendarId, myEmail);
    }

    public async Task DeleteAsync(CalendarEvent e, CancellationToken ct)
    {
        try { await SendAsync(HttpMethod.Delete, $"calendars/{Uri.EscapeDataString(e.CalendarId)}/events/{Uri.EscapeDataString(e.EventId)}?sendUpdates=all", null, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { }   // already gone
    }

    // ───────────────────────── JSON ─────────────────────────

    /// <summary>One event from Google's JSON; null when it has no start (a broken or placeholder entry).</summary>
    public static CalendarEvent? Parse(JsonElement item, string accountId, string calendarId, string myEmail)
    {
        if (!item.TryGetProperty("start", out var start) || !item.TryGetProperty("end", out var end)) return null;
        var e = new CalendarEvent
        {
            AccountId = accountId,
            CalendarId = calendarId,
            EventId = Str(item, "id"),
            Title = Str(item, "summary") is { Length: > 0 } t ? t : "(no title)",
            Location = Str(item, "location"),
            Description = Str(item, "description"),
            Status = Str(item, "status") is { Length: > 0 } st ? st : "confirmed",
            RecurringEventId = Str(item, "recurringEventId"),
            MeetLink = Str(item, "hangoutLink"),
            Updated = DateTimeOffset.TryParse(Str(item, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var up) ? up : DateTimeOffset.MinValue,
        };
        if (start.TryGetProperty("date", out var sd))
        {
            e.AllDay = true;
            e.Start = LocalMidnight(sd.GetString());
            e.End = end.TryGetProperty("date", out var ed) ? LocalMidnight(ed.GetString()) : e.Start.AddDays(1);
        }
        else
        {
            e.Start = DateTimeOffset.Parse(Str(start, "dateTime"), CultureInfo.InvariantCulture).ToLocalTime();
            e.End = end.TryGetProperty("dateTime", out var edt) ? DateTimeOffset.Parse(edt.GetString()!, CultureInfo.InvariantCulture).ToLocalTime() : e.Start.AddHours(1);
        }
        if (e.MeetLink.Length == 0 && item.TryGetProperty("conferenceData", out var conf) && conf.TryGetProperty("entryPoints", out var eps))
            foreach (var ep in eps.EnumerateArray())
                if (Str(ep, "entryPointType") == "video") { e.MeetLink = Str(ep, "uri"); break; }
        if (item.TryGetProperty("recurrence", out var rec) && rec.ValueKind == JsonValueKind.Array)
            e.Recurrence = rec.EnumerateArray().Select(x => x.GetString() ?? "").FirstOrDefault(x => x.StartsWith("RRULE:", StringComparison.Ordinal)) ?? "";

        var organizer = item.TryGetProperty("organizer", out var org) ? org : default;
        e.Organizer = organizer.ValueKind == JsonValueKind.Object ? (Str(organizer, "displayName") is { Length: > 0 } on ? on : Str(organizer, "email")) : "";
        var organizerIsMe = organizer.ValueKind == JsonValueKind.Object
            && ((organizer.TryGetProperty("self", out var os) && os.ValueKind == JsonValueKind.True) || Str(organizer, "email").Equals(myEmail, StringComparison.OrdinalIgnoreCase));
        if (item.TryGetProperty("attendees", out var att) && att.ValueKind == JsonValueKind.Array)
            foreach (var a in att.EnumerateArray())
            {
                var email = Str(a, "email");
                var self = (a.TryGetProperty("self", out var sf) && sf.ValueKind == JsonValueKind.True) || email.Equals(myEmail, StringComparison.OrdinalIgnoreCase);
                var at = new EventAttendee
                {
                    Email = email, Name = Str(a, "displayName"), Answer = ParseAnswer(Str(a, "responseStatus")), Self = self,
                    Organizer = a.TryGetProperty("organizer", out var ao) && ao.ValueKind == JsonValueKind.True,
                };
                e.Attendees.Add(at);
                if (self) e.MyAnswer = at.Answer;
            }
        e.IAmOrganizer = organizerIsMe || organizer.ValueKind != JsonValueKind.Object;
        if (e.IAmOrganizer && e.MyAnswer == EventAnswer.None) e.MyAnswer = EventAnswer.Accepted;

        e.ReminderMinutes = 10;   // the calendar's default (design B2: 10 minutes before)
        if (item.TryGetProperty("reminders", out var rem) && rem.TryGetProperty("useDefault", out var ud) && ud.ValueKind == JsonValueKind.False)
        {
            e.ReminderMinutes = -1;
            if (rem.TryGetProperty("overrides", out var ov) && ov.ValueKind == JsonValueKind.Array)
                foreach (var o in ov.EnumerateArray())
                    if (o.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var mins) && (e.ReminderMinutes < 0 || mins < e.ReminderMinutes)) e.ReminderMinutes = mins;
        }
        return e;
    }

    /// <summary>The JSON Google expects for adding or changing an event.</summary>
    public static JsonObject ToJson(CalendarEvent e, string timeZone)
    {
        JsonObject When(DateTimeOffset t) => e.AllDay
            ? new JsonObject { ["date"] = t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new JsonObject { ["dateTime"] = t.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture), ["timeZone"] = timeZone };
        var o = new JsonObject
        {
            ["summary"] = e.Title,
            ["location"] = e.Location,
            ["description"] = e.Description,
            ["start"] = When(e.Start),
            ["end"] = When(e.AllDay && e.End <= e.Start ? e.Start.AddDays(1) : e.End),
            ["reminders"] = e.ReminderMinutes < 0
                ? new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray() }
                : new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = e.ReminderMinutes }) },
        };
        if (e.IAmOrganizer)
            o["attendees"] = new JsonArray(e.Attendees.Select(a => (JsonNode)new JsonObject { ["email"] = a.Email }).ToArray());
        if (e.Recurrence.Length > 0 && e.RecurringEventId.Length == 0) o["recurrence"] = new JsonArray(e.Recurrence);
        if (e.AddMeet && e.MeetLink.Length == 0)
            o["conferenceData"] = new JsonObject
            {
                ["createRequest"] = new JsonObject
                {
                    ["requestId"] = Guid.NewGuid().ToString("N"),
                    ["conferenceSolutionKey"] = new JsonObject { ["type"] = "hangoutsMeet" },
                },
            };
        return o;
    }

    /// <summary>The repeat choices of the event window (design B2): none / daily / weekly / monthly.</summary>
    public static string RepeatRule(string choice) => choice switch
    {
        "daily" => "RRULE:FREQ=DAILY",
        "weekly" => "RRULE:FREQ=WEEKLY",
        "monthly" => "RRULE:FREQ=MONTHLY",
        _ => "",
    };

    public static string RepeatChoice(string rule) => rule.ToUpperInvariant() switch
    {
        var r when r.Contains("FREQ=DAILY") => "daily",
        var r when r.Contains("FREQ=WEEKLY") => "weekly",
        var r when r.Contains("FREQ=MONTHLY") => "monthly",
        _ => "",
    };

    public static EventAnswer ParseAnswer(string s) => s switch
    {
        "accepted" => EventAnswer.Accepted,
        "tentative" => EventAnswer.Tentative,
        "declined" => EventAnswer.Declined,
        "needsAction" => EventAnswer.NeedsAction,
        _ => EventAnswer.None,
    };

    public static string AnswerText(EventAnswer a) => a switch
    {
        EventAnswer.Accepted => "accepted",
        EventAnswer.Tentative => "tentative",
        EventAnswer.Declined => "declined",
        _ => "needsAction",
    };

    /// <summary>This PC's time zone as an IANA id (Google wants those; Windows has its own names).</summary>
    public static string TimeZoneId()
    {
        var tz = TimeZoneInfo.Local;
        if (tz.HasIanaId) return tz.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : "UTC";
    }

    private static DateTimeOffset LocalMidnight(string? date)
    {
        var d = DateTime.ParseExact(date ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : Enumerable.Empty<JsonElement>();

    // ───────────────────────── HTTP ─────────────────────────

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Api + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(ct));
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode)
            return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
        throw Problem(resp.StatusCode, text);
    }

    /// <summary>Google's error in plain words: the API turned off, the sign-in not allowing the calendar, or the rest.</summary>
    internal static Exception Problem(HttpStatusCode status, string body)
    {
        var reason = "";
        var message = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.GetProperty("error");
            message = Str(err, "message");
            if (err.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0) reason = Str(errs[0], "reason");
            if (reason.Length == 0 && err.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Array)
                foreach (var d in det.EnumerateArray()) if (Str(d, "reason") is { Length: > 0 } r) { reason = r; break; }
        }
        catch (Exception) { }
        if (reason is "accessNotConfigured" or "SERVICE_DISABLED")
            return new CalendarProblem("The Google Calendar API is off in the Google Cloud project Magpie signs in with. Turn it on there (APIs & Services → Library → Google Calendar API), then wait a few minutes.");
        if (status == HttpStatusCode.Unauthorized || reason is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT" || message.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase))
            return new CalendarProblem("Sign in to this Google account again to show its calendar here.", needsSignIn: true);
        return new HttpRequestException($"Google Calendar: {(message.Length > 0 ? message : status.ToString())}", null, status);
    }
}
