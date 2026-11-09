using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Magpie.Core.Security;

/// <summary>Explicit local keyrings only. No GnuPG files, automatic key retrieval or default global context.</summary>
internal sealed class MemoryOpenPgpContext : OpenPgpContext
{
    private readonly List<(SecureMailKeys.Entry Entry, ParsedSecureKey Parsed)> _entries;
    private readonly List<PgpPublicKeyRing> _additionalPublic = new();
    private readonly List<PgpSecretKeyRing> _additionalSecret = new();

    public MemoryOpenPgpContext(List<(SecureMailKeys.Entry, ParsedSecureKey)> entries)
    {
        _entries = entries;
        AutoKeyRetrieve = false;
        DefaultEncryptionAlgorithm = EncryptionAlgorithm.Aes256;
    }

    protected override string GetPasswordForKey(PgpSecretKey key) => _entries
        .FirstOrDefault(e => e.Parsed.SecretRing?.GetSecretKey(key.KeyId) != null).Entry?.Password
        ?? throw new PrivateKeyNotFoundException(key.KeyId, "Import the private key for this message into this account.");

    internal PgpSecretKey SecretKey(long id) => GetSecretKey(id, CancellationToken.None);
    internal string Password(PgpSecretKey key) => GetPasswordForKey(key);

    protected override PgpPublicKeyRing? GetPublicKeyRing(long keyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _entries.Select(e => e.Parsed.PublicRing).Concat(_additionalPublic)
            .FirstOrDefault(r => r?.GetPublicKey(keyId) != null);
    }

    protected override Task<PgpPublicKeyRing?> GetPublicKeyRingAsync(long keyId, CancellationToken cancellationToken)
        => Task.FromResult(GetPublicKeyRing(keyId, cancellationToken));

    protected override PgpSecretKey GetSecretKey(long keyId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _entries.Select(e => e.Parsed.SecretRing?.GetSecretKey(keyId)).FirstOrDefault(k => k != null && !k.IsPrivateKeyEmpty)
            ?? throw new PrivateKeyNotFoundException(keyId, "Import the matching private key into this account to decrypt this message.");
    }

    public override bool CanSign(MailboxAddress mailbox, CancellationToken cancellationToken = default)
    {
        try { return GetSigningKey(mailbox, cancellationToken) != null; } catch { return false; }
    }

    public override bool CanEncrypt(MailboxAddress mailbox, CancellationToken cancellationToken = default)
    {
        try { return GetPublicKeys(new[] { mailbox }, cancellationToken).Count == 1; } catch { return false; }
    }

    public override PgpSecretKey GetSigningKey(MailboxAddress mailbox, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = _entries.SingleOrDefault(e => e.Entry.HasPrivateKey && e.Entry.Active
            && e.Entry.Email.Equals(mailbox.Address, StringComparison.OrdinalIgnoreCase));
        if (selected.Entry == null) throw new PrivateKeyNotFoundException(mailbox, "Select a private signing identity for this sender.");
        return SigningKey(selected.Entry, selected.Parsed, DateTimeOffset.UtcNow);
    }

    internal static PgpSecretKey SigningKey(SecureMailKeys.Entry entry, ParsedSecureKey parsed, DateTimeOffset at)
    {
        SecureKeyParser.RequireUsable(parsed, entry, at, signing: true);
        // Signing subkeys require a back-signature validation policy; this initial profile deliberately uses
        // the self-certified primary signing key, rather than trusting an unvalidated signing subkey.
        var primary = parsed.PublicRing!.GetPublicKey();
        if (!SecureKeyParser.UsablePgpKeys(parsed.PublicRing, entry.Email, true, at).Any(k => k.KeyId == primary.KeyId))
            throw new MailSecurityException("This identity needs an exportable primary RSA signing key.");
        return parsed.SecretRing?.GetSecretKey(primary.KeyId) is { IsPrivateKeyEmpty: false, IsSigningKey: true } key
            ? key : throw new MailSecurityException("The selected OpenPGP identity has no usable private signing key.");
    }

    internal static PgpPublicKey EncryptionKey(SecureMailKeys.Entry entry, ParsedSecureKey parsed, DateTimeOffset at)
    {
        SecureKeyParser.RequireUsable(parsed, entry, at, signing: false);
        foreach (var key in SecureKeyParser.UsablePgpKeys(parsed.PublicRing!, entry.Email, false, at).OrderByDescending(k => k.CreationTime))
        {
            if (entry.HasPrivateKey && parsed.SecretRing?.GetSecretKey(key.KeyId) is not { IsPrivateKeyEmpty: false }) continue;
            return key;
        }
        throw new MailSecurityException("No unexpired encryption key is available for " + entry.Email + ".");
    }

    public override IList<PgpPublicKey> GetPublicKeys(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default)
    {
        var keys = new List<PgpPublicKey>();
        foreach (var mailbox in mailboxes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = _entries.FirstOrDefault(e => e.Entry.Active && e.Entry.FingerprintConfirmed
                && e.Entry.Email.Equals(mailbox.Address, StringComparison.OrdinalIgnoreCase));
            if (selected.Entry == null) throw new PublicKeyNotFoundException(mailbox, "Import and confirm the recipient's public key.");
            keys.Add(EncryptionKey(selected.Entry, selected.Parsed, DateTimeOffset.UtcNow));
        }
        return keys;
    }

    public override void Import(PgpPublicKeyRing keyring, CancellationToken cancellationToken = default) => _additionalPublic.Add(keyring);
    public override void Import(PgpPublicKeyRingBundle bundle, CancellationToken cancellationToken = default)
    { foreach (var ring in bundle.GetKeyRings()) Import(ring, cancellationToken); }
    public override void Import(PgpSecretKeyRing keyring, CancellationToken cancellationToken = default) => _additionalSecret.Add(keyring);
    public override void Import(PgpSecretKeyRingBundle bundle, CancellationToken cancellationToken = default)
    { foreach (var ring in bundle.GetKeyRings()) Import(ring, cancellationToken); }
    public override void Export(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default)
        => base.Export(GetPublicKeys(mailboxes, cancellationToken), stream, armor, cancellationToken);
    public override MimePart Export(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default)
        => base.Export(GetPublicKeys(mailboxes, cancellationToken), cancellationToken);
    public override Task<MimePart> ExportAsync(IEnumerable<MailboxAddress> mailboxes, CancellationToken cancellationToken = default)
        => Task.FromResult(Export(mailboxes, cancellationToken));
    public override Task ExportAsync(IEnumerable<MailboxAddress> mailboxes, Stream stream, bool armor, CancellationToken cancellationToken = default)
    { Export(mailboxes, stream, armor, cancellationToken); return Task.CompletedTask; }
}
