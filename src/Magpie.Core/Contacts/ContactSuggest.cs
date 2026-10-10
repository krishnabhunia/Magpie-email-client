using Magpie.Core.Models;

namespace Magpie.Core.Contacts;

/// <summary>One row of the Contacts page list (design B4): a saved contact, or someone you wrote to ("Recent").</summary>
public sealed record ContactEntry(string Key, string Name, string Email, SavedContact? Saved, int Count, DateTimeOffset Last)
{
    public bool IsRecent => Saved == null;
    /// <summary>The letter of its alphabetical section ("#" for digits and symbols).</summary>
    public string Section => Name.Length > 0 && char.IsLetter(Name[0]) ? char.ToUpperInvariant(Name[0]).ToString() : "#";
}

/// <summary>Design B4: recipient suggestions and the Contacts page lists, from saved contacts and people seen in mail.</summary>
public static class ContactSuggest
{
    /// <summary>
    /// Suggestions for what is typed in To / Cc: saved contacts and people you wrote to whose name or address starts with
    /// it (or a word of the name does), most written-to first; a saved contact before an unsaved one of the same address.
    /// </summary>
    public static List<ContactSuggestion> Rank(string typed, IReadOnlyList<SavedContact> saved,
        IReadOnlyList<(string Address, string Name, int Count, DateTimeOffset Last, bool Sent)> people, int limit = 8)
    {
        var q = (typed ?? "").Trim();
        if (q.Length == 0) return new();
        var byAddress = people.GroupBy(p => p.Address.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var found = new Dictionary<string, (ContactSuggestion S, DateTimeOffset Last)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in saved)
            foreach (var email in c.Emails)
            {
                if (!Matches(q, c.Display, email)) continue;
                byAddress.TryGetValue(email.ToLowerInvariant(), out var seen);
                found[email] = (new ContactSuggestion(c.Display, email, true, seen.Count), seen.Last);
            }
        foreach (var p in people)
        {
            if (found.ContainsKey(p.Address) || !Matches(q, p.Name, p.Address)) continue;
            found[p.Address] = (new ContactSuggestion(p.Name, p.Address, false, p.Count), p.Last);
        }
        return found.Values
            .OrderByDescending(x => Weight(x.S))
            .ThenByDescending(x => x.Last)
            .ThenBy(x => x.S.Label, StringComparer.OrdinalIgnoreCase)
            .Take(limit).Select(x => x.S).ToList();
    }

    /// <summary>How often you write to them, with a small lift for saved contacts.</summary>
    private static double Weight(ContactSuggestion s) => s.Count + (s.Saved ? 0.5 : 0);

    public static bool Matches(string typed, string name, string address)
    {
        if (address.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        var at = address.IndexOf('@');
        if (at > 0 && address[(at + 1)..].StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return true;
        return name.Split(new[] { ' ', '.', '-', ',', '(' }, StringSplitOptions.RemoveEmptyEntries).Any(w => w.StartsWith(typed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The Contacts page list: every saved contact, then the people you wrote to who aren't saved ("Recent"),
    /// alphabetical; <paramref name="search"/> matches name, any address, company and phone.
    /// </summary>
    public static List<ContactEntry> Entries(IReadOnlyList<SavedContact> saved,
        IReadOnlyList<(string Address, string Name, int Count, DateTimeOffset Last, bool Sent)> people, string search = "")
    {
        var counts = people.GroupBy(p => p.Address.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        var list = new List<ContactEntry>();
        var savedAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in saved)
        {
            foreach (var e in c.Emails) savedAddresses.Add(e);
            var count = c.Emails.Sum(e => counts.TryGetValue(e.ToLowerInvariant(), out var p) ? p.Count : 0);
            var last = c.Emails.Select(e => counts.TryGetValue(e.ToLowerInvariant(), out var p) ? p.Last : DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
            list.Add(new ContactEntry("c" + c.Id, c.Display, c.PrimaryEmail, c, count, last));
        }
        foreach (var p in people.Where(p => p.Sent && !savedAddresses.Contains(p.Address)))
            list.Add(new ContactEntry("r" + p.Address.ToLowerInvariant(), string.IsNullOrWhiteSpace(p.Name) ? p.Address : p.Name, p.Address, null, p.Count, p.Last));
        var q = (search ?? "").Trim();
        if (q.Length > 0)
            list = list.Where(e => e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || (e.Saved is { } s && (s.Emails.Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase))
                                       || s.Company.Contains(q, StringComparison.OrdinalIgnoreCase)
                                       || s.Phones.Any(x => Digits(x).Contains(Digits(q)) && Digits(q).Length >= 3)))).ToList();
        return list.OrderBy(e => e.Section == "#" ? 1 : 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Email, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"Frequent": the people you write to most (saved or not), up to 25.</summary>
    public static List<ContactEntry> Frequent(IEnumerable<ContactEntry> entries, int limit = 25) =>
        entries.Where(e => e.Count > 0).OrderByDescending(e => e.Count).ThenByDescending(e => e.Last).Take(limit).ToList();

    /// <summary>"Groups": group name → its saved contacts, groups by name.</summary>
    public static List<(string Group, List<ContactEntry> Members)> Groups(IEnumerable<ContactEntry> entries) =>
        entries.Where(e => e.Saved != null).SelectMany(e => e.Saved!.Groups.Select(g => (g, e)))
            .GroupBy(x => x.g, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.Select(x => x.e).ToList())).ToList();

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
}
