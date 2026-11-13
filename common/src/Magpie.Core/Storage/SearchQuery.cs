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
    /// <summary>
    /// The whole address is the sender or one of the To / Cc mailboxes (design B4, the Contacts page): "ann@x.in" doesn't
    /// match "joann@x.in". Not typed in the search box; set in code, e.g. <see cref="WithMailbox"/>.
    /// </summary>
    public List<string> Mailbox { get; } = new();
    /// <summary>file: — an attachment's name contains this (emails whose text is on this PC; design HM1, A9).</summary>
    public List<string> File { get; } = new();
    /// <summary>Exact sender domain; domain:example.com does not match subdomains.</summary>
    public List<string> Domain { get; } = new();
    /// <summary>Exact local label, with case-insensitive comparison.</summary>
    public List<string> Labels { get; } = new();
    /// <summary>
    /// Design PE1: every email with one exact address, by its part in the email (sender, someone you wrote to, or
    /// copied in on someone else's email). Not typed in the search box; set by <see cref="ForPerson"/>.
    /// </summary>
    public PersonFilter? Person { get; private set; }
    public bool HasAttachment { get; private set; }
    public bool Unread { get; private set; }
    public bool Pinned { get; private set; }
    public DateTimeOffset? Before { get; private set; }
    public DateTimeOffset? After { get; private set; }

    public bool IsEmpty => Terms.Count == 0 && Phrases.Count == 0 && From.Count == 0 && To.Count == 0 && Subject.Count == 0 && With.Count == 0 && File.Count == 0
                           && Mailbox.Count == 0 && Domain.Count == 0 && Labels.Count == 0 && Person == null && !HasAttachment && !Unread && !Pinned && Before == null && After == null;

    /// <summary>Design PE1: emails with exactly this address; <paramref name="mine"/> = the addresses of your own accounts.</summary>
    public static SearchQuery ForPerson(string address, PersonKind kind, IEnumerable<string> mine) =>
        new() { Person = new PersonFilter(address.Trim().ToLowerInvariant(), kind, mine.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).Distinct().ToList()) };

    /// <summary>Emails exchanged with exactly this address (sender, To or Cc), optionally only those with files.</summary>
    public static SearchQuery WithMailbox(string address, bool withAttachment = false)
    {
        var q = new SearchQuery { HasAttachment = withAttachment };
        q.Mailbox.Add(address.Trim().ToLowerInvariant());
        return q;
    }

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
                    case "domain": q.Domain.Add(val.TrimStart('@').ToLowerInvariant()); continue;
                    case "label":
                    case "tag": q.Labels.Add(val); continue;
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
            preds.Add($"({m}.to_list LIKE $sq_t{i} ESCAPE '\\' OR {m}.cc_list LIKE $sq_t{i} ESCAPE '\\' OR COALESCE({m}.bcc_list,'') LIKE $sq_t{i} ESCAPE '\\')");
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
            preds.Add($"({m}.from_addr LIKE $sq_w{i} ESCAPE '\\' OR {m}.from_name LIKE $sq_w{i} ESCAPE '\\' OR {m}.to_list LIKE $sq_w{i} ESCAPE '\\' OR {m}.cc_list LIKE $sq_w{i} ESCAPE '\\' OR COALESCE({m}.bcc_list,'') LIKE $sq_w{i} ESCAPE '\\')");
            i++;
        }
        foreach (var box in Mailbox)
        {
            // to_list / cc_list hold "Name <addr>, addr2, …": a bracketed address, or a bare one between commas (spaces removed).
            cmd.Parameters.AddWithValue($"$sq_m{i}", box.ToLowerInvariant());
            preds.Add($"(lower({m}.from_addr)=$sq_m{i} OR {RecipientSql(m, $"$sq_m{i}")})");
            i++;
        }
        if (Person is { } person) preds.Add(person.ToSql(cmd, m));
        foreach (var f in File)
        {
            cmd.Parameters.AddWithValue($"$sq_n{i}", Like(f));
            preds.Add($"EXISTS (SELECT 1 FROM bodies b, json_each(b.attachments) j WHERE b.message_row={m}.id AND json_extract(j.value,'$.FileName') LIKE $sq_n{i} ESCAPE '\\')");
            i++;
        }
        foreach (var domain in Domain)
        {
            cmd.Parameters.AddWithValue($"$sq_domain{i}", domain);
            preds.Add($"instr({m}.from_addr,'@')>0 AND lower(substr({m}.from_addr,instr({m}.from_addr,'@')+1))=$sq_domain{i}");
            i++;
        }
        foreach (var label in Labels)
        {
            cmd.Parameters.AddWithValue($"$sq_label{i}", "," + label.ToLowerInvariant() + ",");
            preds.Add($"instr(','||lower({m}.tags)||',',$sq_label{i})>0");
            i++;
        }
        if (HasAttachment) preds.Add($"{m}.has_attach=1");
        if (Unread) preds.Add($"({m}.flags & 1)=0");
        if (Pinned) preds.Add($"({m}.flags & 2)<>0");
        if (Before is { } b) { cmd.Parameters.AddWithValue("$sq_b", b.ToUnixTimeMilliseconds()); preds.Add($"{m}.date < $sq_b"); }
        if (After is { } a) { cmd.Parameters.AddWithValue("$sq_a", a.ToUnixTimeMilliseconds()); preds.Add($"{m}.date >= $sq_a"); }
        return preds.Count == 0 ? "1=1" : string.Join(" AND ", preds);
    }

    /// <summary>
    /// The address in <paramref name="param"/> (lower case) is one of the To / Cc / Bcc mailboxes. Those columns hold
    /// "Name &lt;addr&gt;, addr2, …": a bracketed address, or a bare one between commas (spaces removed).
    /// </summary>
    internal static string RecipientSql(string m, string param) =>
        "(" + string.Join(" OR ", new[] { $"{m}.to_list", $"{m}.cc_list", $"COALESCE({m}.bcc_list,'')" }.Select(col =>
            $"instr(lower({col}),'<'||{param}||'>')>0 OR instr(','||replace(lower({col}),' ','')||',',','||{param}||',')>0")) + ")";

    private static string Like(string s) => "%" + s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}

/// <summary>Design PE1: which emails with a person a list shows.</summary>
public enum PersonKind
{
    /// <summary>Every email the address is part of.</summary>
    All = 0,
    /// <summary>The address sent it.</summary>
    From = 1,
    /// <summary>You sent it and the address is in To, Cc or Bcc.</summary>
    YouSent = 2,
    /// <summary>Someone else sent it and the address is in To or Cc.</summary>
    CopiedIn = 3,
}

/// <summary>Design PE1: one exact address (lower case), the part it plays, and your own addresses (lower case).</summary>
public sealed record PersonFilter(string Address, PersonKind Kind, IReadOnlyList<string> Mine)
{
    /// <summary>Adds the parameters once per command and returns the predicate for <see cref="Kind"/> over alias <paramref name="m"/>.</summary>
    public string ToSql(SqliteCommand cmd, string m)
    {
        var (from, mine, rcpt) = Parts(cmd, m);
        return Kind switch
        {
            PersonKind.From => from,
            PersonKind.YouSent => $"(NOT {from} AND {mine} AND {rcpt})",
            PersonKind.CopiedIn => $"(NOT {from} AND NOT {mine} AND {rcpt})",
            _ => $"({from} OR {rcpt})",
        };
    }

    /// <summary>1 = the address sent it, 2 = you sent it to the address, 3 = copied in on someone else's email.</summary>
    public string KindSql(SqliteCommand cmd, string m)
    {
        var (from, mine, _) = Parts(cmd, m);
        return $"CASE WHEN {from} THEN 1 WHEN {mine} THEN 2 ELSE 3 END";
    }

    private (string From, string Mine, string Rcpt) Parts(SqliteCommand cmd, string m)
    {
        if (!cmd.Parameters.Contains("$pe_a"))
        {
            cmd.Parameters.AddWithValue("$pe_a", Address);
            for (int i = 0; i < Mine.Count; i++) cmd.Parameters.AddWithValue("$pe_m" + i, Mine[i]);
        }
        var mine = Mine.Count == 0 ? "0" : $"lower({m}.from_addr) IN ({string.Join(",", Mine.Select((_, i) => "$pe_m" + i))})";
        return ($"lower({m}.from_addr)=$pe_a", mine, SearchQuery.RecipientSql(m, "$pe_a"));
    }
}
