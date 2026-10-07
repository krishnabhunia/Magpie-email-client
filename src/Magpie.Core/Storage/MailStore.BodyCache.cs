using Magpie.Core.Caching;
using Magpie.Core.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Magpie.Core.Storage;

public sealed partial class MailStore
{
    private readonly object _bodyGate = new();
    private sealed record CachedBody(MessageBody Body, string Fingerprint);
    private readonly LruCache<long, CachedBody> _bodyCache = new(256, 64 * 1024 * 1024);

    public string? BodyFingerprint(long rowId, MessageBody body)
    {
        // Hot reads must not wait for a background writer blocked on SQLite.
        // The content and its digest are one immutable cache entry, so they cannot be mixed across saves.
        return _bodyCache.TryGet(rowId, out var saved) && SameBody(saved.Body, body) ? saved.Fingerprint : null;
    }

    private static bool SameBody(MessageBody a, MessageBody b) =>
        a.Html == b.Html && a.Text == b.Text && a.Calendar == b.Calendar && a.ImagesComplete == b.ImagesComplete
        && a.Images.Count == b.Images.Count && a.Images.All(p => b.Images.TryGetValue(p.Key, out var value) && value == p.Value)
        && a.Attachments.Count == b.Attachments.Count && a.Attachments.Zip(b.Attachments).All(p =>
            p.First.Index == p.Second.Index && p.First.FileName == p.Second.FileName && p.First.ContentType == p.Second.ContentType
            && p.First.ContentId == p.Second.ContentId && p.First.Size == p.Second.Size && p.First.Inline == p.Second.Inline);

    private void RememberBody(long rowId, MessageBody body)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body))));
        _bodyCache.Set(rowId, new CachedBody(CopyBody(body), digest), BodyBytes(body) + 128);
    }

    /// <summary>Retain pictures recovered from the local MIME file without growing the SQLite body.</summary>
    public bool RetainInlineImages(long rowId, MessageBody expected, Dictionary<string, string> images)
    {
        lock (_bodyGate)
        {
            var current = ReadBody(rowId);
            if (current == null || !SameBody(current, expected)) return false;
            current.Images = new Dictionary<string, string>(images, StringComparer.OrdinalIgnoreCase);
            RememberBody(rowId, current);
            return true;
        }
    }

    /// <summary>Does not touch the disk. Returned containers are independent of the retained snapshot.</summary>
    public bool TryGetBodyFromMemory(long rowId, out MessageBody? body)
    {
        if (!_bodyCache.TryGet(rowId, out var cached)) { body = null; return false; }
        body = CopyBody(cached.Body);
        return true;
    }

    private static MessageBody CopyBody(MessageBody body) => new()
    {
        Html = body.Html, Text = body.Text, Calendar = body.Calendar, ImagesComplete = body.ImagesComplete,
        Images = new Dictionary<string, string>(body.Images, StringComparer.OrdinalIgnoreCase),
        Attachments = body.Attachments.Select(a => new AttachmentInfo
        {
            Index = a.Index, FileName = a.FileName, ContentType = a.ContentType,
            Size = a.Size, ContentId = a.ContentId, Inline = a.Inline,
        }).ToList(),
    };

    private static long BodyBytes(MessageBody body) => 512L + 2L *
        (body.Html.Length + (long)body.Text.Length + body.Calendar.Length
         + body.Images.Sum(p => p.Key.Length + (long)p.Value.Length)
         + body.Attachments.Sum(a => a.FileName.Length + (long)a.ContentType.Length + a.ContentId.Length + 64));
}
