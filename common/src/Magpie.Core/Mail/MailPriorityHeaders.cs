using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>Sender priority metadata; it never changes the user's pin or read state.</summary>
internal static class MailPriorityHeaders
{
    internal static readonly string[] Names = ["Importance", "X-Priority", "X-MSMail-Priority"];
    internal const MessageFlags MetadataFlags = MessageFlags.HighPriority | MessageFlags.PriorityKnown;

    // Prefer the standard Importance header. A contradictory legacy header must not override an explicit Normal/Low.
    internal static string Read(HeaderList? headers) =>
        Named(headers?["Importance"]) ?? Numbered(headers?["X-Priority"]) ?? Named(headers?["X-MSMail-Priority"]) ?? "Normal";

    internal static MessageFlags Flags(HeaderList? headers) => headers == null ? MessageFlags.None
        : MessageFlags.PriorityKnown | (Read(headers) == "High" ? MessageFlags.HighPriority : MessageFlags.None);

    private static string? Named(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => "High", "normal" => "Normal", "low" => "Low", _ => null,
    };

    private static string? Numbered(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0 || text[0] is < '1' or > '5') return null;
        // Common values include "1 (Highest)". Never treat "10", "1urgent", or a malformed value as priority 1.
        var comment = text[1..].Trim();
        if (comment.Length > 0 && !(comment.StartsWith('(') && comment.EndsWith(')'))) return null;
        return text[0] switch { '1' or '2' => "High", '4' or '5' => "Low", _ => "Normal" };
    }
}
