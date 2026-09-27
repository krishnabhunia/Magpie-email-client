namespace Magpie.Core.Settings;

/// <summary>
/// The AI "Quick setup" presets (design Q1). A preset is just a named combination of the master switch
/// and the four feature switches; any other combination is "Custom".
/// </summary>
public static class AiPresets
{
    public sealed record Preset(string Id, bool Master, bool Summarise, bool Draft, bool Rewrite, bool Replies);

    public static readonly IReadOnlyList<Preset> All = new[]
    {
        new Preset("Off", false, false, false, false, false),
        new Preset("A", true, false, false, false, false),
        new Preset("B", true, true, false, false, false),
        new Preset("C", true, true, true, true, true),
    };

    public static bool TryGet(string id, out Preset preset)
    {
        preset = All.FirstOrDefault(p => p.Id == id)!;
        return preset != null;
    }

    /// <summary>
    /// The preset matching these switches. With the master switch off the feature switches don't matter
    /// (nothing can call a model), so that is always "Off".
    /// </summary>
    public static string Match(bool master, bool summarise, bool draft, bool rewrite, bool replies)
    {
        if (!master) return "Off";
        foreach (var p in All)
            if (p.Master && p.Summarise == summarise && p.Draft == draft && p.Rewrite == rewrite && p.Replies == replies)
                return p.Id;
        return "Custom";
    }
}
