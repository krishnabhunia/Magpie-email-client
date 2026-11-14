using System.Globalization;
using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>
/// Design ID1: the IDs that point at one email or one conversation, and the few lines "Copy email reference" puts on
/// the clipboard. The email ID is the email's own Message-ID, the same in every mail app and with every provider;
/// the conversation ID is Gmail's thread ID and exists only for Gmail accounts.
/// </summary>
public static class EmailReference
{
    public const string SaidEmailId = "Copied the email ID";
    public const string SaidConversationId = "Copied the conversation ID";
    public const string SaidReference = "Copied the email reference";

    /// <summary>The Message-ID as it is written in an email: between angle brackets. "" when the email has none.</summary>
    public static string EmailId(string? messageId)
    {
        var id = (messageId ?? "").Trim().TrimStart('<').TrimEnd('>').Trim();
        return id.Length == 0 ? "" : $"<{id}>";
    }

    /// <summary>Gmail's thread ID (the conversation key Gmail gave, see <see cref="Threading"/>); "" for other accounts.</summary>
    public static string ConversationId(string? threadKey)
    {
        var key = (threadKey ?? "").Trim();
        return key.StartsWith("gm:", StringComparison.Ordinal) && key.Length > 3 ? key[3..] : "";
    }

    /// <summary>
    /// Subject, From, Date, Email ID, Gmail conversation ID and Kept in, one per line (joined with "\n"); a line
    /// without a value is left out.
    /// </summary>
    public static string Text(MessageRow row, string? keptIn = null)
    {
        var from = row.FromAddress.Length == 0 ? row.Sender
            : string.IsNullOrWhiteSpace(row.FromName) || row.FromName.Trim().Equals(row.FromAddress, StringComparison.OrdinalIgnoreCase) ? row.FromAddress
            : $"{row.FromName.Trim()} <{row.FromAddress}>";
        var lines = new List<string>();
        void Add(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value.Trim()}"); }
        Add("Subject", string.IsNullOrWhiteSpace(row.Subject) ? "(no subject)" : row.Subject);
        Add("From", from);
        Add("Date", DateLine(row.Date));
        Add("Email ID", EmailId(row.MessageId));
        Add("Gmail conversation ID", ConversationId(row.ThreadKey));
        Add("Kept in", keptIn);
        return string.Join("\n", lines);
    }

    /// <summary>"Fri, 13 Nov 2026 21:04 +0530" in this PC's time.</summary>
    internal static string DateLine(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.ToString("ddd, d MMM yyyy HH:mm ", CultureInfo.InvariantCulture) + local.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "");
    }
}
