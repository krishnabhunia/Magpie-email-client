using System.Text.Json;
using Magpie.Core;
using Magpie.Core.Mail;

namespace Magpie.App.Services;

/// <summary>Recently attached paths live only on this PC, not in synced account settings.</summary>
internal static class RecentComposeFiles
{
    private const int Maximum = 12;
    private static string Filename => Path.Combine(AppServices.Engine.Paths.LocalRoot, "recent-compose-files.json");

    public static IReadOnlyList<string> Read()
    {
        try
        {
            if (!File.Exists(Filename)) return Array.Empty<string>();
            var saved = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Filename)) ?? Array.Empty<string>();
            return ComposeSafety.RecentAttachmentPaths(Array.Empty<string>(), saved, Maximum)
                .Where(File.Exists).ToArray();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public static void Remember(IEnumerable<string> attachedPaths)
    {
        var target = Filename;
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var list = ComposeSafety.RecentAttachmentPaths(attachedPaths.Where(File.Exists), Read(), Maximum);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(list));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("recent attachments: " + ex.Message); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception) { }
        }
    }
}
