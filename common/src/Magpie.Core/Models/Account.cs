using System.Text.Json.Serialization;

namespace Magpie.Core.Models;

public enum AccountKind { Gmail, Microsoft, Imap }

public enum AuthMethod { Password, OAuth2 }

public enum TlsMode { SslOnConnect, StartTls, None }

/// <summary>A configured mailbox. Secrets (password / refresh token) live in the SecretVault, never here.</summary>
public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Your details for this account (design AC1): shown in the default signature; kept in backups.</summary>
    public string Phone { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public string Company { get; set; } = "";
    /// <summary>The default signature ("Thanks and Regards / name / number") was put in once; never again after that,
    /// so a signature you cleared on purpose stays clear.</summary>
    public bool SignatureDefaultApplied { get; set; }
    public AccountKind Kind { get; set; } = AccountKind.Imap;
    public AuthMethod Auth { get; set; } = AuthMethod.Password;

    public string UserName { get; set; } = "";
    public string ImapHost { get; set; } = "";
    public int ImapPort { get; set; } = 993;
    public TlsMode ImapSecurity { get; set; } = TlsMode.SslOnConnect;
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 465;
    public TlsMode SmtpSecurity { get; set; } = TlsMode.SslOnConnect;

    /// <summary>Colour dot used in the unified inbox (hex, e.g. #14606E).</summary>
    public string Color { get; set; } = "#14606E";
    /// <summary>Plain-text signature (up to 1.1.2). Kept in step with <see cref="SignatureHtml"/> as plain text, so an older
    /// Magpie still shows something sensible; 1.2.0 moves it into <see cref="SignatureHtml"/> once.</summary>
    public string Signature { get; set; } = "";
    /// <summary>Rich signature (design B6): HTML from the signature editor; pictures are data: URIs, sent embedded (cid).</summary>
    public string SignatureHtml { get; set; } = "";
    /// <summary>Design CW1 (D3): every signature of this account; the compose window's Signature ▾ menu lists them.
    /// <see cref="SignatureHtml"/> always mirrors the default one, so older versions and the accounts copy keep working.</summary>
    public List<SignatureDef> Signatures { get; set; } = new();
    /// <summary>Id of the signature put into new messages and replies (the first one when empty or unknown).</summary>
    public string DefaultSignatureId { get; set; } = "";
    /// <summary>Design CW1 (A5): "Replies go to" — put in the Reply-To row of every new message from this account ("" = none).</summary>
    public string ReplyTo { get; set; } = "";
    /// <summary>Design CW1 (C2): attachment limit shown in the compose window, MB. 0 = the usual limit for the kind of account
    /// (Gmail 25, Outlook 20, IMAP 25).</summary>
    public int AttachmentLimitMb { get; set; }
    /// <summary>Add the signature to new messages.</summary>
    public bool SignatureOnNew { get; set; } = true;
    /// <summary>Add the signature to replies and forwards (above the quoted text).</summary>
    public bool SignatureOnReplies { get; set; } = true;
    /// <summary>Design DS1: emails from the last N days are downloaded in the background (readable and searchable
    /// offline); older ones are listed and download when opened. 0 = everything.</summary>
    public int SyncDays { get; set; } = 90;
    /// <summary>Design DS1: background downloads include attachments. Off (default): text only; an email's
    /// attachments come down when it is opened.</summary>
    public bool DownloadAttachments { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Server stores sent mail itself (Gmail, Outlook) — do not IMAP-APPEND a copy.</summary>
    public bool ServerSavesSent { get; set; }

    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Email : $"{DisplayName} <{Email}>";

    /// <summary>The signature new messages start with: <see cref="DefaultSignatureId"/>, else the first one, else null.</summary>
    [JsonIgnore] public SignatureDef? DefaultSignature =>
        (Signatures ?? new()).FirstOrDefault(s => s.Id == DefaultSignatureId) ?? (Signatures ?? new()).FirstOrDefault();

    /// <summary>
    /// Old settings (one <see cref="SignatureHtml"/>) become the first signature; ids are filled in; the default id is checked;
    /// and <see cref="SignatureHtml"/> / <see cref="Signature"/> mirror the default signature for older versions.
    /// </summary>
    public void NormaliseSignatures()
    {
        Signatures = (Signatures ?? new()).Where(s => s != null).ToList();
        foreach (var s in Signatures)
        {
            if (string.IsNullOrWhiteSpace(s.Id)) s.Id = SignatureDef.NewId();
            s.Name = (s.Name ?? "").Trim();
            s.Html ??= "";
        }
        Signatures = Signatures.DistinctBy(s => s.Id).ToList();
        for (var i = 0; i < Signatures.Count; i++)
            if (Signatures[i].Name.Length == 0) Signatures[i].Name = i == 0 ? "Signature" : "Signature " + (i + 1);
        SignatureHtml ??= "";
        if (Signatures.Count == 0 && !string.IsNullOrWhiteSpace(SignatureHtml))
            Signatures.Add(new SignatureDef { Name = "Signature", Html = SignatureHtml });
        if (Signatures.Count > 0 && Signatures.All(s => s.Id != DefaultSignatureId)) DefaultSignatureId = Signatures[0].Id;
        if (Signatures.Count == 0) DefaultSignatureId = "";
        var def = DefaultSignature;
        if (def != null && def.Html != SignatureHtml)
        {
            SignatureHtml = def.Html;
            Signature = Mail.MimeText.HtmlToText(def.Html).Trim();
        }
        ReplyTo ??= "";
        AttachmentLimitMb = AttachmentLimitMb <= 0 ? 0 : Math.Clamp(AttachmentLimitMb, 1, 150);
    }

    public Account Clone()
    {
        var a = (Account)MemberwiseClone();
        a.Signatures = (Signatures ?? new()).Select(s => s.Clone()).ToList();
        return a;
    }
}

/// <summary>One signature of an account (design CW1, D3).</summary>
public sealed class SignatureDef
{
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = "";
    /// <summary>HTML from the signature editor; pictures are data: URIs, sent embedded (cid).</summary>
    public string Html { get; set; } = "";
    public SignatureDef Clone() => new() { Id = Id, Name = Name, Html = Html };
}
