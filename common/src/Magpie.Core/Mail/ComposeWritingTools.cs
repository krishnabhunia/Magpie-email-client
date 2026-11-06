using System.Globalization;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Mail;

/// <summary>Counts only the author's visible new writing, supplied without signatures or quoted conversations.</summary>
public sealed record ComposeWritingStatistics(int Words, int Characters, int ReadingMinutes)
{
    public static ComposeWritingStatistics Measure(string? text)
    {
        // Treat CRLF as one line break, and combining marks / emoji sequences as one visible character.
        var normalized = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var words = ComposeSafety.WordCount(normalized);
        return new(words, new StringInfo(normalized).LengthInTextElements,
            words == 0 ? 0 : ComposeSafety.EstimatedReadingMinutes(words));
    }

    public string Summary => $"{Words:N0} {(Words == 1 ? "word" : "words")} · {Characters:N0} {(Characters == 1 ? "character" : "characters")} · {(Words == 0 ? "0" : "~" + ReadingMinutes)} min read";
}

/// <summary>One expansion snapshot keeps subject/body variables consistent and makes subject replacement explicit.</summary>
public sealed record ExpandedQuickTemplate(string Subject, string Body)
{
    public bool WouldReplaceSubject(string current) => !string.IsNullOrWhiteSpace(current)
        && !string.IsNullOrWhiteSpace(Subject) && !string.Equals(current, Subject, StringComparison.Ordinal);

    public string SubjectFor(string current, bool replaceExisting = false) =>
        string.IsNullOrWhiteSpace(Subject) || (!replaceExisting && !string.IsNullOrWhiteSpace(current)) ? current : Subject;
}

public static class ComposeTemplates
{
    /// <summary>Old templates have an empty subject and always remain body-only. Both fields insert as plain text.</summary>
    public static ExpandedQuickTemplate Expand(QuickTemplate template, Account? sender, string to, string currentSubject, DateTimeOffset now)
    {
        var subject = ComposeSafety.ExpandQuickTemplate(template.Subject ?? "", sender, to, currentSubject, now)
            .Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        var body = ComposeSafety.ExpandQuickTemplate(template.Body ?? "", sender, to, currentSubject, now);
        return new(subject, body);
    }
}
