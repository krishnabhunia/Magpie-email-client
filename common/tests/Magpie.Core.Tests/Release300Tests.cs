using System.Net;
using System.Text;
using System.Text.Json;
using Magpie.Core.Auth;
using Magpie.Core.Calendar;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using Magpie.Core.Mail;
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

/// <summary>3.0.0: where things sit in the calendar views (design B2).</summary>
public class Release300LayoutTests
{
    private static CalendarEvent At(int h, int m, int minutes, string title = "") =>
        new() { Title = title, Start = new DateTimeOffset(new DateTime(2026, 10, 6, h, m, 0)), End = new DateTimeOffset(new DateTime(2026, 10, 6, h, m, 0)).AddMinutes(minutes) };

    [Fact]
    public void Views_cover_the_right_days_with_weeks_from_monday()
    {
        var tue = new DateTime(2026, 10, 6);
        Assert.Equal((new DateTime(2026, 10, 5), new DateTime(2026, 10, 12)), CalendarLayout.Range(CalendarMode.Week, tue));
        Assert.Equal((tue, tue.AddDays(1)), CalendarLayout.Range(CalendarMode.Day, tue));
        var (from, to) = CalendarLayout.Range(CalendarMode.Month, tue);
        Assert.Equal(new DateTime(2026, 9, 28), from);       // the Monday before 1 October
        Assert.Equal(42, (to - from).Days);                   // six weeks, always
        Assert.Equal(new DateTime(2026, 11, 6), CalendarLayout.Step(CalendarMode.Month, tue, 1));
        Assert.Equal(new DateTime(2026, 9, 29), CalendarLayout.Step(CalendarMode.Week, tue, -1));
    }

    [Fact]
    public void Titles_read_like_a_diary()
    {
        Assert.Equal("5 – 11 October 2026", CalendarLayout.Title(CalendarMode.Week, new DateTime(2026, 10, 6)));
        Assert.Equal("28 September – 4 October 2026", CalendarLayout.Title(CalendarMode.Week, new DateTime(2026, 10, 1)));
        Assert.Equal("28 December 2026 – 3 January 2027", CalendarLayout.Title(CalendarMode.Week, new DateTime(2026, 12, 30)));
        Assert.Equal("Tuesday 6 October 2026", CalendarLayout.Title(CalendarMode.Day, new DateTime(2026, 10, 6)));
        Assert.Equal("October 2026", CalendarLayout.Title(CalendarMode.Month, new DateTime(2026, 10, 6)));
    }

    [Fact]
    public void Overlapping_events_sit_side_by_side_and_others_take_the_full_width()
    {
        var day = new DateTime(2026, 10, 6);
        var placed = CalendarLayout.PlaceTimed(new[]
        {
            At(9, 0, 60, "A"), At(9, 30, 60, "B"), At(10, 0, 30, "C"),   // A and B overlap; C starts when A ends, beside B
            At(14, 0, 30, "D"),                                          // alone
        }, day).ToDictionary(p => p.Event.Title);
        Assert.Equal((0, 2), (placed["A"].Column, placed["A"].Columns));
        Assert.Equal((1, 2), (placed["B"].Column, placed["B"].Columns));
        Assert.Equal((0, 2), (placed["C"].Column, placed["C"].Columns));   // reuses A's column
        Assert.Equal((0, 1), (placed["D"].Column, placed["D"].Columns));
        Assert.Equal(9 * 60, placed["A"].StartMinute);
        Assert.Equal(14 * 60 + 30, placed["D"].EndMinute);
    }

    [Fact]
    public void An_event_past_midnight_is_cut_to_each_day_and_all_day_events_stay_out()
    {
        var late = new CalendarEvent { Title = "Flight", Start = new DateTimeOffset(new DateTime(2026, 10, 6, 23, 0, 0)), End = new DateTimeOffset(new DateTime(2026, 10, 7, 1, 0, 0)) };
        var allDay = new CalendarEvent { Title = "Leave", AllDay = true, Start = new DateTimeOffset(new DateTime(2026, 10, 6)), End = new DateTimeOffset(new DateTime(2026, 10, 7)) };
        var tue = Assert.Single(CalendarLayout.PlaceTimed(new[] { late, allDay }, new DateTime(2026, 10, 6)));
        Assert.Equal((23 * 60.0, 24 * 60.0), (tue.StartMinute, tue.EndMinute));
        var wed = Assert.Single(CalendarLayout.PlaceTimed(new[] { late }, new DateTime(2026, 10, 7)));
        Assert.Equal((0.0, 60.0), (wed.StartMinute, wed.EndMinute));
        Assert.True(CalendarLayout.OnDay(allDay, new DateTime(2026, 10, 6)));
        Assert.False(CalendarLayout.OnDay(allDay, new DateTime(2026, 10, 7)));   // the end is exclusive
    }
}

/// <summary>Design HM1: hover cards on addresses, the subject and attachments in the reading pane.</summary>
public class Release300HoverTests
{
    private static string[] Ids(IEnumerable<HoverOption> o) => o.Select(x => x.Id).ToArray();

    [Fact]
    public void Your_own_address_offers_only_copy_write_and_search()
    {
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5" }, Ids(HoverMenus.ForAddress("Me", "me@gmail.com", isMe: true, hasCalendar: true, picturesTrusted: false)));
    }

    [Fact]
    public void Someone_elses_address_offers_everything_that_can_work_now()
    {
        var all = HoverMenus.ForAddress("Anita Rao", "anita@vendorco.in", isMe: false, hasCalendar: true, picturesTrusted: false);
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5", "E7", "E8", "E9", "E10", "E11" }, Ids(all));
        Assert.Contains(all, o => o.Label == "Emails from Anita");
        Assert.Contains(all, o => o.Label == "New event with Anita");
        Assert.Contains(all, o => o.Label == "Always show pictures from Anita");

        // No calendar → no E7; pictures already shown → no E8; Contacts page → E6.
        var some = HoverMenus.ForAddress("Anita Rao", "anita@vendorco.in", false, hasCalendar: false, picturesTrusted: true, hasContacts: true);
        Assert.Equal(new[] { "E1", "E2", "E3", "E4", "E5", "E6", "E9", "E10", "E11" }, Ids(some));
    }

    [Theory]
    [InlineData("Anita Rao", "anita@vendorco.in", "Anita")]
    [InlineData("\"Rao, Anita\"", "anita@vendorco.in", "Anita")]
    [InlineData("", "anita@vendorco.in", "anita")]
    [InlineData("anita@vendorco.in", "anita@vendorco.in", "anita")]
    public void First_names_come_from_the_name_or_the_address(string name, string address, string expected) =>
        Assert.Equal(expected, HoverMenus.FirstName(name, address));

    [Fact]
    public void Name_and_address_is_ready_to_paste()
    {
        Assert.Equal("Anita Rao <anita@vendorco.in>", HoverMenus.NameAndAddress("Anita Rao", "anita@vendorco.in"));
        Assert.Equal("\"Rao, Anita\" <anita@vendorco.in>", HoverMenus.NameAndAddress("Rao, Anita", "anita@vendorco.in"));
        Assert.Equal("anita@vendorco.in", HoverMenus.NameAndAddress("", "anita@vendorco.in"));
    }

    [Fact]
    public void Subject_card_shows_calendar_and_ai_lines_only_when_they_work()
    {
        Assert.Equal(new[] { "S1", "S2", "S3", "S4", "S5", "S6", "S8" }, Ids(HoverMenus.ForSubject(hasCalendar: false, aiSummary: false)));
        Assert.Equal(new[] { "S1", "S2", "S3", "S4", "S5", "S6", "S7", "S8", "S9" }, Ids(HoverMenus.ForSubject(true, true)));
        Assert.Equal("Vendor migration", HoverMenus.CleanSubject("Re: Fwd: Vendor migration"));
        Assert.Equal("3 emails in this conversation · Inbox", HoverMenus.SubjectLine(3, "Inbox"));
    }

    [Fact]
    public void Attachment_card_fits_the_file()
    {
        var pdf = HoverMenus.ForAttachment("Vendor-contract-v3.pdf", "Anita Rao", "anita@vendorco.in", fromMe: false, attachmentCount: 2, saved: false);
        Assert.Equal(new[] { "A1", "A5", "A2", "A3", "A4", "A6", "A8", "A9" }, Ids(pdf));
        Assert.Contains(pdf, o => o.Label == "Save all 2 attachments…" );
        Assert.Contains(pdf, o => o.Label == "Files from Anita");

        // A zip can't be previewed; one file → no Save all; saved → Show in folder; my own file → no "Files from".
        var zip = HoverMenus.ForAttachment("logs.zip", "Me", "me@gmail.com", fromMe: true, attachmentCount: 1, saved: true);
        Assert.Equal(new[] { "A1", "A2", "A4", "A6", "A7", "A9" }, Ids(zip));
    }

    [Fact]
    public void Card_lines_read_plainly()
    {
        Assert.Equal("PDF document · 2.4 MB · on this PC", HoverMenus.AttachmentLine("a.pdf", (long)(2.4 * 1024 * 1024), true));
        Assert.Equal("JPG picture · 820 KB · downloads when opened", HoverMenus.AttachmentLine("site-photo.JPG", 820 * 1024, false));
        Assert.Equal("XYZ file", HoverMenus.KindName("x.xyz"));
        Assert.Equal("PDF", HoverMenus.Badge("a.pdf"));
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(5.5));
        Assert.Equal("anita@vendorco.in · 34 emails, last on 30 Sep", HoverMenus.AddressLine("anita@vendorco.in", 34, now, now));
        Assert.Equal("anita@vendorco.in · 1 email, last on 3 Jan 2025", HoverMenus.AddressLine("anita@vendorco.in", 1, new DateTimeOffset(2025, 1, 3, 12, 0, 0, TimeSpan.FromHours(5.5)), now));
        Assert.Equal("anita@vendorco.in", HoverMenus.AddressLine("anita@vendorco.in", 0, null, now));
    }

    [Fact]
    public void Search_options_type_the_right_search()
    {
        Assert.Equal("from:anita@vendorco.in", HoverMenus.SearchFor("E4", "anita@vendorco.in"));
        Assert.Equal("with:anita@vendorco.in", HoverMenus.SearchFor("E5", "anita@vendorco.in"));
        Assert.Equal("subject:\"Vendor migration\"", HoverMenus.SearchFor("S2", "Vendor migration"));
        Assert.Equal("from:anita@vendorco.in has:attachment", HoverMenus.SearchFor("A8", "anita@vendorco.in"));
        Assert.Equal("file:\"Vendor contract v3.pdf\"", HoverMenus.SearchFor("A9", "Vendor contract v3.pdf"));

        var q = SearchQuery.Parse("with:anita file:\"Vendor contract\"");
        Assert.Equal(new[] { "anita" }, q.With);
        Assert.Equal(new[] { "Vendor contract" }, q.File);
        Assert.False(q.IsEmpty);
    }

    [Fact]
    public void With_finds_the_person_as_sender_or_in_cc_and_file_finds_attachment_names()
    {
        using var dir = new TempDir();
        var (s, inbox, sent) = Rows.NewStore(dir);
        var fromAnita = Rows.Make("A", inbox, "t1", from: "anita@vendorco.in", subject: "Contract");
        var ccAnita = Rows.Make("A", sent, "t2", from: "me@test.local", subject: "Minutes");
        ccAnita.Cc = "Anita Rao <anita@vendorco.in>";
        var other = Rows.Make("A", inbox, "t3", from: "ravi@x.in", subject: "Lunch");
        s.InsertMessages(new[] { fromAnita, ccAnita, other });
        s.SaveBody(fromAnita.Id, new MessageBody { Text = "see file", Attachments = { new AttachmentInfo { Index = 1, FileName = "Vendor-contract-v3.pdf", Size = 1000 } } });

        List<string> Find(string text) => s.ListThreads(new ListQuery { FolderIds = new[] { inbox, sent }, Search = SearchQuery.Parse(text) }, DateTimeOffset.Now)
            .Select(t => t.ThreadKey).OrderBy(k => k).ToList();

        Assert.Equal(new[] { "t1", "t2" }, Find("with:anita@vendorco.in"));
        Assert.Equal(new[] { "t1" }, Find("from:anita@vendorco.in"));
        Assert.Equal(new[] { "t1" }, Find("file:contract-v3"));
        Assert.Empty(Find("file:invoice"));
    }

    [Fact]
    public void Address_stats_count_a_copy_in_two_folders_once()
    {
        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var all = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var d1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var d2 = new DateTimeOffset(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);
        s.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", from: "anita@vendorco.in", date: d1, messageId: "m1@x"),
            Rows.Make("A", all, "t1", from: "anita@vendorco.in", date: d1, messageId: "m1@x"),
            Rows.Make("A", inbox, "t2", from: "Anita@VendorCo.in", date: d2, messageId: "m2@x"),
            Rows.Make("A", inbox, "t3", from: "ravi@x.in", date: d2, messageId: "m3@x"),
        });
        var (count, last) = s.AddressStats("anita@vendorco.in");
        Assert.Equal(2, count);
        Assert.Equal(d2, last);
        Assert.Equal((0, (DateTimeOffset?)null), s.AddressStats("nobody@x.in"));
    }

    [Fact]
    public void Reading_page_marks_people_and_files_for_the_cards()
    {
        var row = Rows.Make("A", 1, "t1", from: "anita@vendorco.in", subject: "Contract");
        row.FromName = "Anita Rao";
        row.To = "\"Me\" <me@test.local>, sandeep@x.in";
        row.Cc = "Ravi <ravi@x.in>";
        row.Id = 7;
        var body = new MessageBody { Html = "<p>hi</p>", Attachments = { new AttachmentInfo { Index = 2, FileName = "a&b.pdf", Size = 10 } } };
        var html = HtmlRenderer.BuildConversation("Contract", new[] { new RenderMessage { Row = row, Body = body, Expanded = true } }, false, DateTimeOffset.Now, hoverDelayMs: 5000).Html;

        Assert.Contains("data-a=\"anita@vendorco.in\" data-n=\"Anita Rao\" data-m=\"7\"", html);
        Assert.Contains("data-a=\"sandeep@x.in\"", html);
        Assert.Contains(">Ravi</span>", html);
        Assert.Contains("class=\"att hm-f\"", html);
        Assert.Contains("data-f=\"a&amp;b.pdf\"", html);
        Assert.Contains("var HM_DELAY=1000;", html);        // clamped to the setting's range
        Assert.Contains("id=\"hm\"", html);
    }
}
