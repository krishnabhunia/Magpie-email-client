using System.Net;
using System.Text;
using System.Text.Json;
using Magpie.Core.Auth;
using Magpie.Core.Calendar;
using Magpie.Core.Models;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>3.0.0: the calendar (design B2) — Google Calendar sync, events kept on this PC, reminders.</summary>
public class Release300Tests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ───────────── reading Google's events ─────────────

    [Fact]
    public void A_timed_invite_reads_with_its_people_meet_link_and_reminder()
    {
        var e = GoogleCalendarClient.Parse(Json("""
            { "id":"ev1", "status":"confirmed", "summary":"Vendor review", "location":"Room 4",
              "start":{"dateTime":"2026-10-05T10:30:00+05:30"}, "end":{"dateTime":"2026-10-05T11:15:00+05:30"},
              "organizer":{"email":"anita@vendorco.in","displayName":"Anita Rao"},
              "attendees":[{"email":"anita@vendorco.in","organizer":true,"responseStatus":"accepted"},
                           {"email":"me@gmail.com","self":true,"responseStatus":"needsAction"}],
              "conferenceData":{"entryPoints":[{"entryPointType":"video","uri":"https://meet.google.com/abc-defg-hij"}]},
              "reminders":{"useDefault":false,"overrides":[{"method":"popup","minutes":30},{"method":"email","minutes":60}]} }
            """), "A", "me@gmail.com", "me@gmail.com")!;
        Assert.Equal("Vendor review", e.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 5, 0, 0, TimeSpan.Zero), e.Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromMinutes(45), e.End - e.Start);
        Assert.False(e.AllDay);
        Assert.False(e.IAmOrganizer);
        Assert.Equal("Anita Rao", e.Organizer);
        Assert.Equal(EventAnswer.NeedsAction, e.MyAnswer);
        Assert.True(e.IsUnansweredInvite);   // drawn dashed
        Assert.Equal("https://meet.google.com/abc-defg-hij", e.MeetLink);
        Assert.Equal(30, e.ReminderMinutes);
    }

    [Fact]
    public void An_all_day_event_covers_whole_days_on_this_pc()
    {
        var e = GoogleCalendarClient.Parse(Json("""
            { "id":"h1", "summary":"Gandhi Jayanti", "start":{"date":"2026-10-02"}, "end":{"date":"2026-10-03"} }
            """), "A", GoogleCalendarClient.IndiaHolidays, "me@gmail.com")!;
        Assert.True(e.AllDay);
        Assert.Equal(new DateTime(2026, 10, 2), e.Start.LocalDateTime);
        Assert.Equal(new DateTime(2026, 10, 3), e.End.LocalDateTime);
        Assert.True(e.IAmOrganizer);          // no organizer: nothing to answer
        Assert.Equal(10, e.ReminderMinutes);  // the calendar's default
    }

    [Fact]
    public void A_new_event_is_sent_with_its_time_zone_repeat_meet_and_people()
    {
        var e = new CalendarEvent
        {
            Title = "Stand-up", Start = new DateTimeOffset(2026, 10, 6, 9, 30, 0, TimeSpan.FromHours(5.5)), End = new DateTimeOffset(2026, 10, 6, 9, 45, 0, TimeSpan.FromHours(5.5)),
            Recurrence = GoogleCalendarClient.RepeatRule("weekly"), AddMeet = true, ReminderMinutes = 5,
            Attendees = { new EventAttendee { Email = "rahul@x.in" } },
        };
        var j = GoogleCalendarClient.ToJson(e, "Asia/Kolkata");
        Assert.Equal("2026-10-06T09:30:00+05:30", (string?)j["start"]!["dateTime"]);
        Assert.Equal("Asia/Kolkata", (string?)j["start"]!["timeZone"]);
        Assert.Equal("RRULE:FREQ=WEEKLY", (string?)j["recurrence"]![0]);
        Assert.Equal("hangoutsMeet", (string?)j["conferenceData"]!["createRequest"]!["conferenceSolutionKey"]!["type"]);
        Assert.Equal("rahul@x.in", (string?)j["attendees"]![0]!["email"]);
        Assert.Equal(5, (int)j["reminders"]!["overrides"]![0]!["minutes"]!);

        var allDay = GoogleCalendarClient.ToJson(new CalendarEvent { Title = "Leave", AllDay = true, Start = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.FromHours(5.5)), End = new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.FromHours(5.5)) }, "Asia/Kolkata");
        Assert.Equal("2026-10-09", (string?)allDay["start"]!["date"]);
        Assert.Equal("2026-10-10", (string?)allDay["end"]!["date"]);   // at least one whole day
        Assert.Null(allDay["recurrence"]);

        Assert.Equal("monthly", GoogleCalendarClient.RepeatChoice("RRULE:FREQ=MONTHLY;BYMONTHDAY=6"));
        Assert.Equal("", GoogleCalendarClient.RepeatRule("none"));
    }

    [Fact]
    public void Googles_errors_say_what_to_do()
    {
        var off = Assert.IsType<CalendarProblem>(GoogleCalendarClient.Problem(HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"Google Calendar API has not been used","errors":[{"reason":"accessNotConfigured"}]}}"""));
        Assert.False(off.NeedsSignIn);
        Assert.Contains("Calendar API is off", off.Message);
        var scope = Assert.IsType<CalendarProblem>(GoogleCalendarClient.Problem(HttpStatusCode.Forbidden,
            """{"error":{"code":403,"message":"Request had insufficient authentication scopes.","errors":[{"reason":"insufficientPermissions"}]}}"""));
        Assert.True(scope.NeedsSignIn);
        Assert.IsType<HttpRequestException>(GoogleCalendarClient.Problem(HttpStatusCode.InternalServerError, "oops"));
    }

    // ───────────── kept on this PC ─────────────

    private static CalendarEvent Ev(string id, DateTimeOffset start, int minutes = 60, string title = "E") =>
        new() { EventId = id, Title = title, Start = start, End = start.AddMinutes(minutes) };

    [Fact]
    public void Google_is_mirrored_and_changes_made_here_wait_for_it()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.SaveCalendars("A", new[] { new CalendarInfo { Id = "me@gmail.com", Name = "Me", Primary = true, CanEdit = true } });
        var now = DateTimeOffset.Now;
        var from = now.AddDays(-60); var to = now.AddDays(400);
        store.ReplaceEvents("A", "me@gmail.com", from, to, new[] { Ev("e1", now.AddDays(1), title: "One"), Ev("e2", now.AddDays(2), title: "Two") });
        Assert.Equal(new[] { "One", "Two" }, store.EventsBetween(now, now.AddDays(3)).Select(x => x.Title));

        var two = store.EventsBetween(now, now.AddDays(3)).Single(x => x.EventId == "e2");
        two.Title = "Two (moved here)";
        two.Pending = PendingEventOp.Update;
        store.SaveLocalEvent(two);

        // Google still has the old title and no longer has e1.
        store.ReplaceEvents("A", "me@gmail.com", from, to, new[] { Ev("e2", now.AddDays(2), title: "Two") });
        var left = store.EventsBetween(now, now.AddDays(3));
        Assert.Equal("Two (moved here)", Assert.Single(left).Title);   // our change waits for Google
        Assert.Single(store.PendingEvents("A"));

        store.SetCalendarSelected("A", "me@gmail.com", false);
        Assert.Empty(store.EventsBetween(now, now.AddDays(3)));        // unticked calendar: hidden
    }

    [Fact]
    public void Calendars_keep_their_tick_and_gone_ones_leave_with_their_events()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.SaveCalendars("A", new[] { new CalendarInfo { Id = "a" }, new CalendarInfo { Id = "b" } });
        store.SetCalendarSelected("A", "a", false);
        store.ReplaceEvents("A", "b", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(9), new[] { Ev("x", DateTimeOffset.Now.AddDays(1)) });
        store.SaveCalendars("A", new[] { new CalendarInfo { Id = "a", Name = "renamed" } });
        var cals = store.GetCalendars("A");
        Assert.False(Assert.Single(cals).Selected);
        Assert.Equal("renamed", cals[0].Name);
        Assert.Empty(store.PendingEvents("A"));
    }

    [Fact]
    public void A_reminder_comes_once_ten_minutes_before()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        store.SaveCalendars("A", new[] { new CalendarInfo { Id = "c" } });
        var now = DateTimeOffset.Now;
        var soon = Ev("s", now.AddMinutes(8), title: "Soon");
        var later = Ev("l", now.AddMinutes(40), title: "Later");
        var declined = Ev("d", now.AddMinutes(5), title: "Declined");
        declined.MyAnswer = EventAnswer.Declined;
        store.ReplaceEvents("A", "c", now.AddDays(-1), now.AddDays(1), new[] { soon, later, declined });
        Assert.Equal("Soon", Assert.Single(store.TakeDueEventReminders(now)).Title);
        Assert.Empty(store.TakeDueEventReminders(now.AddMinutes(1)));                     // not twice
        Assert.Equal("Later", Assert.Single(store.TakeDueEventReminders(now.AddMinutes(31))).Title);
    }

    // ───────────── syncing with Google ─────────────

    private sealed class FakeGoogle : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public string? Posted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            Calls.Add(req.Method + " " + path);
            string body;
            if (path.EndsWith("/users/me/calendarList"))
                body = """{"items":[{"id":"me@gmail.com","summary":"me@gmail.com","primary":true,"accessRole":"owner","backgroundColor":"#9fc6e7"}]}""";
            else if (req.Method == HttpMethod.Post)
            {
                Posted = await req.Content!.ReadAsStringAsync(ct);
                body = """{"id":"new1","summary":"Lunch","start":{"dateTime":"2026-10-07T13:00:00+05:30"},"end":{"dateTime":"2026-10-07T14:00:00+05:30"},"hangoutLink":"https://meet.google.com/xyz"}""";
            }
            else if (path.Contains("holiday"))
                body = """{"items":[{"id":"h1","summary":"Diwali","start":{"date":"2026-11-08"},"end":{"date":"2026-11-09"}}]}""";
            else
                body = """{"items":[{"id":"e1","summary":"Review","start":{"dateTime":"2026-10-05T10:30:00+05:30"},"end":{"dateTime":"2026-10-05T11:00:00+05:30"}}""" +
                       (Posted == null ? "" : """,{"id":"new1","summary":"Lunch","start":{"dateTime":"2026-10-07T13:00:00+05:30"},"end":{"dateTime":"2026-10-07T14:00:00+05:30"},"hangoutLink":"https://meet.google.com/xyz"}""") + "]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Sync_downloads_calendars_and_holidays_and_sends_new_events()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var google = new FakeGoogle();
        var acc = new Account { Email = "me@gmail.com", Kind = AccountKind.Gmail, Auth = AuthMethod.OAuth2 };
        var before = CalendarService.Before;
        CalendarService.Before = TimeSpan.FromDays(3650);   // the fixed dates above are inside the window
        try
        {
            var svc = new CalendarService(store, new HttpClient(google), () => new[] { acc }, (_, _) => Task.FromResult("token"),
                _ => $"https://mail.google.com/ {OAuthService.GoogleCalendarRead} {OAuthService.GoogleCalendarEvents}");
            await svc.SyncNowAsync(CancellationToken.None);
            Assert.Empty(svc.Problems);
            var cals = svc.Calendars();
            Assert.Contains(cals, c => c.Primary && c.CanEdit && c.Color == "#9FC6E7");
            Assert.Contains(cals, c => c.Id == GoogleCalendarClient.IndiaHolidays && c.Name == "Holidays in India" && !c.CanEdit);
            var all = svc.Between(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
            Assert.Contains(all, e => e.Title == "Review");
            Assert.Contains(all, e => e.Title == "Diwali" && e.AllDay);

            // Made here: shown at once, sent at the next sync, then carries Google's id and Meet link.
            var lunch = svc.Save(new CalendarEvent { AccountId = acc.Id, CalendarId = "me@gmail.com", Title = "Lunch", AddMeet = true,
                Start = new DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.FromHours(5.5)), End = new DateTimeOffset(2026, 10, 7, 14, 0, 0, TimeSpan.FromHours(5.5)) });
            Assert.Equal(PendingEventOp.Create, store.GetEvent(lunch.Id)!.Pending);
            await svc.SyncNowAsync(CancellationToken.None);
            var sent = store.GetEvent(lunch.Id)!;
            Assert.Equal(PendingEventOp.None, sent.Pending);
            Assert.Equal("new1", sent.EventId);
            Assert.Equal("https://meet.google.com/xyz", sent.MeetLink);
            Assert.Contains("hangoutsMeet", google.Posted);
        }
        finally { CalendarService.Before = before; }
    }

    [Fact]
    public async Task An_account_signed_in_before_the_calendar_is_asked_to_sign_in_again()
    {
        using var dir = new TempDir();
        var (store, _, _) = Rows.NewStore(dir);
        var google = new FakeGoogle();
        var acc = new Account { Email = "me@gmail.com", Kind = AccountKind.Gmail, Auth = AuthMethod.OAuth2 };
        var svc = new CalendarService(store, new HttpClient(google), () => new[] { acc }, (_, _) => Task.FromResult("token"), _ => "https://mail.google.com/ openid email profile");
        await svc.SyncNowAsync(CancellationToken.None);
        Assert.True(svc.Problems[acc.Id].NeedsSignIn);
        Assert.Empty(google.Calls);   // nothing asked of Google without the permission
    }

    [Fact]
    public void Google_sign_in_asks_for_the_calendar_and_contacts()
    {
        var cfg = OAuthService.Google("id", "secret");
        Assert.True(OAuthService.Allows(cfg.Scopes, OAuthService.GoogleCalendarRead));
        Assert.True(OAuthService.Allows(cfg.Scopes, OAuthService.GoogleCalendarEvents));
        Assert.True(OAuthService.Allows(cfg.Scopes, OAuthService.GoogleContacts));
        Assert.True(OAuthService.Allows(cfg.Scopes, "https://mail.google.com/"));
    }
}
