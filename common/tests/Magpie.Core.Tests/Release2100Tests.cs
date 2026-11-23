using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Settings;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>
/// 21.0.0 — design OW1 (option A): an email in its own window. Its words, which window an email belongs to, the step
/// to the previous / next email of the list, where the window is put, the New window toolbar button and the setting.
/// </summary>
public sealed class Release2100Tests
{
    private static readonly string[] Inbox = { "A\none", "A\ntwo", "B\none", "A\nfour" };
    private static readonly EmailWindow.Area Screen = new(0, 0, 1920, 1080);

    // ───────────────────────── the words ─────────────────────────

    [Fact]
    public void The_window_is_named_after_the_subject()
    {
        Assert.Equal("Handover schedule — follow-up — Magpie", EmailWindow.Title("  Handover schedule — follow-up "));
        Assert.Equal("(no subject) — Magpie", EmailWindow.Title(""));
        Assert.Equal("(no subject) — Magpie", EmailWindow.Title(null));
        Assert.Equal("Open in new window", EmailWindow.OpenWords);
        Assert.Equal("Enter", EmailWindow.OpenKey);
        Assert.Equal("New window", EmailWindow.ButtonName);
        Assert.Contains("double-click", EmailWindow.ButtonTip);
        Assert.EndsWith("(K)", EmailWindow.PreviousTip);
        Assert.EndsWith("(J)", EmailWindow.NextTip);
    }

    [Fact]
    public void One_window_per_conversation_of_one_account()
    {
        Assert.Equal(EmailWindow.Key("A", "one"), EmailWindow.Key("A", "one"));
        Assert.NotEqual(EmailWindow.Key("A", "one"), EmailWindow.Key("B", "one"));     // the same conversation key in another account
        Assert.NotEqual(EmailWindow.Key("A", "one"), EmailWindow.Key("A", "one2"));
        Assert.NotEqual(EmailWindow.Key("A", "b|c"), EmailWindow.Key("A|b", "c"));     // the list's "|" key could not tell these apart
    }

    // ───────────────────────── previous / next ─────────────────────────

    [Fact]
    public void The_window_says_where_the_email_is_in_its_list()
    {
        Assert.Equal("1 of 4 in Inbox", EmailWindow.Position(Inbox, "A\none", "Inbox"));
        Assert.Equal("3 of 4 in Inbox", EmailWindow.Position(Inbox, "B\none", " Inbox "));
        Assert.Equal("4 of 4", EmailWindow.Position(Inbox, "A\nfour", null));          // a list without a name of its own
        Assert.Equal("4 of 4", EmailWindow.Position(Inbox, "A\nfour", "  "));
        Assert.Equal("", EmailWindow.Position(Inbox, "A\ngone", "Inbox"));             // not in the list: nothing to say
        Assert.Equal("", EmailWindow.Position(Array.Empty<string>(), "A\none", "Inbox"));
        var many = Enumerable.Range(1, 1500).Select(i => "A\n" + i).ToList();
        Assert.Equal("1,200 of 1,500 in All Mail", EmailWindow.Position(many, "A\n1200", "All Mail"));
    }

    [Fact]
    public void Previous_and_next_stop_at_the_ends_of_the_list()
    {
        Assert.Equal("A\ntwo", EmailWindow.Step(Inbox, "A\none", 1));
        Assert.Equal("B\none", EmailWindow.Step(Inbox, "A\ntwo", 1));
        Assert.Equal("A\ntwo", EmailWindow.Step(Inbox, "B\none", -1));
        Assert.Null(EmailWindow.Step(Inbox, "A\none", -1));
        Assert.Null(EmailWindow.Step(Inbox, "A\nfour", 1));
        Assert.Null(EmailWindow.Step(Inbox, "A\ngone", 1));
        Assert.Null(EmailWindow.Step(Array.Empty<string>(), "A\none", 1));
        // A bigger number is still one step; zero stays where it is.
        Assert.Equal("A\ntwo", EmailWindow.Step(Inbox, "A\none", 5));
        Assert.Equal("A\none", EmailWindow.Step(Inbox, "A\none", 0));
        // Walking down from the first reaches every email once, in the list's order.
        var walked = new List<string> { Inbox[0] };
        while (EmailWindow.Step(Inbox, walked[^1], 1) is { } next) walked.Add(next);
        Assert.Equal(Inbox, walked);
    }

    // ───────────────────────── size and place ─────────────────────────

    [Fact]
    public void A_first_window_opens_a_little_inside_the_main_window_at_the_default_size()
    {
        var at = EmailWindow.Place(new EmailWindowPlacement(), Screen, new EmailWindow.Area(200, 100, 1320, 840), alreadyOpen: 0);
        Assert.Equal(new EmailWindow.Area(280, 160, EmailWindowPlacement.DefaultWidth, EmailWindowPlacement.DefaultHeight), at);
        // No main window to go by (it is minimised or in the notification area): the middle of the screen.
        var centred = EmailWindow.Place(new EmailWindowPlacement(), Screen, null, 0);
        Assert.Equal(new EmailWindow.Area((1920 - 860) / 2.0, (1080 - 760) / 2.0, 860, 760), centred);
    }

    [Fact]
    public void The_next_window_opens_where_the_last_one_was_closed()
    {
        var saved = new EmailWindowPlacement { HasSavedPlacement = true, Left = 640, Top = 90, Width = 1000, Height = 900 };
        Assert.Equal(new EmailWindow.Area(640, 90, 1000, 900), EmailWindow.Place(saved, Screen, new EmailWindow.Area(0, 0, 1320, 840), 0));
        // On a second screen to the left (negative coordinates) too.
        var two = new EmailWindow.Area(-1920, 0, 3840, 1080);
        var left = new EmailWindowPlacement { HasSavedPlacement = true, Left = -1500, Top = 40 };
        Assert.Equal((-1500d, 40d), (EmailWindow.Place(left, two, null, 0).Left, EmailWindow.Place(left, two, null, 0).Top));
    }

    [Fact]
    public void A_saved_place_that_is_off_the_screen_now_is_not_used()
    {
        // The second screen was unplugged: the window would open where nobody can reach it.
        var saved = new EmailWindowPlacement { HasSavedPlacement = true, Left = 2500, Top = 200, Width = 900, Height = 700 };
        var at = EmailWindow.Place(saved, Screen, new EmailWindow.Area(100, 50, 1320, 840), 0);
        Assert.Equal(new EmailWindow.Area(180, 110, 900, 700), at);                   // inside the main window, its size kept
        Assert.False(EmailWindow.OnScreen(2500, 200, Screen));
        Assert.False(EmailWindow.OnScreen(1850, 200, Screen));                        // less than 100 px of it would show
        Assert.False(EmailWindow.OnScreen(200, 1000, Screen));
        Assert.False(EmailWindow.OnScreen(-60, 200, Screen));
        Assert.False(EmailWindow.OnScreen(double.NaN, 200, Screen));
        Assert.True(EmailWindow.OnScreen(-40, -40, Screen));
        Assert.True(EmailWindow.OnScreen(1800, 960, Screen));
        // The main window is off the screen too: the middle of the screen.
        var centred = EmailWindow.Place(saved, Screen, new EmailWindow.Area(5000, 5000, 1320, 840), 0);
        Assert.Equal(new EmailWindow.Area((1920 - 900) / 2.0, (1080 - 700) / 2.0, 900, 700), centred);
    }

    [Fact]
    public void A_window_is_never_larger_than_the_screen_or_smaller_than_its_minimum()
    {
        var huge = new EmailWindowPlacement { Width = 3000, Height = 2400 };
        var small = new EmailWindow.Area(0, 0, 1280, 720);
        var at = EmailWindow.Place(huge, small, null, 0);
        Assert.Equal((1280d, 720d, 0d, 0d), (at.Width, at.Height, at.Left, at.Top));
        var tiny = EmailWindow.Place(new EmailWindowPlacement { Width = 10, Height = 10 }, Screen, null, 0);
        Assert.Equal((EmailWindowPlacement.MinWidth, EmailWindowPlacement.MinHeight), (tiny.Width, tiny.Height));
    }

    [Fact]
    public void Further_windows_step_down_and_right_and_start_again_before_leaving_the_screen()
    {
        var saved = new EmailWindowPlacement { HasSavedPlacement = true, Left = 100, Top = 100 };
        var first = EmailWindow.Place(saved, Screen, null, 0);
        var second = EmailWindow.Place(saved, Screen, null, 1);
        var third = EmailWindow.Place(saved, Screen, null, 2);
        Assert.Equal((100d, 100d), (first.Left, first.Top));
        Assert.Equal((128d, 128d), (second.Left, second.Top));
        Assert.Equal((156d, 156d), (third.Left, third.Top));
        Assert.Equal((first.Width, first.Height), (third.Width, third.Height));
        // Every one of fifty windows can still be grabbed by its title bar, and none sits exactly on the one before.
        var places = Enumerable.Range(0, 50).Select(n => EmailWindow.Place(saved, Screen, null, n)).ToList();
        Assert.All(places, p => Assert.True(EmailWindow.OnScreen(p.Left, p.Top, Screen), $"{p.Left},{p.Top}"));
        for (var n = 1; n < places.Count; n++) Assert.NotEqual((places[n - 1].Left, places[n - 1].Top), (places[n].Left, places[n].Top));
        Assert.Contains(places.Skip(1), p => p.Left == 100 && p.Top == 100);          // it wrapped back to the start
        // A saved place close to the screen's edge has no room to step: the windows stay there, on the screen.
        var edge = new EmailWindowPlacement { HasSavedPlacement = true, Left = 1800, Top = 960 };
        Assert.All(Enumerable.Range(0, 5).Select(n => EmailWindow.Place(edge, Screen, null, n)), p => Assert.True(EmailWindow.OnScreen(p.Left, p.Top, Screen)));
        Assert.Equal(first, EmailWindow.Place(saved, Screen, null, -3));              // a wrong count is no count
    }

    [Fact]
    public void A_saved_place_between_two_monitors_of_different_heights_is_not_used()
    {
        // The main monitor, and a smaller one to its right that sits higher. The rectangle around both runs from
        // (0, -600) to (3200, 1080), but its bottom-right and top-left parts are on no monitor.
        var main = new EmailWindow.Area(0, 0, 1920, 1040);
        var side = new EmailWindow.Area(1920, -600, 1280, 680);
        var both = new[] { main, side };
        var around = new EmailWindow.Area(0, -600, 3200, 1680);

        var gap = new EmailWindowPlacement { HasSavedPlacement = true, Left = 2400, Top = 500, Width = 900, Height = 700 };
        Assert.True(EmailWindow.OnScreen(gap.Left, gap.Top, around));                  // the one rectangle would have taken it
        Assert.Null(EmailWindow.ScreenOf(gap.Left, gap.Top, both));                    // no monitor shows that corner
        var at = EmailWindow.Place(gap, both, new EmailWindow.Area(100, 50, 1320, 840), 0);
        Assert.Equal((180d, 110d), (at.Left, at.Top));                                 // a little inside the main window instead
        Assert.Equal(main, EmailWindow.ScreenOf(at.Left, at.Top, both));
        // Without a main window to go by: the middle of the main monitor, not of the rectangle around both.
        var centred = EmailWindow.Place(gap, both, null, 0);
        Assert.Equal(((1920 - 900) / 2.0, (1040 - 700) / 2.0), (centred.Left, centred.Top));
        // The same in the empty corner above the main monitor.
        var above = new EmailWindowPlacement { HasSavedPlacement = true, Left = 300, Top = -400 };
        Assert.True(EmailWindow.OnScreen(above.Left, above.Top, around));
        Assert.Equal(main, EmailWindow.ScreenOf(EmailWindow.Place(above, both, null, 0).Left, EmailWindow.Place(above, both, null, 0).Top, both));
    }

    [Fact]
    public void A_window_on_the_second_monitor_stays_there_with_that_monitors_size()
    {
        var main = new EmailWindow.Area(0, 0, 1920, 1040);
        var side = new EmailWindow.Area(1920, -600, 1280, 680);
        var both = new[] { main, side };
        var saved = new EmailWindowPlacement { HasSavedPlacement = true, Left = 2000, Top = -560, Width = 1600, Height = 900 };
        var at = EmailWindow.Place(saved, both, new EmailWindow.Area(100, 50, 1320, 840), 0);
        Assert.Equal(new EmailWindow.Area(2000, -560, 1280, 680), at);                 // its place kept, its size cut to that monitor
        // Further windows step down and right on that monitor and start again before they leave it.
        var places = Enumerable.Range(0, 40).Select(n => EmailWindow.Place(saved, both, null, n)).ToList();
        Assert.All(places, p => Assert.Equal(side, EmailWindow.ScreenOf(p.Left, p.Top, both)));
        Assert.Equal((2028d, -532d), (places[1].Left, places[1].Top));
        Assert.Contains(places.Skip(1), p => p.Left == 2000 && p.Top == -560);
        // A main window that sits on the second monitor takes the new window there too.
        var near = EmailWindow.Place(new EmailWindowPlacement(), both, new EmailWindow.Area(2000, -580, 1000, 600), 0);
        Assert.Equal(side, EmailWindow.ScreenOf(near.Left, near.Top, both));
        Assert.Equal((2080d, -520d, 860d, 680d), (near.Left, near.Top, near.Width, near.Height));
    }

    [Fact]
    public void A_step_never_lands_exactly_on_the_edge_and_no_monitor_at_all_still_gives_a_window()
    {
        // 112 px of room is exactly four steps: the fifth window starts again instead of sitting on the line where
        // the title bar can no longer be grabbed.
        var saved = new EmailWindowPlacement { HasSavedPlacement = true, Left = 1920 - 100 - 112, Top = 100 };
        var places = Enumerable.Range(0, 12).Select(n => EmailWindow.Place(saved, Screen, null, n)).ToList();
        Assert.All(places, p => Assert.True(EmailWindow.OnScreen(p.Left, p.Top, Screen), $"{p.Left},{p.Top}"));
        Assert.Equal(4, places.Select(p => p.Left).Distinct().Count());
        // Windows gave no monitor (or only empty ones): a default-sized window at the top-left corner.
        var none = EmailWindow.Place(new EmailWindowPlacement(), Array.Empty<EmailWindow.Area>(), null, 0);
        Assert.Equal(new EmailWindow.Area(0, 0, EmailWindowPlacement.DefaultWidth, EmailWindowPlacement.DefaultHeight), none);
        Assert.Equal(none, EmailWindow.Place(new EmailWindowPlacement(), new[] { new EmailWindow.Area(0, 0, 0, 0) }, null, 0));
    }

    [Fact]
    public void A_saved_placement_is_repaired_when_it_is_read()
    {
        var p = new EmailWindowPlacement { HasSavedPlacement = true, Left = double.NaN, Top = 10, Width = 50, Height = 99999 };
        p.Normalise();
        Assert.False(p.HasSavedPlacement);
        Assert.Equal((EmailWindowPlacement.MinWidth, 3000d), (p.Width, p.Height));
        var odd = new EmailWindowPlacement { Width = double.PositiveInfinity, Height = double.NaN, HasSavedPlacement = true, Left = 5, Top = 6 };
        odd.Normalise();
        Assert.Equal((EmailWindowPlacement.DefaultWidth, EmailWindowPlacement.DefaultHeight, true), (odd.Width, odd.Height, odd.HasSavedPlacement));
    }

    // ───────────────────────── the toolbar button and the setting ─────────────────────────

    [Fact]
    public void New_window_is_a_toolbar_button_that_shows_by_default()
    {
        var a = new Appearance();
        var shown = a.Toolbar.Where(b => b.Visible).Select(b => b.Id).ToList();
        Assert.Equal(new[] { "archive", "delete", "snooze", "setaside", "remind", "tag", "pin", "move", "window" }, shown);
        Assert.Equal(new[] { "unread", "replyall", "forward" }, a.Toolbar.Where(b => !b.Visible).Select(b => b.Id));
        Assert.Equal(a.Toolbar.Count, a.Toolbar.Select(b => b.Id).Distinct().Count());
    }

    [Fact]
    public void A_toolbar_saved_before_gets_New_window_shown_and_keeps_it_hidden_once_the_user_hides_it()
    {
        // settings.json written by 20.0.0: the user's own order, Pin hidden, no "window" yet.
        var old = JsonSerializer.Deserialize<Appearance>(
            """{"Toolbar":[{"Id":"delete","Visible":true},{"Id":"archive","Visible":true},{"Id":"pin","Visible":false},{"Id":"move","Visible":true}]}""", AppSettings.Json)!;
        old.Normalise();
        Assert.Equal(new[] { "delete", "archive", "pin", "move" }, old.Toolbar.Take(4).Select(b => b.Id));   // his order is kept
        Assert.False(old.Toolbar.Single(b => b.Id == "pin").Visible);
        Assert.True(old.Toolbar.Single(b => b.Id == "window").Visible);                                       // the new button shows
        Assert.All(old.Toolbar.Where(b => b.Id is "snooze" or "unread" or "forward"), b => Assert.False(b.Visible));   // other added ones wait in More
        Assert.Equal(Appearance.ToolbarIds.Length, old.Toolbar.Count);

        // He hides it in Customise toolbar; that is saved and read back as hidden.
        old.Toolbar.Single(b => b.Id == "window").Visible = false;
        var again = JsonSerializer.Deserialize<Appearance>(JsonSerializer.Serialize(old, AppSettings.Json), AppSettings.Json)!;
        again.Normalise();
        Assert.False(again.Toolbar.Single(b => b.Id == "window").Visible);
        Assert.Equal(old.Toolbar.Select(b => b.Id), again.Toolbar.Select(b => b.Id));
    }

    [Fact]
    public void Double_click_opens_the_window_unless_it_is_switched_off()
    {
        Assert.True(new AppSettings().OpenInWindowOnDoubleClick);
        using var dir = new TempDir();
        // settings.json from before 21.0.0 has no such line: the double-click works at once.
        File.WriteAllText(dir.File("settings.json"), """{"MarkReadOnOpen":false}""");
        var store = new SettingsStore(dir.File("settings.json"));
        var s = store.Load();
        Assert.True(s.OpenInWindowOnDoubleClick);
        Assert.False(s.MarkReadOnOpen);
        Assert.NotNull(s.EmailWindow);
        Assert.False(s.EmailWindow.HasSavedPlacement);
        Assert.Equal((EmailWindowPlacement.DefaultWidth, EmailWindowPlacement.DefaultHeight), (s.EmailWindow.Width, s.EmailWindow.Height));

        s.OpenInWindowOnDoubleClick = false;
        s.EmailWindow = new EmailWindowPlacement { HasSavedPlacement = true, Left = 300, Top = 200, Width = 900, Height = 800, Maximized = true };
        store.Save();
        var back = new SettingsStore(dir.File("settings.json")).Load();
        Assert.False(back.OpenInWindowOnDoubleClick);
        Assert.Equal((true, 300d, 200d, 900d, 800d, true),
            (back.EmailWindow.HasSavedPlacement, back.EmailWindow.Left, back.EmailWindow.Top, back.EmailWindow.Width, back.EmailWindow.Height, back.EmailWindow.Maximized));

        // A damaged placement line does not stop the settings from loading.
        File.WriteAllText(dir.File("settings.json"), """{"EmailWindow":null}""");
        Assert.NotNull(new SettingsStore(dir.File("settings.json")).Load().EmailWindow);
    }
}
