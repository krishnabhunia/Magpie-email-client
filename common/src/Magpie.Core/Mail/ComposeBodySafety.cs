using AngleSharp;
using AngleSharp.Html.Parser;
using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>Draft-only knowledge needed to avoid replacing a flattened signature or quoted message.</summary>
public static class ComposeBodySafety
{
    public const string DraftHeader = "X-Magpie-Body-Sections";
    public const string KnownSections = "v1; sections=known";
    public const string LostSections = "v1; sections=lost";
    public const string RestrictionReason = "Magpie cannot reliably separate your writing from any retained signature or quoted message in this draft. Select only the text you want to change and use Rewrite selection, or restore a rich draft revision with its original sections.";

    public static bool IsFullBodyCommand(string mode) => mode is "replaceBody" or "appendBody" or "replyDraft";

    public static void WriteDraftMetadata(MimeMessage message, Draft draft)
    {
        RemoveDraftMetadata(message);
        message.Headers.Add(DraftHeader, draft.BodySectionsLost || draft.IsPlainText ? LostSections : KnownSections);
    }

    public static bool ReadLostSections(MimeMessage message)
    {
        // Plain transport has no section markers, even when an older/foreign header claims otherwise.
        if (message.HtmlBody == null) return true;
        var headers = message.Headers.Where(h => h.Field.Equals(DraftHeader, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (headers.Length == 1 && headers[0].Value.Trim().Equals(KnownSections, StringComparison.OrdinalIgnoreCase)) return false;
        // Explicit loss, unsupported metadata and duplicate headers must never re-enable body-wide edits.
        if (headers.Length != 0) return true;
        // Exported/foreign MIME has no trusted conversion history. Real section markers still identify
        // the protected boundary; otherwise require a selection instead of guessing that all text is new.
        using var document = new HtmlParser().ParseDocument(message.HtmlBody);
        return document.QuerySelector(".magpie-signature,.magpie-quote") == null;
    }

    public static void RemoveDraftMetadata(MimeMessage message) => message.Headers.RemoveAll(DraftHeader);

    /// <summary>Capture the first protected node and everything after it, using the same DOM markers as the rich editor.</summary>
    public static string CapturePlainProtectedTail(string html)
    {
        using var document = new HtmlParser().ParseDocument(html);
        var first = document.QuerySelector(".magpie-signature,.magpie-quote");
        if (first == null) return "";
        var range = document.CreateRange();
        range.SelectContent(document);
        range.StartBefore(first);
        return MimeText.HtmlToText(range.CopyContent().ToHtml()).TrimStart();
    }
}
