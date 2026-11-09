using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

/// <summary>
/// The CW1 pieces main's compose window did not have (PR #137 after "main wins", Krishna 9 Nov 2026): Settings → General →
/// Writing emails and the per-account "Replies go to".
/// </summary>
public sealed class Release1710Tests
{
    private static Account Acc(string id, string email, string replyTo = "") => new() { Id = id, Email = email, ReplyTo = replyTo };

    [Fact]
    public void An_older_settings_json_keeps_todays_compose_behaviour()
    {
        var path = Path.Combine(Path.GetTempPath(), "magpie-1710-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, """{ "Accounts": [ { "Id": "A", "Email": "me@example.com" } ] }""");
            var s = new SettingsStore(path).Load();
            Assert.True(s.Compose.CheckSpelling);
            Assert.True(s.Compose.FoldQuotedText);
            Assert.True(s.Compose.AttachmentCheck);
            Assert.False(s.Compose.AlwaysReadReceipt);
            Assert.Equal(PastedPictureChoice.Ask, s.Compose.PastedPicture);
            Assert.Equal("", s.Accounts[0].ReplyTo);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Writing_email_settings_and_replies_go_to_round_trip()
    {
        var s = new AppSettings
        {
            Compose = new ComposeDefaults { CheckSpelling = false, FoldQuotedText = false, AttachmentCheck = false, AlwaysReadReceipt = true, PastedPicture = PastedPictureChoice.Attach },
            Accounts = new() { Acc("A", "me@example.com", "team@example.com") },
        };
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, AppSettings.Json), AppSettings.Json)!;
        SettingsStore.Normalise(back);
        Assert.False(back.Compose.CheckSpelling);
        Assert.False(back.Compose.FoldQuotedText);
        Assert.False(back.Compose.AttachmentCheck);
        Assert.True(back.Compose.AlwaysReadReceipt);
        Assert.Equal(PastedPictureChoice.Attach, back.Compose.PastedPicture);
        Assert.Equal("team@example.com", back.Accounts[0].ReplyTo);
        Assert.Equal("team@example.com", back.Accounts[0].Clone().ReplyTo);
    }

    [Fact]
    public void Normalise_mends_a_hand_edited_file()
    {
        var s = new AppSettings
        {
            Compose = new ComposeDefaults { PastedPicture = (PastedPictureChoice)42 },
            Accounts = new() { Acc("A", "a@example.com", "not an address"), Acc("B", "b@example.com", "x@example.com, y@example.com"), Acc("C", "c@example.com", "  ok@example.com ") },
        };
        SettingsStore.Normalise(s);
        Assert.Equal(PastedPictureChoice.Ask, s.Compose.PastedPicture);
        Assert.Equal("", s.Accounts[0].ReplyTo);
        Assert.Equal("", s.Accounts[1].ReplyTo);
        Assert.Equal("ok@example.com", s.Accounts[2].ReplyTo);

        var missing = new AppSettings { Compose = null! };
        SettingsStore.Normalise(missing);
        Assert.NotNull(missing.Compose);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("team@example.com", "")]
    [InlineData("Team <team@example.com>", "")]
    [InlineData("team", "This isn't an email address.")]
    [InlineData("a@example.com, b@example.com", "Use one address only.")]
    public void Replies_go_to_takes_exactly_one_address(string text, string problem)
        => Assert.Equal(problem, ComposeDefaults.ReplyToProblem(text));

    [Theory]
    [InlineData(ComposeMode.New)]
    [InlineData(ComposeMode.Reply)]
    [InlineData(ComposeMode.ReplyAll)]
    [InlineData(ComposeMode.Forward)]
    public void A_fresh_message_gets_the_accounts_reply_to_and_the_read_receipt(ComposeMode mode)
    {
        var d = new Draft { AccountId = "A", Mode = mode };
        ComposeDefaults.ApplyToNew(d, Acc("A", "me@example.com", "team@example.com"), new ComposeDefaults { AlwaysReadReceipt = true });
        Assert.Equal("team@example.com", d.ReplyTo);
        Assert.True(d.RequestReadReceipt);
    }

    [Fact]
    public void A_reopened_draft_and_an_existing_reply_to_are_left_alone()
    {
        var reopened = new Draft { AccountId = "A", Mode = ComposeMode.EditDraft };
        ComposeDefaults.ApplyToNew(reopened, Acc("A", "me@example.com", "team@example.com"), new ComposeDefaults { AlwaysReadReceipt = true });
        Assert.Equal("", reopened.ReplyTo);
        Assert.False(reopened.RequestReadReceipt);

        var mailto = new Draft { AccountId = "A", ReplyTo = "chosen@example.com" };
        ComposeDefaults.ApplyToNew(mailto, Acc("A", "me@example.com", "team@example.com"), new ComposeDefaults());
        Assert.Equal("chosen@example.com", mailto.ReplyTo);
        Assert.False(mailto.RequestReadReceipt);

        var noDefault = new Draft { AccountId = "A" };
        ComposeDefaults.ApplyToNew(noDefault, Acc("A", "me@example.com"), null);
        Assert.Equal("", noDefault.ReplyTo);
    }

    [Theory]
    [InlineData("team@a.com", "team@a.com", "", "")]                 // the old default goes with the old account
    [InlineData("team@a.com", "team@a.com", "desk@b.com", "desk@b.com")]
    [InlineData("TEAM@A.COM", "team@a.com", "desk@b.com", "desk@b.com")]
    [InlineData("", "team@a.com", "desk@b.com", "desk@b.com")]       // empty takes the new account's
    [InlineData("typed@x.com", "team@a.com", "desk@b.com", "typed@x.com")]   // typed by hand: kept
    public void Changing_from_moves_only_a_default_reply_to(string current, string oldDefault, string newDefault, string expected)
        => Assert.Equal(expected, ComposeDefaults.ReplyToAfterFromChange(current, Acc("A", "a@a.com", oldDefault), Acc("B", "b@b.com", newDefault)));

    [Fact]
    public void A_clone_is_independent()
    {
        var a = new ComposeDefaults { AlwaysReadReceipt = true };
        var b = a.Clone();
        b.AlwaysReadReceipt = false;
        Assert.True(a.AlwaysReadReceipt);
    }
}
