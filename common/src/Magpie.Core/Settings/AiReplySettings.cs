using Magpie.Core.Mail;

namespace Magpie.Core.Settings;

/// <summary>FR1: full replies triggered by Reply/Reply All, separate from short suggested replies and prompt drafting.</summary>
public sealed class AiReplySettings
{
    public const string ConversationContext = "Current conversation";
    public const string LatestMessageContext = "Latest received message only";
    public static IReadOnlyList<string> Tones { get; } = Array.AsReadOnly(new[] { "Formal", "Informal", "Polite", "Professional", "Friendly", "Warm", "Firm", "Empathetic", "Custom" });
    public static IReadOnlyList<string> Purposes { get; } = Array.AsReadOnly(new[] { "Answer the message", "Make a request", "Give an instruction / order", "Follow up", "Say thank you", "Apologise", "Decline politely", "Custom" });
    public static IReadOnlyList<string> Lengths { get; } = Array.AsReadOnly(new[] { "Short", "Balanced", "Detailed" });
    public static IReadOnlyList<string> Languages { get; } = Array.AsReadOnly(new[] { "Match original", "English", "Bengali", "Hindi" });
    public static IReadOnlyList<string> Contexts { get; } = Array.AsReadOnly(new[] { ConversationContext, LatestMessageContext });

    public bool Enabled { get; set; }
    public bool OnReply { get; set; } = true;
    public bool OnReplyAll { get; set; } = true;
    public string Tone { get; set; } = "Formal";
    public string Purpose { get; set; } = "Answer the message";
    public string Length { get; set; } = "Balanced";
    public string Language { get; set; } = "Match original";
    public string Context { get; set; } = ConversationContext;
    public string Instructions { get; set; } = "";
    public string CustomTone { get; set; } = "";
    public string CustomPurpose { get; set; } = "";

    public bool ShouldGenerate(ComposeMode mode) => Enabled && (mode == ComposeMode.Reply && OnReply || mode == ComposeMode.ReplyAll && OnReplyAll);

    public void Normalise()
    {
        Tone = Pick(Tone, Tones, "Formal");
        Purpose = Pick(Purpose, Purposes, "Answer the message");
        Length = Pick(Length, Lengths, "Balanced");
        Language = Pick(Language, Languages, "Match original");
        Context = Pick(Context, Contexts, ConversationContext);
        Instructions = Clean(Instructions, 4000);
        CustomTone = Clean(CustomTone, 500);
        CustomPurpose = Clean(CustomPurpose, 1000);
    }

    public string? ValidationError() => Tone == "Custom" && string.IsNullOrWhiteSpace(CustomTone) ? "Write your custom reply tone first."
        : Purpose == "Custom" && string.IsNullOrWhiteSpace(CustomPurpose) ? "Write your custom reply purpose first." : null;

    public AiReplySettings Clone() => (AiReplySettings)MemberwiseClone();

    private static string Pick(string? value, IReadOnlyList<string> choices, string fallback) => choices.FirstOrDefault(c => c.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? fallback;
    private static string Clean(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length > max ? text[..max] : text;
    }
}
