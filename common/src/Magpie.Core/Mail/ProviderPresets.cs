using System.Xml.Linq;
using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>Server settings for well-known providers, plus Thunderbird ISPDB autoconfig lookup for the rest.</summary>
public static class ProviderPresets
{
    private sealed record Preset(string Imap, int ImapPort, TlsMode ImapSec, string Smtp, int SmtpPort, TlsMode SmtpSec, AccountKind Kind = AccountKind.Imap);

    private static readonly Dictionary<string, Preset> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail.com"] = new("imap.gmail.com", 993, TlsMode.SslOnConnect, "smtp.gmail.com", 465, TlsMode.SslOnConnect, AccountKind.Gmail),
        ["googlemail.com"] = new("imap.gmail.com", 993, TlsMode.SslOnConnect, "smtp.gmail.com", 465, TlsMode.SslOnConnect, AccountKind.Gmail),
        ["outlook.com"] = new("outlook.office365.com", 993, TlsMode.SslOnConnect, "smtp-mail.outlook.com", 587, TlsMode.StartTls, AccountKind.Microsoft),
        ["hotmail.com"] = new("outlook.office365.com", 993, TlsMode.SslOnConnect, "smtp-mail.outlook.com", 587, TlsMode.StartTls, AccountKind.Microsoft),
        ["live.com"] = new("outlook.office365.com", 993, TlsMode.SslOnConnect, "smtp-mail.outlook.com", 587, TlsMode.StartTls, AccountKind.Microsoft),
        ["msn.com"] = new("outlook.office365.com", 993, TlsMode.SslOnConnect, "smtp-mail.outlook.com", 587, TlsMode.StartTls, AccountKind.Microsoft),
        ["outlook.in"] = new("outlook.office365.com", 993, TlsMode.SslOnConnect, "smtp-mail.outlook.com", 587, TlsMode.StartTls, AccountKind.Microsoft),
        ["hotmail.co.uk"] = new("outlook.office365.com", 993, TlsMode.SslOnConnect, "smtp-mail.outlook.com", 587, TlsMode.StartTls, AccountKind.Microsoft),
        ["yahoo.com"] = new("imap.mail.yahoo.com", 993, TlsMode.SslOnConnect, "smtp.mail.yahoo.com", 465, TlsMode.SslOnConnect),
        ["yahoo.co.in"] = new("imap.mail.yahoo.com", 993, TlsMode.SslOnConnect, "smtp.mail.yahoo.com", 465, TlsMode.SslOnConnect),
        ["ymail.com"] = new("imap.mail.yahoo.com", 993, TlsMode.SslOnConnect, "smtp.mail.yahoo.com", 465, TlsMode.SslOnConnect),
        ["icloud.com"] = new("imap.mail.me.com", 993, TlsMode.SslOnConnect, "smtp.mail.me.com", 587, TlsMode.StartTls),
        ["me.com"] = new("imap.mail.me.com", 993, TlsMode.SslOnConnect, "smtp.mail.me.com", 587, TlsMode.StartTls),
        ["aol.com"] = new("imap.aol.com", 993, TlsMode.SslOnConnect, "smtp.aol.com", 465, TlsMode.SslOnConnect),
        ["zoho.com"] = new("imap.zoho.com", 993, TlsMode.SslOnConnect, "smtp.zoho.com", 465, TlsMode.SslOnConnect),
        ["zohomail.in"] = new("imap.zoho.in", 993, TlsMode.SslOnConnect, "smtp.zoho.in", 465, TlsMode.SslOnConnect),
        ["fastmail.com"] = new("imap.fastmail.com", 993, TlsMode.SslOnConnect, "smtp.fastmail.com", 465, TlsMode.SslOnConnect),
        ["gmx.com"] = new("imap.gmx.com", 993, TlsMode.SslOnConnect, "mail.gmx.com", 465, TlsMode.SslOnConnect),
        ["yandex.com"] = new("imap.yandex.com", 993, TlsMode.SslOnConnect, "smtp.yandex.com", 465, TlsMode.SslOnConnect),
        ["rediffmail.com"] = new("imap.rediffmail.com", 993, TlsMode.SslOnConnect, "smtp.rediffmail.com", 465, TlsMode.SslOnConnect),
        ["proton.me"] = new("127.0.0.1", 1143, TlsMode.StartTls, "127.0.0.1", 1025, TlsMode.StartTls),
    };

    public static string Domain(string email) => email.Contains('@') ? email[(email.LastIndexOf('@') + 1)..].Trim().ToLowerInvariant() : "";

    /// <summary>Fills server fields for a known domain. Returns false when unknown (use <see cref="LookupAsync"/>).</summary>
    public static bool Apply(Account a)
    {
        if (!Known.TryGetValue(Domain(a.Email), out var p)) return false;
        Set(a, p);
        return true;
    }

    public static void ApplyKind(Account a, AccountKind kind)
    {
        if (kind == AccountKind.Gmail) Set(a, Known["gmail.com"]);
        else if (kind == AccountKind.Microsoft) Set(a, Known["outlook.com"]);
        a.Kind = kind;
    }

    private static void Set(Account a, Preset p)
    {
        a.ImapHost = p.Imap; a.ImapPort = p.ImapPort; a.ImapSecurity = p.ImapSec;
        a.SmtpHost = p.Smtp; a.SmtpPort = p.SmtpPort; a.SmtpSecurity = p.SmtpSec;
        a.Kind = p.Kind;
        a.ServerSavesSent = p.Kind is AccountKind.Gmail or AccountKind.Microsoft;
        if (string.IsNullOrWhiteSpace(a.UserName)) a.UserName = a.Email;
    }

    /// <summary>Thunderbird's public ISPDB (autoconfig) lookup, then the domain's own autoconfig. Null when nothing found.</summary>
    public static async Task<bool> LookupAsync(Account a, HttpClient http, CancellationToken ct)
    {
        var domain = Domain(a.Email);
        if (domain.Length == 0) return false;
        var urls = new[]
        {
            $"https://autoconfig.thunderbird.net/v1.1/{domain}",
            $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={Uri.EscapeDataString(a.Email)}",
        };
        foreach (var url in urls)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                var xml = await http.GetStringAsync(url, cts.Token);
                if (ParseAutoconfig(xml, a)) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Info($"autoconfig {url}: {ex.Message}");
            }
        }
        return false;
    }

    internal static bool ParseAutoconfig(string xml, Account a)
    {
        var doc = XDocument.Parse(xml);
        var incoming = doc.Descendants("incomingServer").FirstOrDefault(e => (string?)e.Attribute("type") == "imap");
        var outgoing = doc.Descendants("outgoingServer").FirstOrDefault(e => (string?)e.Attribute("type") == "smtp");
        if (incoming == null || outgoing == null) return false;
        static TlsMode Sec(string? s) => s?.ToUpperInvariant() switch { "SSL" => TlsMode.SslOnConnect, "STARTTLS" => TlsMode.StartTls, _ => TlsMode.None };
        a.ImapHost = (string?)incoming.Element("hostname") ?? "";
        a.ImapPort = int.TryParse((string?)incoming.Element("port"), out var ip) ? ip : 993;
        a.ImapSecurity = Sec((string?)incoming.Element("socketType"));
        a.SmtpHost = (string?)outgoing.Element("hostname") ?? "";
        a.SmtpPort = int.TryParse((string?)outgoing.Element("port"), out var sp) ? sp : 465;
        a.SmtpSecurity = Sec((string?)outgoing.Element("socketType"));
        var user = (string?)incoming.Element("username") ?? "%EMAILADDRESS%";
        var local = a.Email.Split('@')[0];
        a.UserName = user.Replace("%EMAILADDRESS%", a.Email).Replace("%EMAILLOCALPART%", local).Replace("%EMAILDOMAIN%", Domain(a.Email));
        return a.ImapHost.Length > 0 && a.SmtpHost.Length > 0;
    }
}
