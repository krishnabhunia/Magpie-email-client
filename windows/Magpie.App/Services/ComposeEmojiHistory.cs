using System.Text.Json;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Services;

/// <summary>Small, allow-listed recent emoji history on this PC only; no message text or recipients are stored.</summary>
public sealed class ComposeEmojiHistory(string directory)
{
    private static readonly object Gate = new();
    private string Filename => Path.Combine(directory, "recent-compose-emoji.json");

    public IReadOnlyList<string> Read()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Filename) || new FileInfo(Filename).Length > 8192) return Array.Empty<string>();
                return ComposeEmojiCatalog.NormaliseRecent(JsonSerializer.Deserialize<string[]>(File.ReadAllText(Filename)));
            }
            catch (Exception ex) { Log.Warn("read recent emoji: " + ex.Message); return Array.Empty<string>(); }
        }
    }

    public void Remember(string value)
    {
        if (!ComposeEmojiCatalog.Contains(value)) return;
        lock (Gate)
        {
            var temporary = Filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var recent = ComposeEmojiCatalog.Remember(Read(), value);
                Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(recent));
                File.Move(temporary, Filename, overwrite: true);
            }
            catch (Exception ex) { Log.Warn("remember recent emoji: " + ex.Message); }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception) { } }
        }
    }
}
