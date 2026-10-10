using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Security;
using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>Opens authenticated IMAP/SMTP connections for an account (password or OAuth2).</summary>
public sealed class Connector
{
    private readonly SecretVault _vault;
    private readonly OAuthService _oauth;

    public Connector(SecretVault vault, OAuthService oauth)
    {
        _vault = vault;
        _oauth = oauth;
    }

    public static SecureSocketOptions Map(Models.TlsMode s) => s switch
    {
        Models.TlsMode.SslOnConnect => SecureSocketOptions.SslOnConnect,
        Models.TlsMode.StartTls => SecureSocketOptions.StartTls,
        _ => SecureSocketOptions.StartTlsWhenAvailable,
    };

    public async Task<ImapClient> OpenImapAsync(Account a, CancellationToken ct, string? passwordOverride = null)
    {
        var client = new ImapClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(a.ImapHost, a.ImapPort, Map(a.ImapSecurity), ct);
            await AuthenticateAsync(client, a, passwordOverride, ct);
            if (client.Capabilities.HasFlag(ImapCapabilities.Id))
            {
                try { await client.IdentifyAsync(new ImapImplementation { Name = "Magpie", Version = "1.0" }, ct); } catch { }
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task<SmtpClient> OpenSmtpAsync(Account a, CancellationToken ct, string? passwordOverride = null)
    {
        var client = new SmtpClient { Timeout = 90_000 };
        try
        {
            await client.ConnectAsync(a.SmtpHost, a.SmtpPort, Map(a.SmtpSecurity), ct);
            await AuthenticateAsync(client, a, passwordOverride, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private async Task AuthenticateAsync(MailService client, Account a, string? passwordOverride, CancellationToken ct)
    {
        var user = string.IsNullOrWhiteSpace(a.UserName) ? a.Email : a.UserName;
        if (a.Auth == AuthMethod.OAuth2)
        {
            var token = await _oauth.GetAccessTokenAsync(a, ct);
            try
            {
                await client.AuthenticateAsync(new SaslMechanismOAuth2(user, token), ct);
            }
            catch (AuthenticationException)
            {
                // Token may have been revoked server-side; force one refresh then fail with a re-auth prompt.
                _oauth.Forget(a.Id);
                token = await _oauth.GetAccessTokenAsync(a, ct);
                try { await client.AuthenticateAsync(new SaslMechanismOAuth2(user, token), ct); }
                catch (AuthenticationException ex) { throw new ReauthRequiredException("The server rejected the sign-in: " + ex.Message); }
            }
        }
        else
        {
            var pwd = passwordOverride ?? _vault.Get(SecretVault.PasswordKey(a.Id)) ?? "";
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            try { await client.AuthenticateAsync(user, pwd, ct); }
            catch (AuthenticationException ex)
            {
                var hint = a.Kind == AccountKind.Gmail || ProviderPresets.Domain(a.Email).Contains("yahoo")
                    ? " For Gmail/Yahoo use an app password (needs 2-Step Verification) or Google sign-in."
                    : "";
                throw new ReauthRequiredException("Wrong user name or password: " + ex.Message + hint);
            }
        }
    }

    /// <summary>Checks both servers; returns null when OK or a readable error.</summary>
    public async Task<string?> TestAsync(Account a, string? password, CancellationToken ct)
    {
        try
        {
            using (var imap = await OpenImapAsync(a, ct, password)) await imap.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Account test, incoming {a.ImapHost}:{a.ImapPort} {a.ImapSecurity}: {ex}");
            return "Incoming (IMAP): " + Friendly(ex);
        }
        try
        {
            using (var smtp = await OpenSmtpAsync(a, ct, password)) await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn($"Account test, outgoing {a.SmtpHost}:{a.SmtpPort} {a.SmtpSecurity}: {ex}");
            return "Outgoing (SMTP): " + Friendly(ex);
        }
        return null;
    }

    public async Task SendAsync(Account a, MimeMessage msg, CancellationToken ct)
    {
        using var smtp = await OpenSmtpAsync(a, ct);
        await smtp.SendAsync(msg, ct);
        // The server has accepted the message. A failing QUIT (dropped connection, app closing) must not make the
        // outbox treat it as failed — that would send it again on retry.
        try
        {
            using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await smtp.DisconnectAsync(true, quit.Token);
        }
        catch (Exception ex) { Log.Warn("SMTP disconnect after send: " + ex.Message); }
    }

    /// <summary>A failed TLS handshake: without a server certificate the port / security setting is the likely cause;
    /// with one, the certificate wasn't trusted — say who issued it (a company network or antivirus that inspects
    /// secure connections shows up here as an unexpected issuer).</summary>
    public static string SecureFailure(SslHandshakeException h)
    {
        if (h.ServerCertificate is not { } cert)
            return "Secure connection failed. Check the port and security setting (SSL/TLS vs STARTTLS).";
        static string Name(string dn)
        {
            foreach (var part in dn.Split(','))
            {
                var p = part.Trim();
                if (p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase)) return p[3..];
            }
            return dn;
        }
        var issuer = Name(cert.Issuer);
        var root = h.RootCertificateAuthority is { } r ? Name(r.Subject) : null;
        var by = root != null && !string.Equals(root, issuer, StringComparison.Ordinal) ? $"{issuer} (root: {root})" : issuer;
        return $"The server's certificate wasn't trusted. It is for {Name(cert.Subject)}, issued by {by}. "
            + "A company network, VPN or antivirus that inspects secure connections can cause this.";
    }

    public static string Friendly(Exception ex) => ex switch
    {
        ReauthRequiredException r => r.Message,
        System.Net.Sockets.SocketException s => $"Can't reach the server ({s.SocketErrorCode}). Check the host name, port and your connection.",
        SslHandshakeException h => SecureFailure(h),
        AuthenticationException => "Wrong user name or password.",
        ImapProtocolException or SmtpProtocolException => "The server closed the connection unexpectedly.",
        TimeoutException or OperationCanceledException => "The server did not respond in time.",
        SmtpCommandException sc => $"The server refused the message: {sc.Message}",
        _ => ex.Message,
    };
}
