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
    /// <summary>Plain-text signature appended to new messages (converted to HTML paragraphs).</summary>
    public string Signature { get; set; } = "";
    /// <summary>Initial sync window in days (older mail stays on the server and is reachable by server search later).</summary>
    public int SyncDays { get; set; } = 90;
    public bool Enabled { get; set; } = true;
    /// <summary>Server stores sent mail itself (Gmail, Outlook) — do not IMAP-APPEND a copy.</summary>
    public bool ServerSavesSent { get; set; }

    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Email : $"{DisplayName} <{Email}>";

    public Account Clone() => (Account)MemberwiseClone();
}
