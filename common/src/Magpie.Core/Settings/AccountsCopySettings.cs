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
    /// <summary>When "what comes along" was last chosen on this PC (the newest choice of any PC wins).</summary>
    public DateTimeOffset ChoicesChanged { get; set; }
    /// <summary>The "stopped" note (its time) found in the home mailbox when the copy was turned on here again: exactly
    /// that note is written over at the first round, never followed. Any other note is another PC's and is followed.</summary>
    public DateTimeOffset? OverwriteNote { get; set; }
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
    /// <summary>Accounts removed on this PC: their id here → email. A rule still naming one by id (an open Settings window
    /// writes back the rules it loaded) is turned back into "@email" at every save.</summary>
    public Dictionary<string, string> FormerIds { get; set; } = new();
    /// <summary>Accounts added by hand while the copy is on, before this PC saw whether the copy has them: at the next
    /// round they take the copy's version (another PC's signature, name…) and keep the sign-in made here.</summary>
    public List<string> Adopt { get; set; } = new();
    /// <summary>Mailboxes (email) where older copies still have to go after a stop or move whose clean-up didn't finish:
    /// only the newest email there stays. Kept also when the copy is off; tried at every check until done.</summary>
    public List<string> CleanUp { get; set; } = new();
    /// <summary>Spare copies a move that didn't finish may have left in the new home: one goes only when it is still the
    /// newest there and the old home is seen to hold no "moved" note pointing at it (no PC uses it).</summary>
    public List<SpareCopy> Spares { get; set; } = new();
    /// <summary>The time of the "stopped" note this PC wrote (its own stop, also when it didn't finish).</summary>
    public DateTimeOffset? MyStopNote { get; set; }

    public void Normalise()
    {
        HomeEmail = (HomeEmail ?? "").Trim().ToLowerInvariant();
        PcName ??= "";
        UsedBy ??= new();
        Stamps = Lower(Stamps);
        foreach (var st in Stamps.Values) { st.Hash ??= ""; st.Local ??= ""; }
        RemovedHere = Lower(RemovedHere);
        SignedIn = Lower(SignedIn);
        FormerIds = (FormerIds ?? new()).Where(kv => !string.IsNullOrEmpty(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim().ToLowerInvariant());
        CleanUp = (CleanUp ?? new()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
        Spares = (Spares ?? new()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.At)).ToList();
        foreach (var x in Spares) { x.At = x.At.Trim().ToLowerInvariant(); x.Old = (x.Old ?? "").Trim().ToLowerInvariant(); }
        Adopt = (Adopt ?? new()).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
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

public sealed class SpareCopy
{
    /// <summary>The mailbox holding the spare copy.</summary>
    public string At { get; set; } = "";
    /// <summary>That copy's time (as written in it).</summary>
    public DateTimeOffset Updated { get; set; }
    /// <summary>The old home, which may hold a "moved" note pointing at <see cref="At"/>.</summary>
    public string Old { get; set; } = "";
    /// <summary>Known to be unused (the "moved" note never got to the old home): it goes on its time alone.</summary>
    public bool Unused { get; set; }
}

public sealed class CopyStamp
{
    /// <summary>The fingerprint as it is in the copy.</summary>
    public string Hash { get; set; } = "";
    /// <summary>This PC's own fingerprint of the same thing (another Magpie version may differ a little); a change here
    /// is a change of this one.</summary>
    public string Local { get; set; } = "";
    public DateTimeOffset Changed { get; set; }
}
