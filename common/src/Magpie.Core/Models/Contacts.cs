namespace Magpie.Core.Models;

/// <summary>A change made to a saved contact on this PC that hasn't reached Google yet (design B4).</summary>
public enum PendingContactOp { None = 0, Create = 1, Update = 2, Delete = 3 }

/// <summary>A contact saved in a Google account (design B4), kept on this PC.</summary>
public sealed class SavedContact
{
    /// <summary>Row id on this PC.</summary>
    public long Id { get; set; }
    public string AccountId { get; set; } = "";
    /// <summary>Google's "people/c123…"; empty until a new contact has reached Google.</summary>
    public string Resource { get; set; } = "";
    public string Etag { get; set; } = "";
    public string GivenName { get; set; } = "";
    public string FamilyName { get; set; } = "";
    /// <summary>The name as Google shows it ("Anita Rao").</summary>
    public string Name { get; set; } = "";
    public List<string> Emails { get; set; } = new();
    public List<string> Phones { get; set; } = new();
    public string Company { get; set; } = "";
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>Group names ("Family", "Work"); Google's system groups (My Contacts, Starred…) are left out.</summary>
    public List<string> Groups { get; set; } = new();
    public PendingContactOp Pending { get; set; }
    public DateTimeOffset Updated { get; set; }

    public string Display => Name.Length > 0 ? Name : (GivenName + " " + FamilyName).Trim() is { Length: > 0 } n ? n : Emails.FirstOrDefault() ?? "(no name)";
    public string PrimaryEmail => Emails.FirstOrDefault() ?? "";
    /// <summary>"Product manager · VendorCo", "VendorCo" or "".</summary>
    public string Work => string.Join(" · ", new[] { Title, Company }.Where(s => s.Length > 0));
}

/// <summary>One suggestion while typing a recipient (design B4): a saved contact's address, or someone you mailed ("Recent").</summary>
public sealed record ContactSuggestion(string Name, string Address, bool Saved, int Count)
{
    public string Tag => Saved ? "" : "Recent";
    public string Label => string.IsNullOrWhiteSpace(Name) ? Address : Name;
}
