namespace Magpie.Core.Models;

/// <summary>A Google calendar shown in Magpie's calendar (design B2): one or more per Google account, plus holidays.</summary>
public sealed class CalendarInfo
{
    public string AccountId { get; set; } = "";
    /// <summary>Google's calendar id (the primary calendar's id is the account's address).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Its colour, "#RRGGBB" (Google's background colour for it).</summary>
    public string Color { get; set; } = "#14606E";
    /// <summary>Ticked in the calendar list: its events are shown and downloaded.</summary>
    public bool Selected { get; set; } = true;
    public bool Primary { get; set; }
    /// <summary>Events can be added and changed (owner / writer); holidays and shared read-only calendars can't.</summary>
    public bool CanEdit { get; set; }
}

public enum EventAnswer { None = 0, NeedsAction = 1, Accepted = 2, Tentative = 3, Declined = 4 }

public sealed class EventAttendee
{
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public EventAnswer Answer { get; set; }
    public bool Organizer { get; set; }
    /// <summary>This attendee is the account itself.</summary>
    public bool Self { get; set; }
}

/// <summary>A change made on this PC that hasn't reached Google yet (the calendar works offline).</summary>
public enum PendingEventOp { None = 0, Create = 1, Update = 2, Delete = 3, Respond = 4 }

public sealed class CalendarEvent
{
    /// <summary>Row id on this PC.</summary>
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    public string CalendarId { get; set; } = "";
    /// <summary>Google's event id; empty until a new event has reached Google.</summary>
    public string EventId { get; set; } = "";
    /// <summary>Persistent client-generated Google id, reused if a create response is lost.</summary>
    public string CreationId { get; set; } = "";
    public long Revision { get; set; }
    public string Title { get; set; } = "";
    public string Location { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset Start { get; set; }
    /// <summary>End; for an all-day event the (exclusive) midnight after its last day.</summary>
    public DateTimeOffset End { get; set; }
    public bool AllDay { get; set; }
    /// <summary>"confirmed", "tentative" or "cancelled".</summary>
    public string Status { get; set; } = "confirmed";
    public EventAnswer MyAnswer { get; set; }
    public bool IAmOrganizer { get; set; } = true;
    public string Organizer { get; set; } = "";
    public List<EventAttendee> Attendees { get; set; } = new();
    /// <summary>The Google Meet (or other video call) link.</summary>
    public string MeetLink { get; set; } = "";
    /// <summary>"" or the repeat rule of a new event, e.g. "RRULE:FREQ=WEEKLY".</summary>
    public string Recurrence { get; set; } = "";
    /// <summary>Set on one occurrence of a repeating event.</summary>
    public string RecurringEventId { get; set; } = "";
    /// <summary>Minutes before the start to remind; -1 = no reminder.</summary>
    public int ReminderMinutes { get; set; } = 10;
    public DateTimeOffset Updated { get; set; }
    public PendingEventOp Pending { get; set; }
    /// <summary>Ask Google for a Meet link when the event is saved.</summary>
    public bool AddMeet { get; set; }

    /// <summary>An invite from someone else that hasn't been answered (drawn with a dashed border).</summary>
    public bool IsUnansweredInvite => !IAmOrganizer && MyAnswer == EventAnswer.NeedsAction;
    public bool IsCancelled => Status == "cancelled";
    public bool IsRepeating => Recurrence.Length > 0 || RecurringEventId.Length > 0;

    public CalendarEvent Clone()
    {
        var c = (CalendarEvent)MemberwiseClone();
        c.Attendees = Attendees.Select(a => new EventAttendee { Email = a.Email, Name = a.Name, Answer = a.Answer, Organizer = a.Organizer, Self = a.Self }).ToList();
        return c;
    }
}
