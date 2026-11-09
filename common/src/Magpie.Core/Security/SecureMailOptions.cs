using MimeKit;

namespace Magpie.Core.Security;

public enum MailSecurityFormat { Smime, OpenPgp }

/// <summary>Message intent, not a claim that an editable draft has already been signed or encrypted.</summary>
public sealed class SecureMailOptions
{
    public MailSecurityFormat Format { get; set; } = MailSecurityFormat.Smime;
    public bool Sign { get; set; }
    public bool Encrypt { get; set; }
    public bool Invalid { get; set; }
    public bool Requested => Sign || Encrypt || Invalid;
    public SecureMailOptions Clone() => new() { Format = Format, Sign = Sign, Encrypt = Encrypt, Invalid = Invalid };
    public const string DraftHeader = "X-Magpie-Message-Security";

    public void WriteDraftHeader(MimeMessage message)
    {
        message.Headers.RemoveAll(DraftHeader);
        if (!Requested) return;
        if (Invalid || !Enum.IsDefined(Format)) throw new InvalidOperationException("Choose supported message security options before saving or sending.");
        message.Headers.Add(DraftHeader, (Format == MailSecurityFormat.Smime ? "smime" : "openpgp")
            + "; sign=" + (Sign ? "yes" : "no") + "; encrypt=" + (Encrypt ? "yes" : "no"));
    }

    public static SecureMailOptions FromDraft(MimeMessage message)
    {
        var headers = message.Headers.Where(h => h.Field.Equals(DraftHeader, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (headers.Length == 0) return new();
        if (headers.Length != 1) return new() { Invalid = true };
        var fields = headers[0].Value.Split(';', StringSplitOptions.TrimEntries);
        if (fields.Length != 3 || fields[0] is not ("smime" or "openpgp")
            || fields[1] is not ("sign=yes" or "sign=no") || fields[2] is not ("encrypt=yes" or "encrypt=no"))
            return new() { Invalid = true };
        return new() { Format = fields[0] == "smime" ? MailSecurityFormat.Smime : MailSecurityFormat.OpenPgp,
            Sign = fields[1] == "sign=yes", Encrypt = fields[2] == "encrypt=yes" };
    }
}

public sealed class MailSecurityException(string message) : InvalidOperationException(message);
