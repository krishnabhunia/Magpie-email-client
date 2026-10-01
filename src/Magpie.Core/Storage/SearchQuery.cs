using System.Text;
using Microsoft.Data.Sqlite;

namespace Magpie.Core.Storage;

/// <summary>
/// Search box syntax: free words (prefix-matched over subject, people and body), "exact phrases",
/// from:anita  to:sandeep  with:anita (from, to or cc)  subject:invoice  file:contract (attachment name)
/// has:attachment  is:unread  is:pinned  before:2026-09-01  after:2026-08-01
/// </summary>
public sealed class SearchQuery
{
    public List<string> Terms { get; } = new();
    public List<string> Phrases { get; } = new();
    public List<string> From { get; } = new();
    public List<string> To { get; } = new();
    public List<string> Subject { get; } = new();
    /// <summary>with: — the person is the sender or among To / Cc (design HM1, E5).</summary>
    public List<string> With { get; } = new();
    /// <summary>file: — an attachment's name contains this (emails whose text is on this PC; design HM1, A9).</summary>
    public List<string> File { get; } = new();
    public bool HasAttachment { get; private set; }
    public bool Unread { get; private set; }
    public bool Pinned { get; private set; }
    public DateTimeOffset? Before { get; private set; }
    public DateTimeOffset? After { get; private set; }

    public bool IsEmpty => Terms.Count == 0 && Phrases.Count == 0 && From.Count == 0 && To.Count == 0 && Subject.Count == 0 && With.Count == 0 && File.Count == 0
                           && !HasAttachment && !Unread && !Pinned && Before == null && After == null;

    public static SearchQuery Parse(string? text)
    {
        var q = new SearchQuery();
        if (string.IsNullOrWhiteSpace(text)) return q;
        foreach (var tok in Tokenize(text))
        {
            if (tok.quoted) { if (tok.value.Length > 0) q.Phrases.Add(tok.value); continue; }
            var t = tok.value;
            var colon = t.IndexOf(':');
            if (colon > 0 && colon < t.Length - 1)
            {
                var key = t[..colon].ToLowerInvariant();
                var val = t[(colon + 1)..].Trim('"');
                switch (key)
                {
                    case "from": q.From.Add(val); continue;
                    case "to": q.To.Add(val); continue;
                    case "subject": q.Subject.Add(val); continue;
                    case "with": q.With.Add(val); continue;
                    case "file": q.File.Add(val); continue;
                    case "has" when val.StartsWith("attach", StringComparison.OrdinalIgnoreCase): q.HasAttachment = true; continue;
                    case "is" when val.Equals("unread", StringComparison.OrdinalIgnoreCase): q.Unread = true; continue;
                    case "is" when val is "pinned" or "flagged" or "starred": q.Pinned = true; continue;
                    case "before" when DateTime.TryParse(val, out var b): q.Before = new DateTimeOffset(b.Date); continue;
                    case "after" when DateTime.TryParse(val, out var a): q.After = new DateTimeOffset(a.Date); continue;
                }
            }
            q.Terms.Add(t);
        }
        return q;
    }

    /// <summary>Splits on whitespace; "a phrase" is one quoted token; key:"two words" stays one key token.</summary>
    internal static List<(string value, bool quoted)> Tokenize(string s)
    {
        var result = new List<(string, bool)>();
        var sb = new StringBuilder();
        int i = 0;
        void Flush() { if (sb.Length > 0) { result.Add((sb.ToString(), false)); sb.Clear(); } }
        while (i < s.Length)
        {
            var ch = s[i];
            if (ch == '"')
            {
                var end = s.IndexOf('"', i + 1);
                if (end < 0) end = s.Length;
                var inner = s.Substring(i + 1, end - i - 1);
                if (sb.Length > 0 && sb[^1] == ':') { sb.Append(inner); Flush(); }
                else { Flush(); result.Add((inner.Trim(), true)); }
                i = end + 1;
                continue;
            }
            if (char.IsWhiteSpace(ch)) Flush(); else sb.Append(ch);
            i++;
        }
        Flush();
        return result;
    }

    /// <summary>FTS5 MATCH expression for the free words and phrases (null when there are none).</summary>
    public string? FtsExpression()
    {
        var parts = new List<string>();
        foreach (var t in Terms)
        {
            var clean = new string(t.Where(ch => char.IsLetterOrDigit(ch) || ch is '@' or '.' or '-' or '_').ToArray());
            // Split on punctuation the tokenizer treats as separators so "a.b@c.com" still matches.
            foreach (var piece in clean.Split(new[] { '@', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
                parts.Add("\"" + piece + "\"*");
        }
        foreach (var p in Phrases)
        {
            var clean = p.Replace("\"", " ").Trim();
            if (clean.Length > 0) parts.Add("\"" + clean + "\"");
        }
        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>Adds parameters to <paramref name="cmd"/> and returns a SQL predicate over alias <paramref name="m"/>.</summary>
    public string ToSql(SqliteCommand cmd, string m)
    {
        var preds = new List<string>();
        var fts = FtsExpression();
        if (fts != null)
        {
            cmd.Parameters.AddWithValue("$sq_fts", fts);
            preds.Add($"{m}.id IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH $sq_fts)");
        }
        int i = 0;
        foreach (var f in From)
        {
            cmd.Parameters.AddWithValue($"$sq_f{i}", Like(f));
            preds.Add($"({m}.from_addr LIKE $sq_f{i} ESCAPE '\\' OR {m}.from_name LIKE $sq_f{i} ESCAPE '\\')");
            i++;
        }
        foreach (var t in To)
        {
            cmd.Parameters.AddWithValue($"$sq_t{i}", Like(t));
            preds.Add($"({m}.to_list LIKE $sq_t{i} ESCAPE '\\' OR {m}.cc_list LIKE $sq_t{i} ESCAPE '\\')");
            i++;
        }
        foreach (var s in Subject)
        {
            cmd.Parameters.AddWithValue($"$sq_s{i}", Like(s));
            preds.Add($"{m}.subject LIKE $sq_s{i} ESCAPE '\\'");
            i++;
        }
        foreach (var w in With)
        {
            cmd.Parameters.AddWithValue($"$sq_w{i}", Like(w));
            preds.Add($"({m}.from_addr LIKE $sq_w{i} ESCAPE '\\' OR {m}.from_name LIKE $sq_w{i} ESCAPE '\\' OR {m}.to_list LIKE $sq_w{i} ESCAPE '\\' OR {m}.cc_list LIKE $sq_w{i} ESCAPE '\\')");
            i++;
        }
        foreach (var f in File)
        {
            cmd.Parameters.AddWithValue($"$sq_n{i}", Like(f));
            preds.Add($"EXISTS (SELECT 1 FROM bodies b, json_each(b.attachments) j WHERE b.message_row={m}.id AND json_extract(j.value,'$.FileName') LIKE $sq_n{i} ESCAPE '\\')");
            i++;
        }
        if (HasAttachment) preds.Add($"{m}.has_attach=1");
        if (Unread) preds.Add($"({m}.flags & 1)=0");
        if (Pinned) preds.Add($"({m}.flags & 2)<>0");
        if (Before is { } b) { cmd.Parameters.AddWithValue("$sq_b", b.ToUnixTimeMilliseconds()); preds.Add($"{m}.date < $sq_b"); }
        if (After is { } a) { cmd.Parameters.AddWithValue("$sq_a", a.ToUnixTimeMilliseconds()); preds.Add($"{m}.date >= $sq_a"); }
        return preds.Count == 0 ? "1=1" : string.Join(" AND ", preds);
    }

    private static string Like(string s) => "%" + s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
