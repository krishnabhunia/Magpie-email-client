using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Updates;
using MimeKit;
using MimeKit.Cryptography;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Security;
using X509KeyUsageFlags = System.Security.Cryptography.X509Certificates.X509KeyUsageFlags;

namespace Magpie.Core.Tests;

public sealed class SecureMailFixtures
{
    public const string Password = "offline-fixture-passphrase";
    public const string Sender = "sender@example.test", Recipient = "recipient@example.test";
    private readonly Dictionary<(MailSecurityFormat, string), (byte[] Private, byte[] Public)> _keys = new();
    public SecureMailFixtures()
    {
        foreach (var format in Enum.GetValues<MailSecurityFormat>())
            foreach (var email in new[] { Sender, Recipient }) _keys[(format, email)] = Generate(format, email);
    }
    public (byte[] Private, byte[] Public) Key(MailSecurityFormat format, string email) => _keys[(format, email)];
    public static (byte[] Private, byte[] Public) Generate(MailSecurityFormat format, string email, bool expired = false, bool encryptionSubkey = false)
    {
        using var rsa = RSA.Create(2048);
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        if (format == MailSecurityFormat.Smime)
        {
            var request = new CertificateRequest("CN=Offline mail test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder(); names.AddEmailAddress(email);
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.4") }, true));
            using var cert = request.CreateSelfSigned(created, expired ? created.AddMinutes(1) : created.AddDays(7));
            return (cert.Export(X509ContentType.Pkcs12, Password), cert.Export(X509ContentType.Cert));
        }
        var pair = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, DotNetUtilities.GetRsaKeyPair(rsa), created.UtcDateTime);
        var flags = new PgpSignatureSubpacketGenerator();
        flags.SetKeyFlags(false, encryptionSubkey ? 1 | 2 : 1 | 2 | 4 | 8);
        flags.SetKeyExpirationTime(false, expired ? 60 : 7 * 24 * 60 * 60);
        flags.SetPreferredSymmetricAlgorithms(false, new[] { (int)SymmetricKeyAlgorithmTag.Aes256 });
        var generator = new PgpKeyRingGenerator(PgpSignature.PositiveCertification, pair, "Offline test <" + email + ">",
            SymmetricKeyAlgorithmTag.Aes256, HashAlgorithmTag.Sha256, Password.ToCharArray(), true, flags.Generate(), null, new SecureRandom());
        if (encryptionSubkey)
        {
            using var encryptionRsa = RSA.Create(2048);
            var subkey = new PgpKeyPair(PublicKeyAlgorithmTag.RsaEncrypt, DotNetUtilities.GetRsaKeyPair(encryptionRsa), created.UtcDateTime);
            var subFlags = new PgpSignatureSubpacketGenerator(); subFlags.SetKeyFlags(false, 4 | 8); subFlags.SetKeyExpirationTime(false, 7 * 24 * 60 * 60);
            generator.AddSubKey(subkey, subFlags.Generate(), null, HashAlgorithmTag.Sha256);
        }
        return (generator.GenerateSecretKeyRing().GetEncoded(), generator.GeneratePublicKeyRing().GetEncoded());
    }
}

/// <summary>Generated offline keys exercise real MimeKit and BouncyCastle operations, without SMTP or keyservers.</summary>
public sealed class SecureMailTests(SecureMailFixtures fixtures) : IClassFixture<SecureMailFixtures>
{
    private static Account Account(string id = "A", string email = SecureMailFixtures.Sender) => new() { Id = id, Email = email, Enabled = false };
    private const string Body = "Confidential body with no final newline";
    private static MimeMessage Message()
    {
        var message = new MimeMessage { MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(), Subject = "Visible subject", Date = DateTimeOffset.UtcNow };
        message.From.Add(MailboxAddress.Parse(SecureMailFixtures.Sender));
        message.To.Add(MailboxAddress.Parse(SecureMailFixtures.Recipient));
        message.Body = new TextPart("plain") { Text = Body, ContentTransferEncoding = ContentEncoding.Base64 };
        return message;
    }
    private SecureMailService Service(TempDir dir, MailSecurityFormat format, TestProtector protector, bool recipient = true)
    {
        var service = new SecureMailService(dir.File("keys.protected"), protector);
        var sender = fixtures.Key(format, SecureMailFixtures.Sender);
        service.Keys.Import("A", SecureMailFixtures.Sender, format, sender.Private, SecureMailFixtures.Password, true);
        if (recipient)
        {
            var key = service.Keys.Import("A", SecureMailFixtures.Recipient, format, fixtures.Key(format, SecureMailFixtures.Recipient).Public, "", false);
            service.Keys.Select("A", key.Id, key.Fingerprint);
        }
        return service;
    }
    private void ImportReceiver(SecureMailService service, MailSecurityFormat format)
    {
        service.Keys.Import("B", SecureMailFixtures.Recipient, format, fixtures.Key(format, SecureMailFixtures.Recipient).Private, SecureMailFixtures.Password, true);
        var sender = service.Keys.Import("B", SecureMailFixtures.Sender, format, fixtures.Key(format, SecureMailFixtures.Sender).Public, "", false);
        service.Keys.Select("B", sender.Id, sender.Fingerprint);
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime, true, false)]
    [InlineData(MailSecurityFormat.Smime, false, true)]
    [InlineData(MailSecurityFormat.Smime, true, true)]
    [InlineData(MailSecurityFormat.OpenPgp, true, false)]
    [InlineData(MailSecurityFormat.OpenPgp, false, true)]
    [InlineData(MailSecurityFormat.OpenPgp, true, true)]
    public void Wire_roundtrip_decrypts_for_recipient_and_sender_and_verifies_body(MailSecurityFormat format, bool sign, bool encrypt)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector); ImportReceiver(service, format);
        using var source = Message();
        var outgoing = service.PrepareOutgoing(source, Account(), new() { Format = format, Sign = sign, Encrypt = encrypt }, DateTimeOffset.UtcNow);
        using var wire = Composer.FromBytes(Composer.ToBytes(outgoing.Message));
        Assert.Null(wire.Headers[SecureMailOptions.DraftHeader]);
        if (encrypt) Assert.DoesNotContain(Body, Encoding.UTF8.GetString(Composer.ToBytes(wire)));
        foreach (var account in new[] { "A", "B" })
        {
            using var read = service.Open(wire, account);
            Assert.Equal(encrypt, read.Encrypted);
            Assert.Equal(Body, read.Message.TextBody?.Replace("\r\n", "\n"));
            if (sign)
            {
                var signature = Assert.Single(read.Signatures);
                Assert.True(signature.Valid); Assert.True(signature.MatchesSender); Assert.True(signature.FingerprintConfirmed); Assert.True(signature.KeyCurrentlyValid);
                Assert.Contains("Certificate-chain trust and online revocation were not checked", signature.Status);
            }
            else Assert.Empty(read.Signatures);
        }
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Modified_signed_content_is_never_reported_valid(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector);
        var outgoing = service.PrepareOutgoing(Message(), Account(), new() { Format = format, Sign = true }, DateTimeOffset.UtcNow);
        var signed = Assert.IsType<MultipartSigned>(outgoing.Message.Body);
        Assert.IsType<TextPart>(signed[0]).Text = "Tampered content";
        using var read = service.Open(outgoing.Message, "A");
        Assert.NotEqual(true, Assert.Single(read.Signatures).Valid);
    }

    [Fact]
    public void OpenPgp_encrypted_payload_mdc_tamper_is_rejected_before_content_is_returned()
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, MailSecurityFormat.OpenPgp, protector);
        var outgoing = service.PrepareOutgoing(Message(), Account(), new() { Format = MailSecurityFormat.OpenPgp, Encrypt = true }, DateTimeOffset.UtcNow);
        var payload = Assert.IsType<MimePart>(Assert.IsType<MultipartEncrypted>(outgoing.Message.Body)[1]);
        using var encoded = new MemoryStream(); payload.Content!.DecodeTo(encoded); encoded.Position = 0;
        using var decoded = PgpUtilities.GetDecoderStream(encoded); using var raw = new MemoryStream(); decoded.CopyTo(raw);
        var bytes = raw.ToArray(); bytes[^1] ^= 0x20;
        payload.Content = new MimeContent(new MemoryStream(bytes));
        Assert.Throws<MailSecurityException>(() => service.Open(outgoing.Message, "A"));
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Missing_unconfirmed_wrong_account_and_bcc_keys_fail_closed(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector, recipient: false);
        var options = new SecureMailOptions { Format = format, Encrypt = true, Sign = true };
        Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(Message(), Account(), options, DateTimeOffset.UtcNow));
        var key = service.Keys.Import("A", SecureMailFixtures.Recipient, format, fixtures.Key(format, SecureMailFixtures.Recipient).Public, "", false);
        Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(Message(), Account(), options, DateTimeOffset.UtcNow));
        Assert.Throws<MailSecurityException>(() => service.Keys.Select("A", key.Id, "incorrect fingerprint"));
        service.Keys.Select("A", key.Id, key.Fingerprint);
        Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(Message(), Account("Wrong"), options, DateTimeOffset.UtcNow));
        using var bcc = Message(); bcc.Bcc.Add(MailboxAddress.Parse("hidden@example.test"));
        Assert.Contains("Bcc", Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(bcc, Account(), options, DateTimeOffset.UtcNow)).Message);
        var signed = service.PrepareOutgoing(bcc, Account(), new() { Format = format, Sign = true }, DateTimeOffset.UtcNow);
        Assert.Single(signed.Message.Bcc.Mailboxes);
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Wrong_email_password_expired_key_and_future_schedule_are_rejected(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector);
        var key = fixtures.Key(format, SecureMailFixtures.Recipient);
        Assert.Throws<MailSecurityException>(() => service.Keys.Import("B", "someoneelse@example.test", format, key.Private, SecureMailFixtures.Password, true));
        Assert.Throws<MailSecurityException>(() => service.Keys.Import("B", SecureMailFixtures.Recipient, format, key.Private, "wrong", true));
        Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(Message(), Account(), new() { Format = format, Sign = true }, DateTimeOffset.UtcNow.AddDays(8)));
        var expired = SecureMailFixtures.Generate(format, SecureMailFixtures.Sender, expired: true);
        var expiredKey = service.Keys.Import("A", SecureMailFixtures.Sender, format, expired.Private, SecureMailFixtures.Password, true);
        service.Keys.Select("A", expiredKey.Id, expiredKey.Fingerprint);
        Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(Message(), Account(), new() { Format = format, Sign = true }, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Protected_store_restart_export_public_and_wrong_protector_do_not_expose_private_material(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector);
        var disk = File.ReadAllBytes(dir.File("keys.protected"));
        Assert.DoesNotContain(SecureMailFixtures.Password, Encoding.UTF8.GetString(disk));
        var reopened = new SecureMailService(dir.File("keys.protected"), protector);
        var identity = reopened.Keys.List("A").Single(k => k.HasPrivateKey);
        var exported = reopened.Keys.ExportPublic("A", identity.Id);
        reopened.Keys.Import("B", SecureMailFixtures.Sender, format, exported, "", false);
        Assert.False(Assert.Single(reopened.Keys.List("B")).HasPrivateKey);
        disk = File.ReadAllBytes(dir.File("keys.protected"));
        using var wrong = new TestProtector();
        Assert.Throws<MailSecurityException>(() => new SecureMailService(dir.File("keys.protected"), wrong).Keys.List("A"));
        Assert.Equal(disk, File.ReadAllBytes(dir.File("keys.protected")));
        Assert.Single(Directory.GetFiles(dir.Path));
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Queue_restart_undo_and_draft_history_preserve_security_intent(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector(); long queued, local, revision;
        using (var engine = Engine(dir, protector))
        {
            ImportIntoEngine(engine, format);
            var draft = new Draft { AccountId = "A", To = SecureMailFixtures.Recipient, Subject = "Secret draft", Html = "<p>Exact private draft</p>",
                Security = new() { Format = format, Sign = true, Encrypt = true } };
            local = engine.SaveLocalDraft(draft, false);
            revision = engine.CaptureDraftRevision(draft);
            queued = engine.QueueSend(draft, DateTimeOffset.UtcNow.AddHours(1), null);
            Assert.NotNull(engine.Store.SecureOutboxState(queued));
            Assert.DoesNotContain("Exact private draft", Encoding.UTF8.GetString(engine.Store.GetOutbox().Single().Mime));
        }
        using var reopened = Engine(dir, protector);
        Assert.True(reopened.OpenLocalDraft(local)!.Security.Encrypt);
        var item = Assert.Single(reopened.Store.GetOutbox());
        var history = reopened.Store.OpenDraftRevision("A", item.MessageId, revision)!;
        Assert.True(history.Security.Sign); Assert.True(history.Security.Encrypt); Assert.Equal(format, history.Security.Format);
        // Undo uses the protected recovery snapshot even if the identity has since been removed.
        foreach (var key in reopened.SecureMail.Keys.List("A")) reopened.SecureMail.Keys.Remove("A", key.Id);
        var recalled = reopened.Recall(queued)!;
        Assert.Contains("Exact private draft", recalled.Html); Assert.True(recalled.Security.Encrypt); Assert.True(recalled.Security.Sign);
        Assert.Equal(format, recalled.Security.Format); Assert.Empty(reopened.Store.GetOutbox());
        Assert.Null(reopened.Store.SecureOutboxState(queued));
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public async Task Outbox_rechecks_keys_before_smtp_and_sends_only_protected_mime(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector(); var sent = new List<byte[]>();
        using var engine = Engine(dir, protector, (_, mime, _) => { sent.Add(Composer.ToBytes(mime)); return Task.CompletedTask; });
        ImportIntoEngine(engine, format);
        Draft Draft() => new() { AccountId = "A", To = SecureMailFixtures.Recipient, Html = "<p>Private outgoing text</p>", Security = new() { Format = format, Sign = true, Encrypt = true } };
        var first = engine.QueueSend(Draft(), DateTimeOffset.UtcNow.AddSeconds(-1), null);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Single(sent); Assert.Null(engine.Store.SecureOutboxState(first));
        using var wire = Composer.FromBytes(sent[0]); Assert.True(SecureMailService.IsProtected(wire.Body));
        var blocked = engine.QueueSend(Draft(), DateTimeOffset.UtcNow.AddSeconds(-1), null);
        var recipient = engine.SecureMail.Keys.List("A").Single(k => !k.HasPrivateKey);
        engine.SecureMail.Keys.Remove("A", recipient.Id);
        await engine.ProcessOutboxAsync(CancellationToken.None);
        Assert.Single(sent); Assert.NotNull(engine.Store.SecureOutboxState(blocked));
        Assert.NotNull(engine.Recall(blocked));
    }

    [Fact]
    public void Invalid_draft_security_metadata_is_not_silently_downgraded()
    {
        using var message = Message(); message.Headers.Add(SecureMailOptions.DraftHeader, "smime; sign=perhaps; encrypt=yes");
        var draft = Composer.FromMime(message, "A", "thread"); Assert.True(draft.Security.Invalid);
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = new SecureMailService(dir.File("keys"), protector);
        Assert.Throws<MailSecurityException>(() => service.PrepareOutgoing(message, Account(), draft.Security, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Key_import_read_is_bounded_and_account_removal_leaves_other_accounts_intact()
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        using (var file = File.Create(dir.File("oversized-key"))) file.SetLength(SecureMailKeys.MaxImportBytes + 1L);
        Assert.Throws<MailSecurityException>(() => SecureMailKeys.ReadImportFile(dir.File("oversized-key")));
        File.Delete(dir.File("oversized-key"));
        using var engine = Engine(dir, protector); ImportIntoEngine(engine, MailSecurityFormat.Smime);
        engine.SecureMail.Keys.Import("B", SecureMailFixtures.Recipient, MailSecurityFormat.Smime,
            fixtures.Key(MailSecurityFormat.Smime, SecureMailFixtures.Recipient).Private, SecureMailFixtures.Password, true);
        engine.RemoveAccount("A");
        Assert.Empty(engine.SecureMail.Keys.List("A")); Assert.Single(engine.SecureMail.Keys.List("B"));
    }

    [Fact]
    public async Task Paired_version_snapshot_restores_keys_with_the_encrypted_outbox()
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var paths = new AppPaths(dir.Path); var version = AppVersion.TryParse("15.0.0")!;
        var application = dir.File("Magpie.exe"); File.WriteAllText(application, "offline application snapshot fixture");
        VersionSnapshot snapshot; byte[] keyFile; long queued;
        using (var engine = Engine(dir, protector))
        {
            ImportIntoEngine(engine, MailSecurityFormat.Smime); engine.Settings.Save(notify: false);
            queued = engine.QueueSend(new Draft { AccountId = "A", To = SecureMailFixtures.Recipient, Html = "<p>Restored encrypted draft</p>",
                Security = new() { Sign = true, Encrypt = true } }, DateTimeOffset.UtcNow.AddHours(1), null);
            await engine.StopForVersionChangeAsync();
            keyFile = File.ReadAllBytes(Path.Combine(paths.Root, SecureMailKeys.FileName));
            snapshot = VersionBackup.Create(paths, version, application);
        }
        var keyPath = Path.Combine(paths.Root, SecureMailKeys.FileName);
        new SecureMailService(keyPath, protector).Keys.RemoveAccount("A");
        VersionBackup.StageRestore(paths, snapshot.Id);
        Assert.True(VersionBackup.ApplyPending(paths, version));
        Assert.Equal(keyFile, File.ReadAllBytes(keyPath));
        using var restored = Engine(dir, protector);
        var item = Assert.Single(restored.Store.GetOutbox());
        restored.SecureMail.ValidateQueued(item, restored.Store.SecureOutboxState(queued));
        Assert.Contains("Restored encrypted draft", restored.Recall(queued)!.Html);
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Signature_validity_sender_binding_and_local_trust_are_separate(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector);
        var outgoing = service.PrepareOutgoing(Message(), Account(), new() { Format = format, Sign = true }, DateTimeOffset.UtcNow);
        service.Keys.Import("B", SecureMailFixtures.Sender, format, fixtures.Key(format, SecureMailFixtures.Sender).Public, "", false);
        using (var read = service.Open(outgoing.Message, "B"))
        {
            var signature = Assert.Single(read.Signatures);
            Assert.True(signature.Valid); Assert.True(signature.MatchesSender); Assert.False(signature.FingerprintConfirmed);
        }
        outgoing.Message.From.Clear(); outgoing.Message.From.Add(MailboxAddress.Parse("impostor@example.test"));
        using var changedHeader = service.Open(outgoing.Message, "B");
        Assert.True(Assert.Single(changedHeader.Signatures).Valid);
        Assert.False(changedHeader.Signatures[0].MatchesSender);
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Decryption_requires_private_key_in_the_selected_account_and_undo_is_payload_bound(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector);
        var outgoing = service.PrepareOutgoing(Message(), Account(), new() { Format = format, Sign = true, Encrypt = true }, DateTimeOffset.UtcNow);
        Assert.Throws<MailSecurityException>(() => service.Open(outgoing.Message, "OtherAccount"));
        var item = new OutboxItem { AccountId = "A", Mime = Composer.ToBytes(outgoing.Message), MessageId = Threading.NormalizeId(outgoing.Message.MessageId) };
        service.ValidateQueued(item, outgoing.ProtectedRecall);
        var recoveredBody = service.ReadQueuedBody(item, outgoing.ProtectedRecall);
        Assert.Equal(Body, recoveredBody.Text.Replace("\r\n", "\n"));
        Assert.Throws<MailSecurityException>(() => Composer.FromMime(outgoing.Message, "A", "thread"));
        item.Mime[^1] ^= 1;
        Assert.Throws<MailSecurityException>(() => service.RestoreQueuedDraft(item, outgoing.ProtectedRecall));
        Assert.Throws<MailSecurityException>(() => service.ValidateQueued(item, outgoing.ProtectedRecall));
        Assert.Throws<MailSecurityException>(() => service.ReadQueuedBody(item, outgoing.ProtectedRecall));
    }

    [Fact]
    public void OpenPgp_verified_encryption_subkey_and_primary_signing_key_roundtrip()
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = new SecureMailService(dir.File("keys.protected"), protector);
        var key = SecureMailFixtures.Generate(MailSecurityFormat.OpenPgp, SecureMailFixtures.Sender, encryptionSubkey: true);
        service.Keys.Import("A", SecureMailFixtures.Sender, MailSecurityFormat.OpenPgp, key.Private, SecureMailFixtures.Password, true);
        using var source = Message(); source.To.Clear(); source.To.Add(source.From.Single());
        var outgoing = service.PrepareOutgoing(source, Account(), new() { Format = MailSecurityFormat.OpenPgp, Sign = true, Encrypt = true }, DateTimeOffset.UtcNow);
        using var read = service.Open(outgoing.Message, "A");
        Assert.Equal(Body, read.Message.TextBody); Assert.True(Assert.Single(read.Signatures).Valid);
    }

    [Theory]
    [InlineData(NewLineFormat.Dos)]
    [InlineData(NewLineFormat.Unix)]
    public void Legacy_OpenPgp_without_integrity_protection_is_not_opened(NewLineFormat lineEndings)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, MailSecurityFormat.OpenPgp, protector);
        var key = new PgpPublicKeyRing(fixtures.Key(MailSecurityFormat.OpenPgp, SecureMailFixtures.Sender).Public).GetPublicKey();
        var generator = new PgpEncryptedDataGenerator(SymmetricKeyAlgorithmTag.Aes256, false, new SecureRandom()); generator.AddMethod(key);
        using var encoded = new MemoryStream();
        using (var encrypted = generator.Open(encoded, new byte[4096]))
        {
            var literal = new PgpLiteralDataGenerator(); var bytes = Encoding.UTF8.GetBytes("Content-Type: text/plain\r\n\r\nUnauthenticated");
            using var clear = literal.Open(encrypted, PgpLiteralData.Binary, "message", bytes.Length, DateTime.UtcNow); clear.Write(bytes);
        }
        using var message = Message();
        message.Body = new MultipartEncrypted
        {
            new MimePart("application", "pgp-encrypted") { Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes("Version: 1\r\n"))) },
            new MimePart("application", "octet-stream")
            {
                Content = new MimeContent(new MemoryStream(encoded.ToArray())),
                ContentTransferEncoding = ContentEncoding.Base64,
            },
        };
        // This is binary packet data, not ASCII-armoured OpenPGP. Encode it explicitly so a MIME
        // newline conversion cannot corrupt the packet before the intended integrity check runs.
        var options = FormatOptions.Default.Clone(); options.NewLineFormat = lineEndings;
        using var wire = new MemoryStream(); message.WriteTo(options, wire); wire.Position = 0;
        using var reopened = MimeMessage.Load(wire);
        using var decoded = new MemoryStream();
        var payload = Assert.IsType<MimePart>(Assert.IsType<MultipartEncrypted>(reopened.Body)[1]);
        Assert.NotNull(payload.Content);
        payload.Content.DecodeTo(decoded);
        Assert.Equal(encoded.ToArray(), decoded.ToArray());
        Assert.Contains("integrity protection", Assert.Throws<MailSecurityException>(() => service.Open(reopened, "A")).Message);
    }

    [Theory]
    [InlineData(MailSecurityFormat.Smime)]
    [InlineData(MailSecurityFormat.OpenPgp)]
    public void Actual_From_address_selects_identity_and_reader_writes_no_plaintext_files(MailSecurityFormat format)
    {
        using var dir = new TempDir(); using var protector = new TestProtector();
        var service = Service(dir, format, protector);
        using var source = Message();
        var outgoing = service.PrepareOutgoing(source, Account(email: "base-account@example.test"), new() { Format = format, Sign = true, Encrypt = true }, DateTimeOffset.UtcNow);
        var before = Directory.GetFiles(dir.Path).ToDictionary(p => p, File.ReadAllBytes);
        using var read = service.Open(outgoing.Message, "A"); Assert.True(Assert.Single(read.Signatures).MatchesSender);
        var after = Directory.GetFiles(dir.Path); Assert.Equal(before.Keys.Order(), after.Order());
        foreach (var file in after) Assert.Equal(before[file], File.ReadAllBytes(file));
    }

    private static MailEngine Engine(TempDir dir, TestProtector protector, Func<Account, MimeMessage, CancellationToken, Task>? send = null)
    {
        var engine = new MailEngine(new AppPaths(dir.Path), protector, sendMessage: send);
        if (engine.AccountById("A") == null) engine.Config.Accounts.Add(Account());
        return engine;
    }
    private void ImportIntoEngine(MailEngine engine, MailSecurityFormat format)
    {
        engine.SecureMail.Keys.Import("A", SecureMailFixtures.Sender, format, fixtures.Key(format, SecureMailFixtures.Sender).Private, SecureMailFixtures.Password, true);
        var publicKey = engine.SecureMail.Keys.Import("A", SecureMailFixtures.Recipient, format, fixtures.Key(format, SecureMailFixtures.Recipient).Public, "", false);
        engine.SecureMail.Keys.Select("A", publicKey.Id, publicKey.Fingerprint);
    }
    private sealed class TestProtector : ISecretProtector, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plain)
        {
            var result = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16)); return result;
        }
        public byte[] Unprotect(byte[] cipher)
        {
            var plain = new byte[cipher.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(cipher.AsSpan(0, 12), cipher.AsSpan(28), cipher.AsSpan(12, 16), plain); return plain;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}
