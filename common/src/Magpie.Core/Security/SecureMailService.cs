using System.Security.Cryptography;
using System.Text.Json;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg.OpenPgp;

namespace Magpie.Core.Security;

public sealed record SecureOutgoing(MimeMessage Message, byte[]? ProtectedRecall);
public sealed record MailSignatureReport(string Status, bool? Valid, string Email, string Fingerprint,
    bool MatchesSender, bool FingerprintConfirmed, bool KeyCurrentlyValid, string Algorithm);

public sealed class SecureMailReadResult : IDisposable
{
    public required MimeMessage Message { get; init; }
    public bool Encrypted { get; init; }
    public bool Protected { get; init; }
    public IReadOnlyList<MailSignatureReport> Signatures { get; init; } = Array.Empty<MailSignatureReport>();
    public string Summary => (Encrypted ? "Decrypted in memory. " : "") + (Signatures.Count == 0 ? "No verifiable message-body signature." :
        string.Join("\n", Signatures.Select(s => s.Status)));
    public void Dispose() => Message.Dispose();
}

/// <summary>MimeKit/BouncyCastle cryptography with explicit account keys and protected, payload-bound Undo state.</summary>
public sealed class SecureMailService
{
    public const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly ISecretProtector _protector;
    public SecureMailKeys Keys { get; }
    public SecureMailService(string path, ISecretProtector protector) { Keys = new(path, protector); _protector = protector; }

    private sealed class RecallState
    {
        public int Version { get; set; } = 1;
        public string AccountId { get; set; } = "";
        public string MessageId { get; set; } = "";
        public byte[] WireDigest { get; set; } = Array.Empty<byte>();
        public byte[] Draft { get; set; } = Array.Empty<byte>();
        public MailSecurityFormat Format { get; set; }
        public List<RequiredKey> Keys { get; set; } = new();
    }

    private sealed class RequiredKey
    {
        public string Email { get; set; } = "";
        public bool Private { get; set; }
        public bool Sign { get; set; }
        public bool Encrypt { get; set; }
        public byte[] MaterialDigest { get; set; } = Array.Empty<byte>();
    }

    public SecureOutgoing PrepareOutgoing(MimeMessage original, Account account, SecureMailOptions options,
        DateTimeOffset sendingAt, bool preserveRecall = true)
    {
        if (!options.Requested) { original.Headers.RemoveAll(SecureMailOptions.DraftHeader); return new(original, null); }
        if (options.Invalid || !Enum.IsDefined(options.Format)) throw new MailSecurityException("Choose supported security options before sending.");
        if (original.From.Mailboxes.Count() != 1) throw new MailSecurityException("Secure messages require exactly one sender address.");
        var sender = SecureMailKeys.NormalAddress(original.From.Mailboxes.Single().Address);
        if (options.Encrypt && original.Bcc.Mailboxes.Any())
            throw new MailSecurityException("Encrypt messages with To/Cc only. Send a separate encrypted message to each Bcc recipient so their key identity cannot reveal them to other recipients.");
        var entries = Keys.Load(account.Id, options.Format);
        byte[]? editableBytes = null;
        try
        {
            var parsed = entries.Select(e => (Entry: e, Parsed: SecureKeyParser.Parse(e))).ToList();
            var identity = parsed.SingleOrDefault(e => e.Entry.HasPrivateKey && e.Entry.Active && e.Entry.Email == sender);
            if (identity.Entry == null) throw new MailSecurityException("Import and select a private " + FormatName(options.Format) + " identity for " + sender + " in this account.");
            // Validate both now and at the planned sending time. The sender is always an encryption recipient
            // so their encrypted Sent copy remains readable with the same private identity.
            var at = sendingAt > DateTimeOffset.UtcNow ? sendingAt : DateTimeOffset.UtcNow;
            if (options.Sign) { SecureKeyParser.RequireUsable(identity.Parsed, identity.Entry, at, true); SecureKeyParser.RequireUsable(identity.Parsed, identity.Entry, DateTimeOffset.UtcNow, true); }
            var recipientEntries = new List<(SecureMailKeys.Entry Entry, ParsedSecureKey Parsed)>();
            if (options.Encrypt)
            {
                var recipients = original.To.Mailboxes.Concat(original.Cc.Mailboxes).Select(m => SecureMailKeys.NormalAddress(m.Address))
                    .Append(sender).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (var address in recipients)
                {
                    var recipient = address == sender ? identity : parsed.SingleOrDefault(e => !e.Entry.HasPrivateKey
                        && e.Entry.Active && e.Entry.FingerprintConfirmed && e.Entry.Email == address);
                    if (recipient.Entry == null) throw new MailSecurityException("Import and confirm the full fingerprint of " + address + " before encrypting. Nothing was queued.");
                    SecureKeyParser.RequireUsable(recipient.Parsed, recipient.Entry, at, false);
                    SecureKeyParser.RequireUsable(recipient.Parsed, recipient.Entry, DateTimeOffset.UtcNow, false);
                    recipientEntries.Add(recipient);
                }
            }
            options.WriteDraftHeader(original);
            editableBytes = BoundedBytes(original);
            var message = Composer.FromBytes(editableBytes);
            message.Headers.RemoveAll(SecureMailOptions.DraftHeader);
            try
            {
                if (options.Format == MailSecurityFormat.Smime)
                {
                    using var context = SmimeContext(parsed);
                    var body = message.Body ?? throw new MailSecurityException("The message has no MIME body to protect.");
                    if (options.Sign)
                    {
                        var signer = new CmsSigner(identity.Parsed.CertificateChain, identity.Parsed.PrivateKey!) { DigestAlgorithm = DigestAlgorithm.Sha256 };
                        body = MultipartSigned.Create(context, signer, body);
                    }
                    if (options.Encrypt)
                    {
                        var recipients = new CmsRecipientCollection();
                        foreach (var recipient in recipientEntries)
                            recipients.Add(new CmsRecipient(recipient.Parsed.Certificate!) { EncryptionAlgorithms = new[] { EncryptionAlgorithm.Aes256 } });
                        body = ApplicationPkcs7Mime.Encrypt(context, recipients, body);
                    }
                    message.Body = body;
                }
                else
                {
                    using var context = new MemoryOpenPgpContext(parsed);
                    var body = message.Body ?? throw new MailSecurityException("The message has no MIME body to protect.");
                    if (options.Sign) body = MultipartSigned.Create(context,
                        MemoryOpenPgpContext.SigningKey(identity.Entry, identity.Parsed, at), DigestAlgorithm.Sha256, body);
                    if (options.Encrypt) body = MultipartEncrypted.Encrypt(context, EncryptionAlgorithm.Aes256,
                        recipientEntries.Select(r => MemoryOpenPgpContext.EncryptionKey(r.Entry, r.Parsed, at)), body);
                    message.Body = body;
                }
                var wire = BoundedBytes(message);
                var wireDigest = SHA256.HashData(wire);
                CryptographicOperations.ZeroMemory(wire);
                byte[]? protectedRecall = null;
                if (preserveRecall)
                {
                    var required = recipientEntries.Select(r => new RequiredKey
                    {
                        Email = r.Entry.Email, Private = r.Entry.HasPrivateKey, Encrypt = true,
                        Sign = r.Entry == identity.Entry && options.Sign, MaterialDigest = SHA256.HashData(r.Entry.Material),
                    }).ToList();
                    if (!options.Encrypt) required.Add(new RequiredKey { Email = identity.Entry.Email, Private = true, Sign = true,
                        MaterialDigest = SHA256.HashData(identity.Entry.Material) });
                    var state = new RecallState { AccountId = account.Id, MessageId = original.MessageId ?? "",
                        WireDigest = wireDigest, Draft = editableBytes, Format = options.Format, Keys = required };
                    var plain = JsonSerializer.SerializeToUtf8Bytes(state);
                    try
                    {
                        protectedRecall = _protector.Protect(plain);
                        if (ReferenceEquals(protectedRecall, plain)) protectedRecall = protectedRecall.ToArray();
                    }
                    finally { CryptographicOperations.ZeroMemory(plain); }
                }
                return new(message, protectedRecall);
            }
            catch { message.Dispose(); throw; }
        }
        catch (MailSecurityException) { throw; }
        catch { throw new MailSecurityException("The selected security operation failed. No unprotected message was queued; check the account keys and recipient certificates."); }
        finally
        {
            original.Headers.RemoveAll(SecureMailOptions.DraftHeader);
            if (editableBytes != null) CryptographicOperations.ZeroMemory(editableBytes);
            SecureMailKeys.Dispose(entries);
        }
    }

    public Draft RestoreQueuedDraft(OutboxItem item, byte[]? protectedRecall)
    {
        var message = RestoreQueuedMessage(item, protectedRecall);
        try { return Composer.FromMime(message, item.AccountId, item.ThreadKey); }
        catch { message.Dispose(); throw; }
        // Draft owns the returned MIME attachment streams, matching Composer.FromMime's normal ownership.
    }

    /// <summary>For last-minute cloud-link checks: authenticated recovery content, no attachment ownership or cache writes.</summary>
    public (string Html, string Text) ReadQueuedBody(OutboxItem item, byte[]? protectedRecall)
    {
        using var message = RestoreQueuedMessage(item, protectedRecall);
        var html = message.HtmlBody ?? MimeText.TextToHtml(message.TextBody ?? "");
        return (html, message.TextBody ?? MimeText.HtmlToText(html));
    }

    private MimeMessage RestoreQueuedMessage(OutboxItem item, byte[]? protectedRecall)
    {
        if (protectedRecall == null)
        {
            var ordinary = Composer.FromBytes(item.Mime);
            if (IsProtected(ordinary.Body)) { ordinary.Dispose(); throw new MailSecurityException("The protected recovery copy for this queued message is unavailable. Keep the queued message or restore this profile's protected recovery data."); }
            return ordinary;
        }
        byte[]? plain = null;
        RecallState? state = null;
        try
        {
            if (protectedRecall.Length > MaxMessageBytes * 2) throw new MailSecurityException("The queued recovery copy exceeds its safety limit.");
            plain = _protector.Unprotect(protectedRecall);
            state = JsonSerializer.Deserialize<RecallState>(plain) ?? throw new JsonException();
            if (state.Version != 1 || state.AccountId != item.AccountId || state.Draft.Length > MaxMessageBytes
                || !CryptographicOperations.FixedTimeEquals(state.WireDigest, SHA256.HashData(item.Mime)))
                throw new MailSecurityException("The protected recovery copy does not match this queued message.");
            var message = Composer.FromBytes(state.Draft);
            if (!string.Equals(message.MessageId, state.MessageId, StringComparison.Ordinal)
                || Threading.NormalizeId(message.MessageId) != item.MessageId)
            { message.Dispose(); throw new MailSecurityException("The protected recovery copy has a different message identity."); }
            return message;
        }
        catch (MailSecurityException) { throw; }
        catch { throw new MailSecurityException("This operating-system user cannot unlock the queued recovery copy. The queued message was kept unchanged."); }
        finally
        {
            if (state != null) CryptographicOperations.ZeroMemory(state.Draft);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Recheck the exact selected identities before SMTP, including after restart or a long schedule.</summary>
    public void ValidateQueued(OutboxItem item, byte[]? protectedRecall)
    {
        if (protectedRecall == null)
        {
            using var message = Composer.FromBytes(item.Mime);
            if (IsProtected(message.Body)) throw new MailSecurityException("This protected queued message has no matching local security record. Reopen and review it before sending.");
            return;
        }
        byte[]? plain = null;
        RecallState? state = null;
        List<SecureMailKeys.Entry>? entries = null;
        try
        {
            if (protectedRecall.Length > MaxMessageBytes * 2) throw new MailSecurityException("The queued security record exceeds its safety limit.");
            plain = _protector.Unprotect(protectedRecall);
            state = JsonSerializer.Deserialize<RecallState>(plain) ?? throw new JsonException();
            if (state.Version != 1 || state.AccountId != item.AccountId || !Enum.IsDefined(state.Format)
                || state.Keys.Count is 0 or > 1000 || !CryptographicOperations.FixedTimeEquals(state.WireDigest, SHA256.HashData(item.Mime)))
                throw new MailSecurityException("The queued message no longer matches its security record.");
            entries = Keys.Load(item.AccountId, state.Format);
            foreach (var required in state.Keys)
            {
                var selected = entries.SingleOrDefault(e => e.Active && e.FingerprintConfirmed
                    && e.HasPrivateKey == required.Private && e.Email == required.Email);
                if (selected == null || !CryptographicOperations.FixedTimeEquals(required.MaterialDigest, SHA256.HashData(selected.Material)))
                    throw new MailSecurityException("A selected security key changed or was removed after this message was queued. Reopen, review and queue it again.");
                var parsed = SecureKeyParser.Parse(selected);
                if (required.Sign)
                {
                    SecureKeyParser.RequireUsable(parsed, selected, DateTimeOffset.UtcNow, true);
                    if (state.Format == MailSecurityFormat.OpenPgp) _ = MemoryOpenPgpContext.SigningKey(selected, parsed, DateTimeOffset.UtcNow);
                }
                if (required.Encrypt)
                {
                    SecureKeyParser.RequireUsable(parsed, selected, DateTimeOffset.UtcNow, false);
                    if (state.Format == MailSecurityFormat.OpenPgp) _ = MemoryOpenPgpContext.EncryptionKey(selected, parsed, DateTimeOffset.UtcNow);
                }
            }
        }
        catch (MailSecurityException) { throw; }
        catch { throw new MailSecurityException("The security record cannot be unlocked. Nothing was sent without protection."); }
        finally
        {
            if (state != null) CryptographicOperations.ZeroMemory(state.Draft);
            if (plain != null) CryptographicOperations.ZeroMemory(plain);
            if (entries != null) SecureMailKeys.Dispose(entries);
        }
    }

    public static bool IsProtected(MimeEntity? entity) => entity is MultipartSigned or MultipartEncrypted or ApplicationPkcs7Mime;
    public static string FormatName(MailSecurityFormat format) => format == MailSecurityFormat.Smime ? "S/MIME" : "OpenPGP";

    public SecureMailReadResult Open(MimeMessage source, string accountId)
    {
        var sourceBytes = BoundedBytes(source);
        MimeMessage message;
        try { message = Composer.FromBytes(sourceBytes); }
        finally { CryptographicOperations.ZeroMemory(sourceBytes); }
        var entries = new List<SecureMailKeys.Entry>();
        try
        {
            entries.AddRange(Keys.Load(accountId, MailSecurityFormat.Smime));
            entries.AddRange(Keys.Load(accountId, MailSecurityFormat.OpenPgp));
            var parsed = entries.Select(e => (Entry: e, Parsed: SecureKeyParser.Parse(e))).ToList();
            using var smime = SmimeContext(parsed.Where(e => e.Entry.Format == MailSecurityFormat.Smime).ToList());
            using var pgp = new MemoryOpenPgpContext(parsed.Where(e => e.Entry.Format == MailSecurityFormat.OpenPgp).ToList());
            var reports = new List<MailSignatureReport>();
            var encrypted = false;
            var protectedMessage = IsProtected(message.Body);
            for (var level = 0; level < 6 && IsProtected(message.Body); level++)
            {
                switch (message.Body)
                {
                    case MultipartSigned signed:
                        if (signed.Count != 2) throw new MailSecurityException("The signed MIME structure is incomplete.");
                        try
                        {
                            var signatures = signed.Verify(signed.ContentType.Parameters["protocol"]?.Contains("pgp", StringComparison.OrdinalIgnoreCase) == true ? pgp : smime);
                            reports.AddRange(Describe(signatures, message.From.Mailboxes.SingleOrDefault()?.Address, parsed));
                        }
                        catch { reports.Add(new("The message-body signature could not be verified. Import the sender's public key if it is missing.", null, "", "", false, false, false, "")); }
                        message.Body = signed[0];
                        break;
                    case ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.EnvelopedData } envelope:
                        message.Body = envelope.Decrypt(smime); encrypted = true;
                        break;
                    case ApplicationPkcs7Mime { SecureMimeType: SecureMimeType.SignedData } opaque:
                        var opaqueSignatures = opaque.Verify(smime, out var content);
                        reports.AddRange(Describe(opaqueSignatures, message.From.Mailboxes.SingleOrDefault()?.Address, parsed));
                        message.Body = content;
                        break;
                    case MultipartEncrypted pgpEnvelope:
                        if (pgpEnvelope.Count != 2 || pgpEnvelope[1] is not MimePart payload)
                            throw new MailSecurityException("The OpenPGP encrypted message is incomplete.");
                        using (var encoded = new SecurityBuffer(MaxMessageBytes))
                        {
                            (payload.Content ?? throw new MailSecurityException("The encrypted payload is missing.")).DecodeTo(encoded);
                            var bytes = encoded.ToArray();
                            VerifyPgpIntegrity(bytes, pgp);
                            using var input = new MemoryStream(bytes, false);
                            using var output = new SecurityBuffer(MaxMessageBytes);
                            var signatures = pgp.DecryptTo(input, output);
                            if (signatures != null) reports.AddRange(Describe(signatures, message.From.Mailboxes.SingleOrDefault()?.Address, parsed));
                            output.Position = 0;
                            message.Body = MimeEntity.Load(output);
                        }
                        encrypted = true;
                        break;
                    default: throw new MailSecurityException("This secure-message format is not supported. No decrypted content was cached.");
                }
            }
            if (IsProtected(message.Body)) throw new MailSecurityException("The message has too many nested security layers.");
            using (var sizeCheck = new SecurityBuffer(MaxMessageBytes)) message.WriteTo(sizeCheck);
            return new() { Message = message, Encrypted = encrypted, Protected = protectedMessage, Signatures = reports };
        }
        catch (MailSecurityException) { message.Dispose(); throw; }
        catch { message.Dispose(); throw new MailSecurityException("The message could not be decrypted or authenticated. Import the matching private key for this account; damaged messages remain hidden."); }
        finally { SecureMailKeys.Dispose(entries); }
    }

    private static TemporarySecureMimeContext SmimeContext(List<(SecureMailKeys.Entry Entry, ParsedSecureKey Parsed)> entries)
    {
        var context = new TemporarySecureMimeContext { CheckCertificateRevocation = false };
        foreach (var algorithm in Enum.GetValues<EncryptionAlgorithm>())
            if (algorithm != EncryptionAlgorithm.Aes256) context.Disable(algorithm);
        foreach (var (entry, parsed) in entries)
        {
            if (entry.HasPrivateKey) { using var input = new MemoryStream(entry.Material, false); context.Import(input, entry.Password); }
            else context.Import(parsed.Certificate!);
        }
        return context;
    }

    private static IEnumerable<MailSignatureReport> Describe(DigitalSignatureCollection signatures, string? from,
        List<(SecureMailKeys.Entry Entry, ParsedSecureKey Parsed)> entries)
    {
        foreach (var signature in signatures)
        {
            bool? valid;
            try { valid = signature.Verify(verifySignatureOnly: true); } catch { valid = null; }
            var cert = signature.SignerCertificate;
            var email = cert?.Email ?? "";
            var fingerprint = cert?.Fingerprint ?? "";
            var matches = false;
            var current = cert != null && cert.CreationDate.ToUniversalTime() <= DateTime.UtcNow && cert.ExpirationDate.ToUniversalTime() > DateTime.UtcNow;
            if (cert is SecureMimeDigitalCertificate smime)
            {
                fingerprint = Convert.ToHexString(SHA256.HashData(smime.Certificate.GetEncoded()));
                matches = from != null && SecureKeyParser.CertificateEmails(smime.Certificate).Contains(from, StringComparer.OrdinalIgnoreCase);
            }
            if (cert is OpenPgpDigitalCertificate pgp)
            {
                var primary = pgp.KeyRing.GetPublicKey();
                fingerprint = Convert.ToHexString(primary.GetFingerprint());
                // A valid subkey signature alone does not establish a binding to the pinned primary key.
                // Until signing-subkey back-signatures are supported, report its math but never imply sender trust.
                matches = pgp.PublicKey.IsMasterKey && pgp.PublicKey.KeyId == primary.KeyId
                    && from != null && SecureKeyParser.CertifiedIdentity(primary, from) != null;
                current &= matches && SecureKeyParser.UsablePgpKeys(pgp.KeyRing, from!, true).Any(k => k.KeyId == pgp.PublicKey.KeyId);
            }
            if (matches && from != null) email = from;
            var confirmed = matches && from != null && entries.Any(e => e.Entry.FingerprintConfirmed
                && e.Entry.Email.Equals(from, StringComparison.OrdinalIgnoreCase) && e.Parsed.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
            var status = valid == true ? "Message-body signature is valid." : valid == false ? "INVALID message-body signature: content was changed." : "Message-body signature could not be verified.";
            status += matches ? " Signing identity matches From." : " Signing identity does not establish the From address.";
            status += confirmed ? " Fingerprint was confirmed locally." : " Sender fingerprint has not been confirmed locally.";
            if (!current) status += " The key is expired, revoked, or its validity is unknown.";
            status += " Certificate-chain trust and online revocation were not checked.";
            yield return new(status, valid, email, fingerprint, matches, confirmed, current, signature.DigestAlgorithm.ToString());
        }
    }

    private static void VerifyPgpIntegrity(byte[] encrypted, MemoryOpenPgpContext context)
    {
        // MimeKit 4.18 DecryptTo parses OpenPGP content but does not call PgpEncryptedData.Verify().
        // Verify the MDC before permitting any plaintext to reach a reader, then let MimeKit parse/verify MIME.
        using var input = new MemoryStream(encrypted, false);
        using var decoder = PgpUtilities.GetDecoderStream(input);
        var factory = new PgpObjectFactory(decoder);
        var packet = factory.NextPgpObject();
        if (packet is PgpMarker) packet = factory.NextPgpObject();
        if (packet is not PgpEncryptedDataList list) throw new MailSecurityException("Invalid OpenPGP encrypted data.");
        foreach (var item in list.GetEncryptedDataObjects().OfType<PgpPublicKeyEncryptedData>())
        {
            PgpSecretKey key;
            try { key = context.SecretKey(item.KeyId); }
            catch (PrivateKeyNotFoundException) { continue; }
            if (!item.IsIntegrityProtected()) throw new MailSecurityException("Legacy OpenPGP encryption without integrity protection is not opened by this reader.");
            var password = context.Password(key).ToCharArray();
            try
            {
                using var clear = item.GetDataStream(key.ExtractPrivateKey(password));
                using var bounded = new SecurityBuffer(MaxMessageBytes);
                clear.CopyTo(bounded);
                if (!item.Verify()) throw new MailSecurityException("OpenPGP integrity verification failed. The decrypted message was not displayed.");
                return;
            }
            finally { Array.Clear(password); }
        }
        throw new MailSecurityException("Import the matching OpenPGP private key into this account to decrypt this message.");
    }

    internal static byte[] BoundedBytes(MimeMessage message)
    {
        using var stream = new SecurityBuffer(MaxMessageBytes);
        message.WriteTo(stream);
        return stream.ToArray();
    }

    internal sealed class SecurityBuffer(int maximum) : MemoryStream
    {
        private void Check(int count) { if (count > maximum - Length) throw new MailSecurityException("This secure message exceeds the 32 MB safety limit."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        protected override void Dispose(bool disposing)
        { if (disposing && TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan()); base.Dispose(disposing); }
    }
}
