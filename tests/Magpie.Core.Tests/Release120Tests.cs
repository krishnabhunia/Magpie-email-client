using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

/// <summary>1.2.0: dark theme (B1), rules (B5), Gatekeeper + set aside (B7).</summary>
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

    // ───────────── B5 rules ─────────────

    private static MailRule Rule(bool all, params (RuleField f, RuleOp o, string v)[] conds) => new()
    {
        Name = "r", MatchAll = all,
        Conditions = conds.Select(c => new RuleCondition { Field = c.f, Op = c.o, Value = c.v }).ToList(),
        Actions = { new RuleAction { Kind = RuleActionKind.MarkRead } },
    };

    [Fact]
    public void Rule_conditions_match_from_to_subject_body_attachment_category_and_account()
    {
        var m = Rows.Make("A", 1, "t", from: "orders@amazon.in", subject: "Your order has shipped", preview: "Track your parcel here");
        m.FromName = "Amazon.in";
        m.To = "Krishna <me@test.local>";
        m.Cc = "team@work.com";
        m.HasAttachments = true;
        m.Category = Category.Notifications;
        var ctx = new RuleContext("me@test.local", "");

        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.From, RuleOp.Contains, "AMAZON")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.From, RuleOp.Is, "orders@amazon.in")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.From, RuleOp.Is, "Amazon.in")), m, ctx));        // the name counts too
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.From, RuleOp.EndsWith, "@amazon.in")), m, ctx));
        Assert.False(RuleEngine.Matches(Rule(true, (RuleField.From, RuleOp.DoesNotContain, "amazon")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.ToCc, RuleOp.Is, "team@work.com")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.ToCc, RuleOp.Contains, "krishna")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.Subject, RuleOp.StartsWith, "your order")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.Body, RuleOp.Contains, "parcel")), m, ctx));     // preview when no body yet
        Assert.False(RuleEngine.Matches(Rule(true, (RuleField.Body, RuleOp.Contains, "parcel")), m, ctx with { BodyText = "Nothing here" }));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.HasAttachment, RuleOp.Is, "Yes")), m, ctx));
        Assert.False(RuleEngine.Matches(Rule(true, (RuleField.HasAttachment, RuleOp.Is, "No")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.Category, RuleOp.Is, "Notifications")), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.Category, RuleOp.DoesNotContain, "People")), m, ctx));   // "isn't"
        Assert.True(RuleEngine.Matches(Rule(true, (RuleField.Account, RuleOp.Is, "ME@test.local")), m, ctx));

        // all vs any
        var both = new[] { (RuleField.From, RuleOp.Contains, "amazon"), (RuleField.Subject, RuleOp.Contains, "invoice") };
        Assert.False(RuleEngine.Matches(Rule(true, both), m, ctx));
        Assert.True(RuleEngine.Matches(Rule(false, both), m, ctx));
        // empty text never matches (a half-written rule does nothing)
        Assert.False(RuleEngine.Matches(Rule(true, (RuleField.Subject, RuleOp.Contains, "  ")), m, ctx));
    }

    [Fact]
    public void Only_complete_enabled_rules_run_and_old_or_broken_settings_load()
    {
        Assert.True(RuleEngine.IsRunnable(Rule(true, (RuleField.From, RuleOp.Contains, "x"))));
        Assert.False(RuleEngine.IsRunnable(Rule(true)));                                             // no conditions
        Assert.False(RuleEngine.IsRunnable(Rule(true, (RuleField.From, RuleOp.Contains, ""))));      // empty value
        Assert.True(RuleEngine.IsRunnable(Rule(true, (RuleField.HasAttachment, RuleOp.Is, ""))));   // needs no text
        var off = Rule(true, (RuleField.From, RuleOp.Contains, "x")); off.Enabled = false;
        Assert.False(RuleEngine.IsRunnable(off));
        var noAction = Rule(true, (RuleField.From, RuleOp.Contains, "x")); noAction.Actions.Clear();
        Assert.False(RuleEngine.IsRunnable(noAction));

        // 1.1.2 settings have no Rules; a hand-edited file may have nulls, repeats and unknown values.
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""
            {"Rules":[{"Id":"a","Name":"One","Conditions":null,"Actions":[{"Kind":"Tag","Target":"Work"},{"Kind":99}]},
                      {"Id":"a","Name":"Two","Conditions":[{"Field":"From","Op":"Contains","Value":null},{"Field":42}]}, null]}
            """, AppSettings.Json)!;
        SettingsStore.Normalise(s);
        Assert.Equal(2, s.Rules.Count);
        Assert.NotEqual(s.Rules[0].Id, s.Rules[1].Id);
        Assert.Empty(s.Rules[0].Conditions);
        Assert.Single(s.Rules[0].Actions);
        Assert.Equal("", Assert.Single(s.Rules[1].Conditions).Value);
        Assert.Empty(new AppSettings().Rules);
    }

    private static MailEngine Engine(TempDir dir, out long inbox, out long receipts, out long trash)
    {
        var e = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
        e.Settings.Current.Accounts.Add(new Account { Id = "A", Email = "me@test.local", Enabled = false });
        inbox = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        receipts = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Receipts", Name = "Receipts" });
        trash = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Trash", Name = "Trash", Role = FolderRole.Trash });
        return e;
    }

    [Fact]
    public void Rules_run_top_to_bottom_and_a_moved_message_is_not_seen_by_later_rules()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out var receipts, out var trash);
        var rows = e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", from: "orders@amazon.in", subject: "Order shipped"),
            Rows.Make("A", inbox, "t2", from: "news@paper.com", subject: "Daily digest"),
            Rows.Make("A", inbox, "t3", from: "friend@x.com", subject: "Lunch?"),
        });
        e.Config.Rules = new()
        {
            new MailRule { Name = "Receipts", Conditions = { new() { Field = RuleField.From, Op = RuleOp.Contains, Value = "amazon" } },
                Actions = { new() { Kind = RuleActionKind.Tag, Target = "Shopping" }, new() { Kind = RuleActionKind.MoveToFolder, Target = "Receipts" } } },
            new MailRule { Name = "Everything read", Conditions = { new() { Field = RuleField.Subject, Op = RuleOp.Contains, Value = "d" } },   // "shipped", "digest"
                Actions = { new() { Kind = RuleActionKind.MarkRead }, new() { Kind = RuleActionKind.SkipNotification } } },
            new MailRule { Name = "Off", Enabled = false, Conditions = { new() { Field = RuleField.From, Op = RuleOp.Contains, Value = "friend" } },
                Actions = { new() { Kind = RuleActionKind.Delete } } },
        };

        var quiet = e.RunRules("A", rows);

        var inInbox = e.Store.GetMessagesIn(new[] { inbox });
        Assert.DoesNotContain(inInbox, m => m.FromAddress == "orders@amazon.in");            // moved (locally at once)
        var ops = e.Store.GetPendingOps("A");
        Assert.Contains(ops, o => o.Kind == PendingOpKind.Move && o.Arg == receipts);          // and queued for the server
        var digest = Assert.Single(inInbox, m => m.FromAddress == "news@paper.com");
        Assert.True(digest.IsSeen);
        Assert.Contains(ops, o => o.Kind == PendingOpKind.SetSeen);
        Assert.False(inInbox.Single(m => m.FromAddress == "friend@x.com").IsSeen);           // disabled rule did nothing
        Assert.DoesNotContain(ops, o => o.Kind == PendingOpKind.Move && o.Arg == trash);
        // The Amazon mail was moved by rule 1, so rule 2 never marked it read; both stay quiet, the friend is announced.
        Assert.Contains(rows[0].Id, quiet);
        Assert.Contains(rows[1].Id, quiet);
        Assert.DoesNotContain(rows[2].Id, quiet);
        Assert.DoesNotContain(ops, o => o.Kind == PendingOpKind.SetSeen && o.Uid == rows[0].Uid);
    }

    [Fact]
    public void Rule_can_be_previewed_and_applied_to_mail_already_in_the_inbox()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var trash);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", from: "promo@shop.com"),
            Rows.Make("A", inbox, "t2", from: "promo@shop.com", flags: MessageFlags.Seen),
            Rows.Make("A", inbox, "t3", from: "boss@work.com"),
        });
        var rule = new MailRule { Name = "Promos", Conditions = { new() { Field = RuleField.From, Op = RuleOp.EndsWith, Value = "@shop.com" } },
            Actions = { new() { Kind = RuleActionKind.Delete } } };
        Assert.Equal(2, e.RuleMatchesInInbox(rule).Count);
        Assert.Equal(3, e.Store.GetMessagesIn(new[] { inbox }).Count);                      // preview changes nothing

        Assert.Equal(2, e.ApplyRuleToInbox(rule));
        Assert.Equal("boss@work.com", Assert.Single(e.Store.GetMessagesIn(new[] { inbox })).FromAddress);
        Assert.Equal(2, e.Store.GetPendingOps("A").Count(o => o.Kind == PendingOpKind.Move && o.Arg == trash));
    }

    // ───────────── B7 set aside ─────────────

    [Fact]
    public void Set_aside_leaves_the_inbox_without_a_date_and_comes_back_on_top()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t1", date: now.AddDays(-3)),
            Rows.Make("A", inbox, "t2", date: now.AddHours(-1)),
            Rows.Make("A", inbox, "t3", date: now.AddHours(-2)),
        });
        e.Snooze("A", "t3", now.AddDays(1));
        e.SetAside("A", "t1", true);

        var ids = new[] { inbox };
        Assert.Equal(new[] { "t2" }, e.Store.ListThreads(new ListQuery { FolderIds = ids }, now).Select(t => t.ThreadKey));
        var aside = Assert.Single(e.Store.ListThreads(new ListQuery { FolderIds = ids, SetAside = true }, now));
        Assert.Equal("t1", aside.ThreadKey);
        Assert.True(aside.IsSetAside);
        Assert.False(aside.IsSnoozed(now));
        Assert.Equal(new[] { "t3" }, e.Store.ListThreads(new ListQuery { FolderIds = ids, Snoozed = true }, now).Select(t => t.ThreadKey));
        Assert.Equal(1, e.Store.CountSetAsideThreads());
        Assert.Equal(1, e.Store.CountSnoozedThreads(now));
        Assert.Equal((1, 1), e.Store.CountThreads(ids, now));                                  // the Inbox count leaves both out
        Assert.DoesNotContain(e.Store.WakeDueSnoozes(now.AddYears(50)), r => r.ThreadKey == "t1");   // never wakes (t3 does)

        e.SetAside("A", "t1", false);
        var order = e.Store.ListThreads(new ListQuery { FolderIds = ids }, DateTimeOffset.Now.AddSeconds(1)).Select(t => t.ThreadKey).ToList();
        Assert.True(order.IndexOf("t1") >= 0 && order.IndexOf("t1") < order.IndexOf("t2"));   // back, above newer mail it was older than
        Assert.Equal(0, e.Store.CountSetAsideThreads());
    }

    [Fact]
    public void Gatekeeper_keeps_new_senders_at_the_door_and_allow_or_block_decides()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        var junk = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Spam", Name = "Spam", Role = FolderRole.Junk });
        var allMail = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var now = DateTimeOffset.Now;
        e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "old", from: "friend@x.com", date: now.AddDays(-30), flags: MessageFlags.Seen) });
        e.Config.Gatekeeper.Enabled = true;
        e.Config.Gatekeeper.Allowed.Add("ok@new.com");

        const string mid = "promo-1@shop.com";
        e.Store.InsertMessages(new[] { Rows.Make("A", allMail, "t-shop", from: "promo@shop.com", messageId: mid) });   // Gmail's All Mail copy
        var arrived = e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "t-shop", from: "promo@shop.com", messageId: mid),
            Rows.Make("A", inbox, "t-shop2", from: "Promo@Shop.com"),
            Rows.Make("A", inbox, "t-friend", from: "friend@x.com"),
            Rows.Make("A", inbox, "t-ok", from: "ok@new.com"),
            Rows.Make("A", inbox, "t-me", from: "me@test.local"),
        });

        var kept = e.RunGatekeeper("A", arrived);
        Assert.Equal(new[] { arrived[0].Id, arrived[1].Id }, kept.OrderBy(x => x));    // its own All Mail copy doesn't make promo@ known
        var inboxKeys = e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, now).Select(t => t.ThreadKey).ToHashSet();
        Assert.DoesNotContain("t-shop", inboxKeys);
        Assert.Contains("t-friend", inboxKeys);
        Assert.Contains("t-ok", inboxKeys);
        Assert.Contains("t-me", inboxKeys);
        Assert.Empty(e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, Snoozed = true }, now));
        Assert.Empty(e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, SetAside = true }, now));
        Assert.Equal(0, e.Store.CountSnoozedThreads(now));
        var waiting = Assert.Single(e.GateSenders());
        Assert.Equal("promo@shop.com", waiting.Address);
        Assert.Equal(2, waiting.Count);

        e.AllowSender("promo@shop.com");
        Assert.Contains("promo@shop.com", e.Config.Gatekeeper.Allowed);
        Assert.Empty(e.GateSenders());
        Assert.Contains("t-shop", e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now).Select(t => t.ThreadKey));

        // Blocking: waiting mail goes to Spam, and future mail follows even with the Gatekeeper off.
        var spam1 = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t-spam", from: "spam@bad.com") });
        e.RunGatekeeper("A", spam1);
        e.BlockSender("spam@bad.com");
        Assert.Empty(e.GateSenders());
        Assert.Contains(e.Store.GetPendingOps("A"), o => o.Kind == PendingOpKind.Move && o.Arg == junk && o.Uid == spam1[0].Uid);
        e.Config.Gatekeeper.Enabled = false;
        var spam2 = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t-spam2", from: "spam@bad.com") });
        Assert.Contains(spam2[0].Id, e.RunGatekeeper("A", spam2));
        Assert.Contains(e.Store.GetPendingOps("A"), o => o.Kind == PendingOpKind.Move && o.Arg == junk && o.Uid == spam2[0].Uid);
        // Off: new senders come straight in.
        var fresh = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t-fresh", from: "someone@else.com") });
        Assert.Empty(e.RunGatekeeper("A", fresh));
    }

    [Fact]
    public void Switching_the_gatekeeper_off_lets_everyone_waiting_in()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        e.Config.Gatekeeper.Enabled = true;
        var rows = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "a@new.com"), Rows.Make("A", inbox, "t2", from: "b@new.com") });
        e.RunGatekeeper("A", rows);
        Assert.Equal(2, e.GateSenders().Count);
        e.OpenGate();
        Assert.Empty(e.GateSenders());
        Assert.Equal(2, e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now).Count);
        Assert.Empty(e.Config.Gatekeeper.Allowed);                                             // not marked allowed

        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"Gatekeeper":{"Allowed":["X@Y.com","x@y.com",null,"nope"],"Blocked":["x@y.com"]}}""", AppSettings.Json)!;
        SettingsStore.Normalise(s);
        Assert.Empty(s.Gatekeeper.Allowed);                                                    // blocked wins
        Assert.Equal(new[] { "x@y.com" }, s.Gatekeeper.Blocked);
        Assert.False(new AppSettings().Gatekeeper.Enabled);
    }

    private static int Count(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }
}
