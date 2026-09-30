using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Magpie.Core.Storage;

namespace Magpie.Core.Tests;

/// <summary>1.2.0: dark theme (B1), invites (B3), rules (B5), signatures + quick replies (B6), Gatekeeper + set aside (B7), auto-delete (AD1–AD4).</summary>
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

    // ───────────── B6 signatures + quick replies ─────────────

    [Fact]
    public void Plain_signature_moves_to_the_rich_one_once_and_each_switch_is_honoured()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"Accounts":[{"Id":"a","Email":"k@x.com","Signature":"Krishna\nMagpie & co"}]}""", AppSettings.Json)!;
        SettingsStore.Normalise(s);
        var a = s.Accounts[0];
        Assert.Equal("-- <br>Krishna<br>Magpie &amp; co", a.SignatureHtml);
        Assert.True(a.SignatureOnNew);
        Assert.True(a.SignatureOnReplies);
        var block = Composer.SignatureHtml(a, reply: false);
        Assert.StartsWith("<div class=\"magpie-signature\">", block);
        Assert.Contains("Krishna<br>Magpie &amp; co", block);

        a.SignatureOnNew = false;
        Assert.Equal("", Composer.SignatureHtml(a, reply: false));
        Assert.NotEqual("", Composer.SignatureHtml(a, reply: true));
        a.SignatureOnReplies = false;
        Assert.Equal("", Composer.SignatureHtml(a, reply: true));

        // Rich signature: scripts are removed, a pasted logo (data: URI) stays and is sent embedded (cid).
        var rich = new Account { Id = "b", Email = "k@x.com", SignatureHtml = "<b>Krishna</b><script>alert(1)</script><img src=\"data:image/png;base64,iVBORw0KGgo=\">" };
        var sig = Composer.SignatureHtml(rich, reply: false);
        Assert.DoesNotContain("<script", sig);
        Assert.Contains("data:image/png", sig);
        var msg = Composer.Build(new Draft { AccountId = "b", To = "x@y.com", Subject = "Hi", Html = "<p>Hello</p>" + sig }, rich);
        var html = msg.HtmlBody;
        Assert.Contains("cid:", html);
        Assert.DoesNotContain("data:image/png", html);
        Assert.Contains(msg.BodyParts, p => !string.IsNullOrEmpty(p.ContentId));

        // An empty rich signature adds nothing (and doesn't come back from the old text once cleared).
        // (2.2.0: an account that never had the default signature gets it once — SignatureDefaultApplied says it did.)
        Assert.Equal("", Composer.SignatureHtml(new Account { SignatureHtml = "<p><br></p>" }, reply: false));
        var cleared = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"Accounts":[{"Id":"a","Signature":"","SignatureHtml":"","SignatureDefaultApplied":true}]}""", AppSettings.Json)!;
        SettingsStore.Normalise(cleared);
        Assert.Equal("", cleared.Accounts[0].SignatureHtml);
    }

    [Fact]
    public void Quick_replies_default_normalise_and_build_a_reply_above_signature_and_quote()
    {
        Assert.Equal(new[] { "Thanks!", "Got it, will do.", "Sounds good to me." }, new AppSettings().QuickReplies);
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"QuickReplies":["  Thanks! ","", null,"Thanks!","On it"]}""", AppSettings.Json)!;
        SettingsStore.Normalise(s);
        Assert.Equal(new[] { "Thanks!", "On it" }, s.QuickReplies);

        var acc = new Account { Id = "A", Email = "me@test.local", SignatureHtml = "Krishna" };
        var o = Rows.Make("A", 1, "t", from: "asha@x.com", subject: "Lunch?");
        o.MessageId = "m1@x.com";
        var d = Composer.QuickReply(acc, o, new MessageBody { Html = "<p>Free at 1?</p>" }, new[] { "me@test.local" }, "Sounds good to me.");
        Assert.StartsWith("<p>Sounds good to me.</p><div class=\"magpie-signature\">", d.Html);
        Assert.Contains("Free at 1?", d.Html);
        Assert.Contains("asha@x.com", d.To);
        Assert.Equal("Re: Lunch?", d.Subject);
        Assert.Equal("<m1@x.com>", d.InReplyTo);
    }

    [Fact]
    public async Task Quick_reply_goes_through_the_outbox_with_the_undo_window()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        e.Config.UndoSendSeconds = 10;
        var row = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "asha@x.com", subject: "Lunch?") })[0];
        e.Store.SaveBody(row.Id, new MessageBody { Html = "<p>Free at 1?</p>", Text = "Free at 1?" });
        var before = DateTimeOffset.Now;
        var id = await e.QuickReplyAsync(e.Store.GetMessage(row.Id)!, "Thanks!");
        var item = Assert.Single(e.Outbox());
        Assert.Equal(id, item.Id);
        Assert.Equal("Re: Lunch?", item.Subject);
        Assert.InRange(item.SendAt, before.AddSeconds(9), DateTimeOffset.Now.AddSeconds(11));   // Undo still possible
        Assert.NotNull(e.Recall(id));                                                                 // and it works
        Assert.DoesNotContain(e.Outbox(), o => o.Status == OutboxStatus.Queued);
    }

    // ───────────── AD1–AD4 auto-delete / OTP delete ─────────────

    [Theory]
    [InlineData("Codes@Bank.com", "codes@bank.com")]
    [InlineData(" *@XYZ.com ", "*@xyz.com")]
    [InlineData("@xyz.com", "*@xyz.com")]
    [InlineData("xyz.com", null)]
    [InlineData("a*b@xyz.com", null)]
    [InlineData("*@*.com", null)]
    [InlineData("a@b", null)]
    [InlineData("a @b.com", null)]
    public void Auto_delete_patterns_are_an_address_or_everyone_at_a_domain(string input, string? expected) =>
        Assert.Equal(expected, AutoDelete.NormalisePattern(input));

    [Fact]
    public void Auto_delete_matching_dates_tags_and_colours()
    {
        Assert.True(AutoDelete.Matches("*@xyz.com", "Alerts@XYZ.com"));
        Assert.False(AutoDelete.Matches("*@xyz.com", "a@notxyz.com.au"));
        Assert.True(AutoDelete.Matches("codes@bank.com", "CODES@bank.com"));
        Assert.False(AutoDelete.Matches("codes@bank.com", "other@bank.com"));
        Assert.Equal("*@xyz.com", AutoDelete.DomainPattern("news@XYZ.com"));

        var t = new DateTimeOffset(2026, 9, 27, 11, 20, 0, TimeSpan.Zero);
        Assert.Equal(t.AddHours(24), AutoDelete.DeleteAt(new AutoDeleteRule { Otp = true, Amount = 30, Unit = DeleteUnit.Years }, t));
        Assert.Equal(t.AddDays(7), AutoDelete.DeleteAt(new AutoDeleteRule { Amount = 7 }, t));
        Assert.Equal(t.AddMonths(3), AutoDelete.DeleteAt(new AutoDeleteRule { Amount = 3, Unit = DeleteUnit.Months }, t));
        Assert.Equal(t.AddYears(10), AutoDelete.DeleteAt(new AutoDeleteRule { Amount = 10, Unit = DeleteUnit.Years }, t));
        Assert.Equal("OTP · 24 hours after arrival", AutoDelete.Describe(new AutoDeleteRule { Otp = true }));
        Assert.Equal("1 month after arrival", AutoDelete.Describe(new AutoDeleteRule { Amount = 1, Unit = DeleteUnit.Months }));

        var now = DateTimeOffset.Now;
        Assert.Equal("OTP · deletes in 23 h 54 m", AutoDelete.TagText(now.AddHours(23).AddMinutes(54).AddSeconds(30), now, otp: true));
        Assert.Equal("Deletes tomorrow", AutoDelete.TagText(new DateTimeOffset(now.LocalDateTime.Date.AddDays(1).AddHours(12)), now, otp: false));
        Assert.Equal("Deletes 20 Sep 2036", AutoDelete.TagText(new DateTimeOffset(new DateTime(2036, 9, 20, 12, 0, 0)), now, otp: false));
        Assert.Equal(DeleteUrgency.Soon, AutoDelete.Urgency(now.AddDays(20), now, otp: true));      // every OTP is red
        Assert.Equal(DeleteUrgency.Soon, AutoDelete.Urgency(now.AddHours(47), now, otp: false));
        Assert.Equal(DeleteUrgency.Weeks, AutoDelete.Urgency(now.AddDays(6), now, otp: false));
        Assert.Equal(DeleteUrgency.Later, AutoDelete.Urgency(now.AddDays(45), now, otp: false));
        Assert.EndsWith("(in 6 days).", AutoDelete.BarText(now.AddDays(6).AddMinutes(1), now));
        Assert.Equal(17, AutoDelete.Choices.Length);
    }

    [Fact]
    public void Store_from_1_1_2_gets_the_auto_delete_columns()
    {
        using var dir = new TempDir();
        var path = dir.File("mail.db");
        _ = new MailStore(path);
        using (var c = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + path))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP INDEX ix_msg_delete; ALTER TABLE messages DROP COLUMN delete_at; ALTER TABLE messages DROP COLUMN delete_rule; DROP TABLE auto_delete_rules; PRAGMA user_version=4;";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var s = new MailStore(path);
        var inbox = s.UpsertFolder(new MailFolder { AccountId = "A", Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        var row = s.InsertMessages(new[] { Rows.Make("A", inbox, "t") })[0];
        s.SaveAutoDeleteRule(new AutoDeleteRule { Id = "r1", Pattern = "anita@x.com" });
        Assert.Equal(1, s.SetDeleteTimers(new[] { (row.Id, DateTimeOffset.Now.AddDays(1)) }, "r1"));
        Assert.Single(s.GetAutoDeleteRules());
    }

    [Fact]
    public void Auto_delete_tags_new_mail_moves_due_mail_to_trash_and_respects_pin_pause_and_keep()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var trash);
        var now = DateTimeOffset.Now;
        e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "codes@bank.com", Otp = true }, startOnExisting: false);
        e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "*@shop.com", Amount = 7 }, startOnExisting: false);
        e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "deals@shop.com", Amount = 1 }, startOnExisting: false);   // earlier than the domain rule
        var otpRule = e.AutoDeleteRules().Single(r => r.Otp);

        var arrived = e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "otp", from: "codes@bank.com", date: now.AddHours(-1)),
            Rows.Make("A", inbox, "news", from: "news@shop.com", date: now),
            Rows.Make("A", inbox, "deals", from: "deals@shop.com", date: now),
            Rows.Make("A", inbox, "pinned", from: "codes@bank.com", date: now, flags: MessageFlags.Flagged),
            Rows.Make("A", inbox, "friend", from: "friend@x.com", date: now),
        });
        e.TagArrivals("A", arrived);

        var list = e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, now).ToDictionary(t => t.ThreadKey);
        Near(now.AddHours(23).ToUnixTimeMilliseconds(), list["otp"].DeleteAt!.Value);
        Assert.Equal(otpRule.Id, list["otp"].DeleteRule);
        Near(now.AddDays(7).ToUnixTimeMilliseconds(), list["news"].DeleteAt!.Value);
        Near(now.AddDays(1).ToUnixTimeMilliseconds(), list["deals"].DeleteAt!.Value);
        Assert.Null(list["pinned"].DeleteAt);                                                   // pinned mail is never auto-deleted
        Assert.Null(list["friend"].DeleteAt);
        Assert.Equal(2, e.Store.CountDeletingSoon(now.AddDays(1).AddMinutes(1)));             // otp + deals
        Assert.Equal(new[] { "deals", "otp" }, e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, DeletingBefore = now.AddDays(1).AddMinutes(1) }, now)
            .Select(t => t.ThreadKey).OrderBy(k => k));

        // Keep this one: the timer comes off and a later rule run can't put it back.
        e.KeepFromAutoDelete("A", "news");
        Assert.Empty(e.Store.DeleteTimers("A", "news"));
        e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "news@shop.com", Amount = 3 }, startOnExisting: true);
        Assert.Empty(e.Store.DeleteTimers("A", "news"));

        // Paused rules delete nothing; due mail goes to Trash (queued) once resumed.
        e.PauseAutoDeleteRule(otpRule.Id, true);
        Assert.Equal(1, e.RunDueDeletes(now.AddDays(1).AddMinutes(1)));                        // only "deals"
        e.PauseAutoDeleteRule(otpRule.Id, false);
        Assert.Equal(1, e.RunDueDeletes(now.AddDays(1).AddMinutes(1)));                        // now "otp"
        var left = e.Store.GetMessagesIn(new[] { inbox }).Select(m => m.ThreadKey).ToHashSet();
        Assert.DoesNotContain("otp", left);
        Assert.DoesNotContain("deals", left);
        Assert.Contains("pinned", left);
        Assert.Equal(2, e.Store.GetPendingOps("A").Count(o => o.Kind == PendingOpKind.Move && o.Arg == trash));
    }

    [Fact]
    public void Existing_mail_is_opt_in_and_removing_a_rule_can_keep_or_clear_its_timers()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        e.Store.InsertMessages(new[]
        {
            Rows.Make("A", inbox, "a1", from: "alerts@xyz.com", date: DateTimeOffset.Now.AddYears(-2)),
            Rows.Make("A", inbox, "a2", from: "promo@xyz.com"),
            Rows.Make("A", inbox, "a3", from: "promo@xyz.com", flags: MessageFlags.Flagged),
        });
        var rule = new AutoDeleteRule { Pattern = "*@XYZ.com", Amount = 7 };
        Assert.Equal(2, e.ExistingFor(rule).Count);                                           // pinned one left out
        Assert.Throws<ArgumentException>(() => e.SaveAutoDeleteRule(new AutoDeleteRule { Pattern = "xyz" }, false));

        Assert.Equal(0, e.SaveAutoDeleteRule(rule, startOnExisting: false));
        Assert.Equal(0, e.Store.CountDeletingSoon(DateTimeOffset.Now.AddYears(1)));           // existing mail untouched by default
        Assert.Equal(2, e.SaveAutoDeleteRule(rule, startOnExisting: true));
        var t = e.Store.DeleteTimers("A", "a1").Single();
        Assert.True(t.At > DateTimeOffset.Now.AddDays(6));                                     // counted from now, not from 2 years ago
        Assert.Equal((2, t.At), (e.Store.AutoDeleteStats()[rule.Id].Waiting, e.Store.AutoDeleteStats()[rule.Id].Next!.Value));

        e.RemoveAutoDeleteRule(rule.Id, clearTimers: false);
        Assert.Empty(e.AutoDeleteRules());
        Assert.Equal(2, e.Store.CountDeletingSoon(DateTimeOffset.Now.AddYears(1)));           // timers kept
        e.SaveAutoDeleteRule(rule, startOnExisting: false);
        e.RemoveAutoDeleteRule(rule.Id, clearTimers: true);
        Assert.Equal(0, e.Store.CountDeletingSoon(DateTimeOffset.Now.AddYears(1)));

        e.SaveAutoDeleteRule(rule, startOnExisting: true);
        e.KeepAllDeletingSoon();                                                               // 7 days out → all kept
        Assert.Equal(0, e.Store.CountDeletingSoon(DateTimeOffset.Now.AddYears(1)));
    }

    // ───────────── B3 meeting invites ─────────────

    private const string GoogleInvite =
        "BEGIN:VCALENDAR\r\nPRODID:-//Google Inc//Google Calendar 70.9054//EN\r\nVERSION:2.0\r\nMETHOD:REQUEST\r\n" +
        "BEGIN:VTIMEZONE\r\nTZID:Asia/Kolkata\r\nBEGIN:STANDARD\r\nTZOFFSETFROM:+0530\r\nTZOFFSETTO:+0530\r\nDTSTART:19700101T000000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n" +
        "BEGIN:VEVENT\r\nDTSTART;TZID=Asia/Kolkata:20261006T140000\r\nDTEND;TZID=Asia/Kolkata:20261006T150000\r\n" +
        "DTSTAMP:20260929T101500Z\r\nORGANIZER;CN=Asha Rao:mailto:asha@x.com\r\nUID:abc123@google.com\r\n" +
        "ATTENDEE;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=NEEDS-ACTION;RSVP=\r\n TRUE;CN=me@test.local;X-NUM-GUESTS=0:mailto:me@test.local\r\n" +
        "ATTENDEE;CUTYPE=INDIVIDUAL;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;CN=Asha Rao:mailto:asha@x.com\r\n" +
        "X-GOOGLE-CONFERENCE:https://meet.google.com/abc-defg-hij\r\nDESCRIPTION:Plan for Q4\\, budget\\nJoin: https://meet.google.com/abc-defg-hij\r\n" +
        "LOCATION:Room 4\\, 2nd floor\r\nSEQUENCE:2\r\nSTATUS:CONFIRMED\r\nSUMMARY:Quarterly review\r\n" +
        "BEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:This is an event reminder\r\nTRIGGER:-P0DT0H10M0S\r\nEND:VALARM\r\n" +
        "END:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Invites_are_parsed_from_google_outlook_and_all_day_calendars()
    {
        var inv = Invites.Parse(GoogleInvite)!;
        Assert.True(inv.IsRequest);
        Assert.Equal("abc123@google.com", inv.Uid);
        Assert.Equal(2, inv.Sequence);
        Assert.Equal("Quarterly review", inv.Summary);                                        // not the VALARM's description
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 8, 30, 0, TimeSpan.Zero), inv.Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(1), inv.End - inv.Start);
        Assert.Equal("Room 4, 2nd floor", inv.Location);
        Assert.Equal("https://meet.google.com/abc-defg-hij", inv.MeetLink);
        Assert.Equal(("Asha Rao", "asha@x.com"), (inv.Organizer!.Name, inv.Organizer.Email));
        var me = Assert.Single(inv.Attendees, a => a.Email == "me@test.local");                 // folded line joined back
        Assert.Equal("NEEDS-ACTION", me.PartStat);

        var utc = Invites.Parse("BEGIN:VCALENDAR\nMETHOD:REQUEST\nBEGIN:VEVENT\nUID:u1\nDTSTART:20261006T090000Z\nDURATION:PT45M\nSUMMARY:Stand-up\nORGANIZER:mailto:boss@work.com\nEND:VEVENT\nEND:VCALENDAR")!;
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero), utc.Start);
        Assert.Equal(TimeSpan.FromMinutes(45), utc.End - utc.Start);
        var allDay = Invites.Parse("BEGIN:VCALENDAR\nMETHOD:CANCEL\nBEGIN:VEVENT\nUID:u2\nDTSTART;VALUE=DATE:20261010\nSUMMARY:Offsite\nEND:VEVENT\nEND:VCALENDAR")!;
        Assert.True(allDay.AllDay);
        Assert.True(allDay.IsCancel);
        Assert.Equal(TimeSpan.FromDays(1), allDay.End - allDay.Start);
        Assert.EndsWith("all day", Invites.WhenText(allDay));
        Assert.Null(Invites.Parse("BEGIN:VCALENDAR\nEND:VCALENDAR"));
        Assert.Null(Invites.Parse(""));
    }

    [Fact]
    public void Reply_is_an_itip_reply_with_the_answer_and_folded_lines()
    {
        var inv = Invites.Parse(GoogleInvite)!;
        var ics = Invites.BuildReply(inv, "me@test.local", "Krishna Dipayan Bhunia", InviteAnswer.Accepted, "Will join from the car, might be 5 minutes late because of traffic on the way", DateTimeOffset.Now);
        Assert.Contains("METHOD:REPLY\r\n", ics);
        Assert.Contains("UID:abc123@google.com\r\n", ics);
        Assert.Contains("SEQUENCE:2\r\n", ics);
        Assert.Contains("DTSTART;TZID=Asia/Kolkata:20261006T140000\r\n", ics);
        Assert.Contains("ORGANIZER;CN=Asha Rao:mailto:asha@x.com\r\n", ics);
        var unfolded = ics.Replace("\r\n ", "");
        Assert.Contains("ATTENDEE;PARTSTAT=ACCEPTED;CN=\"Krishna Dipayan Bhunia\":mailto:me@test.local", unfolded);
        Assert.Contains("COMMENT:Will join from the car\\, might be 5 minutes late", unfolded);
        Assert.All(ics.Split("\r\n"), l => Assert.True(System.Text.Encoding.UTF8.GetByteCount(l) <= 75, l));
        Assert.Equal("Declined: Quarterly review", Invites.SubjectFor(InviteAnswer.Declined, inv.Summary));
        Assert.Equal(inv.Uid, Invites.Parse(ics)!.Uid);                                       // our own reply reads back
    }

    [Fact]
    public void Calendar_part_is_kept_with_the_body()
    {
        var msg = new MimeKit.MimeMessage();
        var cal = new MimeKit.TextPart("calendar") { Text = GoogleInvite };
        cal.ContentType.Parameters.Add("method", "REQUEST");
        msg.Body = new MimeKit.Multipart("alternative") { new MimeKit.TextPart("plain") { Text = "You're invited" }, cal };
        var body = MimeText.Extract(msg);
        Assert.Contains("UID:abc123@google.com", body.Calendar);

        using var dir = new TempDir();
        var (s, inbox, _) = Rows.NewStore(dir);
        var row = s.InsertMessages(new[] { Rows.Make("A", inbox, "t") })[0];
        s.SaveBody(row.Id, body);
        Assert.Equal(body.Calendar, s.GetBody(row.Id)!.Calendar);
    }

    [Fact]
    public void Answering_an_invite_sends_the_reply_remembers_it_and_undo_takes_it_back()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        e.Config.UndoSendSeconds = 10;
        var row = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "asha@x.com", subject: "Invitation: Quarterly review") })[0];
        var inv = Invites.Parse(GoogleInvite)!;

        var (id, before) = e.AnswerInvite(row, inv, InviteAnswer.Tentative, null);
        Assert.Null(before);
        var item = Assert.Single(e.Outbox());
        Assert.Equal("Tentative: Quarterly review", item.Subject);
        var mime = MimeKit.MimeMessage.Load(new MemoryStream(item.Mime));
        Assert.Equal("asha@x.com", mime.To.Mailboxes.Single().Address);
        var part = mime.BodyParts.OfType<MimeKit.TextPart>().Single(p => p.ContentType.IsMimeType("text", "calendar"));
        Assert.Equal("REPLY", part.ContentType.Parameters["method"]);
        Assert.Contains("PARTSTAT=TENTATIVE", part.Text.Replace("\r\n ", ""));
        Assert.Equal("TENTATIVE", e.Store.GetEvent("A", inv.Uid)!.Answer);

        Assert.True(e.UndoInviteAnswer(id, "A", inv.Uid, before));
        Assert.DoesNotContain(e.Outbox(), o => o.Status == OutboxStatus.Queued);
        Assert.Equal("", e.Store.GetEvent("A", inv.Uid)!.Answer);

        // Accepted events show up as clashes; an update that moves the time asks again; a cancel marks it cancelled.
        e.AnswerInvite(row, inv, InviteAnswer.Accepted, "See you there");
        Assert.Single(e.Store.EventsOverlapping(inv.Start.AddMinutes(30), inv.End.AddHours(1), "other"));
        Assert.Empty(e.Store.EventsOverlapping(inv.End, inv.End.AddHours(1), "other"));
        var moved = Invites.Parse(GoogleInvite.Replace("SEQUENCE:2", "SEQUENCE:3").Replace("T140000", "T160000"))!;
        Assert.Equal("UPDATED", e.TrackInvite("A", moved)!.Answer);
        var cancel = Invites.Parse(GoogleInvite.Replace("METHOD:REQUEST", "METHOD:CANCEL").Replace("SEQUENCE:2", "SEQUENCE:4"))!;
        Assert.True(e.TrackInvite("A", cancel)!.Cancelled);
        Assert.Empty(e.Store.EventsOverlapping(moved.Start, moved.End, "other"));
    }

    // ───────────── fixes from the branch review ─────────────

    [Fact]
    public void Gatekeeper_holds_later_mail_while_the_first_still_waits_and_spam_doesnt_count_as_known()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out var trash);
        var allMail = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "[Gmail]/All Mail", Name = "All Mail", Role = FolderRole.All });
        var junk = e.Store.UpsertFolder(new MailFolder { AccountId = "A", Path = "Spam", Name = "Spam", Role = FolderRole.Junk });
        e.Config.Gatekeeper.Enabled = true;

        var first = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "new@shop.com", messageId: "m1@shop.com") });
        e.RunGatekeeper("A", first);
        e.Store.InsertMessages(new[] { Rows.Make("A", allMail, "t1", from: "new@shop.com", messageId: "m1@shop.com") });   // its All Mail copy syncs later
        var second = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t2", from: "new@shop.com", messageId: "m2@shop.com") });
        Assert.Contains(second[0].Id, e.RunGatekeeper("A", second));                          // still waits
        Assert.Equal(2, Assert.Single(e.GateSenders()).Count);

        e.Store.InsertMessages(new[] { Rows.Make("A", junk, "s1", from: "spammer@x.com"), Rows.Make("A", trash, "s2", from: "binned@x.com") });
        var fromSpam = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "s3", from: "spammer@x.com"), Rows.Make("A", inbox, "s4", from: "binned@x.com") });
        Assert.Equal(2, e.RunGatekeeper("A", fromSpam).Count);                                  // mail in Spam / Trash isn't "heard from"
    }

    [Fact]
    public void Blocked_sender_waits_at_the_door_when_there_is_no_spam_folder_and_allowed_mail_meets_the_rules()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);                                // no Spam folder
        e.Config.Gatekeeper.Blocked.Add("bad@x.com");
        var rows = e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "bad@x.com") });
        Assert.Contains(rows[0].Id, e.RunGatekeeper("A", rows));
        Assert.DoesNotContain("t1", e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now).Select(t => t.ThreadKey));

        // Allowed later: the waiting mail comes in and the rules run on it then.
        e.Config.Rules.Add(new MailRule { Name = "Tag", Conditions = { new() { Field = RuleField.From, Op = RuleOp.Is, Value = "bad@x.com" } },
            Actions = { new() { Kind = RuleActionKind.Tag, Target = "Checked" } } });
        e.AllowSender("bad@x.com");
        var t = Assert.Single(e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox } }, DateTimeOffset.Now));
        Assert.Contains("Checked", t.Latest.Tags);
    }

    private static string Event(string lines) => "BEGIN:VCALENDAR\nMETHOD:REQUEST\nBEGIN:VEVENT\nUID:x\n" + lines + "\nEND:VEVENT\nEND:VCALENDAR";

    [Theory]
    [InlineData("DTSTART:2026-10-06T11:00:00Z")]
    [InlineData("DTSTART:20261006T1100Z")]
    [InlineData("DTSTART;VALUE=DATE:2026")]
    [InlineData("DTSTART:20261006T110000Z\nDTEND:tomorrow")]
    public void A_broken_invite_is_ignored_not_fatal(string lines) => Assert.Null(Invites.Parse(Event(lines)));

    [Fact]
    public void An_absurd_duration_doesnt_throw() =>
        Assert.NotNull(Invites.Parse(Event("DTSTART:20261006T110000Z\nDURATION:PT99999999999H")));

    [Fact]
    public void Applying_a_switched_off_rule_to_the_inbox_does_what_it_says()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "promo@shop.com") });
        var rule = new MailRule { Name = "Off", Enabled = false, Conditions = { new() { Field = RuleField.From, Op = RuleOp.Contains, Value = "shop" } },
            Actions = { new() { Kind = RuleActionKind.MarkRead } } };
        Assert.Equal(1, e.ApplyRuleToInbox(rule));
        Assert.True(Assert.Single(e.Store.GetMessagesIn(new[] { inbox })).IsSeen);
        Assert.False(rule.Enabled);                                                             // the rule itself stays off
    }

    [Fact]
    public void Deleting_soon_leaves_out_paused_rules_and_legacy_signature_matches_the_editor()
    {
        using var dir = new TempDir();
        using var e = Engine(dir, out var inbox, out _, out _);
        var rule = new AutoDeleteRule { Pattern = "otp@bank.com", Otp = true };
        e.SaveAutoDeleteRule(rule, false);
        e.TagArrivals("A", e.Store.InsertMessages(new[] { Rows.Make("A", inbox, "t1", from: "otp@bank.com") }));
        var week = DateTimeOffset.Now.AddDays(7);
        Assert.Equal(1, e.Store.CountDeletingSoon(week));
        e.PauseAutoDeleteRule(rule.Id, true);
        Assert.Equal(0, e.Store.CountDeletingSoon(week));
        Assert.Empty(e.Store.ListThreads(new ListQuery { FolderIds = new[] { inbox }, DeletingBefore = week }, DateTimeOffset.Now));
        Assert.Empty(e.Store.RowsDeletingBefore(week));

        Assert.Equal("-- <br>Krishna's café &amp; co", Composer.LegacySignatureHtml("Krishna's café & co"));
    }

    // ───────────── Z1 portable mode, A1 auto update ─────────────

    [Fact]
    public void Portable_txt_next_to_the_exe_keeps_everything_in_MagpieData()
    {
        using var dir = new TempDir();
        var normal = AppPaths.For(dir.Path);
        Assert.False(normal.IsPortable);
        Assert.EndsWith(Path.Combine("Magpie"), normal.Root);                                  // %APPDATA%\Magpie as before
        Assert.DoesNotContain(dir.Path, normal.Root);

        File.WriteAllText(Path.Combine(dir.Path, AppPaths.PortableMarker), "portable");
        var p = AppPaths.For(dir.Path);
        Assert.True(p.IsPortable);
        var data = Path.Combine(dir.Path, "MagpieData");
        Assert.Equal(data, p.Root);
        Assert.Equal(Path.Combine(data, "mail.db"), p.Database);
        Assert.Equal(Path.Combine(data, "settings.json"), p.Settings);
        Assert.Equal(Path.Combine(data, "secrets.json"), p.Secrets);
        Assert.StartsWith(data, p.WebView2Data);
        Assert.StartsWith(data, p.RenderCache);
        Assert.StartsWith(data, p.Updates);
        Assert.True(Directory.Exists(p.MimeCache));
    }

    [Theory]
    [InlineData("""{"Updates":{}}""", true)]                                               // 1.1.x defaults
    [InlineData("""{"Updates":{"AutoCheck":false,"AutoDownload":true}}""", false)]
    [InlineData("""{"Updates":{"AutoCheck":true,"AutoDownload":false}}""", false)]         // wanted to be asked → not automatic
    [InlineData("""{"Updates":{"AutoUpdate":true,"AutoCheck":false}}""", true)]            // 1.2.0 setting wins
    public void Auto_update_setting_comes_from_the_old_switches_once_and_mirrors_them(string json, bool expected)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!;
        SettingsStore.Normalise(s);
        Assert.Equal(expected, s.Updates.AutoUpdate);
        Assert.Equal(expected, s.Updates.AutoCheck);                                          // an older Magpie reads the same choice
        Assert.Equal(expected, s.Updates.AutoDownload);
        Assert.True(new AppSettings { Updates = new UpdateSettings() }.Updates.AutoCheck);
    }

    [Fact]
    public void Background_install_note_is_written_read_and_cleared()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, "updates");
        Assert.Null(Magpie.Core.Updates.InstalledUpdate.Read(folder));
        Magpie.Core.Updates.InstalledUpdate.Write(folder, "1.2.0", "1.2.1");
        Assert.Equal(new Magpie.Core.Updates.InstalledUpdate("1.2.0", "1.2.1"), Magpie.Core.Updates.InstalledUpdate.Read(folder));
        Magpie.Core.Updates.InstalledUpdate.Clear(folder);
        Assert.Null(Magpie.Core.Updates.InstalledUpdate.Read(folder));
        File.WriteAllText(Path.Combine(folder, Magpie.Core.Updates.InstalledUpdate.FileName), "{not json");
        Assert.Null(Magpie.Core.Updates.InstalledUpdate.Read(folder));                        // a broken note never stops start-up
    }

    private static void Near(long expectedMs, DateTimeOffset actual) =>
        Assert.InRange(actual.ToUnixTimeMilliseconds(), expectedMs - 2000, expectedMs + 2000);

    private static int Count(string s, string what)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(what, i, StringComparison.Ordinal)) >= 0) { n++; i += what.Length; }
        return n;
    }
}
