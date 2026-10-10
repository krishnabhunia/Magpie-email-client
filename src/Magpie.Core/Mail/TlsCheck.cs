using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Magpie.Core.Mail;

/// <summary>
/// Server certificate check for IMAP / SMTP. A certificate is accepted when Windows finds nothing wrong with it, or when
/// the only problem is that the revocation server couldn't be reached (seen in magpie.log right after the PC wakes:
/// every account failed with "the revocation server was offline"). Browsers treat that the same way. Any other problem
/// (wrong name, expired, untrusted, revoked) still refuses the connection.
/// </summary>
public static class TlsCheck
{
    private const X509ChainStatusFlags RevocationUnknown = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;

    public static bool Callback(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        var status = new List<X509ChainStatusFlags>();
        if (chain != null)
        {
            status.AddRange(chain.ChainStatus.Select(s => s.Status));
            foreach (var element in chain.ChainElements) status.AddRange(element.ChainElementStatus.Select(s => s.Status));
        }
        return Accept(errors, status);
    }

    public static bool Accept(SslPolicyErrors errors, IEnumerable<X509ChainStatusFlags> chainStatus)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors) return false;   // wrong name or no certificate
        var flags = chainStatus.Aggregate(X509ChainStatusFlags.NoError, (a, f) => a | f);
        return flags != X509ChainStatusFlags.NoError && (flags & ~RevocationUnknown) == X509ChainStatusFlags.NoError;
    }
}
