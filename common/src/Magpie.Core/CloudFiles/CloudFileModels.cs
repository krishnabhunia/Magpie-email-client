using System.Net;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.Core.CloudFiles;

public enum CloudFileAccess { View, Edit }

/// <summary>A file already owned by the selected cloud account; selecting it never modifies it.</summary>
public sealed record CloudExistingFile(AccountKind Provider, string Id, string Name, string WebUrl, string DriveId = "")
{
    public bool IsValid => Provider is AccountKind.Gmail or AccountKind.Microsoft
        && CloudUploadedFile.SafeId(Id) && CloudUploadedFile.SafeLinkTitle(Name)
        && CloudUploadedFile.IsProviderWebUrl(Provider, WebUrl)
        && (Provider != AccountKind.Microsoft || CloudUploadedFile.SafeId(DriveId));
}

public sealed record CloudExistingFilesPage(IReadOnlyList<CloudExistingFile> Files, bool HasMore);

/// <summary>Consent to change an existing file's named access, independent of new-copy uploads.</summary>
public sealed record CloudExistingSharePlan(Account Account, CloudExistingFile File, string LinkTitle,
    string[] Recipients, CloudFileAccess Access)
{
    public static CloudExistingSharePlan Create(Account account, CloudExistingFile file, string title,
        string to, string cc, string bcc, CloudFileAccess access)
    {
        if (account.Kind is not (AccountKind.Gmail or AccountKind.Microsoft) || account.Kind != file.Provider || !file.IsValid)
            throw new InvalidOperationException("Choose a valid file from the selected cloud account.");
        title = title.Trim();
        if (!Enum.IsDefined(access) || !CloudUploadedFile.SafeLinkTitle(title))
            throw new InvalidOperationException("Choose a link title of at most 160 characters without control characters and a supported access role.");
        return new(account.Clone(), file, title, CloudSharePlan.RecipientsFor(to, cc, bcc), access);
    }
}

/// <summary>An explicit upload and named-recipient grant, never an anonymous sharing link.</summary>
public sealed record CloudSharePlan(Account Account, string FileName, long Length, string[] Recipients, CloudFileAccess Access)
{
    public const long MaximumBytes = 100L * 1024 * 1024;
    public const int MaximumRecipients = 50;

    public static CloudSharePlan Create(Account account, string fileName, long length,
        string to, string cc, string bcc, CloudFileAccess access)
    {
        if (account.Kind is not (AccountKind.Gmail or AccountKind.Microsoft))
            throw new InvalidOperationException("Choose a Google or Microsoft account for the cloud file.");
        if (!Enum.IsDefined(access)) throw new ArgumentOutOfRangeException(nameof(access));
        if (!ComposeSafety.ValidAttachmentName(fileName))
            throw new InvalidOperationException("Choose a file with a valid filename of at most 160 characters.");
        if (length <= 0 || length > MaximumBytes)
            throw new InvalidOperationException("Cloud attachments must contain data and be no larger than 100 MB.");
        var recipients = RecipientsFor(to, cc, bcc);
        return new(account.Clone(), fileName, length, recipients, access);
    }

    public static string[] RecipientsFor(string to, string cc, string bcc)
    {
        if (Composer.InvalidAddresses(to).Concat(Composer.InvalidAddresses(cc)).Concat(Composer.InvalidAddresses(bcc)).Any())
            throw new InvalidOperationException("Correct the recipient addresses before sharing a cloud file.");
        var visible = Composer.ParseAddresses(to).Mailboxes.Concat(Composer.ParseAddresses(cc).Mailboxes)
            .Select(x => x.Address).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        // Cloud collaborators can inspect access lists. Granting a Bcc-only identity could expose a hidden recipient.
        if (Composer.ParseAddresses(bcc).Mailboxes.Any(x => !visible.Contains(x.Address, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Cloud sharing cannot include hidden Bcc recipients. Attach the file normally, or review the recipients before uploading.");
        if (visible.Length == 0 || visible.Length > MaximumRecipients)
            throw new InvalidOperationException("Add between 1 and 50 recipients in To or Cc before sharing a cloud file.");
        return visible;
    }
}

/// <summary>Non-secret receipt for an app-created file. It never contains access tokens or upload-session URLs.</summary>
public sealed record CloudUploadedFile
{
    public string AccountId { get; init; } = "";
    public string AccountEmail { get; init; } = "";
    public AccountKind Provider { get; init; }
    public string FileId { get; init; } = "";
    public string ContainerId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string LinkTitle { get; init; } = "";
    public bool IsExisting { get; init; }
    public string DisplayName => string.IsNullOrEmpty(LinkTitle) ? FileName : LinkTitle;
    public string WebUrl { get; init; } = "";
    public string[] Recipients { get; init; } = Array.Empty<string>();
    public Dictionary<string, string> PermissionIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public CloudFileAccess Access { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool IsValid => Provider is AccountKind.Gmail or AccountKind.Microsoft
        && !string.IsNullOrWhiteSpace(AccountId) && AccountId.Length <= 256
        && (IsExisting ? SafeLinkTitle(FileName) : ComposeSafety.ValidAttachmentName(FileName))
        && (string.IsNullOrEmpty(LinkTitle) || SafeLinkTitle(LinkTitle)) && SafeId(FileId)
        && (Provider != AccountKind.Microsoft || SafeId(ContainerId))
        && IsProviderWebUrl(Provider, WebUrl) && Enum.IsDefined(Access)
        && Recipients is { Length: > 0 and <= CloudSharePlan.MaximumRecipients }
        && Recipients.All(x => Composer.ParseAddresses(x).Mailboxes.Count() == 1 && !Composer.InvalidAddresses(x).Any())
        && PermissionIds != null && PermissionIds.Count <= CloudSharePlan.MaximumRecipients
        && PermissionIds.All(x => Recipients.Contains(x.Key, StringComparer.OrdinalIgnoreCase) && SafeId(x.Value))
        && Recipients.All(x => x.Equals(AccountEmail, StringComparison.OrdinalIgnoreCase)
            || PermissionIds.Keys.Contains(x, StringComparer.OrdinalIgnoreCase));

    public static bool SafeId(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 512
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '!' or '.' or '=' or '+');

    public static bool SafeLinkTitle(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 160
        && value == value.Trim() && !value.Any(char.IsControl);

    public static bool IsProviderWebUrl(AccountKind provider, string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsDefaultPort || uri.UserInfo.Length > 0 || value!.Length > 4096) return false;
        var host = uri.IdnHost;
        return provider == AccountKind.Gmail ? host is "drive.google.com" or "docs.google.com"
            : provider == AccountKind.Microsoft && (host is "onedrive.live.com" or "1drv.ms"
                || host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase));
    }

    public bool AppearsIn(string html) => WebUtility.HtmlDecode(html).Contains(WebUrl, StringComparison.Ordinal);

    public string? RecipientProblem(string to, string cc, string bcc)
    {
        string[] current;
        try { current = CloudSharePlan.RecipientsFor(to, cc, bcc); }
        catch (InvalidOperationException ex) { return ex.Message; }
        return current.Any(x => !Recipients.Contains(x, StringComparer.OrdinalIgnoreCase)
                && !x.Equals(AccountEmail, StringComparison.OrdinalIgnoreCase))
            ? IsExisting
                ? $"The recipients changed after sharing {FileName}. Remove its link, review access to the existing file again for the new recipients, or attach a local copy."
                : $"The recipients changed after sharing {FileName}. Remove its cloud link and upload a new copy for the reviewed recipients, or attach the file normally."
            : null;
    }
}

public sealed record CloudUploadProgress(string Stage, long UploadedBytes = 0, long TotalBytes = 0);
public sealed record CloudAuthorization(string AccessToken, string Email, string UserId, string? DriveOwnerId = null);

public interface ICloudFileAuthorization
{
    Task<CloudAuthorization> AuthorizeAsync(Account account, CancellationToken ct);
}

/// <summary>Broader file-browsing permission is an independent, opt-in credential.</summary>
public interface ICloudExistingFileAuthorization
{
    Task<CloudAuthorization> AuthorizeExistingAsync(Account account, CancellationToken ct);
}

public sealed class CloudSharingException : Exception
{
    public bool CleanupRequired { get; }
    public string? RecoveryUrl { get; }
    public CloudSharingException(string message, bool cleanupRequired = false, string? recoveryUrl = null) : base(message)
    {
        CleanupRequired = cleanupRequired;
        RecoveryUrl = recoveryUrl;
    }
}
