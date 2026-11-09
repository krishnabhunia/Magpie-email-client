using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.Core.Tests;

public class ScheduleTimeTests
{
    private static TimeZoneInfo India() => TimeZoneInfo.CreateCustomTimeZone(
        "FixtureIndia", TimeSpan.FromMinutes(330), "India fixture", "India Standard Time");

    private static TimeZoneInfo ClockChanges()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2027, 1, 1), new DateTime(2027, 12, 31),
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 14),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 7));
        return TimeZoneInfo.CreateCustomTimeZone("FixtureClockChanges", TimeSpan.FromHours(-5), "Clock change fixture",
            "Fixture Standard Time", "Fixture Daylight Time", new[] { rule });
    }

    [Theory]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Utc)]
    public void Indian_wall_time_resolves_to_the_exact_instant_and_displays_its_half_hour_offset(DateTimeKind kind)
    {
        var wall = new DateTime(2027, 7, 9, 8, 30, 0, kind);
        Assert.True(ScheduleTime.TryResolve(wall, India(), out var instant, out var error));
        Assert.Equal("", error);
        Assert.Equal(new DateTimeOffset(2027, 7, 9, 3, 0, 0, TimeSpan.Zero), instant.ToUniversalTime());
        Assert.Equal(TimeSpan.FromMinutes(330), instant.Offset);
        Assert.Equal("Local time — India Standard Time (UTC+05:30)", ScheduleTime.ZoneLabel(instant, India()));
    }

    [Fact]
    public void Clock_change_gaps_and_repeated_wall_times_are_rejected_instead_of_choosing_an_instant_silently()
    {
        var zone = ClockChanges();
        Assert.False(ScheduleTime.TryResolve(new DateTime(2027, 3, 14, 2, 30, 0), zone, out _, out var gap));
        Assert.Contains("does not exist", gap);
        Assert.False(ScheduleTime.TryResolve(new DateTime(2027, 11, 7, 1, 30, 0), zone, out _, out var repeated));
        Assert.Contains("occurs twice", repeated);
        Assert.True(ScheduleTime.TryResolve(new DateTime(2027, 3, 14, 3, 30, 0), zone, out var daylight, out _));
        Assert.True(ScheduleTime.TryResolve(new DateTime(2027, 11, 7, 2, 30, 0), zone, out var standard, out _));
        Assert.Equal(TimeSpan.FromHours(-4), daylight.Offset);
        Assert.Equal(TimeSpan.FromHours(-5), standard.Offset);
        Assert.Contains("Daylight Time (UTC−04:00)", ScheduleTime.ZoneLabel(daylight, zone));
        Assert.Contains("Standard Time (UTC−05:00)", ScheduleTime.ZoneLabel(standard, zone));
    }

    [Fact]
    public void Existing_absolute_queue_time_survives_restart_independently_of_displayed_local_zone()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        Assert.True(ScheduleTime.TryResolve(new DateTime(2027, 7, 9, 8, 30, 0), India(), out var instant, out _));
        using (var engine = new MailEngine(paths, new FakeProtector()))
        {
            engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@example.test", Enabled = false });
            engine.QueueSend(new Draft { AccountId = "A", To = "recipient@example.test", Subject = "Local schedule", Html = "<p>Scheduled</p>" }, instant, null);
        }
        using var reopened = new MailEngine(paths, new FakeProtector());
        var queued = Assert.Single(reopened.Store.GetOutbox());
        Assert.Equal(instant.ToUnixTimeMilliseconds(), queued.SendAt.ToUnixTimeMilliseconds());
        Assert.Contains("UTC", ScheduleTime.Describe(queued.SendAt, DateTime.Now));
        Assert.Contains("off or offline", ScheduleTime.ExecutionHelp);
        Assert.Contains("pause automatic retries", ScheduleTime.ExecutionHelp);
    }

    [Fact]
    public async Task Automatic_retry_exhaustion_matches_the_engine_and_manual_send_still_requeues_the_message()
    {
        using var dir = new TempDir();
        var attempts = 0;
        using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector(), sendMessage: (_, _, _) =>
        {
            attempts++;
            return attempts <= OutboxItem.MaximumAutomaticAttempts
                ? Task.FromException(new IOException("Offline fixture")) : Task.CompletedTask;
        });
        engine.Config.Accounts.Add(new Account { Id = "A", Email = "me@example.test", Enabled = false });
        var id = engine.QueueSend(new Draft { AccountId = "A", To = "recipient@example.test", Subject = "Retry", Html = "<p>Retry</p>" },
            DateTimeOffset.Now.AddSeconds(-1), null);
        for (var index = 0; index < OutboxItem.MaximumAutomaticAttempts; index++)
        {
            if (index != 0) engine.Store.RescheduleOutbox(id, DateTimeOffset.Now.AddSeconds(-1));
            await engine.ProcessOutboxAsync(CancellationToken.None);
        }
        var exhausted = Assert.Single(engine.Store.GetOutbox());
        Assert.True(exhausted.AutomaticRetryExhausted);
        // Bring the due time forward while retaining Failed, to exercise the retry stop independently of its backoff.
        engine.Store.SetOutboxResult(id, OutboxStatus.Failed, "Offline fixture", DateTimeOffset.Now.AddSeconds(-1));
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(OutboxItem.MaximumAutomaticAttempts, attempts);
        engine.Store.RescheduleOutbox(id, DateTimeOffset.Now.AddSeconds(-1));
        Assert.False(Assert.Single(engine.Store.GetOutbox()).AutomaticRetryExhausted);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Equal(OutboxItem.MaximumAutomaticAttempts + 1, attempts);
        Assert.Empty(engine.Store.GetOutbox());
        Assert.Equal(OutboxStatus.Sent, Assert.Single(engine.Store.GetOutbox(includeDone: true)).Status);
    }
}
