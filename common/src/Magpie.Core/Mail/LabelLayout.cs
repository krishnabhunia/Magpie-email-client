using Magpie.Core.Models;

namespace Magpie.Core.Mail;

public enum SidebarRowKind { Folder, Container, Group, More }

/// <summary>One row of an account's folder list in the sidebar (design LB1).</summary>
/// <param name="Path">The folder's server path; <see cref="LabelLayout.GroupKey"/> / <see cref="LabelLayout.MoreKey"/> for those rows.</param>
/// <param name="ParentPath">The row this one folds under ("" = always shown): a parent label, the group, or More labels.</param>
public sealed record SidebarRow(SidebarRowKind Kind, MailFolder? Folder, string Path, string ParentPath, int Depth, string Label, bool HasChildren);

/// <summary>What the layout needs to know besides the folders (all per account).</summary>
public sealed class LabelLayoutState
{
    /// <summary>Unread conversations per folder path ("" when not counted yet = 0).</summary>
    public IReadOnlyDictionary<string, int> Unread { get; init; } = new Dictionary<string, int>();
    /// <summary>When each folder was last opened or first seen; a folder missing here is treated as just seen.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Opened { get; init; } = new Dictionary<string, DateTimeOffset>();
    public IReadOnlySet<string> AlwaysShow { get; init; } = new HashSet<string>();
    public bool OnlyUnread { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Design LB1 C (approved 30 Oct 2026): Gmail labels that fold.
/// <list type="bullet">
/// <item>A parent label Gmail lists as a container only (<c>\Noselect</c>, not stored) comes back as a <see cref="SidebarRowKind.Container"/>
/// row, so "Clients/Acme" folds under "Clients" instead of showing flat. Gmail's own "[Gmail]" container stays hidden.</item>
/// <item>The account's own folders (Inbox, Sent, Trash …) stay on top; every label is in one <see cref="SidebarRowKind.Group"/>
/// ("Labels" for Gmail, "Folders" for other accounts).</item>
/// <item>Labels with no unread mail that weren't opened for <see cref="QuietDays"/> days go into <see cref="SidebarRowKind.More"/>
/// labels at the end, shown with their full path; "Always show" keeps one out.</item>
/// </list>
/// </summary>
public static class LabelLayout
{
    public const string GroupKey = "\u0001labels";
    public const string MoreKey = "\u0001more";
    public const int QuietDays = 30;

    public static bool IsGmailContainer(string name) =>
        name.Equals("[Gmail]", StringComparison.OrdinalIgnoreCase) || name.Equals("[Google Mail]", StringComparison.OrdinalIgnoreCase);

    public static string GroupName(bool gmail) => gmail ? "Labels" : "Folders";

    public static List<SidebarRow> Layout(IEnumerable<MailFolder> folders, bool gmail, LabelLayoutState state)
    {
        var real = folders.ToList();
        var paths = new HashSet<string>(real.Select(f => f.Path), StringComparer.Ordinal);
        var containers = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<MailFolder>(real);
        foreach (var f in real)
        {
            if (f.Delimiter == '\0' || f.Role == FolderRole.Inbox) continue;
            var parts = f.Path.Split(f.Delimiter);
            if (parts.Length < 2 || IsGmailContainer(parts[0])) continue;
            // An INBOX.* server (every folder under INBOX) keeps its folders as they are.
            if (real.Any(r => r.Role == FolderRole.Inbox && r.Path == parts[0])) continue;
            for (var i = 1; i < parts.Length; i++)
            {
                var p = string.Join(f.Delimiter, parts.Take(i));
                if (paths.Contains(p) || !containers.Add(p)) continue;
                all.Add(new MailFolder { AccountId = f.AccountId, Path = p, Name = parts[i - 1], Role = FolderRole.Other, Delimiter = f.Delimiter, Synced = false });
            }
        }

        var tree = FolderTree.Order(all);
        // Labels = top-level "Other" folders (and containers) with everything under them; the rest are the account's own folders.
        var top = new Dictionary<string, string>(StringComparer.Ordinal);   // path → its top-level ancestor's path
        foreach (var (f, depth, parent) in tree) top[f.Path] = parent.Length == 0 ? f.Path : top.TryGetValue(parent, out var t) ? t : f.Path;
        var byPath = tree.ToDictionary(x => x.Folder.Path, x => x.Folder, StringComparer.Ordinal);
        bool IsLabel(MailFolder f) => byPath.TryGetValue(top[f.Path], out var t) && t.Role == FolderRole.Other;

        var children = tree.GroupBy(x => x.Parent).ToDictionary(g => g.Key, g => g.Select(x => x.Folder.Path).ToList(), StringComparer.Ordinal);
        int UnreadOf(string path) => state.Unread.TryGetValue(path, out var n) ? n : 0;
        var subtreeUnread = new Dictionary<string, int>(StringComparer.Ordinal);
        int SubtreeUnread(string path)
        {
            if (subtreeUnread.TryGetValue(path, out var n)) return n;
            n = UnreadOf(path) + (children.TryGetValue(path, out var kids) ? kids.Sum(SubtreeUnread) : 0);
            return subtreeUnread[path] = n;
        }
        bool Quiet(MailFolder f) =>
            !containers.Contains(f.Path) && UnreadOf(f.Path) == 0 && !state.AlwaysShow.Contains(f.Path)
            && state.Opened.TryGetValue(f.Path, out var at) && state.Now - at >= TimeSpan.FromDays(QuietDays);
        // In More when quiet and everything under it is in More too; a container goes when all of its labels have.
        var inMore = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool InMore(string path)
        {
            if (inMore.TryGetValue(path, out var b)) return b;
            var kids = children.TryGetValue(path, out var k) ? k : new List<string>();
            var f = byPath[path];
            b = containers.Contains(path) ? kids.Count > 0 && kids.All(InMore) : Quiet(f) && kids.All(InMore);
            return inMore[path] = b;
        }

        var rows = new List<SidebarRow>();
        var labels = tree.Where(x => IsLabel(x.Folder)).ToList();
        foreach (var (f, depth, parent) in tree.Where(x => !IsLabel(x.Folder)))
            rows.Add(new SidebarRow(SidebarRowKind.Folder, f, f.Path, parent, depth, f.Name, children.ContainsKey(f.Path)));
        if (labels.Count == 0) return rows;

        bool Shown(string path) => !state.OnlyUnread || SubtreeUnread(path) > 0;
        var main = labels.Where(x => Shown(x.Folder.Path) && (state.OnlyUnread || !InMore(x.Folder.Path))).ToList();
        var more = state.OnlyUnread ? new() : labels.Where(x => !containers.Contains(x.Folder.Path) && InMore(x.Folder.Path)).ToList();
        rows.Add(new SidebarRow(SidebarRowKind.Group, null, GroupKey, "", 0, GroupName(gmail), true));
        var mainPaths = main.Select(x => x.Folder.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var (f, depth, parent) in main)
        {
            var kind = containers.Contains(f.Path) ? SidebarRowKind.Container : SidebarRowKind.Folder;
            var hasKids = children.TryGetValue(f.Path, out var kids) && kids.Any(mainPaths.Contains);
            rows.Add(new SidebarRow(kind, kind == SidebarRowKind.Folder ? f : null, f.Path, parent.Length == 0 ? GroupKey : parent, depth + 1, f.Name, hasKids));
        }
        if (more.Count > 0)
        {
            rows.Add(new SidebarRow(SidebarRowKind.More, null, MoreKey, GroupKey, 1, "More labels", true));
            foreach (var (f, _, _) in more.OrderBy(x => FullName(x.Folder), StringComparer.OrdinalIgnoreCase))
                rows.Add(new SidebarRow(SidebarRowKind.Folder, f, f.Path, MoreKey, 2, FullName(f), false));
        }
        return rows;
    }

    /// <summary>"Clients / Initech" for a label in More labels (it's out of its parent there).</summary>
    public static string FullName(MailFolder f) =>
        f.Delimiter == '\0' ? f.Name : string.Join(" / ", f.Path.Split(f.Delimiter).Where(p => p.Length > 0 && !IsGmailContainer(p)));

    /// <summary>The labels of a group, container or More row (for its count and Open all / Close all).</summary>
    public static IEnumerable<SidebarRow> Under(IReadOnlyList<SidebarRow> rows, string path)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { path };
        foreach (var r in rows)
            if (keys.Contains(r.ParentPath)) { keys.Add(r.Path); yield return r; }
    }
}
