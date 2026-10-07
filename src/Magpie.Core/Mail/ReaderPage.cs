using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>A prepared page. The reader shell keeps its scripts and reuses connected, rendered DOM trees.</summary>
public sealed record ReaderPage(string Key, string Fingerprint, string Html, int BlockedImages,
    string Context = "", bool Retain = true, IReadOnlyDictionary<long, bool>? ReadStates = null)
{
    public long Bytes => 2L * Html.Length;
    public string Style => Between(Html, "<style>", "</style>");
    public string Body => Between(Html, "<body>", "</body>");

    private static string Between(string html, string start, string end)
    {
        var first = html.IndexOf(start, StringComparison.Ordinal);
        if (first < 0) return "";
        first += start.Length;
        var last = html.LastIndexOf(end, StringComparison.Ordinal);
        return last >= first ? html[first..last] : "";
    }

    /// <summary>Content, rather than HTML length, determines whether a prepared page can be reused.</summary>
    public static string ContentFingerprint(string subject, IReadOnlyList<RenderMessage> messages,
        bool allow, bool dark, int hoverDelayMs, DateTimeOffset now, Func<long, MessageBody, string?>? bodyFingerprint = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            subject, allow, dark, hoverDelayMs, TimeBucket = now.ToUnixTimeSeconds() / 600,
            Messages = messages.Select(m => new
            {
                Row = new { m.Row.Id, m.Row.FromName, m.Row.FromAddress, m.Row.To, m.Row.Cc, m.Row.Preview, m.Row.Date },
                Body = m.Body != null && bodyFingerprint?.Invoke(m.Row.Id, m.Body) is { } digest ? (object)digest : m.Body,
                ImageKeys = m.InlineImages.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
                Images = m.InlineImages.Where(p => m.Body == null || !m.Body.Images.TryGetValue(p.Key, out var stored) || stored != p.Value)
                    .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase),
                m.IsMine, m.LoadingText, m.LoadError,
            }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
