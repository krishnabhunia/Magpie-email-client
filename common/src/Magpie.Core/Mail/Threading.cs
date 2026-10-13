using System.Text.RegularExpressions;

namespace Magpie.Core.Mail;

/// <summary>
/// Conversation keys. Gmail supplies X-GM-THRID; everywhere else the key is the root Message-ID of the
/// References chain, falling back to an already-known thread of any referenced message, so parents and
/// replies land in the same conversation whichever arrives first.
/// </summary>
public static class Threading
{
    public static string NormalizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        id = id.Trim();
        if (id.StartsWith('<') && id.EndsWith('>')) id = id[1..^1];
        return id.Trim().ToLowerInvariant();
    }

    public static List<string> ParseReferences(string? refs)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(refs)) return list;
        foreach (Match m in Regex.Matches(refs, "<([^<>]+)>"))
        {
            var id = NormalizeId(m.Groups[1].Value);
            if (id.Length > 0 && !list.Contains(id)) list.Add(id);
        }
        if (list.Count == 0)
            foreach (var part in refs.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var id = NormalizeId(part);
                if (id.Contains('@') && !list.Contains(id)) list.Add(id);
            }
        return list;
    }

    /// <summary>
    /// Conversation key for a message, plus existing keys that must be merged into it (a message that
    /// links two conversations, e.g. a parent arriving after its replies were stored under another key).
    /// </summary>
    /// <param name="related">Given the message's own id and its reference chain, returns the thread keys of
    /// already-known messages that are that message, one of its ancestors, or one of its direct replies.</param>
    public static (string key, List<string> merge) Resolve(string messageId, string inReplyTo, IReadOnlyList<string> references, ulong? gmailThreadId,
                                 Func<string, IReadOnlyList<string>, IReadOnlyList<string>>? related, string fallbackUnique)
    {
        if (gmailThreadId is { } gm && gm != 0) return ("gm:" + gm.ToString("x"), new List<string>());
        var mid = NormalizeId(messageId);
        var irt = NormalizeId(inReplyTo);
        var chain = new List<string>(references);
        if (irt.Length > 0 && !chain.Contains(irt)) chain.Add(irt);

        var known = related?.Invoke(mid, chain)?.Where(k => !string.IsNullOrEmpty(k)).Distinct().ToList() ?? new List<string>();
        string key;
        if (known.Count > 0) key = known[0];
        else if (chain.Count > 0) key = "r:" + chain[0];
        else if (mid.Length > 0) key = "r:" + mid;
        else key = "u:" + fallbackUnique;
        return (key, known.Where(k => k != key).ToList());
    }

    /// <summary>Convenience wrapper when no merging is needed.</summary>
    public static string Compute(string messageId, string inReplyTo, IReadOnlyList<string> references, ulong? gmailThreadId,
                                 Func<IEnumerable<string>, string?>? lookup, string fallbackUnique) =>
        Resolve(messageId, inReplyTo, references, gmailThreadId,
            lookup == null ? null : (mid, chain) => lookup(chain.Append(mid).Where(x => x.Length > 0)) is { } k ? new[] { k } : Array.Empty<string>(),
            fallbackUnique).key;

    private static readonly Regex Prefix = new(@"^\s*((re|fw|fwd|aw|wg|sv|vs|antw|rif|tr)(\[\d+\])?\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>"Re: Fwd: Hello" → "Hello".</summary>
    public static string StripSubjectPrefixes(string? subject) =>
        string.IsNullOrEmpty(subject) ? "" : Prefix.Replace(subject, "").Trim();

    public static string ReplySubject(string? subject)
    {
        var s = subject ?? "";
        return Regex.IsMatch(s, @"^\s*re\s*:", RegexOptions.IgnoreCase) ? s : "Re: " + StripSubjectPrefixes(s);
    }

    public static string ForwardSubject(string? subject) => "Fwd: " + StripSubjectPrefixes(subject);
}
