using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>5.0.0: when an email gets deleted (design DD1) — H1–H3, L1–L4, R1–R3 and the switch between them.</summary>
public class Release500Tests
{
    private static DateTimeOffset L(int y, int mo, int d, int h = 0, int mi = 0) => new(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Local));

    private static readonly AutoDeleteRule Week = new() { Pattern = "alert@magicbricks.com", Amount = 7, Unit = DeleteUnit.Days };

    [Fact]
    public void Time_left_reads_in_hours_days_months_and_years()
    {
        var now = L(2026, 10, 1, 11, 6);
        Assert.Equal("in 3 days", DeleteDates.Left(now.AddDays(3), now));
        Assert.Equal("in 5 h", DeleteDates.Left(now.AddHours(5).AddMinutes(20), now));
        Assert.Equal("in 25 min", DeleteDates.Left(now.AddMinutes(25), now));
        Assert.Equal("in 3 months", DeleteDates.Left(now.AddDays(92), now));
        Assert.Equal("in 2 years", DeleteDates.Left(now.AddYears(2), now));
        Assert.Equal("any moment now", DeleteDates.Left(now.AddMinutes(-1), now));
        Assert.Equal("3 d", DeleteDates.Short(now.AddDays(3), now));
        Assert.Equal("5 h", DeleteDates.Short(now.AddHours(5), now));
        Assert.Equal("2 mo", DeleteDates.Short(now.AddDays(61), now));
        Assert.Equal("now", DeleteDates.Short(now, now));
    }

    [Fact]
    public void H1_tooltip_has_received_deletes_on_and_the_rule()
    {
        var now = L(2026, 10, 1, 9, 0);
        var tip = DeleteDates.Tooltip(L(2026, 9, 28, 11, 6), L(2026, 10, 5, 11, 6), now, Week);
        Assert.Equal("Received 28 Sep 2026, 11:06\nDeletes on 5 Oct 2026 (in 4 days)\nRule: alert@magicbricks.com after 7 days", tip);
    }

    [Fact]
    public void Rule_line_covers_domains_OTP_paused_and_removed_rules()
    {
        Assert.Equal("Rule: anyone at bank.in after 1 month", DeleteDates.RuleLine(new AutoDeleteRule { Pattern = "*@bank.in", Amount = 1, Unit = DeleteUnit.Months }));
        Assert.Equal("Rule: OTP from otp@bank.in, 24 hours (paused)", DeleteDates.RuleLine(new AutoDeleteRule { Pattern = "otp@bank.in", Otp = true, Paused = true }));
        Assert.StartsWith("Its rule was removed", DeleteDates.RuleLine(null));
    }

    [Fact]
    public void H3_long_tag_and_R1_R2_texts()
    {
        var now = L(2026, 10, 1, 9, 0);
        var at = L(2026, 10, 4, 11, 6);   // a Sunday
        Assert.Equal("Deletes Sun 4 Oct, 11:06 · rule: after 7 days", DeleteDates.LongTag(at, now, Week));
        Assert.Equal("Deletes Mon 4 Oct 2027, 11:06 · rule: OTP, 24 hours", DeleteDates.LongTag(L(2027, 10, 4, 11, 6), now, new AutoDeleteRule { Otp = true }));
        Assert.Equal("Deletes on Sunday 4 Oct 2026, 11:06", DeleteDates.BannerDate(at));
        Assert.Equal("Deletes on 4 Oct 2026 · in 3 days", DeleteDates.CardTitle(at, now));
        Assert.Equal("Deletes 4 Oct", DeleteDates.Chip(at, now));
        Assert.Equal("Deletes tomorrow", DeleteDates.Chip(L(2026, 10, 2, 8, 0), now));
        Assert.Equal("Deletes today 18:30", DeleteDates.Chip(L(2026, 10, 1, 18, 30), now));
        Assert.Equal("Deletes 4 Jan 2027", DeleteDates.Chip(L(2027, 1, 4), now));
        Assert.Equal("deletes in 3 days", DeleteDates.MessageNote(at, now));
    }

    [Fact]
    public void Ring_and_bar_show_the_share_of_time_left()
    {
        var at = L(2026, 10, 5, 11, 6);
        var start = DeleteDates.Start(Week, at, DateTimeOffset.MinValue);
        Assert.Equal(L(2026, 9, 28, 11, 6), start);
        Assert.Equal(3.0 / 7, DeleteDates.ShareLeft(start, at, at.AddDays(-3)), 3);
        Assert.Equal(0, DeleteDates.ShareLeft(start, at, at.AddHours(1)));
        Assert.Equal(1, DeleteDates.ShareLeft(start, at, start.AddDays(-1)));
        Assert.Equal(at.AddHours(-24), DeleteDates.Start(new AutoDeleteRule { Otp = true }, at, DateTimeOffset.MinValue));
        Assert.Equal(at.AddMonths(-2), DeleteDates.Start(new AutoDeleteRule { Amount = 2, Unit = DeleteUnit.Months }, at, DateTimeOffset.MinValue));
        var fallback = L(2026, 9, 1);
        Assert.Equal(fallback, DeleteDates.Start(null, at, fallback));
        Assert.Equal(0, DeleteDates.ShareLeft(at, at, at.AddDays(-1)));   // no time span: nothing to show
    }

    [Fact]
    public void Old_settings_get_the_recommended_options_and_bad_ids_are_corrected()
    {
        var old = JsonSerializer.Deserialize<Appearance>("{\"Theme\":0}", AppSettings.Json)!;
        old.Normalise();
        Assert.Equal(("H1", "L1", "R1"), (old.DeleteDates.Hover, old.DeleteDates.List, old.DeleteDates.Reader));

        var odd = JsonSerializer.Deserialize<Appearance>("{\"DeleteDates\":{\"Hover\":\"h2\",\"List\":\"L9\",\"Reader\":null}}", AppSettings.Json)!;
        odd.Normalise();
        Assert.Equal(("H2", "L1", "R1"), (odd.DeleteDates.Hover, odd.DeleteDates.List, odd.DeleteDates.Reader));

        var a = new Appearance { DeleteDates = new DeleteDateLook { Hover = "H3", List = "L4", Reader = "R3" } };
        var copy = JsonSerializer.Deserialize<Appearance>(JsonSerializer.Serialize(a.Clone(), AppSettings.Json), AppSettings.Json)!;
        copy.Normalise();
        Assert.Equal("H3, L4, R3", copy.DeleteDates.ToString());
    }

    [Fact]
    public void R3_puts_each_emails_time_on_its_header()
    {
        var soon = Rows.Make("A", 1, "a", from: "alert@magicbricks.com", subject: "Price alert");
        var later = Rows.Make("A", 1, "b", from: "alert@magicbricks.com", subject: "Price alert");
        var kept = Rows.Make("A", 1, "c", from: "alert@magicbricks.com", subject: "Price alert");
        var page = HtmlRenderer.BuildConversation("Price alert", new[]
        {
            new RenderMessage { Row = soon, DeleteNote = "deletes in 5 h", DeleteUrgency = DeleteUrgency.Soon, DeleteTip = "Deletes on <Sat>" },
            new RenderMessage { Row = later, DeleteNote = "deletes in 3 days", DeleteUrgency = DeleteUrgency.Weeks },
            new RenderMessage { Row = kept, Expanded = true },
        }, false, DateTimeOffset.Now).Html;
        Assert.Contains("class=\"dd soon\" title=\"Deletes on &lt;Sat&gt;\">🗑 deletes in 5 h</span>", page);
        Assert.Contains("class=\"dd weeks\" title=\"\">🗑 deletes in 3 days</span>", page);
        Assert.Equal(2, page.Split("class=\"dd ").Length - 1);
        Assert.Contains("--ddsoon:#B3261E", page);
        Assert.Contains("--ddsoon:#F28B82", HtmlRenderer.BuildConversation("x", new[] { new RenderMessage { Row = kept } }, false, DateTimeOffset.Now, dark: true).Html);
    }
}
