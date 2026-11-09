namespace Magpie.Core.Mail;

public static class ComposeTranslation
{
    public static string Instruction(string? language)
    {
        var target = language?.Trim() ?? "";
        if (target.Length is 0 or > 60 || target.Any(char.IsControl))
            throw new ArgumentException("Enter a target language of 1–60 characters.", nameof(language));
        return "Translate the selected message text into " + target
            + ". Preserve its meaning, names, numbers, paragraphs and tone. Return only the translated text; do not add commentary or send anything.";
    }
}
