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

    public Account Clone() => (Account)MemberwiseClone();
}
