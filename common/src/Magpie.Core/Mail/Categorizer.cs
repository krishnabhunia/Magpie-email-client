using System.Text.RegularExpressions;
using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>Headers the smart-inbox classifier looks at.</summary>
public sealed record CategorySignals(
    string FromAddress,
    string FromName,
    string ListUnsubscribe = "",
    string ListId = "",
    string Precedence = "",
    string AutoSubmitted = "",
    string XMailer = "",
    bool HasFeedbackId = false,
    bool HasCampaignHeaders = false,
    bool FromKnownContact = false);

/// <summary>
/// Spark-style Smart Inbox: People / Notifications / Newsletters, decided locally from headers only
/// (no content is sent anywhere). The user can move a sender and that choice wins (SenderOverrides).
/// </summary>
public static class Categorizer
{
    private static readonly Regex NoReply = new(@"(^|[._\-+])(no-?reply|do-?not-?reply|donotreply|notifications?|notify|alerts?|mailer-daemon|postmaster|bounce[s]?|automated|system|updates?|info|news|newsletter|marketing|hello|team|support|billing|receipts?|orders?)([._\-+]|@)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NewsletterLocal = new(@"(^|[._\-+])(news|newsletter|digest|marketing|promo|offers?|deals|weekly|mailchimp|campaign)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] BulkMailers = { "mailchimp", "sendgrid", "mailgun", "sendinblue", "brevo", "hubspot", "marketo", "klaviyo", "constant contact", "campaign monitor", "substack", "beehiiv", "convertkit" };

    public static Category Classify(CategorySignals s)
    {
        if (s.FromKnownContact) return Category.People;

        var local = s.FromAddress.Split('@')[0];
        var mailer = s.XMailer.ToLowerInvariant();
        bool bulkMailer = BulkMailers.Any(mailer.Contains);
        bool isList = s.ListUnsubscribe.Length > 0 || s.ListId.Length > 0;
        bool bulkPrecedence = s.Precedence.Equals("bulk", StringComparison.OrdinalIgnoreCase) || s.Precedence.Equals("list", StringComparison.OrdinalIgnoreCase);
        bool auto = s.AutoSubmitted.Length > 0 && !s.AutoSubmitted.Equals("no", StringComparison.OrdinalIgnoreCase);

        // Mailing lists with unsubscribe links, campaign tooling, or newsletter-ish senders → Newsletters.
        if (s.HasCampaignHeaders || bulkMailer) return Category.Newsletters;
        if (isList && (NewsletterLocal.IsMatch(local + "@") || bulkPrecedence || s.ListUnsubscribe.Length > 0) && !auto)
        {
            // Discussion lists (Google Groups, mailman) have List-Id but are conversations between people.
            if (s.ListId.Length > 0 && s.ListUnsubscribe.Length == 0 && !bulkPrecedence) return Category.People;
            return Category.Newsletters;
        }

        // Machine-generated: auto-submitted, no-reply style senders, feedback ids (transactional ESPs).
        if (auto || s.HasFeedbackId || NoReply.IsMatch(local + "@")) return Category.Notifications;
        // A list without unsubscribe/bulk markers is a discussion list: people talking.
        return Category.People;
    }
}
