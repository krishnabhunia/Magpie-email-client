namespace Magpie.Core.Settings;

/// <summary>
/// Design SY1 (#71, approved A + SA1): "Bring my accounts to other PCs". A locked copy of the accounts (and, if chosen,
/// settings) is kept as one email in a hidden "Magpie Sync" folder of the home account's mailbox. The sync password
/// itself is kept in the SecretVault, never here.
/// </summary>
public sealed class AccountsCopySettings
{
    public bool Enabled { get; set; }
    /// <summary>The home account (whose mailbox holds the copy), by email address.</summary>
    public string HomeEmail { get; set; } = "";
    /// <summary>Rules, quick replies, templates, tags, Gatekeeper lists and look come along too.</summary>
    public bool IncludeSettings { get; set; } = true;
    /// <summary>The AI connections and their keys come along too (off by default).</summary>
    public bool IncludeAiKeys { get; set; }
    /// <summary>Accounts added on another PC are added here by themselves.</summary>
    public bool AutoAdd { get; set; } = true;
    /// <summary>This PC's name in the copy ("used by HP-OMEN, NEW-PC"); empty = the computer name.</summary>
    public string PcName { get; set; } = "";

    /// <summary>The status line: when the copy was last written / read, how many accounts, how big, which PCs use it.</summary>
    public DateTimeOffset? CopyUpdated { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public int CopyAccounts { get; set; }
    public long CopySize { get; set; }
    public List<string> UsedBy { get; set; } = new();

    /// <summary>Per account (lower-case email) and "settings" / "ai": a fingerprint and when it last changed on this PC.
    /// The newer change wins between PCs.</summary>
    public Dictionary<string, CopyStamp> Stamps { get; set; } = new();
    /// <summary>Accounts removed on this PC (email → when), written into the copy so other PCs can ask.</summary>
    public Dictionary<string, DateTimeOffset> RemovedHere { get; set; } = new();
    /// <summary>Accounts in the copy kept off this PC on purpose ("Not now", "Keep it off", removed here).</summary>
    public List<string> NotHere { get; set; } = new();
    /// <summary>When each account last signed in again on any PC (part of its fingerprint, so a new sign-in travels).</summary>
    public Dictionary<string, DateTimeOffset> SignedIn { get; set; } = new();

    public void Normalise()
    {
        HomeEmail = (HomeEmail ?? "").Trim().ToLowerInvariant();
        PcName ??= "";
        UsedBy ??= new();
        Stamps = Lower(Stamps);
        RemovedHere = Lower(RemovedHere);
        SignedIn = Lower(SignedIn);
        NotHere = (NotHere ?? new()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
        if (Enabled && HomeEmail.Length == 0) Enabled = false;
    }

    private static Dictionary<string, T> Lower<T>(Dictionary<string, T>? d)
    {
        var r = new Dictionary<string, T>();
        foreach (var (k, v) in d ?? new()) if (!string.IsNullOrWhiteSpace(k) && v != null) r[k.Trim().ToLowerInvariant()] = v;
        return r;
    }
}

public sealed class CopyStamp
{
    public string Hash { get; set; } = "";
    public DateTimeOffset Changed { get; set; }
}
