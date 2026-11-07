using Magpie.Core.Models;
using MimeKit;

namespace Magpie.Core.Mail;

public sealed record SenderIdentity(string Address, string Name, bool Available = true)
{
    public string Label => Available
        ? new MailboxAddress(Name, Address).ToString()
        : Address + " — unavailable; choose a configured sender";
}

/// <summary>Explicit send-as identities. These never change the account used for authentication or archiving.</summary>
public static class SenderIdentities
{
    public const int MaximumAliases = 20;

    public static IReadOnlyList<SenderIdentity> ForAccount(Account account) =>
        new[] { new SenderIdentity(account.Email, account.DisplayName) }
            .Concat(Normalise(account.SenderAliases, account.Email).Select(a => new SenderIdentity(a.Email, a.Name))).ToArray();

    public static SenderIdentity Resolve(Account account, string? address)
    {
        var selected = string.IsNullOrWhiteSpace(address) ? account.Email : address.Trim();
        return ForAccount(account).FirstOrDefault(a => string.Equals(a.Address, selected, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected sender is no longer configured for this account. Choose a sender from the Send as list before saving or sending.");
    }

    public static List<SenderAlias> Parse(string? text, string primaryAddress)
    {
        var result = new List<SenderAlias>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryAddress };
        foreach (var line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0) continue;
            if (value.Length > 500 || value.Any(char.IsControl) || !MailboxAddress.TryParse(value, out var mailbox)
                || Composer.InvalidAddresses(value).Count != 0 || mailbox.Address.Length > 320 || (mailbox.Name?.Length ?? 0) > 100)
                throw new FormatException("Enter one valid sender address per line, optionally as Name <address@example.com>.");
            if (!seen.Add(mailbox.Address))
                throw new FormatException("Each sender alias must be unique and different from the account's main address.");
            if (result.Count == MaximumAliases)
                throw new FormatException($"You can configure up to {MaximumAliases} sender aliases per account.");
            result.Add(new SenderAlias { Email = mailbox.Address, Name = mailbox.Name ?? "" });
        }
        return result;
    }

    public static List<SenderAlias> Normalise(IEnumerable<SenderAlias>? aliases, string primaryAddress)
    {
        var result = new List<SenderAlias>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaryAddress };
        foreach (var alias in aliases ?? Array.Empty<SenderAlias>())
        {
            if (alias == null || string.IsNullOrWhiteSpace(alias.Email) || alias.Email.Length > 320
                || (alias.Name?.Length ?? 0) > 100 || (alias.Email + alias.Name).Any(char.IsControl)) continue;
            try
            {
                var parsed = Parse(new MailboxAddress(alias.Name ?? "", alias.Email.Trim()).ToString(), primaryAddress).Single();
                if (seen.Add(parsed.Email)) result.Add(parsed);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { }
            if (result.Count == MaximumAliases) break;
        }
        return result;
    }

    public static string Format(IEnumerable<SenderAlias>? aliases) => string.Join(Environment.NewLine,
        (aliases ?? Array.Empty<SenderAlias>()).Select(a => new MailboxAddress(a.Name ?? "", a.Email).ToString()));
}
