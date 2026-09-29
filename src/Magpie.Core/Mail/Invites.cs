using System.Globalization;
using System.Text;

namespace Magpie.Core.Mail;

// ───────────────────────── Meeting invites in threads (design B3) ─────────────────────────
// A message carrying a text/calendar part (iCalendar, RFC 5545) with METHOD:REQUEST shows an invite card; the answer
// goes back to the organiser as METHOD:REPLY with PARTSTAT (iTIP, RFC 5546). METHOD:CANCEL / a higher SEQUENCE
// update or remove the event we keep for it.

public enum InviteAnswer { Accepted = 0, Tentative = 1, Declined = 2 }

public sealed record InvitePerson(string Name, string Email, string PartStat)
{
    public string Display => string.IsNullOrWhiteSpace(Name) ? Email : Name;
}

public sealed class CalendarInvite
{
    public string Method { get; init; } = "";
    public string Uid { get; init; } = "";
    public int Sequence { get; init; }
    public string Summary { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public bool AllDay { get; init; }
    public string Location { get; init; } = "";
    /// <summary>A video-call link (Google Meet, Teams, Zoom) found in the event.</summary>
    public string MeetLink { get; init; } = "";
    public InvitePerson? Organizer { get; init; }
    public List<InvitePerson> Attendees { get; init; } = new();
    /// <summary>The event's own lines (DTSTART, DTEND, ORGANIZER…), copied as-is into the reply.</summary>
    internal List<string> RawLines { get; init; } = new();

    public bool IsRequest => Method.Equals("REQUEST", StringComparison.OrdinalIgnoreCase);
    public bool IsCancel => Method.Equals("CANCEL", StringComparison.OrdinalIgnoreCase);
}

public static class Invites
{
    /// <summary>Parses the first VEVENT of an iCalendar text; null when there is none (or it has no start).</summary>
    public static CalendarInvite? Parse(string? ics)
    {
        if (string.IsNullOrWhiteSpace(ics)) return null;
        var lines = Unfold(ics);
        string method = "";
        var inEvent = false;
        var depth = 0;
        var props = new List<(string Name, Dictionary<string, string> Params, string Value, string Raw)>();
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            var (name, ps, value) = Split(line);
            if (name == "BEGIN")
            {
                if (value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase) && !inEvent) { inEvent = true; depth = 0; continue; }
                if (inEvent) depth++;
                continue;
            }
            if (name == "END")
            {
                if (inEvent && depth == 0 && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) break;
                if (inEvent) depth--;
                continue;
            }
            if (!inEvent) { if (name == "METHOD") method = value.Trim(); continue; }
            if (depth == 0) props.Add((name, ps, value, line));   // skip nested VALARM etc.
        }
        string P(string n) => props.FirstOrDefault(p => p.Name == n).Value ?? "";
        var start = props.FirstOrDefault(p => p.Name == "DTSTART");
        if (start.Name == null) return null;
        var (s, allDay) = ParseDate(start.Value, start.Params);
        DateTimeOffset e;
        var end = props.FirstOrDefault(p => p.Name == "DTEND");
        if (end.Name != null) e = ParseDate(end.Value, end.Params).When;
        else if (P("DURATION") is { Length: > 0 } dur && TryDuration(dur, out var span)) e = s + span;
        else e = allDay ? s.AddDays(1) : s.AddHours(1);

        var organizer = props.Where(p => p.Name == "ORGANIZER").Select(p => Person(p.Params, p.Value)).FirstOrDefault();
        var attendees = props.Where(p => p.Name == "ATTENDEE").Select(p => Person(p.Params, p.Value)).ToList();
        var location = Unescape(P("LOCATION"));
        var meet = FindMeetLink(new[] { P("X-GOOGLE-CONFERENCE"), location, Unescape(P("DESCRIPTION")), P("URL") });
        return new CalendarInvite
        {
            Method = method.ToUpperInvariant(), Uid = P("UID").Trim(), Sequence = int.TryParse(P("SEQUENCE"), out var seq) ? seq : 0,
            Summary = Unescape(P("SUMMARY")).Trim(), Start = s, End = e, AllDay = allDay, Location = location.Trim(), MeetLink = meet,
            Organizer = organizer, Attendees = attendees,
            RawLines = props.Where(p => p.Name is "DTSTART" or "DTEND" or "DURATION" or "ORGANIZER" or "SUMMARY" or "RECURRENCE-ID").Select(p => p.Raw).ToList(),
        };
    }

    /// <summary>The iTIP reply (METHOD:REPLY) for one attendee.</summary>
    public static string BuildReply(CalendarInvite inv, string myEmail, string myName, InviteAnswer answer, string? comment, DateTimeOffset now)
    {
        var partstat = answer switch { InviteAnswer.Accepted => "ACCEPTED", InviteAnswer.Tentative => "TENTATIVE", _ => "DECLINED" };
        var sb = new StringBuilder();
        void L(string s) { foreach (var f in Fold(s)) sb.Append(f).Append("\r\n"); }
        L("BEGIN:VCALENDAR");
        L("PRODID:-//Magpie//Magpie Mail//EN");
        L("VERSION:2.0");
        L("CALSCALE:GREGORIAN");
        L("METHOD:REPLY");
        L("BEGIN:VEVENT");
        L("UID:" + inv.Uid);
        L("SEQUENCE:" + inv.Sequence.ToString(CultureInfo.InvariantCulture));
        L("DTSTAMP:" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        foreach (var raw in inv.RawLines) L(raw);
        var cn = string.IsNullOrWhiteSpace(myName) ? "" : ";CN=" + QuoteParam(myName);
        L($"ATTENDEE;PARTSTAT={partstat}{cn}:mailto:{myEmail}");
        if (!string.IsNullOrWhiteSpace(comment)) L("COMMENT:" + Escape(comment.Trim()));
        L("END:VEVENT");
        L("END:VCALENDAR");
        return sb.ToString();
    }

    public static string SubjectFor(InviteAnswer a, string summary) => a switch
    {
        InviteAnswer.Accepted => "Accepted: ",
        InviteAnswer.Tentative => "Tentative: ",
        _ => "Declined: ",
    } + (string.IsNullOrWhiteSpace(summary) ? "(no title)" : summary);

    public static string AnswerText(InviteAnswer a) => a switch
    {
        InviteAnswer.Accepted => "You accepted",
        InviteAnswer.Tentative => "You said maybe",
        _ => "You declined",
    };

    /// <summary>"Tue 6 Oct, 11:00 – 12:00" in local time (all-day: "Tue 6 Oct, all day").</summary>
    public static string WhenText(CalendarInvite inv)
    {
        var s = inv.Start.ToLocalTime();
        var e = inv.End.ToLocalTime();
        if (inv.AllDay)
        {
            var lastDay = e.AddDays(-1);
            return lastDay.Date > s.Date ? $"{s:ddd d MMM} – {lastDay:ddd d MMM}, all day" : $"{s:ddd d MMM}, all day";
        }
        return e.Date == s.Date ? $"{s:ddd d MMM}, {s:HH:mm} – {e:HH:mm}" : $"{s:ddd d MMM, HH:mm} – {e:ddd d MMM, HH:mm}";
    }

    // ───────────── parsing helpers ─────────────

    private static List<string> Unfold(string ics)
    {
        var raw = ics.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<string>();
        foreach (var l in raw)
        {
            if ((l.StartsWith(' ') || l.StartsWith('\t')) && lines.Count > 0) lines[^1] += l[1..];
            else lines.Add(l.TrimEnd());
        }
        return lines;
    }

    /// <summary>NAME;PARAM=V;PARAM="V:X":VALUE — the value starts at the first ':' outside quotes.</summary>
    private static (string Name, Dictionary<string, string> Params, string Value) Split(string line)
    {
        var inQuote = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuote = !inQuote;
            else if (line[i] == ':' && !inQuote) { colon = i; break; }
        }
        var head = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        var parts = new List<string>();
        var sb = new StringBuilder();
        inQuote = false;
        foreach (var ch in head)
        {
            if (ch == '"') inQuote = !inQuote;
            if (ch == ';' && !inQuote) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        parts.Add(sb.ToString());
        var ps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=');
            if (eq > 0) ps[p[..eq].Trim()] = p[(eq + 1)..].Trim().Trim('"');
        }
        return (parts[0].Trim().ToUpperInvariant(), ps, value);
    }

    private static (DateTimeOffset When, bool AllDay) ParseDate(string value, Dictionary<string, string> ps)
    {
        value = value.Trim();
        if ((ps.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || value.Length == 8)
        {
            var d = DateTime.ParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture);
            return (new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)), true);
        }
        var utc = value.EndsWith('Z');
        var dt = DateTime.ParseExact(value.TrimEnd('Z')[..15], "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
        if (utc) return (new DateTimeOffset(dt, TimeSpan.Zero), false);
        if (ps.TryGetValue("TZID", out var tzid) && FindZone(tzid) is { } zone)
            return (new DateTimeOffset(dt, zone.GetUtcOffset(dt)), false);
        return (new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)), false);   // floating time = local
    }

    private static TimeZoneInfo? FindZone(string tzid)
    {
        var id = tzid.Trim().Trim('"');
        // Outlook sometimes sends "(UTC+05:30) Chennai, Kolkata…"-style names; those fall back to local time.
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var win)) { try { return TimeZoneInfo.FindSystemTimeZoneById(win); } catch { } }
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) { try { return TimeZoneInfo.FindSystemTimeZoneById(iana); } catch { } }
        return null;
    }

    private static bool TryDuration(string s, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        var m = System.Text.RegularExpressions.Regex.Match(s.Trim(), @"^([+-])?P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return false;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        span = new TimeSpan(G(2) * 7 + G(3), G(4), G(5), G(6));
        if (m.Groups[1].Value == "-") span = -span;
        return true;
    }

    private static InvitePerson Person(Dictionary<string, string> ps, string value)
    {
        var email = value.Trim();
        if (email.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) email = email[7..];
        return new InvitePerson(ps.TryGetValue("CN", out var cn) ? cn : "", email.Trim().ToLowerInvariant(),
            ps.TryGetValue("PARTSTAT", out var st) ? st.ToUpperInvariant() : "NEEDS-ACTION");
    }

    private static string FindMeetLink(IEnumerable<string> texts)
    {
        var rx = new System.Text.RegularExpressions.Regex(@"https://(?:meet\.google\.com/[a-z0-9\-]+|teams\.microsoft\.com/l/meetup-join/\S+|[a-z0-9\-]+\.zoom\.us/j/\S+|zoom\.us/j/\S+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var t in texts)
        {
            if (string.IsNullOrEmpty(t)) continue;
            var m = rx.Match(t);
            if (m.Success) return m.Value.TrimEnd('>', ')', '.', ',', ';', '"');
        }
        return "";
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");
    private static string QuoteParam(string s) => "\"" + s.Replace("\"", "'") + "\"";

    /// <summary>RFC 5545 folding: lines longer than 75 octets continue on the next line after a space.</summary>
    private static IEnumerable<string> Fold(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75) { yield return line; yield break; }
        var sb = new StringBuilder();
        var count = 0;
        var first = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (count + n > (first ? 75 : 74))
            {
                yield return (first ? "" : " ") + sb;
                sb.Clear();
                count = 0;
                first = false;
            }
            sb.Append(rune.ToString());
            count += n;
        }
        if (sb.Length > 0) yield return (first ? "" : " ") + sb;
    }
}
