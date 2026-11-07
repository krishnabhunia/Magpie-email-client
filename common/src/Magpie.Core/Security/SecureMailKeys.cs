using System.Security.Cryptography;
using System.Text.Json;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.X509;

namespace Magpie.Core.Security;

public sealed record MailKeyInfo(string Id, string AccountId, string Email, MailSecurityFormat Format,
    string Fingerprint, bool HasPrivateKey, bool Active, bool FingerprintConfirmed, DateTimeOffset? Expires,
    bool CanSign, bool CanEncrypt)
{
    public string FormatLabel => Format == MailSecurityFormat.Smime ? "S/MIME" : "OpenPGP";
    public string KindLabel => HasPrivateKey ? "Personal identity" : "Recipient public key";
    public string Status => (Active ? "Selected" : "Not selected") + (FingerprintConfirmed ? " · fingerprint confirmed" : " · fingerprint not confirmed")
        + (Expires < DateTimeOffset.UtcNow ? " · expired" : "");
}

/// <summary>Local key material is protected by DPAPI/Keychain before any file is created. No keyserver access.</summary>
public sealed class SecureMailKeys
{
    public const string FileName = "mail-keys.protected";
    public const int MaxImportBytes = 4 * 1024 * 1024;
    private const int MaxStoreBytes = 20 * 1024 * 1024;
    private readonly string _path;
    private readonly ISecretProtector _protector;
    private readonly object _gate = new();

    internal sealed class Entry : IDisposable
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string AccountId { get; set; } = "";
        public string Email { get; set; } = "";
        public MailSecurityFormat Format { get; set; }
        public byte[] Material { get; set; } = Array.Empty<byte>();
        public string Password { get; set; } = "";
        public bool HasPrivateKey { get; set; }
        public bool Active { get; set; }
        public bool FingerprintConfirmed { get; set; }
        public void Dispose() { CryptographicOperations.ZeroMemory(Material); Password = ""; }
    }

    public SecureMailKeys(string path, ISecretProtector protector) { _path = path; _protector = protector; }

    /// <summary>Bound the read itself, including a file that grows after it is selected. No private temporary file.</summary>
    public static byte[] ReadImportFile(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                if (count > MaxImportBytes - output.Length) throw new MailSecurityException("Choose a key file smaller than 4 MB.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (output.TryGetBuffer(out var contents)) CryptographicOperations.ZeroMemory(contents.AsSpan());
        }
    }

    public IReadOnlyList<MailKeyInfo> List(string accountId)
    {
        lock (_gate)
        {
            var entries = Read();
            try { return entries.Where(e => e.AccountId == accountId).Select(e => SecureKeyParser.Parse(e).Info(e)).ToArray(); }
            finally { Dispose(entries); }
        }
    }

    public MailKeyInfo Import(string accountId, string email, MailSecurityFormat format, byte[] material,
        string password, bool privateKey)
    {
        if (string.IsNullOrWhiteSpace(accountId)) throw new MailSecurityException("Choose an account for this key.");
        email = NormalAddress(email);
        if (!Enum.IsDefined(format) || material.Length is 0 or > MaxImportBytes)
            throw new MailSecurityException("Choose one certificate or OpenPGP key file smaller than 4 MB.");
        var candidate = new Entry { AccountId = accountId, Email = email, Format = format, Material = material.ToArray(),
            Password = password, HasPrivateKey = privateKey, FingerprintConfirmed = privateKey };
        try
        {
            var parsed = SecureKeyParser.Parse(candidate);
            lock (_gate)
            {
                var entries = Read();
                try
                {
                    if (entries.Count >= 64) throw new MailSecurityException("Remove an unused key before importing another (64 keys per profile).");
                    if (entries.Any(e => e.AccountId == accountId && e.Email == email && e.Format == format
                        && e.HasPrivateKey == privateKey && SecureKeyParser.Parse(e).Fingerprint == parsed.Fingerprint))
                        throw new MailSecurityException("That key is already imported for this address.");
                    candidate.Active = privateKey && !entries.Any(e => SameSlot(e, candidate) && e.Active);
                    entries.Add(candidate);
                    Write(entries);
                    return parsed.Info(candidate);
                }
                finally { Dispose(entries); }
            }
        }
        finally { candidate.Dispose(); }
    }

    /// <summary>Called only after the person explicitly confirms the displayed full fingerprint.</summary>
    public void Select(string accountId, string id, string confirmedFingerprint)
    {
        lock (_gate)
        {
            var entries = Read();
            try
            {
                var chosen = entries.SingleOrDefault(e => e.AccountId == accountId && e.Id == id)
                    ?? throw new MailSecurityException("That key is no longer available.");
                var parsed = SecureKeyParser.Parse(chosen);
                if (!string.Equals(parsed.Fingerprint, confirmedFingerprint.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                    throw new MailSecurityException("Confirm the exact fingerprint shown for this key.");
                foreach (var e in entries.Where(e => SameSlot(e, chosen))) e.Active = e.Id == id;
                chosen.FingerprintConfirmed = true;
                Write(entries);
            }
            finally { Dispose(entries); }
        }
    }

    public void Remove(string accountId, string id)
    {
        lock (_gate)
        {
            var entries = Read();
            try
            {
                foreach (var removed in entries.Where(e => e.AccountId == accountId && e.Id == id)) removed.Dispose();
                entries.RemoveAll(e => e.AccountId == accountId && e.Id == id);
                Write(entries);
            }
            finally { Dispose(entries); }
        }
    }

    public void RemoveAccount(string accountId)
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return;
            var entries = Read();
            try
            {
                var removed = entries.Where(e => e.AccountId == accountId).ToArray();
                if (removed.Length == 0) return;
                foreach (var entry in removed) entry.Dispose();
                entries.RemoveAll(e => e.AccountId == accountId);
                Write(entries);
            }
            finally { Dispose(entries); }
        }
    }

    public byte[] ExportPublic(string accountId, string id)
    {
        lock (_gate)
        {
            var entries = Read();
            try
            {
                var entry = entries.SingleOrDefault(e => e.AccountId == accountId && e.Id == id)
                    ?? throw new MailSecurityException("That key is no longer available.");
                var parsed = SecureKeyParser.Parse(entry);
                return parsed.Certificate?.GetEncoded() ?? parsed.PublicRing!.GetEncoded();
            }
            finally { Dispose(entries); }
        }
    }

    internal List<Entry> Load(string accountId, MailSecurityFormat format)
    {
        lock (_gate)
        {
            var all = Read();
            var selected = all.Where(e => e.AccountId == accountId && e.Format == format).ToList();
            foreach (var entry in all.Except(selected)) entry.Dispose();
            return selected;
        }
    }

    internal static string NormalAddress(string address)
    {
        if (!MailboxAddress.TryParse(address, out var mailbox) || mailbox.Address != address.Trim()
            || !mailbox.Address.Contains('@') || mailbox.Address.Any(char.IsControl))
            throw new MailSecurityException("Enter one full email address without a display name.");
        return mailbox.Address.Trim().ToLowerInvariant();
    }

    private static bool SameSlot(Entry a, Entry b) => a.AccountId == b.AccountId && a.Email == b.Email
        && a.Format == b.Format && a.HasPrivateKey == b.HasPrivateKey;

    private List<Entry> Read()
    {
        if (!File.Exists(_path)) return new();
        if (new FileInfo(_path).Length > MaxStoreBytes) throw new MailSecurityException("The protected key store exceeds its safety limit.");
        byte[]? plain = null;
        try
        {
            plain = _protector.Unprotect(File.ReadAllBytes(_path));
            if (plain.Length > MaxStoreBytes) throw new MailSecurityException("The protected key store exceeds its safety limit.");
            var entries = JsonSerializer.Deserialize<List<Entry>>(plain) ?? throw new JsonException();
            if (entries.Count > 64 || entries.Any(e => e.Material.Length is 0 or > MaxImportBytes || !Enum.IsDefined(e.Format)))
            { Dispose(entries); throw new JsonException(); }
            return entries;
        }
        catch (MailSecurityException) { throw; }
        catch { throw new MailSecurityException("The key store cannot be unlocked for this operating-system user. Restore access or reimport the keys; the existing file was kept."); }
        finally { if (plain != null) CryptographicOperations.ZeroMemory(plain); }
    }

    private void Write(List<Entry> entries)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries);
        try
        {
            if (plain.Length > MaxStoreBytes - 4096) throw new MailSecurityException("The local key store is full. Remove unused keys before importing another.");
            var cipher = _protector.Protect(plain);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, cipher); File.Move(temporary, _path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static void Dispose(IEnumerable<Entry> entries) { foreach (var entry in entries) entry.Dispose(); }
}

internal sealed class ParsedSecureKey
{
    public X509Certificate? Certificate { get; init; }
    public X509Certificate[] CertificateChain { get; init; } = Array.Empty<X509Certificate>();
    public AsymmetricKeyParameter? PrivateKey { get; init; }
    public PgpPublicKeyRing? PublicRing { get; init; }
    public PgpSecretKeyRing? SecretRing { get; init; }
    public string Fingerprint { get; init; } = "";
    public DateTimeOffset? Expires { get; init; }
    public bool CanSign { get; init; }
    public bool CanEncrypt { get; init; }
    public bool Revoked { get; init; }
    public MailKeyInfo Info(SecureMailKeys.Entry entry) => new(entry.Id, entry.AccountId, entry.Email, entry.Format,
        Fingerprint, entry.HasPrivateKey, entry.Active, entry.FingerprintConfirmed, Expires, CanSign, CanEncrypt);
}

internal static class SecureKeyParser
{
    public static ParsedSecureKey Parse(SecureMailKeys.Entry entry)
    {
        try { return entry.Format == MailSecurityFormat.Smime ? ReadCertificate(entry) : ReadPgp(entry); }
        catch (MailSecurityException) { throw; }
        catch { throw new MailSecurityException("The key file or its password is invalid or unsupported. Import one email identity at a time."); }
    }

    private static ParsedSecureKey ReadCertificate(SecureMailKeys.Entry entry)
    {
        X509Certificate cert;
        X509Certificate[] chain;
        AsymmetricKeyParameter? privateKey = null;
        if (entry.HasPrivateKey)
        {
            var store = new Pkcs12StoreBuilder().Build();
            var password = entry.Password.ToCharArray();
            try { using var input = new MemoryStream(entry.Material, false); store.Load(input, password); }
            finally { Array.Clear(password); }
            var aliases = store.Aliases.Where(store.IsKeyEntry).ToArray();
            if (aliases.Length != 1) throw new MailSecurityException("Import a PKCS#12 file containing exactly one private email identity.");
            cert = store.GetCertificate(aliases[0]).Certificate;
            chain = store.GetCertificateChain(aliases[0]).Select(c => c.Certificate).ToArray();
            privateKey = store.GetKey(aliases[0]).Key;
            if (privateKey is not RsaKeyParameters secret || cert.GetPublicKey() is not RsaKeyParameters pub
                || !secret.IsPrivate || !secret.Modulus.Equals(pub.Modulus))
                throw new MailSecurityException("This version supports matching RSA S/MIME identities of at least 2048 bits.");
        }
        else
        {
            var certs = new X509CertificateParser().ReadCertificates(entry.Material);
            var matches = certs.Where(c => CertificateEmails(c).Contains(entry.Email, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new MailSecurityException("The certificate file must contain one certificate for the entered email address.");
            cert = matches[0]; chain = new[] { cert };
        }
        if (!CertificateEmails(cert).Contains(entry.Email, StringComparer.OrdinalIgnoreCase))
            throw new MailSecurityException("The certificate's email address does not match the address entered for this account.");
        if (cert.GetPublicKey() is not RsaKeyParameters rsa || rsa.Modulus.BitLength < 2048)
            throw new MailSecurityException("This version supports RSA S/MIME certificates with keys of at least 2048 bits.");
        var usage = cert.GetExtendedKeyUsage();
        if (usage != null && !usage.Any(u => u.ToString() == "1.3.6.1.5.5.7.3.4"))
            throw new MailSecurityException("This certificate is not valid for email protection.");
        var bits = cert.GetKeyUsage();
        return new()
        {
            Certificate = cert, CertificateChain = chain, PrivateKey = privateKey,
            Fingerprint = Convert.ToHexString(SHA256.HashData(cert.GetEncoded())),
            Expires = new DateTimeOffset(DateTime.SpecifyKind(cert.NotAfter, DateTimeKind.Utc)),
            CanSign = bits == null || bits[0], CanEncrypt = bits == null || bits[2],
        };
    }

    internal static IReadOnlyList<string> CertificateEmails(X509Certificate cert)
    {
        var emails = cert.SubjectDN.GetValueList(X509Name.EmailAddress).Select(e => e.ToLowerInvariant()).ToList();
        if (cert.GetSubjectAlternativeNames() is { } alternatives)
            foreach (var name in alternatives)
                if (name.Count > 1 && (int)name[0] == GeneralName.Rfc822Name && name[1] is string email) emails.Add(email.ToLowerInvariant());
        return emails.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static ParsedSecureKey ReadPgp(SecureMailKeys.Entry entry)
    {
        using var input = new MemoryStream(entry.Material, false);
        using var decoded = PgpUtilities.GetDecoderStream(input);
        var factory = new PgpObjectFactory(decoded);
        var first = factory.NextPgpObject();
        if (first is PgpMarker) first = factory.NextPgpObject();
        PgpSecretKeyRing? secret = null;
        PgpPublicKeyRing ring;
        if (entry.HasPrivateKey)
        {
            secret = first as PgpSecretKeyRing ?? throw new MailSecurityException("Import an OpenPGP secret key for a personal identity.");
            var password = entry.Password.ToCharArray();
            try
            {
                foreach (var key in secret.GetSecretKeys())
                    if (!key.IsPrivateKeyEmpty && key.ExtractPrivateKey(password) == null) throw new MailSecurityException("The private key cannot be unlocked.");
            }
            finally { Array.Clear(password); }
            if (secret.GetSecretKeys().All(k => k.IsPrivateKeyEmpty)) throw new MailSecurityException("Hardware-token stubs are not supported; import an exportable private identity.");
            using var publicBytes = new MemoryStream();
            foreach (var key in secret.GetSecretKeys().Select(k => k.PublicKey).Concat(secret.GetExtraPublicKeys())) key.Encode(publicBytes);
            publicBytes.Position = 0;
            ring = new PgpPublicKeyRing(publicBytes);
        }
        else ring = first as PgpPublicKeyRing ?? throw new MailSecurityException("Import an OpenPGP public key for a recipient.");
        if (factory.NextPgpObject() != null) throw new MailSecurityException("Import one OpenPGP identity at a time.");
        var primary = ring.GetPublicKey();
        var certified = CertifiedIdentity(primary, entry.Email);
        if (certified == null) throw new MailSecurityException("This OpenPGP key has no valid self-certified user ID for the entered email address.");
        if (!StrongRsa(primary)) throw new MailSecurityException("This version supports version 4 RSA OpenPGP keys of at least 2048 bits.");
        var validSeconds = certified.GetHashedSubPackets()?.GetKeyExpirationTime() ?? 0;
        return new()
        {
            PublicRing = ring, SecretRing = secret, Fingerprint = Convert.ToHexString(primary.GetFingerprint()),
            Expires = validSeconds > 0 ? new DateTimeOffset(primary.CreationTime.ToUniversalTime()).AddSeconds(validSeconds) : null,
            CanSign = UsablePgpKeys(ring, entry.Email, signing: true).Any(),
            CanEncrypt = UsablePgpKeys(ring, entry.Email, signing: false).Any(),
            Revoked = primary.HasRevocation(),
        };
    }

    internal static bool StrongRsa(PgpPublicKey key) => key.Version == 4 && key.BitStrength >= 2048
        && key.Algorithm is PublicKeyAlgorithmTag.RsaGeneral or PublicKeyAlgorithmTag.RsaSign or PublicKeyAlgorithmTag.RsaEncrypt;

    internal static PgpSignature? CertifiedIdentity(PgpPublicKey primary, string email)
    {
        var signatures = new List<PgpSignature>();
        foreach (var userId in primary.GetUserIds())
        {
            if (!MailboxAddress.TryParse(userId, out var mailbox) || !mailbox.Address.Equals(email, StringComparison.OrdinalIgnoreCase)) continue;
            var certifications = new List<PgpSignature>();
            DateTime? revoked = null;
            foreach (var signature in primary.GetSignaturesForId(userId))
            {
                if (signature.KeyId != primary.KeyId || !(PgpSignature.IsCertification(signature.SignatureType)
                    || signature.SignatureType == PgpSignature.CertificationRevocation)) continue;
                try
                {
                    signature.InitVerify(primary);
                    if (!signature.VerifyCertification(userId, primary)) continue;
                    if (signature.SignatureType == PgpSignature.CertificationRevocation)
                    { if (revoked == null || signature.CreationTime > revoked) revoked = signature.CreationTime; }
                    else certifications.Add(signature);
                }
                catch { /* An invalid certification never establishes an address binding. */ }
            }
            signatures.AddRange(certifications.Where(s => revoked == null || s.CreationTime > revoked));
        }
        return signatures.OrderByDescending(s => s.CreationTime).FirstOrDefault();
    }

    internal static IEnumerable<PgpPublicKey> UsablePgpKeys(PgpPublicKeyRing ring, string email, bool signing, DateTimeOffset? validAt = null)
    {
        var at = (validAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        var primary = ring.GetPublicKey();
        var self = CertifiedIdentity(primary, email);
        if (self == null || primary.HasRevocation()) yield break;
        foreach (var key in ring.GetPublicKeys())
        {
            if (!StrongRsa(key) || key.HasRevocation()) continue;
            PgpSignature? binding = key.IsMasterKey ? self : null;
            if (!key.IsMasterKey)
                foreach (var candidate in key.GetSignaturesOfType(PgpSignature.SubkeyBinding).OrderByDescending(s => s.CreationTime))
                {
                    try { candidate.InitVerify(primary); if (candidate.VerifyCertification(primary, key)) binding = candidate; }
                    catch { }
                    if (binding != null) break;
                }
            if (binding == null || binding.CreationTime.ToUniversalTime() > at || key.CreationTime.ToUniversalTime() > at) continue;
            var bindingLifetime = binding.GetHashedSubPackets()?.GetSignatureExpirationTime() ?? 0;
            if (bindingLifetime > 0 && binding.CreationTime.ToUniversalTime().AddSeconds(bindingLifetime) <= at) continue;
            var keyLifetime = binding.GetHashedSubPackets()?.GetKeyExpirationTime() ?? 0;
            if (keyLifetime > 0 && key.CreationTime.ToUniversalTime().AddSeconds(keyLifetime) <= at) continue;
            var flags = binding.GetHashedSubPackets()?.GetKeyFlags() ?? 0;
            if (signing ? (flags & 2) == 0 : (flags & (4 | 8)) == 0) continue;
            if (signing && key.Algorithm == PublicKeyAlgorithmTag.RsaEncrypt || !signing && !key.IsEncryptionKey) continue;
            yield return key;
        }
    }

    internal static void RequireUsable(ParsedSecureKey parsed, SecureMailKeys.Entry entry, DateTimeOffset at, bool signing)
    {
        if (parsed.Revoked || parsed.Expires <= at || (signing ? !parsed.CanSign : !parsed.CanEncrypt))
            throw new MailSecurityException("The selected key for " + entry.Email + " is expired, revoked, or not permitted for " + (signing ? "signing." : "encryption."));
        if (parsed.Certificate is { } cert && !cert.IsValid(at.UtcDateTime))
            throw new MailSecurityException("The selected certificate for " + entry.Email + " is not valid at the planned sending time.");
        if (parsed.PublicRing is { } ring)
        {
            var primary = ring.GetPublicKey();
            var self = CertifiedIdentity(primary, entry.Email)!;
            var seconds = self.GetHashedSubPackets()?.GetSignatureExpirationTime() ?? 0;
            if (primary.CreationTime.ToUniversalTime() > at.UtcDateTime || self.CreationTime.ToUniversalTime() > at.UtcDateTime
                || seconds > 0 && self.CreationTime.ToUniversalTime().AddSeconds(seconds) <= at.UtcDateTime)
                throw new MailSecurityException("The OpenPGP identity certification is not valid at the planned sending time.");
        }
    }
}
