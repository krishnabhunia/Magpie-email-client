namespace Magpie.Core.Settings;

public sealed record ComposeToolbarDefinition(string Id, string Name, string Group, bool Required);

/// <summary>A consecutive visible group, kept together by the native toolbar's responsive overflow.</summary>
public sealed record ComposeToolbarGroup(string Name, IReadOnlyList<string> ItemIds, bool AlwaysVisible);

/// <summary>Stable compose-command identities shared by Settings and the native toolbar. No executable commands are stored.</summary>
public static class ComposeToolbarCatalog
{
    public const int MaximumInputEntries = 512;

    public static IReadOnlyList<ComposeToolbarDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new ComposeToolbarDefinition("attach", "Attach files", "Files", true),
        new ComposeToolbarDefinition("recentFiles", "Recent files", "Files", false),
        new ComposeToolbarDefinition("fontFamily", "Font family", "Font", false),
        new ComposeToolbarDefinition("fontSize", "Font size", "Font", false),
        new ComposeToolbarDefinition("bold", "Bold", "Text styles", false),
        new ComposeToolbarDefinition("italic", "Italic", "Text styles", false),
        new ComposeToolbarDefinition("underline", "Underline", "Text styles", false),
        new ComposeToolbarDefinition("strikeThrough", "Strikethrough", "Text styles", false),
        new ComposeToolbarDefinition("foreColor", "Text colour", "Colours", false),
        new ComposeToolbarDefinition("hiliteColor", "Highlight colour", "Colours", false),
        new ComposeToolbarDefinition("insertUnorderedList", "Bulleted list", "Lists", false),
        new ComposeToolbarDefinition("insertOrderedList", "Numbered list", "Lists", false),
        new ComposeToolbarDefinition("justifyLeft", "Align left", "Paragraph", false),
        new ComposeToolbarDefinition("justifyCenter", "Align centre", "Paragraph", false),
        new ComposeToolbarDefinition("indent", "Increase indent", "Paragraph", false),
        new ComposeToolbarDefinition("outdent", "Decrease indent", "Paragraph", false),
        new ComposeToolbarDefinition("link", "Insert or edit link", "Links", false),
        new ComposeToolbarDefinition("removeLink", "Remove link", "Links", false),
        new ComposeToolbarDefinition("table", "Insert table", "Insert", false),
        new ComposeToolbarDefinition("image", "Insert inline picture", "Insert", false),
        new ComposeToolbarDefinition("resizeImage", "Resize selected picture", "Insert", false),
        new ComposeToolbarDefinition("emoji", "Emoji and symbols", "Insert", false),
        new ComposeToolbarDefinition("undo", "Undo", "History", false),
        new ComposeToolbarDefinition("redo", "Redo", "History", false),
        new ComposeToolbarDefinition("find", "Find and replace", "Editing", false),
        new ComposeToolbarDefinition("more", "More formatting", "More", true),
        new ComposeToolbarDefinition("removeFormat", "Clear formatting", "Editing", false),
    });

    private static readonly IReadOnlyDictionary<string, ComposeToolbarDefinition> ById =
        Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);

    public static List<ToolbarButton> DefaultButtons() =>
        Definitions.Select(definition => new ToolbarButton { Id = definition.Id, Visible = true }).ToList();

    /// <summary>
    /// Preserve the first explicit visibility and individual order; required commands cannot be hidden.
    /// Unknown/null/duplicate entries are ignored, and missing commands are appended in default order, visible.
    /// Every returned button is a fresh object; callers cannot mutate the source settings through this result.
    /// </summary>
    public static List<ToolbarButton> Normalise(IEnumerable<ToolbarButton>? buttons)
    {
        var result = new List<ToolbarButton>(Definitions.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var button in (buttons ?? Enumerable.Empty<ToolbarButton>()).Take(MaximumInputEntries))
        {
            if (button == null || string.IsNullOrEmpty(button.Id)) continue;
            var id = button.Id;
            if (!ById.TryGetValue(id, out var definition) || !seen.Add(id)) continue;
            result.Add(new ToolbarButton { Id = id, Visible = definition.Required || button.Visible });
        }
        foreach (var definition in Definitions)
            if (seen.Add(definition.Id)) result.Add(new ToolbarButton { Id = definition.Id, Visible = true });
        return result;
    }

    /// <summary>
    /// Build contiguous groups in the visible command order, never globally regrouping a user's layout.
    /// Required Attach and More each occupy their own always-visible group, even beside their natural peers.
    /// </summary>
    public static IReadOnlyList<ComposeToolbarGroup> GroupVisible(IEnumerable<ToolbarButton>? buttons)
    {
        var groups = new List<ComposeToolbarGroup>();
        var current = new List<string>();
        var groupName = "";
        void Flush()
        {
            if (current.Count == 0) return;
            groups.Add(new ComposeToolbarGroup(groupName, Array.AsReadOnly(current.ToArray()), false));
            current.Clear();
        }
        foreach (var button in Normalise(buttons).Where(button => button.Visible))
        {
            var definition = ById[button.Id];
            if (definition.Required)
            {
                Flush();
                groups.Add(new ComposeToolbarGroup(definition.Group, Array.AsReadOnly(new[] { definition.Id }), true));
                groupName = "";
                continue;
            }
            if (!string.Equals(groupName, definition.Group, StringComparison.Ordinal))
            {
                Flush();
                groupName = definition.Group;
            }
            current.Add(definition.Id);
        }
        Flush();
        return groups.AsReadOnly();
    }
}
