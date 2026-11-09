using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Settings;

/// <summary>What happens with a picture pasted from the clipboard (design CW1, C4).</summary>
public enum PastedPictureChoice { Ask = 0, Inline = 1, Attach = 2 }

/// <summary>
/// Design CW1 (Q91), the parts main's compose window did not have yet: Settings → General → Writing emails, plus the
/// per-account "Replies go to" address. Every switch keeps today's behaviour by default, so an older settings.json
/// changes nothing.
/// </summary>
public sealed class ComposeDefaults
{
    /// <summary>Spelling starts on in a new compose window (Compose → More → Spellcheck still switches it per window).</summary>
    public bool CheckSpelling { get; set; } = true;
    /// <summary>Replies and forwards open with the quoted message folded (Windows; the expand button still opens it).</summary>
    public bool FoldQuotedText { get; set; } = true;
    /// <summary>Ask "Did you forget an attachment?" at Send when the new text mentions one and nothing is attached.</summary>
    public bool AttachmentCheck { get; set; } = true;
    /// <summary>Every new message, reply and forward starts with "Request a read receipt" ticked.</summary>
    public bool AlwaysReadReceipt { get; set; }
    /// <summary>Ask each time, put the picture in the message, or save and attach it as a PNG.</summary>
    public PastedPictureChoice PastedPicture { get; set; } = PastedPictureChoice.Ask;

    public void Normalise()
    {
        if (!Enum.IsDefined(PastedPicture)) PastedPicture = PastedPictureChoice.Ask;
    }

    public ComposeDefaults Clone() => (ComposeDefaults)MemberwiseClone();

    /// <summary>
    /// Fill a freshly started message (New, Reply, Reply all, Forward, mailto) from the account's "Replies go to" and the
    /// read-receipt switch. Never used for a reopened draft or outbox item: what was saved there stays as it was.
    /// </summary>
    public static void ApplyToNew(Draft draft, Account? account, ComposeDefaults? defaults)
    {
        if (draft == null || draft.Mode == ComposeMode.EditDraft) return;
        if (string.IsNullOrWhiteSpace(draft.ReplyTo) && account != null && account.ReplyTo.Length > 0)
            draft.ReplyTo = account.ReplyTo;
        if (defaults?.AlwaysReadReceipt == true) draft.RequestReadReceipt = true;
    }

    /// <summary>
    /// Changing From while writing: a Reply-To that is still the previous account's "Replies go to" follows the new
    /// account (its own address, or empty). A Reply-To typed by hand stays.
    /// </summary>
    public static string ReplyToAfterFromChange(string current, Account? previous, Account? next)
    {
        current = (current ?? "").Trim();
        var previousDefault = previous?.ReplyTo ?? "";
        if (current.Length > 0 && !string.Equals(current, previousDefault, StringComparison.OrdinalIgnoreCase)) return current;
        return next?.ReplyTo ?? "";
    }

    /// <summary>"" when <paramref name="text"/> is empty or one valid address; otherwise what is wrong, in plain words.</summary>
    public static string ReplyToProblem(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0) return "";
        if (Composer.InvalidAddresses(value).Count > 0) return "This isn't an email address.";
        if (!InternetAddressList.TryParse(value, out var list) || list.Mailboxes.Count() != 1) return "Use one address only.";
        return "";
    }

    /// <summary>The stored form: trimmed, or "" when it isn't exactly one valid address (an old or hand-edited settings.json).</summary>
    public static string CleanReplyTo(string? text) => ReplyToProblem(text).Length == 0 ? (text ?? "").Trim() : "";
}
