using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

/// <summary>1.2.0: dark theme (B1).</summary>
public class Release120Tests
{
    // ───────────── B1 dark theme ─────────────

    [Fact]
    public void Theme_defaults_to_match_windows_and_survives_save_load_and_clone()
    {
        Assert.Equal(ThemeMode.MatchWindows, new Appearance().Theme);

        // Settings saved by 1.1.2 have no Theme: Match Windows.
        var old = System.Text.Json.JsonSerializer.Deserialize<Appearance>("""{"ButtonStyle":"IconOnly"}""", AppSettings.Json)!;
        old.Normalise();
        Assert.Equal(ThemeMode.MatchWindows, old.Theme);

        // A number that isn't a theme (hand-edited file) falls back too.
        var odd = System.Text.Json.JsonSerializer.Deserialize<Appearance>("""{"Theme":7}""", AppSettings.Json)!;
        odd.Normalise();
        Assert.Equal(ThemeMode.MatchWindows, odd.Theme);

        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        var s = store.Load();
        s.Appearance.Theme = ThemeMode.Dark;
        store.Save(s);
        Assert.Contains("\"Dark\"", File.ReadAllText(dir.File("settings.json")));
        var again = new SettingsStore(dir.File("settings.json")).Load();
        Assert.Equal(ThemeMode.Dark, again.Appearance.Theme);
        Assert.Equal(ThemeMode.Dark, again.Appearance.Clone().Theme);
    }

    [Theory]
    [InlineData("<p>Hello</p><blockquote>quoted</blockquote>", false)]
    [InlineData("<div style=\"font-size:14px;border-color:#ccc\">x</div>", false)]   // border-color isn't a text / page colour
    [InlineData("<table bgcolor=\"#ffffff\"><tr><td>x</td></tr></table>", true)]
    [InlineData("<div style=\"background-color: #fafafa\">x</div>", true)]
    [InlineData("<div style=\"background:#fff url(x)\">x</div>", true)]
    [InlineData("<p style=\"color:#333\">x</p>", true)]
    [InlineData("<font color=\"black\">x</font>", true)]
    [InlineData("<style>a{color:#1a73e8}</style><p>x</p>", true)]
    [InlineData("", false)]
    public void Messages_with_their_own_colours_are_recognised(string html, bool own) =>
        Assert.Equal(own, HtmlRenderer.HasOwnColours(html));

    private static RenderMessage Msg(long id, string html) => new()
    {
        Row = new MessageRow { Id = id, FromName = "Asha", FromAddress = "asha@x.com", Subject = "S", Date = DateTimeOffset.Now, Flags = MessageFlags.Seen },
        Body = new MessageBody { Html = html },
        Expanded = true,
    };

    [Fact]
    public void Dark_page_uses_dark_colours_and_puts_coloured_mail_on_a_white_card()
    {
        var plain = Msg(1, "<p>Plain words</p>");
        var coloured = Msg(2, "<table bgcolor=\"#ffffff\"><tr><td style=\"color:#222\">Newsletter</td></tr></table>");

        var dark = HtmlRenderer.BuildConversation("S", new[] { plain, coloured }, allowRemote: false, DateTimeOffset.Now, dark: true).Html;
        Assert.Contains("--page:#181C20", dark);
        Assert.Contains("--ink:#E8E6E1", dark);
        Assert.Equal(1, Count(dark, "<div class=\"paper\">"));   // only the newsletter
        // The plain message's frame gets light text; the newsletter's frame keeps the light-theme text colour.
        Assert.Contains(System.Net.WebUtility.HtmlEncode("color:#D9D6D0"), dark);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("color:#23282E"), dark);

        var light = HtmlRenderer.BuildConversation("S", new[] { plain, coloured }, allowRemote: false, DateTimeOffset.Now).Html;
        Assert.Contains("--page:#FFFFFF", light);
        Assert.DoesNotContain("<div class=\"paper\">", light);
        Assert.DoesNotContain("#181C20", light);
    }

    [Fact]
    public void Placeholder_and_loading_page_follow_the_theme()
    {
        Assert.Contains("#181C20", HtmlRenderer.Placeholder("T", "D", dark: true));
        Assert.DoesNotContain("#181C20", HtmlRenderer.Placeholder("T", "D"));
        Assert.Contains("#E8E6E1", HtmlRenderer.LoadingBody("S", "Asha", "Mon", dark: true));
        Assert.DoesNotContain("#E8E6E1", HtmlRenderer.LoadingBody("S", "Asha", "Mon"));
    }

    private static int Count(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }
}
