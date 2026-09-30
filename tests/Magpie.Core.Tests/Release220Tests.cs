using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Xunit;

namespace Magpie.Core.Tests;

/// <summary>2.2.0: reader loads from this PC (RL1), account details (AC1), default signature (SG1).</summary>
public class Release220Tests
{
    // ───────────── RL1: pictures inside an email are kept with its text ─────────────

    [Fact]
    public void Body_keeps_the_pictures_inside_the_email()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t1") }).Single();
        var body = new MessageBody
        {
            Html = "<img src=\"cid:logo\">", Text = "",
            Attachments = { new AttachmentInfo { Index = 3, FileName = "logo.png", Inline = true, ContentId = "logo" } },
            Images = { ["logo"] = "data:image/png;base64,AA==" },
        };
        store.SaveBody(row.Id, body);
        var back = store.GetBody(row.Id)!;
        Assert.Equal("data:image/png;base64,AA==", back.Images["logo"]);
        Assert.False(MimeText.NeedsDownload(back));   // nothing to read again when it is opened
    }

    [Fact]
    public void Loading_page_shows_only_while_an_email_of_the_conversation_is_not_here_yet()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var rows = store.InsertMessages(new[] { Rows.Make("A", inbox, "t1"), Rows.Make("A", inbox, "t1") });
        Assert.True(store.HasUncachedBody("A", "t1"));
        foreach (var r in rows) store.SaveBody(r.Id, new MessageBody { Html = "<p>hi</p>", Text = "hi" });
        Assert.False(store.HasUncachedBody("A", "t1"));
        Assert.False(store.HasUncachedBody("A", "no-such-thread"));
    }

    [Fact]
    public void Old_databases_get_the_pictures_column()
    {
        using var dir = new TempDir();
        var (store, inbox, _) = Rows.NewStore(dir);
        var row = store.InsertMessages(new[] { Rows.Make("A", inbox, "t1") }).Single();
        store.SaveBody(row.Id, new MessageBody { Html = "<p>x</p>", Text = "x" });   // no pictures: column stays empty
        Assert.Empty(store.GetBody(row.Id)!.Images);
    }

    // ───────────── AC1 + SG1: your details and the default signature ─────────────

    [Fact]
    public void Default_signature_is_thanks_and_regards_name_role_and_number()
    {
        var a = new Account { DisplayName = "Krishna Bhunia", Phone = "+91 98765 43210", JobTitle = "Senior Engineer", Company = "Acme <Pharma>" };
        var html = Composer.DefaultSignatureHtml(a);
        Assert.Equal("<p>Thanks and Regards<br><b>Krishna Bhunia</b><br>Senior Engineer · Acme &lt;Pharma&gt;<br>+91 98765 43210</p>", html);
        Assert.Equal("<p>Thanks and Regards</p>", Composer.DefaultSignatureHtml(new Account()));   // empty lines left out
    }

    [Fact]
    public void Default_signature_is_put_in_once_and_a_cleared_one_stays_clear()
    {
        var s = new AppSettings();
        s.Accounts.Add(new Account { Email = "me@x.com", DisplayName = "Me", Phone = "123" });
        SettingsStore.Normalise(s);
        var a = s.Accounts[0];
        Assert.Contains("Thanks and Regards", a.SignatureHtml);
        Assert.True(a.SignatureDefaultApplied);
        a.SignatureHtml = "";
        SettingsStore.Normalise(s);
        Assert.Equal("", a.SignatureHtml);   // cleared on purpose: not put back
        var had = new Account { Email = "old@x.com", Signature = "Old plain signature" };
        s.Accounts.Add(had);
        SettingsStore.Normalise(s);
        Assert.DoesNotContain("Thanks and Regards", had.SignatureHtml);   // an existing signature is never replaced (the old text moves over instead)
    }
}
