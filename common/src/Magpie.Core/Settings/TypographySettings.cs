using System.Text.Json.Serialization;

namespace Magpie.Core.Settings;

/// <summary>One complete font override. The existing role's emphasis (bold/italic) is deliberately not changed.</summary>
public sealed class TextStyle
{
    public string Family { get; set; } = "Segoe UI";
    public double Size { get; set; } = 13;

    [JsonIgnore]
    public bool IsValid => TypographyCatalog.IsValidFamily(Family) && TypographyCatalog.IsValidSize(Size);

    public TextStyle Clone() => new() { Family = Family, Size = Size };
}

public sealed record TextRole(string Id, string Area, string Name, string DefaultFamily, double DefaultSize, string Sample);

/// <summary>FR1: stable setting identifiers, independent of translated labels or individual message content.</summary>
public static class TypographyCatalog
{
    public const double MinimumSize = 8;
    public const double MaximumSize = 72;

    public static IReadOnlyList<TextRole> Roles { get; } = Array.AsReadOnly(new TextRole[]
    {
        new("appTitle", "App & navigation", "App title", "IBM Plex Serif", 17, "Magpie"),
        new("toolbar", "App & navigation", "Toolbar button labels", "IBM Plex Sans", 12.5, "Archive · Reply · Reply All"),
        new("menu", "App & navigation", "Menus & command palette", "IBM Plex Sans", 13, "Move to folder"),
        new("search", "App & navigation", "Search fields", "IBM Plex Sans", 13, "Search your mail"),
        new("accountName", "App & navigation", "Account display name", "IBM Plex Sans", 13, "Primary"),
        new("accountAddress", "App & navigation", "Account email address", "IBM Plex Sans", 12, "account@example.com"),
        new("folder", "App & navigation", "Folder names", "IBM Plex Sans", 13, "Inbox · Sent · Archive"),
        new("folderCount", "App & navigation", "Folder counts", "IBM Plex Sans", 11, "3 / 24"),
        new("navHeading", "App & navigation", "Navigation section headings", "IBM Plex Sans", 11, "FOLDERS"),
        new("tags", "App & navigation", "Tags", "IBM Plex Sans", 11, "Projects"),
        new("listSender", "Inbox list", "Sender name", "IBM Plex Sans", 13, "Maya Chen"),
        new("listSubject", "Inbox list", "Email subject", "IBM Plex Sans", 13, "Agenda for Monday’s design review"),
        new("listPreview", "Inbox list", "Message preview", "IBM Plex Sans", 12, "I have attached the agenda for Monday."),
        new("listDate", "Inbox list", "Date & time", "IBM Plex Sans", 11.5, "Today · 10:42 AM"),
        new("listBadges", "Inbox list", "Unread & status badges", "IBM Plex Sans", 10.5, "Unread · Pinned"),
        new("threadSubject", "Reading pane", "Thread subject", "IBM Plex Serif", 19, "Agenda for Monday’s design review"),
        new("readSender", "Reading pane", "Sender name", "IBM Plex Sans", 13, "Maya Chen"),
        new("readAddress", "Reading pane", "Email addresses", "IBM Plex Sans", 12, "maya@example.com → me"),
        new("readDate", "Reading pane", "Message timestamp", "IBM Plex Sans", 12, "24 October 2026, 10:42 AM"),
        new("readBody", "Reading pane", "Message body", "IBM Plex Sans", 14, "Hi Krishna, I have attached the agenda for Monday’s design review."),
        new("readH1", "Reading pane", "Message heading 1", "IBM Plex Sans", 24, "Our next design review"),
        new("readH2", "Reading pane", "Message heading 2", "IBM Plex Sans", 20, "Topics to discuss"),
        new("readH3", "Reading pane", "Message heading 3", "IBM Plex Sans", 16, "Open questions"),
        new("readQuote", "Reading pane", "Quoted message text", "IBM Plex Sans", 13, "On Friday, Maya wrote…"),
        new("fileName", "Reading pane", "Attachment file name", "IBM Plex Sans", 12, "Design-review-agenda.pdf"),
        new("fileInfo", "Reading pane", "Attachment details", "IBM Plex Sans", 11, "240 KB · PDF document"),
        new("composeBody", "Writing & signatures", "Message body", "Arial", 18, "Hi Maya, thank you for sharing the agenda."),
        new("composeSubject", "Writing & signatures", "Subject field", "IBM Plex Sans", 13, "Re: Agenda for Monday’s design review"),
        new("composeAddress", "Writing & signatures", "Recipient fields", "IBM Plex Sans", 13, "Maya Chen <maya@example.com>"),
        new("composeQuote", "Writing & signatures", "Quoted reply text", "Arial", 18, "On 24 October, Maya wrote…"),
        new("signature", "Writing & signatures", "Signature text", "Arial", 18, "Thanks and Regards, Krishna"),
        new("editorToolbar", "Writing & signatures", "Editor toolbar labels", "IBM Plex Sans", 12, "Arial · 18 px · Normal"),
        new("aiDraft", "AI text", "Generated draft preview", "IBM Plex Sans", 14, "Thank you for sharing the agenda."),
        new("aiSummary", "AI text", "Thread summaries", "IBM Plex Sans", 13, "Maya shared an agenda and invited suggestions."),
        new("aiSuggested", "AI text", "Suggested reply text", "IBM Plex Sans", 12, "Thanks for sending this through."),
        new("aiStatus", "AI text", "AI prompts & status", "IBM Plex Sans", 12, "Writing your reply…"),
        new("settingsTitle", "Settings & dialogs", "Page titles", "IBM Plex Serif", 21, "Fonts & text"),
        new("settingsSection", "Settings & dialogs", "Section headings", "IBM Plex Sans", 11, "WRITING DEFAULTS"),
        new("fieldLabel", "Settings & dialogs", "Field labels", "IBM Plex Sans", 13, "Default reply tone"),
        new("fieldValue", "Settings & dialogs", "Field values", "IBM Plex Sans", 13, "Formal"),
        new("helper", "Settings & dialogs", "Help text", "IBM Plex Sans", 12.5, "Choose a font and size for this text."),
        new("dialogTitle", "Settings & dialogs", "Dialog titles", "IBM Plex Serif", 19, "Use this AI draft?"),
        new("dialogBody", "Settings & dialogs", "Dialog messages", "IBM Plex Sans", 13, "Keep your existing text or replace it."),
        new("validation", "Settings & dialogs", "Errors & validation", "IBM Plex Sans", 12, "Enter a size between 8 and 72 px."),
        new("calendarTitle", "Calendar", "Calendar headings", "IBM Plex Serif", 20, "October 2026"),
        new("calendarLabel", "Calendar", "Weekday & date labels", "IBM Plex Sans", 11.5, "Saturday, 24 October"),
        new("calendarNumber", "Calendar", "Day numbers", "IBM Plex Sans", 16, "24"),
        new("eventTitle", "Calendar", "Event titles", "IBM Plex Sans", 12, "Design review"),
        new("eventTime", "Calendar", "Event times", "IBM Plex Sans", 11, "2:30 PM – 3:00 PM"),
        new("notificationTitle", "Notifications & status", "In-app notification titles", "IBM Plex Sans", 14, "New message from Maya"),
        new("notificationBody", "Notifications & status", "In-app notification body", "IBM Plex Sans", 12, "Agenda for Monday’s design review"),
        new("statusBar", "Notifications & status", "Status bar text", "IBM Plex Sans", 12, "Everything is up to date"),
        new("activity", "Notifications & status", "Activity details", "IBM Plex Sans", 12, "Primary · Last checked just now"),
    });

    private static readonly Dictionary<string, TextRole> ById = Roles.ToDictionary(r => r.Id, StringComparer.Ordinal);
    public static TextRole? Find(string? id) => id != null && ById.TryGetValue(id, out var role) ? role : null;

    public static bool IsValidSize(double size) => double.IsFinite(size) && size >= MinimumSize && size <= MaximumSize && size * 2 == Math.Truncate(size * 2);
    public static bool IsValidFamily(string? family) => !string.IsNullOrWhiteSpace(family) && family.Trim().Length <= 160
        && !family.Any(c => char.IsControl(c) || c is '<' or '>' or ';' or '{' or '}' or '\\' or '/' or ':' or '"');
}

/// <summary>Only user overrides are stored; absent rows keep their built-in styling. New writing text uses Arial 18px.</summary>
public sealed class TypographySettings
{
    public Dictionary<string, TextStyle> Styles { get; set; } = new(StringComparer.Ordinal);
    public bool OverrideReceivedFonts { get; set; }

    public bool HasOverride(string roleId) => Styles != null && Styles.TryGetValue(roleId, out var style) && style?.IsValid == true && TypographyCatalog.Find(roleId) != null;

    public TextStyle Resolve(string roleId)
    {
        var role = TypographyCatalog.Find(roleId) ?? throw new ArgumentException("Unknown typography role.", nameof(roleId));
        return HasOverride(roleId) ? Styles[roleId].Clone() : new TextStyle { Family = role.DefaultFamily, Size = role.DefaultSize };
    }

    public void Normalise()
    {
        var styles = new Dictionary<string, TextStyle>(StringComparer.Ordinal);
        foreach (var entry in Styles ?? new())
            if (TypographyCatalog.Find(entry.Key) != null && entry.Value?.IsValid == true)
                styles[entry.Key] = new TextStyle { Family = entry.Value.Family.Trim(), Size = entry.Value.Size };
        Styles = styles;
    }

    public TypographySettings Clone() => new()
    {
        OverrideReceivedFonts = OverrideReceivedFonts,
        Styles = (Styles ?? new()).Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal),
    };
}
