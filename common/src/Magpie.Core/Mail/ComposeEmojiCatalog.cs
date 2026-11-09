namespace Magpie.Core.Mail;

public sealed record ComposeEmoji(string Value, string Name, string Category, string Keywords);

/// <summary>A small, offline catalog of common emoji and symbols. No provider or network lookup is involved.</summary>
public static class ComposeEmojiCatalog
{
    public const int MaximumRecent = 24;
    public static IReadOnlyList<ComposeEmoji> Entries { get; } = Array.AsReadOnly(new[]
    {
        new ComposeEmoji("🙂", "Slightly smiling face", "Smileys", "happy smile friendly"),
        new ComposeEmoji("😊", "Smiling face", "Smileys", "happy pleased thanks"),
        new ComposeEmoji("😀", "Grinning face", "Smileys", "happy joy smile"),
        new ComposeEmoji("😄", "Big smile", "Smileys", "happy laughing joy"),
        new ComposeEmoji("😂", "Tears of joy", "Smileys", "laugh funny"),
        new ComposeEmoji("😉", "Winking face", "Smileys", "wink friendly"),
        new ComposeEmoji("🤔", "Thinking face", "Smileys", "think question consider"),
        new ComposeEmoji("😅", "Relieved smile", "Smileys", "relief sweat"),
        new ComposeEmoji("😔", "Pensive face", "Smileys", "sad sorry"),
        new ComposeEmoji("😮", "Surprised face", "Smileys", "surprise wow"),
        new ComposeEmoji("👍", "Thumbs up", "Gestures", "yes approve good agree"),
        new ComposeEmoji("👎", "Thumbs down", "Gestures", "no disagree"),
        new ComposeEmoji("🙏", "Folded hands", "Gestures", "thanks thank you please namaste gratitude"),
        new ComposeEmoji("👏", "Clapping hands", "Gestures", "well done congratulations praise"),
        new ComposeEmoji("👋", "Waving hand", "Gestures", "hello goodbye hi"),
        new ComposeEmoji("👌", "OK hand", "Gestures", "okay good agree"),
        new ComposeEmoji("🤝", "Handshake", "Gestures", "agreement partnership deal"),
        new ComposeEmoji("💪", "Flexed arm", "Gestures", "strong strength support"),
        new ComposeEmoji("🎉", "Party popper", "Celebration", "celebrate congratulations success"),
        new ComposeEmoji("🎊", "Confetti", "Celebration", "celebrate party congratulations"),
        new ComposeEmoji("🎂", "Birthday cake", "Celebration", "birthday happy cake"),
        new ComposeEmoji("🎁", "Gift", "Celebration", "present birthday thanks"),
        new ComposeEmoji("🏆", "Trophy", "Celebration", "award winner success"),
        new ComposeEmoji("⭐", "Star", "Celebration", "favourite favorite excellent"),
        new ComposeEmoji("✨", "Sparkles", "Celebration", "new special shine"),
        new ComposeEmoji("🪔", "Diya lamp", "Celebration", "diwali deepavali festival india"),
        new ComposeEmoji("📎", "Paperclip", "Work", "attach attachment file"),
        new ComposeEmoji("📁", "Folder", "Work", "files directory documents"),
        new ComposeEmoji("📄", "Document", "Work", "file page report"),
        new ComposeEmoji("📅", "Calendar", "Work", "meeting date schedule"),
        new ComposeEmoji("⏰", "Alarm clock", "Work", "reminder time deadline"),
        new ComposeEmoji("📌", "Pushpin", "Work", "pin important note"),
        new ComposeEmoji("📝", "Memo", "Work", "note write draft"),
        new ComposeEmoji("💻", "Laptop", "Work", "computer code software"),
        new ComposeEmoji("📊", "Chart", "Work", "data report analysis graph"),
        new ComposeEmoji("💡", "Light bulb", "Work", "idea suggestion insight"),
        new ComposeEmoji("✉️", "Envelope", "Work", "email mail message"),
        new ComposeEmoji("📞", "Telephone", "Work", "call phone contact"),
        new ComposeEmoji("☕", "Coffee", "Everyday", "coffee tea break"),
        new ComposeEmoji("🌞", "Sun", "Everyday", "morning day sunny"),
        new ComposeEmoji("🌙", "Moon", "Everyday", "night evening"),
        new ComposeEmoji("🌱", "Seedling", "Everyday", "growth nature plant"),
        new ComposeEmoji("🌸", "Blossom", "Everyday", "flower spring"),
        new ComposeEmoji("🏠", "House", "Everyday", "home remote"),
        new ComposeEmoji("🚗", "Car", "Everyday", "drive travel commute"),
        new ComposeEmoji("✈️", "Airplane", "Everyday", "flight travel trip"),
        new ComposeEmoji("❤️", "Red heart", "Symbols", "love care support"),
        new ComposeEmoji("✅", "Check mark", "Symbols", "done yes complete approved"),
        new ComposeEmoji("❌", "Cross mark", "Symbols", "no cancel error"),
        new ComposeEmoji("⚠️", "Warning", "Symbols", "warning attention caution"),
        new ComposeEmoji("ℹ️", "Information", "Symbols", "info help note"),
        new ComposeEmoji("❓", "Question mark", "Symbols", "question help unknown"),
        new ComposeEmoji("❗", "Exclamation", "Symbols", "important urgent attention"),
        new ComposeEmoji("→", "Right arrow", "Symbols", "next forward arrow"),
        new ComposeEmoji("←", "Left arrow", "Symbols", "back previous arrow"),
        new ComposeEmoji("₹", "Indian rupee", "Symbols", "rupee rupees inr currency india money"),
    });
    public static IReadOnlyList<string> Categories { get; } = Array.AsReadOnly(Entries.Select(e => e.Category).Distinct().ToArray());
    private static readonly IReadOnlyDictionary<string, ComposeEmoji> ByValue = Entries.ToDictionary(e => e.Value, StringComparer.Ordinal);

    public static bool Contains(string? value) => value != null && ByValue.ContainsKey(value);

    public static IReadOnlyList<ComposeEmoji> Search(string? query = null, string? category = null, IEnumerable<string>? recent = null)
    {
        IEnumerable<ComposeEmoji> source = category == "Recent"
            ? NormaliseRecent(recent).Select(value => ByValue[value]) : Entries;
        if (!string.IsNullOrEmpty(category) && category is not ("All" or "Recent")) source = source.Where(e => e.Category == category);
        var text = (query ?? "").Trim();
        if (text.Length > 200) text = text[..200];
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(8).ToArray();
        return source.Where(e => tokens.All(token => (e.Value + " " + e.Name + " " + e.Keywords).Contains(token, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public static IReadOnlyList<string> NormaliseRecent(IEnumerable<string>? recent) =>
        (recent ?? Enumerable.Empty<string>()).Take(256).Where(Contains).Distinct(StringComparer.Ordinal).Take(MaximumRecent).ToArray();

    public static IReadOnlyList<string> Remember(IEnumerable<string>? recent, string? value) =>
        NormaliseRecent(Contains(value) ? new[] { value! }.Concat(recent ?? Enumerable.Empty<string>()) : recent);
}
