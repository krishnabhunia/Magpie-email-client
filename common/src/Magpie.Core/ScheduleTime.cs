using System.Globalization;

namespace Magpie.Core;

/// <summary>Wall-clock selection and honest descriptions for this PC's existing local send queue.</summary>
public static class ScheduleTime
{
    public const string ExecutionHelp = "Scheduled messages stay on this PC. Magpie must be running and able to reach the mail server. If the PC is off or offline when the time passes, sending is delayed. Repeated failures pause automatic retries; use Send now in Scheduled to try again.";

    public static bool TryResolve(DateTime wallTime, TimeZoneInfo zone, out DateTimeOffset instant, out string error)
    {
        var local = DateTime.SpecifyKind(wallTime, DateTimeKind.Unspecified);
        instant = default;
        if (zone.IsInvalidTime(local))
        {
            error = "This local time does not exist because the clocks move forward. Choose another time.";
            return false;
        }
        if (zone.IsAmbiguousTime(local))
        {
            error = "This local time occurs twice because the clocks move back. Choose a time outside that clock change.";
            return false;
        }
        instant = new DateTimeOffset(local, zone.GetUtcOffset(local));
        error = "";
        return true;
    }

    public static string ZoneLabel(DateTimeOffset instant, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var name = zone.IsDaylightSavingTime(instant) ? zone.DaylightName : zone.StandardName;
        if (string.IsNullOrWhiteSpace(name)) name = zone.Id;
        var offset = local.Offset;
        return "Local time — " + name + " (UTC" + (offset < TimeSpan.Zero ? "−" : "+")
            + offset.Duration().ToString(@"hh\:mm", CultureInfo.InvariantCulture) + ")";
    }

    public static string Describe(DateTimeOffset instant, DateTime nowLocal) =>
        TimePresets.Describe(instant, nowLocal) + " · " + ZoneLabel(instant);
}
