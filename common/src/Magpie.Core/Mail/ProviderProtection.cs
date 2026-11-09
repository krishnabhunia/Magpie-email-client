using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>Provider-managed expiry and revocation must be applied by the provider, not simulated with MIME headers.</summary>
public sealed record ProviderProtection(string Provider, string Explanation, string? Website, string? Help)
{
    public static ProviderProtection For(Account? account) => account?.Kind switch
    {
        AccountKind.Gmail => new("Gmail", "Gmail confidential mode must be selected in Gmail. It can offer expiry, access removal and passcodes. Magpie's SMTP connection cannot apply those controls. Open Gmail, verify the sending account and enable confidential mode before sending there. Your message remains in Magpie; opening the website does not copy or send it.",
            "https://mail.google.com/", "https://support.google.com/mail/answer/7674059"),
        AccountKind.Microsoft => new("Outlook", "Outlook's Encrypt / Do Not Forward controls depend on your Microsoft account, subscription and organisation's policies. Magpie's SMTP connection cannot apply that provider-managed protection. Open Outlook and verify the account and available controls before sending there. A Confidential sensitivity level alone does not prevent forwarding. Your message remains in Magpie; opening the website does not copy or send it.",
            IsPersonalMicrosoft(account.Email) ? "https://outlook.live.com/mail/" : "https://outlook.office.com/mail/",
            "https://support.microsoft.com/en-us/outlook/send-encrypted-messages-with-a-microsoft-365-personal-or-family-subscription"),
        _ => new("Your mail provider", "Provider-managed expiry or access removal is not available through this account's SMTP connection. Check your provider's own website for supported protection. Magpie cannot revoke a message already delivered to a recipient, and adding a Confidential header would not provide that protection.", null, null),
    };

    private static bool IsPersonalMicrosoft(string email) => ProviderPresets.Domain(email).ToLowerInvariant()
        is "outlook.com" or "hotmail.com" or "live.com" or "msn.com" or "outlook.in" or "hotmail.co.uk" or "live.co.uk";
}
