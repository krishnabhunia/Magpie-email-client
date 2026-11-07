using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>Filename metadata shared by normal MIME attachments and attached messages (.eml).</summary>
public static class MimeAttachmentNames
{
    public static string? Get(MimeEntity part) => part.ContentDisposition?.FileName is { Length: > 0 } disposition
        ? disposition : part.ContentType.Name is { Length: > 0 } name ? name : null;

    /// <summary>Change only the outgoing filename metadata, never the attachment's content or original file.</summary>
    public static void Rename(MimeEntity part, string name)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (!ComposeSafety.ValidAttachmentName(name)) throw new ArgumentException("Choose a valid attachment filename.", nameof(name));
        part.ContentDisposition ??= new ContentDisposition(ContentDisposition.Attachment);
        part.ContentDisposition.FileName = name;
        part.ContentType.Name = name;
    }
}
