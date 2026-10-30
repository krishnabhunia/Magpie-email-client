using Magpie.Core.Settings;

namespace Magpie.Core.Ai;

/// <summary>AW1: the "Example · using these defaults" text in Settings → AI writing. Built locally from fixed sentences — it never calls a
/// model, so it only illustrates how tone, formality, length and culture choices move a reply ("Can you finish the Q3 report by Friday?").</summary>
public static class AiWritingExample
{
    private static int Register(string formality) => formality switch { "Very casual" or "Casual" => 0, "Neutral" => 1, _ => 2 };

    private static readonly Dictionary<string, string[]> Openers = new()
    {
        ["Polite"] = new[] { "Thanks for asking.", "Thank you for your message.", "Thank you for your email." },
        ["Professional"] = new[] { "", "Thanks for the note on the Q3 report.", "Thank you for your email regarding the Q3 report." },
        ["Friendly"] = new[] { "Thanks for the heads-up!", "Thanks for reaching out!", "Thank you for reaching out." },
        ["Warm"] = new[] { "Hope your week's going well!", "I hope your week is going well.", "I hope this finds you well." },
        ["Firm"] = new[] { "", "", "" },
        ["Empathetic"] = new[] { "I know Friday's tight for everyone.", "I understand the deadline is tight.", "I appreciate that the timeline is demanding." },
        ["Apologetic"] = new[] { "Sorry for the slow reply.", "Apologies for the delayed reply.", "Please accept my apologies for the delayed response." },
        ["Enthusiastic"] = new[] { "Love this one!", "Happy to help with this!", "I would be delighted to assist." },
        ["Custom"] = new[] { "", "", "" },
    };

    private static readonly string[] Main =
    {
        "Sure, I'll get the Q3 report done by Friday {d}.",
        "Yes, I can finish the Q3 report by Friday {d}.",
        "I confirm that I will complete the Q3 report by Friday {d}.",
    };

    private static readonly string[] Extra =
    {
        "I'll {p} it and ping you if anything's missing.",
        "I'll {p} it and let you know if I need anything else.",
        "I shall {p} it and will inform you in advance should anything be missing.",
    };

    public static string Build(AiReplySettings settings)
    {
        var s = settings.Clone();
        s.Normalise();
        var r = Register(s.Formality);
        var us = s.EnglishStyle == "American English";
        var date = us ? "(Oct 31)" : "(31 Oct)";
        var parts = new List<string>();
        var opener = Openers.TryGetValue(s.Tone, out var o) ? o[r] : "";
        if (r >= 1 && s.Etiquette == "Japanese") opener = "Thank you very much for thinking of me for this.";
        else if (r >= 1 && s.Etiquette == "Middle East") opener = "I hope you and your family are well.";
        else if (s.Etiquette == "US") opener = "Thanks for looping me in.";
        if (s.Directness == "Direct" || s.Etiquette == "German") opener = s.Tone == "Apologetic" ? opener : "";
        var main = Main[r].Replace("{d}", date);
        if (s.Emoji != "Never" && r == 0) main += " 👍";
        if (s.Length != "Short" && opener.Length > 0) parts.Add(opener);
        parts.Add(main);
        if (s.Length == "Detailed") parts.Add(Extra[r].Replace("{p}", us ? "prioritize" : "prioritise"));
        if (s.Length != "Short" && r >= 1 && s.Etiquette == "Japanese") parts.Add("I appreciate your understanding.");
        if (s.Length != "Short" && r >= 1 && s.Etiquette == "Indian") parts.Add("Please do let me know if anything further is needed.");
        var greeting = Greeting(s);
        var body = string.Join(s.Length == "Detailed" ? "\n\n" : " ", parts);
        return greeting.Length > 0 ? greeting + "\n\n" + body : body;
    }

    private static string Greeting(AiReplySettings s)
    {
        if (s.Greeting == "No greeting") return "";
        var veryFormal = s.Formality == "Very formal";
        var address = s.Address == "Auto" ? (veryFormal ? "Ms / Mr + surname" : "First name") : s.Address;
        var name = address switch
        {
            "Ms / Mr + surname" => "Ms Chen",
            "Sir / Madam" => "Madam",
            "Name + ji" => "Maya ji",
            _ => "Maya",
        };
        var word = s.Greeting;
        if (word == "Auto")
            word = s.Etiquette == "Indian" && veryFormal && address == "Sir / Madam" ? "Respected" : Register(s.Formality) == 2 ? "Dear" : "Hi";
        return $"{word} {name},";
    }
}
