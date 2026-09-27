using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;
using MimeKit;

namespace Magpie.Core.Tests;

public class ThreadingTests
{
    [Fact]
    public void Reply_and_parent_share_key_in_either_order()
    {
        var keys = new Dictionary<string, string>();
        string? Lookup(IEnumerable<string> ids) { foreach (var i in ids) if (keys.TryGetValue(i, out var k)) return k; return null; }

        // Reply arrives first (common when syncing Sent before Inbox)
        var reply = Threading.Compute("<b@x>", "<a@x>", Threading.ParseReferences("<a@x>"), null, Lookup, "1");
        keys["b@x"] = reply;
        var parent = Threading.Compute("<a@x>", "", new List<string>(), null, Lookup, "2");
        Assert.Equal(reply, parent);
        Assert.Equal("r:a@x", parent);
    }

    [Fact]
    public void Deep_chain_uses_root_and_gmail_thread_id_wins()
    {
        var k = Threading.Compute("<d@x>", "<c@x>", Threading.ParseReferences("<A@X> <b@x>\r\n <c@x>"), null, null, "1");
        Assert.Equal("r:a@x", k);
        Assert.Equal("gm:ff", Threading.Compute("<d@x>", "<c@x>", new List<string>(), 255UL, null, "1"));
        Assert.Equal("u:9", Threading.Compute("", "", new List<string>(), null, null, "9"));
    }

    [Theory]
    [InlineData("Re: Re: Fwd: Budget", "Budget")]
    [InlineData("AW: WG: Termin", "Termin")]
    [InlineData("RE[2]: Offer", "Offer")]
    [InlineData("Regarding the plan", "Regarding the plan")]
    public void Subject_prefixes_are_stripped(string input, string expected) => Assert.Equal(expected, Threading.StripSubjectPrefixes(input));

    [Fact]
    public void Reply_and_forward_subjects()
    {
        Assert.Equal("Re: Budget", Threading.ReplySubject("Budget"));
        Assert.Equal("RE: Budget", Threading.ReplySubject("RE: Budget"));
        Assert.Equal("Fwd: Budget", Threading.ForwardSubject("Re: Budget"));
    }
}

public class CategorizerTests
{
    [Fact]
    public void People_by_default_and_known_contacts_always_people()
    {
        Assert.Equal(Category.People, Categorizer.Classify(new CategorySignals("anita@vendor.com", "Anita")));
        Assert.Equal(Category.People, Categorizer.Classify(new CategorySignals("noreply@x.com", "", ListUnsubscribe: "<mailto:u@x>", FromKnownContact: true)));
    }

    [Theory]
    [InlineData("no-reply@accounts.google.com", "")]
    [InlineData("notifications@github.com", "")]
    [InlineData("alerts@hdfcbank.net", "")]
    [InlineData("orders@amazon.in", "")]
    public void Machine_senders_are_notifications(string from, string unsub) =>
        Assert.Equal(Category.Notifications, Categorizer.Classify(new CategorySignals(from, "", unsub)));

    [Fact]
    public void Auto_submitted_is_notification()
    {
        Assert.Equal(Category.Notifications, Categorizer.Classify(new CategorySignals("jira@corp.com", "Jira", AutoSubmitted: "auto-generated")));
        Assert.Equal(Category.Notifications, Categorizer.Classify(new CategorySignals("x@corp.com", "X", HasFeedbackId: true)));
    }

    [Fact]
    public void Lists_with_unsubscribe_are_newsletters_but_discussion_lists_are_people()
    {
        Assert.Equal(Category.Newsletters, Categorizer.Classify(new CategorySignals("hello@substack.com", "Weekly", ListUnsubscribe: "<https://x/unsub>")));
        Assert.Equal(Category.Newsletters, Categorizer.Classify(new CategorySignals("anna@brand.com", "Anna", XMailer: "Mailchimp Mailer")));
        Assert.Equal(Category.People, Categorizer.Classify(new CategorySignals("dev@lists.x.org", "Dev", ListId: "<dev.lists.x.org>")));
    }
}

public class SearchQueryTests
{
    [Fact]
    public void Parses_operators_phrases_and_words()
    {
        var q = SearchQuery.Parse("from:anita subject:\"q3 budget\" \"exact words\" invoice has:attachment is:unread is:pinned after:2026-08-01");
        Assert.Equal(new[] { "anita" }, q.From);
        Assert.Equal(new[] { "q3 budget" }, q.Subject);
        Assert.Equal(new[] { "exact words" }, q.Phrases);
        Assert.Equal(new[] { "invoice" }, q.Terms);
        Assert.True(q.HasAttachment && q.Unread && q.Pinned);
        Assert.Equal(new DateTime(2026, 8, 1), q.After!.Value.Date);
        Assert.Equal("\"invoice\"* AND \"exact words\"", q.FtsExpression());
    }

    [Fact]
    public void Hostile_input_cannot_break_fts_syntax()
    {
        var q = SearchQuery.Parse("NEAR( a\"b OR * ^col:");
        var e = q.FtsExpression();
        Assert.NotNull(e);
        Assert.DoesNotContain("(", e);
        Assert.True(SearchQuery.Parse("   ").IsEmpty);
    }
}

public class MimeContentTests
{
    [Fact]
    public void Strips_quoted_history()
    {
        var text = "Sounds good, Friday works.\n\nOn Tue, 3 Sep 2026 at 10:02, Anita Rao <a@b.com> wrote:\n> Can we move it?\n> Thanks";
        Assert.Equal("Sounds good, Friday works.", MimeText.StripQuoted(text));
        var outlook = "Approved.\r\n\r\n-----Original Message-----\r\nFrom: Bob\r\nSent: Monday";
        Assert.Equal("Approved.", MimeText.StripQuoted(outlook));
        var wrapped = "Yes.\nOn Tue, 3 Sep 2026 at 10:02, Anita Rao <a@b.com>\nwrote:\n> old";
        Assert.Equal("Yes.", MimeText.StripQuoted(wrapped));
    }

    [Fact]
    public void Preview_is_one_line_without_quotes()
    {
        var p = MimeText.Preview("Hi Anita,\n\nThursday   works.\n> quoted\nOn Mon, X wrote:\nold stuff");
        Assert.Equal("Hi Anita, Thursday works.", p);
        Assert.EndsWith("…", MimeText.Preview(new string('a', 400)));
    }

    [Fact]
    public void Html_to_text_keeps_paragraphs_and_drops_scripts()
    {
        var t = MimeText.HtmlToText("<html><head><style>p{}</style></head><body><p>Hello</p><p>World<br>again</p><script>x()</script></body></html>");
        Assert.Contains("Hello", t);
        Assert.Contains("World\nagain", t);
        Assert.DoesNotContain("x()", t);
        Assert.DoesNotContain("p{}", t);
    }

    [Fact]
    public void Extracts_body_and_attachments()
    {
        var b = new BodyBuilder { HtmlBody = "<p>Hi <img src=\"cid:logo\"></p>", TextBody = "Hi" };
        var img = b.LinkedResources.Add("logo.png", new byte[] { 137, 80, 78, 71 });
        img.ContentId = "logo";
        b.Attachments.Add("report.pdf", new byte[2048]);
        var msg = new MimeMessage { Body = b.ToMessageBody() };
        var body = MimeText.Extract(msg);
        Assert.Contains("Hi", body.Html);
        Assert.Equal("Hi", body.Text);
        Assert.Contains(body.Attachments, a => a.FileName == "report.pdf" && !a.Inline && a.Size > 1000);
        Assert.Contains(body.Attachments, a => a.Inline && a.ContentId == "logo");
        var images = MimeText.InlineImages(msg);
        Assert.StartsWith("data:image/png;base64,", images["logo"]);
        var pdf = body.Attachments.First(a => a.FileName == "report.pdf");
        Assert.IsType<MimePart>(MimeText.PartAt(msg, pdf.Index));
    }
}

public class HtmlRendererTests
{
    [Fact]
    public void Sanitizer_removes_scripts_handlers_and_forms()
    {
        var (html, _) = HtmlRenderer.SanitizeBody("<p onclick=\"evil()\">Hi</p><script>alert(1)</script><form action=x><input name=p></form><iframe src=x></iframe><a href=\"javascript:alert(1)\">x</a>", new Dictionary<string, string>(), false);
        Assert.Contains("Hi", html);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remote_images_blocked_and_counted_cid_resolved()
    {
        var src = "<img src=\"https://tracker.example/p.gif\"><img src='cid:logo'><div style=\"background:url('https://x/y.png')\">t</div>";
        var (blocked, n) = HtmlRenderer.SanitizeBody(src, new Dictionary<string, string> { ["logo"] = "data:image/png;base64,AAAA" }, false);
        Assert.Equal(2, n);
        Assert.DoesNotContain(" src=\"https://tracker.example", blocked);
        Assert.Contains("data-magpie-src", blocked);
        Assert.Contains("data:image/png;base64,AAAA", blocked);
        var (allowed, n2) = HtmlRenderer.SanitizeBody(src, new Dictionary<string, string>(), true);
        Assert.Equal(0, n2);
        Assert.Contains("https://tracker.example/p.gif", allowed);
    }

    [Fact]
    public void Conversation_page_puts_each_body_in_a_sandboxed_frame()
    {
        var row = Rows.Make("A", 1, "t", from: "anita@x.com", subject: "S");
        var page = HtmlRenderer.BuildConversation("S", new[]
        {
            new RenderMessage { Row = row, Body = new MessageBody { Html = "<b>Body</b><script>bad()</script>", Attachments = { new AttachmentInfo { FileName = "a.pdf", Index = 2, Size = 5000 } } }, Expanded = true },
        }, false, DateTimeOffset.Now);
        Assert.Contains("sandbox=\"allow-same-origin allow-popups allow-popups-to-escape-sandbox\"", page.Html);
        Assert.DoesNotContain("allow-scripts", page.Html);
        Assert.DoesNotContain("bad()", page.Html);
        Assert.Contains("a.pdf", page.Html);
        Assert.Contains("Content-Security-Policy", page.Html);
    }

    [Theory]
    [InlineData("Anita Rao", "AR")]
    [InlineData("\"sandeep.menon\"", "SM")]
    [InlineData("bob", "B")]
    [InlineData("123", "?")]
    public void Initials(string name, string expected) => Assert.Equal(expected, HtmlRenderer.Initials(name));
}

public class ComposerTests
{
    private static readonly string[] Me = { "me@test.local" };

    [Fact]
    public void Reply_all_excludes_me_and_duplicates()
    {
        var o = new MessageRow { FromName = "Anita", FromAddress = "anita@x.com", To = "me@test.local, bob@y.com", Cc = "Anita <anita@x.com>, carol@z.com, BOB@y.com" };
        var (to, cc) = Composer.ReplyRecipients(o, true, Me, false);
        Assert.Equal("\"Anita\" <anita@x.com>", to);
        Assert.Equal("bob@y.com, carol@z.com", cc);
        var (to1, cc1) = Composer.ReplyRecipients(o, false, Me, false);
        Assert.Equal("", cc1);
        Assert.Contains("anita@x.com", to1);
    }

    [Fact]
    public void Reply_uses_reply_to_and_replying_to_own_mail_keeps_recipients()
    {
        var o = new MessageRow { FromAddress = "news@x.com", ReplyTo = "desk@x.com", To = "me@test.local" };
        Assert.Equal("desk@x.com", Composer.ReplyRecipients(o, false, Me, false).to);
        var mine = new MessageRow { FromAddress = "me@test.local", To = "anita@x.com" };
        Assert.Equal("anita@x.com", Composer.ReplyRecipients(mine, false, Me, true).to);
    }

    [Fact]
    public void Built_reply_carries_threading_headers_and_roundtrips()
    {
        var acc = new Account { Email = "me@test.local", DisplayName = "Krishna", Signature = "Krishna\nMagpie" };
        var o = new MessageRow { FromName = "Anita", FromAddress = "anita@x.com", To = "me@test.local", Subject = "Budget", MessageId = "m2@x", References = "<m1@x>", ThreadKey = "r:m1@x", Date = DateTimeOffset.Now };
        var d = Composer.Prepare(ComposeMode.Reply, acc, o, new MessageBody { Html = "<p>Original <script>x</script></p>" }, Me, null);
        Assert.Equal("Re: Budget", d.Subject);
        Assert.Contains("magpie-signature", d.Html);
        Assert.DoesNotContain("<script", d.Html);
        d.Html = "<p>Thanks!</p>" + d.Html;
        var msg = Composer.Build(d, acc);
        Assert.Equal("m2@x", msg.InReplyTo);
        Assert.Equal(new[] { "m1@x", "m2@x" }, msg.References.ToArray());
        Assert.Equal("Krishna", ((MailboxAddress)msg.From[0]).Name);
        Assert.Contains("Thanks!", msg.TextBody);
        var back = Composer.FromMime(Composer.FromBytes(Composer.ToBytes(msg)), acc.Id, "r:m1@x");
        Assert.Equal("Re: Budget", back.Subject);
        Assert.Contains("Thanks!", back.Html);
        Assert.Equal("<m2@x>", back.InReplyTo);
    }

    [Fact]
    public void Forward_keeps_original_attachments()
    {
        var b = new BodyBuilder { TextBody = "see file" };
        b.Attachments.Add("plan.xlsx", new byte[100]);
        var orig = new MimeMessage { Subject = "Plan", Body = b.ToMessageBody() };
        var acc = new Account { Email = "me@test.local" };
        var row = new MessageRow { FromAddress = "a@x.com", Subject = "Plan", Date = DateTimeOffset.Now };
        var d = Composer.Prepare(ComposeMode.Forward, acc, row, new MessageBody { Text = "see file" }, Me, orig);
        Assert.Equal("Fwd: Plan", d.Subject);
        d.To = "bob@y.com";
        var msg = Composer.Build(d, acc);
        Assert.Contains(msg.Attachments, a => a is MimePart p && p.FileName == "plan.xlsx");
    }

    [Fact]
    public void Address_validation()
    {
        Assert.Empty(Composer.InvalidAddresses("Anita <anita@x.com>; bob@y.co.in\ncarol@z.com"));
        Assert.Equal(new[] { "not-an-address" }, Composer.InvalidAddresses("bob@y.com, not-an-address"));
        Assert.Equal(3, Composer.ParseAddresses("a@x.com; b@y.com\nc@z.com").Count);
    }
}

public class PresetTests
{
    [Fact]
    public void Known_domains_fill_servers()
    {
        var a = new Account { Email = "krishna@gmail.com" };
        Assert.True(ProviderPresets.Apply(a));
        Assert.Equal(AccountKind.Gmail, a.Kind);
        Assert.Equal("imap.gmail.com", a.ImapHost);
        Assert.True(a.ServerSavesSent);
        var o = new Account { Email = "k@outlook.com" };
        Assert.True(ProviderPresets.Apply(o));
        Assert.Equal(AccountKind.Microsoft, o.Kind);
        Assert.False(ProviderPresets.Apply(new Account { Email = "k@mycompany.in" }));
    }

    [Fact]
    public void Parses_thunderbird_autoconfig()
    {
        var xml = """
            <clientConfig version="1.1"><emailProvider id="example.org">
              <incomingServer type="pop3"><hostname>pop.example.org</hostname></incomingServer>
              <incomingServer type="imap"><hostname>imap.example.org</hostname><port>993</port><socketType>SSL</socketType><username>%EMAILLOCALPART%</username></incomingServer>
              <outgoingServer type="smtp"><hostname>smtp.example.org</hostname><port>587</port><socketType>STARTTLS</socketType><username>%EMAILADDRESS%</username></outgoingServer>
            </emailProvider></clientConfig>
            """;
        var a = new Account { Email = "k@example.org" };
        Assert.True(ProviderPresets.ParseAutoconfig(xml, a));
        Assert.Equal("imap.example.org", a.ImapHost);
        Assert.Equal(TlsMode.StartTls, a.SmtpSecurity);
        Assert.Equal(587, a.SmtpPort);
        Assert.Equal("k", a.UserName);
    }
}

public class TimePresetTests
{
    [Fact]
    public void Weekday_morning_presets()
    {
        var wed = new DateTime(2026, 9, 23, 9, 10, 0); // Wednesday
        var p = TimePresets.For(wed);
        Assert.Equal(new[] { "Later today", "This evening", "Tomorrow", "This weekend", "Next week" }, p.Select(x => x.Label));
        Assert.Equal(new DateTime(2026, 9, 23, 12, 0, 0), p[0].When.LocalDateTime);
        Assert.Equal(new DateTime(2026, 9, 26, 9, 0, 0), p.Single(x => x.Label == "This weekend").When.LocalDateTime);
        Assert.Equal(new DateTime(2026, 9, 28, 8, 0, 0), p.Single(x => x.Label == "Next week").When.LocalDateTime);
    }

    [Fact]
    public void Late_sunday_and_send_later_variants()
    {
        var sun = new DateTime(2026, 9, 27, 22, 0, 0);
        var p = TimePresets.For(sun);
        Assert.DoesNotContain(p, x => x.Label is "Later today" or "This evening" or "This weekend");
        Assert.Equal(new DateTime(2026, 10, 5, 8, 0, 0), p.Single(x => x.Label == "Next week").When.LocalDateTime); // not tomorrow's Monday
        var s = TimePresets.For(new DateTime(2026, 9, 23, 9, 0, 0), sendLater: true);
        Assert.Contains(s, x => x.Label == "Tomorrow afternoon");
        Assert.All(s, x => Assert.True(x.When > new DateTimeOffset(new DateTime(2026, 9, 23, 9, 0, 0))));
    }
}

public class ComposerImageTests
{
    [Fact]
    public void Pasted_data_images_become_inline_attachments()
    {
        var png = Convert.ToBase64String(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var d = new Draft { To = "a@x.com", Subject = "pic", Html = $"<p>see <img src=\"data:image/png;base64,{png}\"></p>" };
        var msg = Composer.Build(d, new Account { Email = "me@test.local" });
        Assert.DoesNotContain("data:image", msg.HtmlBody);
        Assert.Contains("cid:", msg.HtmlBody);
        var cid = System.Text.RegularExpressions.Regex.Match(msg.HtmlBody, "cid:([^\"]+)").Groups[1].Value;
        Assert.Contains(msg.BodyParts.OfType<MimePart>(), p => p.ContentId == cid && p.ContentType.MimeType == "image/png");
    }
}
