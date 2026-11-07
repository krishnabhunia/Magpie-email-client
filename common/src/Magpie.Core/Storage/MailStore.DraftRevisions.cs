using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    public const int MaxDraftRevisions = 30;
    public const int DraftRevisionRetentionDays = 30;
    public const int MaxDraftRevisionBytes = 32 * 1024 * 1024;
    public const long MaxDraftRevisionTotalBytes = 200L * 1024 * 1024;
    private readonly object _draftRevisionGate = new();

    // Additive side table: older app versions can still open the mail store. History is local-only and does
    // not depend on local_drafts ids, which change when drafts are uploaded, reopened or saved again.
    private void EnsureDraftRevisionSchema()
    {
        using var c = Open();
        using var tx = c.BeginTransaction();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS draft_revisions(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              account_id TEXT NOT NULL, message_id TEXT NOT NULL, created INTEGER NOT NULL,
              subject TEXT NOT NULL, to_text TEXT NOT NULL, preview TEXT NOT NULL,
              size_bytes INTEGER NOT NULL CHECK(size_bytes >= 0),
              digest TEXT NOT NULL, payload_digest TEXT NOT NULL, snapshot BLOB NOT NULL, mime BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_draft_revisions_message
              ON draft_revisions(account_id,message_id,id DESC);
            CREATE INDEX IF NOT EXISTS ix_draft_revisions_created ON draft_revisions(created,id);
            """);
        PruneDraftRevisions(c, DateTimeOffset.UtcNow);
        tx.Commit();
    }

    /// <summary>Serializes directly into a bounded buffer, without first allocating an arbitrarily large MIME byte array.</summary>
    public long SaveDraftRevision(Draft draft, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(message);
        if (!ValidDraftRevisionKey(draft.AccountId, Threading.NormalizeId(draft.MessageId))) return 0;
        try
        {
            using var buffer = new RevisionBuffer(MaxDraftRevisionBytes);
            message.WriteTo(FormatOptions.Default, buffer);
            return SaveDraftRevision(draft, buffer.ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ParseException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Saves a self-contained revision and returns its id. An unchanged latest version returns the same id;
    /// invalid/oversized snapshots return 0. Storage failures are not swallowed; callers may treat history as
    /// best-effort during autosave, but must require a successful checkpoint before replacing current content.
    /// </summary>
    public long SaveDraftRevision(Draft draft, byte[] mime)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(mime);
        var messageId = Threading.NormalizeId(draft.MessageId);
        if (!ValidDraftRevisionKey(draft.AccountId, messageId) || mime.Length is 0 or > MaxDraftRevisionBytes) return 0;

        byte[] snapshot;
        string digest, payloadDigest, preview;
        try
        {
            var state = DraftRevisionState.Capture(draft);
            // Bound JSON serialization too: escaping a large HTML string must not create an unbounded allocation.
            using var snapshotStream = new RevisionBuffer(MaxDraftRevisionBytes - mime.Length);
            JsonSerializer.Serialize(snapshotStream, state);
            snapshot = snapshotStream.ToArray();
            using var message = Composer.FromBytes(mime);
            if (message.Body == null || Threading.NormalizeId(message.MessageId) != messageId) return 0;
            digest = RevisionFingerprint(snapshot, message);
            payloadDigest = RevisionPayloadDigest(snapshot, mime);
            var sample = state.Html.Length > 8192 ? state.Html[..8192] : state.Html;
            preview = ShortRevisionText(Regex.Replace(MimeText.HtmlToText(sample), @"\s+", " ").Trim(), 160);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ParseException or JsonException or ArgumentException)
        {
            return 0;
        }

        lock (_draftRevisionGate)
        {
            using var c = Open();
            // SQLite's immediate write transaction also serializes deduplication across separate store instances.
            using var tx = c.BeginTransaction();
            var now = DateTimeOffset.UtcNow;
            PruneDraftRevisions(c, now);
            long latestId = 0;
            string? latestDigest = null;
            using (var last = c.CreateCommand())
            {
                last.CommandText = "SELECT id,digest FROM draft_revisions WHERE account_id=$a AND message_id=$m ORDER BY id DESC LIMIT 1";
                last.Parameters.AddWithValue("$a", draft.AccountId);
                last.Parameters.AddWithValue("$m", messageId);
                using var row = last.ExecuteReader();
                if (row.Read()) { latestId = row.GetInt64(0); latestDigest = row.GetString(1); }
            }
            // A duplicate capture is also a recovery checkpoint: never reuse a damaged snapshot merely
            // because its separately stored semantic digest still matches the current editor content.
            if (latestDigest == digest && HasIntactDraftRevisionPayload(c, latestId))
            {
                tx.Commit();
                return latestId;
            }

            var id = Convert.ToInt64(Scalar(c, """
                INSERT INTO draft_revisions(account_id,message_id,created,subject,to_text,preview,size_bytes,digest,payload_digest,snapshot,mime)
                VALUES($a,$m,$c,$s,$to,$p,$n,$d,$pd,$j,$mime) RETURNING id
                """, ("$a", draft.AccountId), ("$m", messageId), ("$c", now.ToUnixTimeMilliseconds()),
                ("$s", ShortRevisionText(draft.Subject, 512)), ("$to", ShortRevisionText(draft.To, 1024)),
                ("$p", preview), ("$n", (long)snapshot.Length + mime.Length), ("$d", digest),
                ("$pd", payloadDigest), ("$j", snapshot), ("$mime", mime)));
            Exec(c, """
                DELETE FROM draft_revisions WHERE account_id=$a AND message_id=$m AND id NOT IN
                  (SELECT id FROM draft_revisions WHERE account_id=$a AND message_id=$m ORDER BY id DESC LIMIT $limit)
                """, ("$a", draft.AccountId), ("$m", messageId), ("$limit", MaxDraftRevisions));
            PruneDraftRevisions(c, now);
            tx.Commit();
            return id;
        }
    }

    /// <summary>Newest-first metadata only. MIME/snapshots are not read until a selected revision is opened.</summary>
    public List<DraftRevision> GetDraftRevisions(string accountId, string messageId)
    {
        messageId = Threading.NormalizeId(messageId);
        if (!ValidDraftRevisionKey(accountId, messageId)) return new();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,account_id,message_id,created,subject,to_text,preview,size_bytes FROM draft_revisions
            WHERE account_id=$a AND message_id=$m AND created >= $cutoff
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$m", messageId);
        cmd.Parameters.AddWithValue("$cutoff", RevisionCutoff(DateTimeOffset.UtcNow));
        cmd.Parameters.AddWithValue("$limit", MaxDraftRevisions);
        var result = new List<DraftRevision>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result.Add(new DraftRevision
            {
                Id = r.GetInt64(0), AccountId = r.GetString(1), MessageId = r.GetString(2),
                Created = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)), Subject = r.GetString(4),
                ToText = r.GetString(5), Preview = r.GetString(6), SizeBytes = r.GetInt64(7),
            });
        return result;
    }

    /// <summary>
    /// Opens one account/message-scoped snapshot. Attachments come from captured MIME, never original file paths.
    /// Returns null for an expired, missing, corrupted or incompatible revision. This does not save or send it.
    /// </summary>
    public Draft? OpenDraftRevision(string accountId, string messageId, long revisionId)
    {
        messageId = Threading.NormalizeId(messageId);
        if (revisionId <= 0 || !ValidDraftRevisionKey(accountId, messageId)) return null;
        byte[] snapshot, mime;
        string payloadDigest;
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            // Actual lengths are checked before loading BLOBs, even if size_bytes was damaged.
            cmd.CommandText = """
                SELECT snapshot,mime,payload_digest FROM draft_revisions
                WHERE id=$id AND account_id=$a AND message_id=$m AND created >= $cutoff
                  AND typeof(snapshot)='blob' AND typeof(mime)='blob' AND typeof(payload_digest)='text'
                  AND length(snapshot)+length(mime) <= $max
                """;
            cmd.Parameters.AddWithValue("$id", revisionId);
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$m", messageId);
            cmd.Parameters.AddWithValue("$cutoff", RevisionCutoff(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$max", MaxDraftRevisionBytes);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            snapshot = (byte[])r.GetValue(0);
            mime = (byte[])r.GetValue(1);
            payloadDigest = r.GetString(2);
        }

        MimeMessage? message = null;
        try
        {
            if (RevisionPayloadDigest(snapshot, mime) != payloadDigest) return null;
            var state = JsonSerializer.Deserialize<DraftRevisionState>(snapshot);
            if (state?.IsSupported() != true) return null;
            message = Composer.FromBytes(mime);
            if (message.Body == null || Threading.NormalizeId(message.MessageId) != messageId)
            {
                message.Dispose();
                return null;
            }
            var draft = Composer.FromMime(message, accountId, state.ThreadKey);
            state.RestoreFields(draft);
            draft.AttachmentOrder = draft.CarriedParts.Select((_, index) => new DraftAttachmentPosition { CarriedIndex = index }).ToList();
            // The MIME parts escape with the draft; do not dispose their in-memory content streams here.
            return draft;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ParseException or JsonException or ArgumentException)
        {
            message?.Dispose();
            return null;
        }
    }

    public void DeleteDraftRevisions(string accountId, string messageId)
    {
        messageId = Threading.NormalizeId(messageId);
        if (!ValidDraftRevisionKey(accountId, messageId)) return;
        using var c = Open();
        Exec(c, "DELETE FROM draft_revisions WHERE account_id=$a AND message_id=$m", ("$a", accountId), ("$m", messageId));
    }

    public void DeleteDraftRevisionsForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > 256) return;
        using var c = Open();
        Exec(c, "DELETE FROM draft_revisions WHERE account_id=$a", ("$a", accountId));
    }

    private static bool ValidDraftRevisionKey(string? accountId, string messageId) =>
        !string.IsNullOrWhiteSpace(accountId) && accountId.Length <= 256 && messageId.Length is > 0 and <= 1024;

    private static long RevisionCutoff(DateTimeOffset now) => now.AddDays(-DraftRevisionRetentionDays).ToUnixTimeMilliseconds();

    private static string ShortRevisionText(string? text, int length) => text?.Length > length ? text[..length] : text ?? "";

    private static bool HasIntactDraftRevisionPayload(SqliteConnection c, long revisionId)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT snapshot,mime,payload_digest FROM draft_revisions WHERE id=$id
              AND typeof(snapshot)='blob' AND typeof(mime)='blob' AND typeof(payload_digest)='text'
              AND length(snapshot)+length(mime) <= $max
            """;
        cmd.Parameters.AddWithValue("$id", revisionId);
        cmd.Parameters.AddWithValue("$max", MaxDraftRevisionBytes);
        using var r = cmd.ExecuteReader();
        return r.Read() && RevisionPayloadDigest((byte[])r.GetValue(0), (byte[])r.GetValue(1)) == r.GetString(2);
    }

    private static void PruneDraftRevisions(SqliteConnection c, DateTimeOffset now)
    {
        Exec(c, "DELETE FROM draft_revisions WHERE created < $cutoff", ("$cutoff", RevisionCutoff(now)));
        if (Convert.ToInt64(Scalar(c, "SELECT COALESCE(SUM(size_bytes),0) FROM draft_revisions")) <= MaxDraftRevisionTotalBytes) return;
        // Keep the newest contiguous set within the global payload budget. Capture ids stay ordered even if the clock changes.
        Exec(c, """
            DELETE FROM draft_revisions WHERE id IN (
              SELECT id FROM (SELECT id,SUM(size_bytes) OVER (ORDER BY id DESC) AS retained_bytes FROM draft_revisions)
              WHERE retained_bytes > $max)
            """, ("$max", MaxDraftRevisionTotalBytes));
    }

    private static string RevisionPayloadDigest(byte[] snapshot, byte[] mime)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendRevisionBytes(hash, snapshot);
        AppendRevisionBytes(hash, mime);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string RevisionFingerprint(byte[] snapshot, MimeMessage message)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendRevisionBytes(hash, snapshot);
        // Body/options are in the editable snapshot. Hash decoded attachment content, not Date, transfer
        // encoding or random multipart boundaries regenerated by Composer.Build on every autosave.
        foreach (var entity in message.Attachments)
        {
            AppendRevisionText(hash, entity.ContentType.MimeType);
            AppendRevisionText(hash, entity.ContentDisposition?.Disposition ?? "");
            AppendRevisionText(hash, entity.ContentDisposition?.FileName
                ?? entity.ContentType.Parameters.FirstOrDefault(p => p.Name.Equals("name", StringComparison.OrdinalIgnoreCase))?.Value ?? "");
            foreach (var parameter in entity.ContentType.Parameters.Where(p => !p.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            {
                AppendRevisionText(hash, parameter.Name.ToLowerInvariant());
                AppendRevisionText(hash, parameter.Value);
            }
            using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var sink = new RevisionHashSink(contentHash, MaxDraftRevisionBytes);
            if (entity is MimePart part) part.Content?.DecodeTo(sink);
            else if (entity is MessagePart attached && attached.Message != null) attached.Message.WriteTo(sink);
            else entity.WriteTo(sink);
            AppendRevisionBytes(hash, contentHash.GetHashAndReset());
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendRevisionText(IncrementalHash hash, string value) => AppendRevisionBytes(hash, Encoding.UTF8.GetBytes(value));

    private static void AppendRevisionBytes(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    /// <summary>Editable fields that MIME may normalize (notably incomplete addresses) must survive recovery.</summary>
    private sealed class DraftRevisionState
    {
        public DraftRevisionState() { }

        public int Version { get; set; }
        public string To { get; set; } = "";
        public string Cc { get; set; } = "";
        public string Bcc { get; set; } = "";
        public string ReplyTo { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Html { get; set; } = "";
        public string Priority { get; set; } = "Normal";
        public bool RequestReadReceipt { get; set; }
        public bool IsPlainText { get; set; }
        public TextStyle? ComposeStyle { get; set; }
        public string InReplyTo { get; set; } = "";
        public string References { get; set; } = "";
        public string ThreadKey { get; set; } = "";
        public ReplyArchiveSource? ReplyArchiveSource { get; set; }
        public string CalendarReply { get; set; } = "";

        public static DraftRevisionState Capture(Draft draft) => new()
        {
            Version = 1, To = draft.To ?? "", Cc = draft.Cc ?? "", Bcc = draft.Bcc ?? "", ReplyTo = draft.ReplyTo ?? "",
            Subject = draft.Subject ?? "", Html = draft.Html ?? "", Priority = draft.Priority ?? "Normal",
            RequestReadReceipt = draft.RequestReadReceipt, IsPlainText = draft.IsPlainText,
            ComposeStyle = draft.ComposeStyle?.Clone(), InReplyTo = draft.InReplyTo ?? "", References = draft.References ?? "",
            ThreadKey = draft.ThreadKey ?? "", ReplyArchiveSource = draft.ReplyArchiveSource,
            CalendarReply = draft.CalendarReply ?? "",
        };

        public bool IsSupported() => Version == 1 && To != null && Cc != null && Bcc != null && ReplyTo != null
            && Subject != null && Html != null && Priority != null && InReplyTo != null && References != null
            && ThreadKey != null && CalendarReply != null
            && (ReplyArchiveSource == null || (ReplyArchiveSource.IsValid && ReplyArchiveSource.ThreadKey == ThreadKey));

        public void RestoreFields(Draft draft)
        {
            draft.To = To; draft.Cc = Cc; draft.Bcc = Bcc; draft.ReplyTo = ReplyTo;
            draft.Subject = Subject; draft.Html = Html; draft.Priority = Priority;
            draft.RequestReadReceipt = RequestReadReceipt; draft.IsPlainText = IsPlainText;
            draft.ComposeStyle = ComposeStyle?.Clone(); draft.InReplyTo = InReplyTo; draft.References = References;
            draft.ThreadKey = ThreadKey; draft.CalendarReply = CalendarReply;
            draft.ReplyArchiveSource = ReplyArchiveSource;
        }
    }

    private sealed class RevisionBuffer(long limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new InvalidDataException("Draft revision exceeds its storage limit.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new InvalidDataException("Draft revision exceeds its storage limit.");
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            if (Length + 1 > limit) throw new InvalidDataException("Draft revision exceeds its storage limit.");
            base.WriteByte(value);
        }
    }

    private sealed class RevisionHashSink(IncrementalHash hash, long limit) : Stream
    {
        private long _length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_length + buffer.Length > limit) throw new InvalidDataException("Draft attachment exceeds its revision limit.");
            hash.AppendData(buffer);
            _length += buffer.Length;
        }
    }
}
