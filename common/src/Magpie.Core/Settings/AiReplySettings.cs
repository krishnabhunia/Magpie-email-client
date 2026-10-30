using Magpie.Core.Mail;

namespace Magpie.Core.Settings;

/// <summary>FR1 + AW1: full drafts triggered by Reply / Reply All / Forward, separate from short suggested replies and prompt drafting.</summary>
public sealed class AiReplySettings
{
    public const string ConversationContext = "Current conversation";
    public const string LatestMessageContext = "Latest received message only";
    /// <summary>AW1 (Q87): Formal / Informal moved to <see cref="Formalities"/>; Apologetic and Enthusiastic added.</summary>
    public static IReadOnlyList<string> Tones { get; } = Array.AsReadOnly(new[] { "Polite", "Professional", "Friendly", "Warm", "Firm", "Empathetic", "Apologetic", "Enthusiastic", "Custom" });
    public static IReadOnlyList<string> Purposes { get; } = Array.AsReadOnly(new[] { "Answer the message", "Make a request", "Give an instruction / order", "Follow up", "Say thank you", "Apologise", "Decline politely", "Custom" });
    public static IReadOnlyList<string> Lengths { get; } = Array.AsReadOnly(new[] { "Short", "Balanced", "Detailed" });
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "Match original", "English", "Bengali", "Hindi", "Marathi", "Tamil", "Telugu", "Gujarati", "Spanish", "French", "German", "Japanese" });
    public static IReadOnlyList<string> Contexts { get; } = Array.AsReadOnly(new[] { ConversationContext, LatestMessageContext });

    // ── AW1 (Q87): formality and culture & etiquette ──
    public static IReadOnlyList<string> Formalities { get; } = Array.AsReadOnly(new[] { "Very casual", "Casual", "Neutral", "Formal", "Very formal" });
    public static IReadOnlyList<string> EnglishStyles { get; } = Array.AsReadOnly(new[] { "Match sender", "Indian English", "British English", "American English", "Global plain English" });
    public static IReadOnlyList<string> Etiquettes { get; } = Array.AsReadOnly(new[] { "Neutral", "Indian", "Japanese", "German", "Middle East", "US" });
    public static IReadOnlyList<string> Directnesses { get; } = Array.AsReadOnly(new[] { "Direct", "Balanced", "Diplomatic" });
    public static IReadOnlyList<string> Greetings { get; } = Array.AsReadOnly(new[] { "Auto", "Dear", "Hi", "Hello", "Respected", "No greeting" });
    public static IReadOnlyList<string> Addresses { get; } = Array.AsReadOnly(new[] { "Auto", "First name", "Ms / Mr + surname", "Sir / Madam", "Name + ji" });
    public static IReadOnlyList<string> EmojiChoices { get; } = Array.AsReadOnly(new[] { "Never", "Only when Casual or Very casual" });

    public bool Enabled { get; set; }
    public bool OnReply { get; set; } = true;
    public bool OnReplyAll { get; set; } = true;
    /// <summary>AW1 (Q85): new switch, Off until the user turns it on.</summary>
    public bool OnForward { get; set; }
    /// <summary>AW1 (Q86): show "What the reply says" (Magpie suggests the answer; the user can change it).</summary>
    public bool ShowStance { get; set; } = true;
    /// <summary>AW1 (Q87): use the style last used with a recipient for the next draft to that address.</summary>
    public bool RememberPerContact { get; set; }

    public string Tone { get; set; } = "Professional";
    public string Purpose { get; set; } = "Answer the message";
    public string Length { get; set; } = "Balanced";
    public string Language { get; set; } = "Match original";
    public string Context { get; set; } = ConversationContext;
    public string Instructions { get; set; } = "";
    public string CustomTone { get; set; } = "";
    public string CustomPurpose { get; set; } = "";

    public string Formality { get; set; } = "Formal";
    public string EnglishStyle { get; set; } = "Indian English";
    public string Etiquette { get; set; } = "Neutral";
    public string Directness { get; set; } = "Balanced";
    public string Greeting { get; set; } = "Auto";
    public string Address { get; set; } = "Auto";
    public string Emoji { get; set; } = "Never";

    /// <summary>Lower-case address → the style last used with that person (only filled when <see cref="RememberPerContact"/> is on).</summary>
    public Dictionary<string, AiStyle> ContactStyles { get; set; } = new();
    public const int MaxContactStyles = 300;

    public bool ShouldGenerate(ComposeMode mode) => Enabled && (mode == ComposeMode.Reply && OnReply
        || mode == ComposeMode.ReplyAll && OnReplyAll || mode == ComposeMode.Forward && OnForward);

    public void Normalise()
    {
        // AW1 migration: Formal / Informal were tones until 10.0.0; they are now the formality scale.
        var oldTone = (Tone ?? "").Trim();
        if (oldTone.Equals("Formal", StringComparison.OrdinalIgnoreCase)) { Tone = "Professional"; Formality = "Formal"; }
        else if (oldTone.Equals("Informal", StringComparison.OrdinalIgnoreCase)) { Tone = "Friendly"; Formality = "Casual"; }
        Tone = Pick(Tone, Tones, "Professional");
        Purpose = Pick(Purpose, Purposes, "Answer the message");
        Length = Pick(Length, Lengths, "Balanced");
        Language = Pick(Language, Languages, "Match original");
        Context = Pick(Context, Contexts, ConversationContext);
        Formality = Pick(Formality, Formalities, "Formal");
        EnglishStyle = Pick(EnglishStyle, EnglishStyles, "Indian English");
        Etiquette = Pick(Etiquette, Etiquettes, "Neutral");
        Directness = Pick(Directness, Directnesses, "Balanced");
        Greeting = Pick(Greeting, Greetings, "Auto");
        Address = Pick(Address, Addresses, "Auto");
        Emoji = Pick(Emoji, EmojiChoices, "Never");
        Instructions = Clean(Instructions, 4000);
        CustomTone = Clean(CustomTone, 500);
        CustomPurpose = Clean(CustomPurpose, 1000);
        var styles = new Dictionary<string, AiStyle>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, style) in ContactStyles ?? new())
        {
            var address = (key ?? "").Trim().ToLowerInvariant();
            if (address.Length is 0 or > 320 || !address.Contains('@') || style == null) continue;
            var copy = style.Clone();
            copy.Normalise();
            styles[address] = copy;
        }
        ContactStyles = styles.OrderByDescending(p => p.Value.UsedAt).Take(MaxContactStyles).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
    }

    public string? ValidationError() => Tone == "Custom" && string.IsNullOrWhiteSpace(CustomTone) ? "Write your custom reply tone first."
        : Purpose == "Custom" && string.IsNullOrWhiteSpace(CustomPurpose) ? "Write your custom reply purpose first." : null;

    public AiReplySettings Clone()
    {
        var c = (AiReplySettings)MemberwiseClone();
        c.ContactStyles = (ContactStyles ?? new()).ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        return c;
    }

    /// <summary>The style part of these defaults.</summary>
    public AiStyle Style() => new()
    {
        Tone = Tone, CustomTone = CustomTone, Formality = Formality, Length = Length, Language = Language,
        EnglishStyle = EnglishStyle, Etiquette = Etiquette, Directness = Directness, Greeting = Greeting, Address = Address, Emoji = Emoji,
    };

    /// <summary>Writes a style into these settings (used for "Save these choices as my defaults" and for one request).</summary>
    public void Apply(AiStyle s)
    {
        Tone = s.Tone; CustomTone = s.CustomTone; Formality = s.Formality; Length = s.Length; Language = s.Language;
        EnglishStyle = s.EnglishStyle; Etiquette = s.Etiquette; Directness = s.Directness; Greeting = s.Greeting; Address = s.Address; Emoji = s.Emoji;
    }

    /// <summary>The remembered style for an address, or null.</summary>
    public AiStyle? StyleFor(string? address)
    {
        if (!RememberPerContact || string.IsNullOrWhiteSpace(address)) return null;
        return ContactStyles != null && ContactStyles.TryGetValue(address.Trim().ToLowerInvariant(), out var s) ? s.Clone() : null;
    }

    /// <summary>Remembers a style for an address. Returns false when it was already stored unchanged (nothing to save).</summary>
    public bool Remember(string? address, AiStyle style, DateTimeOffset now)
    {
        if (!RememberPerContact || string.IsNullOrWhiteSpace(address) || !address.Contains('@')) return false;
        var key = address.Trim().ToLowerInvariant();
        var copy = style.Clone();
        copy.Normalise();
        ContactStyles ??= new(StringComparer.OrdinalIgnoreCase);
        if (ContactStyles.TryGetValue(key, out var old) && old.SameAs(copy) && now - old.UsedAt < TimeSpan.FromDays(1)) return false;
        copy.UsedAt = now;
        ContactStyles[key] = copy;
        if (ContactStyles.Count > MaxContactStyles)
            ContactStyles = ContactStyles.OrderByDescending(p => p.Value.UsedAt).Take(MaxContactStyles).ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    internal static string Pick(string? value, IReadOnlyList<string> choices, string fallback) => choices.FirstOrDefault(c => c.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
    internal static string Clean(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length > max ? text[..max] : text;
    }
}

/// <summary>AW1: how a draft sounds — the part that can be changed for one draft, saved as defaults or remembered per contact.</summary>
public sealed class AiStyle
{
    public string Tone { get; set; } = "Professional";
    public string CustomTone { get; set; } = "";
    public string Formality { get; set; } = "Formal";
    public string Length { get; set; } = "Balanced";
    public string Language { get; set; } = "Match original";
    public string EnglishStyle { get; set; } = "Indian English";
    public string Etiquette { get; set; } = "Neutral";
    public string Directness { get; set; } = "Balanced";
    public string Greeting { get; set; } = "Auto";
    public string Address { get; set; } = "Auto";
    public string Emoji { get; set; } = "Never";
    public DateTimeOffset UsedAt { get; set; }

    public AiStyle Clone() => (AiStyle)MemberwiseClone();

    public void Normalise()
    {
        var oldTone = (Tone ?? "").Trim();
        if (oldTone.Equals("Formal", StringComparison.OrdinalIgnoreCase)) { Tone = "Professional"; Formality = "Formal"; }
        else if (oldTone.Equals("Informal", StringComparison.OrdinalIgnoreCase)) { Tone = "Friendly"; Formality = "Casual"; }
        Tone = AiReplySettings.Pick(Tone, AiReplySettings.Tones, "Professional");
        CustomTone = AiReplySettings.Clean(CustomTone, 500);
        if (Tone == "Custom" && CustomTone.Length == 0) Tone = "Professional";
        Formality = AiReplySettings.Pick(Formality, AiReplySettings.Formalities, "Formal");
        Length = AiReplySettings.Pick(Length, AiReplySettings.Lengths, "Balanced");
        Language = AiReplySettings.Pick(Language, AiReplySettings.Languages, "Match original");
        EnglishStyle = AiReplySettings.Pick(EnglishStyle, AiReplySettings.EnglishStyles, "Indian English");
        Etiquette = AiReplySettings.Pick(Etiquette, AiReplySettings.Etiquettes, "Neutral");
        Directness = AiReplySettings.Pick(Directness, AiReplySettings.Directnesses, "Balanced");
        Greeting = AiReplySettings.Pick(Greeting, AiReplySettings.Greetings, "Auto");
        Address = AiReplySettings.Pick(Address, AiReplySettings.Addresses, "Auto");
        Emoji = AiReplySettings.Pick(Emoji, AiReplySettings.EmojiChoices, "Never");
    }

    public bool SameAs(AiStyle o) => Tone == o.Tone && CustomTone == o.CustomTone && Formality == o.Formality && Length == o.Length
        && Language == o.Language && EnglishStyle == o.EnglishStyle && Etiquette == o.Etiquette && Directness == o.Directness
        && Greeting == o.Greeting && Address == o.Address && Emoji == o.Emoji;
}
