using System.Security.Cryptography;
using System.Text;
using MailKit;
using MailKit.Net.Smtp;
using MimeKit;

namespace Magpie.Core.Mail;

/// <summary>A request for an SMTP delivery-status notification, not evidence that a message was delivered or read.</summary>
public enum DeliveryReceiptState { Pending = 0, Requested = 1, Unavailable = 2 }

public sealed record DeliveryReceiptResult(DeliveryReceiptState State, string ServerResponse)
{
    public string Summary => State switch
    {
        DeliveryReceiptState.Requested => "Delivery notification requested — delivery is not confirmed",
        DeliveryReceiptState.Unavailable => "Delivery notification unavailable — the server accepted this message without a notification request",
        _ => "Delivery notification requested — server result not yet recorded",
    };

    public string Detail => Summary + (string.IsNullOrEmpty(ServerResponse) ? "" : "\nSMTP response: " + ServerResponse);
}

public static class DeliveryReceipts
{
    // Used only in saved drafts. SMTP NOTIFY is an envelope option, not a message header.
    public const string DraftHeader = "X-Magpie-Delivery-Receipt";
    public const string DraftRequest = "success,failure";
    public const string ComposeHelp = "Requests success or failure notifications from the sending server. If it does not support them, the message sends normally and its status says notifications were unavailable. A notification is not a read receipt or a delivery guarantee.";

    public static bool ReadDraftRequest(MimeMessage message) =>
        string.Equals(message.Headers[DraftHeader]?.Trim(), DraftRequest, StringComparison.OrdinalIgnoreCase);

    public static void RemoveDraftMetadata(MimeMessage message) => message.Headers.RemoveAll(DraftHeader);

    public static string SafeServerResponse(string? response)
    {
        var value = string.Concat((response ?? "").Where(c => !char.IsControl(c))).Trim();
        return value[..Math.Min(value.Length, 512)];
    }

    // A fixed, printable identifier remains within RFC 3461's 100-character bound for every Message-ID.
    public static string EnvelopeId(MimeMessage message) =>
        "magpie-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.MessageId ?? ""))).ToLowerInvariant();
}

/// <summary>Uses negotiated SMTP DSN support. Unsupported or rejected requests never masquerade as delivery.</summary>
internal sealed class DeliveryReceiptSmtpClient : SmtpClient
{
    private bool _request;

    protected override DeliveryStatusNotification? GetDeliveryStatusNotifications(MimeMessage message, MailboxAddress mailbox) =>
        _request ? DeliveryStatusNotification.Success | DeliveryStatusNotification.Failure : null;

    protected override string? GetEnvelopeId(MimeMessage message) => _request ? DeliveryReceipts.EnvelopeId(message) : null;

    internal async Task<DeliveryReceiptResult> SendWithReceiptAsync(MimeMessage message, CancellationToken ct)
    {
        DeliveryReceipts.RemoveDraftMetadata(message);
        var supported = Capabilities.HasFlag(SmtpCapabilities.Dsn);
        _request = supported;
        // Request only headers in reports, rather than asking a server to return the original message body.
        DeliveryStatusNotificationType = supported ? DeliveryStatusNotificationType.HeadersOnly : DeliveryStatusNotificationType.Unspecified;
        try
        {
            var response = await SendAsync(message, ct);
            return new DeliveryReceiptResult(supported ? DeliveryReceiptState.Requested : DeliveryReceiptState.Unavailable,
                DeliveryReceipts.SafeServerResponse(response));
        }
        finally
        {
            _request = false;
            DeliveryStatusNotificationType = DeliveryStatusNotificationType.Unspecified;
        }
    }
}
