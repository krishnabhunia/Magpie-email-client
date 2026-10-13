namespace Magpie.Core.Models;

/// <summary>Deterministic local palette matching. Every entered word must match a title, alias or hint.</summary>
public static class WorkspaceCommandSearch
{
    public static bool Matches(string query, string title, string details, string aliases)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var text = title + " " + details + " " + aliases;
        return words.All(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
