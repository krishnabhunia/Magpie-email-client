using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.Core.Tests;

public class ProviderProtectionTests
{
    [Theory]
    [InlineData(AccountKind.Gmail, "person@gmail.com", "mail.google.com")]
    [InlineData(AccountKind.Microsoft, "person@outlook.com", "outlook.live.com")]
    [InlineData(AccountKind.Microsoft, "person@company.example", "outlook.office.com")]
    public void Known_provider_handoff_contains_no_message_recipient_or_account_data(AccountKind kind, string email, string host)
    {
        var protection = ProviderProtection.For(new Account { Kind = kind, Email = email });
        var website = new Uri(protection.Website!);
        Assert.Equal("https", website.Scheme);
        Assert.Equal(host, website.Host);
        Assert.Empty(website.Query);
        Assert.Empty(website.Fragment);
        Assert.DoesNotContain(email, protection.Website!);
        Assert.Contains("cannot apply", protection.Explanation);
        Assert.Contains("does not copy or send", protection.Explanation);
        Assert.NotNull(protection.Help);
        Assert.Equal("https", new Uri(protection.Help).Scheme);
    }

    [Fact]
    public void Unknown_provider_never_guesses_a_website_or_claims_expiry_and_revocation_support()
    {
        foreach (var account in new Account?[] { null, new() { Kind = AccountKind.Imap, SmtpHost = "smtp.custom.example" } })
        {
            var protection = ProviderProtection.For(account);
            Assert.Null(protection.Website);
            Assert.Null(protection.Help);
            Assert.Contains("not available", protection.Explanation);
            Assert.Contains("cannot revoke", protection.Explanation);
        }
    }

    [Fact]
    public void Displayed_delivery_status_escapes_server_response_and_invalidates_the_reader_cache()
    {
        var row = new MessageRow { Id = 1, AccountId = "sender", MessageId = "receipt@example.test", Date = DateTimeOffset.Now };
        var initial = new[] { new RenderMessage { Row = row, Expanded = true, Body = new MessageBody { Text = "Message" } } };
        var updated = new[]
        {
            new RenderMessage
            {
                Row = row, Expanded = true, Body = initial[0].Body,
                DeliveryReceipt = new DeliveryReceiptResult(DeliveryReceiptState.Unavailable, "2.0.0 <script>not code</script> \"quoted\""),
            },
        };
        var now = DateTimeOffset.UtcNow;
        Assert.NotEqual(ReaderPage.ContentFingerprint("Subject", initial, false, false, 250, now),
            ReaderPage.ContentFingerprint("Subject", updated, false, false, 250, now));
        var rendered = HtmlRenderer.BuildConversation("Subject", updated, false, now, false);
        Assert.Contains("Delivery notification unavailable", rendered.Html);
        Assert.Contains("SMTP response:", rendered.Html);
        Assert.DoesNotContain("<script>not code</script>", rendered.Html);
        Assert.Contains("&lt;script&gt;not code&lt;/script&gt;", rendered.Html);
    }
}
