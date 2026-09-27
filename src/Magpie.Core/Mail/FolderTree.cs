using Magpie.Core.Models;

namespace Magpie.Core.Mail;

/// <summary>
/// Turns an account's flat IMAP folder list into sidebar order (design Q2): top-level folders first
/// (Inbox, then special folders, then the rest A–Z), each followed by its subfolders, depth-first.
/// A folder whose parent is not in the list (e.g. "[Gmail]/Sent" when "[Gmail]" is not selectable)
/// is treated as top level.
/// </summary>
public static class FolderTree
{
    private static int RoleRank(FolderRole r) => r switch
    {
        FolderRole.Inbox => 0,
        FolderRole.Sent => 1,
        FolderRole.Drafts => 2,
        FolderRole.Archive => 3,
        FolderRole.Flagged => 4,
        FolderRole.Important => 5,
        FolderRole.All => 6,
        FolderRole.Junk => 7,
        FolderRole.Trash => 8,
        _ => 20,
    };

    public static string ParentPathOf(MailFolder f)
    {
        if (f.Delimiter == '\0') return "";
        var i = f.Path.LastIndexOf(f.Delimiter);
        return i <= 0 ? "" : f.Path[..i];
    }

    private static string TopAncestor(MailFolder f)
    {
        if (f.Delimiter == '\0') return f.Path;
        var i = f.Path.IndexOf(f.Delimiter);
        return i <= 0 ? f.Path : f.Path[..i];
    }

    /// <returns>Each folder with its depth and its parent's path ("" at top level).</returns>
    public static List<(MailFolder Folder, int Depth, string Parent)> Order(IEnumerable<MailFolder> folders)
    {
        var list = folders.ToList();
        var paths = new HashSet<string>(list.Select(f => f.Path), StringComparer.Ordinal);
        var inboxes = new HashSet<string>(list.Where(f => f.Role == FolderRole.Inbox).Select(f => f.Path), StringComparer.Ordinal);
        // Some servers (Courier, cPanel Dovecot) keep every folder under INBOX ("INBOX.Sent", "INBOX.Work").
        // Then INBOX's children are shown as top-level folders rather than all hidden inside Inbox.
        var others = list.Where(f => f.Role != FolderRole.Inbox).ToList();
        var allUnderInbox = others.Count > 0 && inboxes.Count > 0 && others.All(f => inboxes.Contains(TopAncestor(f)));
        var children = new Dictionary<string, List<MailFolder>>(StringComparer.Ordinal);
        foreach (var f in list)
        {
            var p = ParentPathOf(f);
            if (!paths.Contains(p)) p = "";
            // Special folders (Sent, Drafts, Trash…) never hide under Inbox.
            if (p.Length > 0 && inboxes.Contains(p) && (allUnderInbox || f.Role != FolderRole.Other)) p = "";
            if (!children.TryGetValue(p, out var kids)) children[p] = kids = new List<MailFolder>();
            kids.Add(f);
        }

        var result = new List<(MailFolder, int, string)>(list.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(string parent, int depth)
        {
            if (!children.TryGetValue(parent, out var kids)) return;
            var sorted = parent.Length == 0
                ? kids.OrderBy(k => RoleRank(k.Role)).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
                : kids.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var k in sorted)
            {
                if (!seen.Add(k.Path)) continue;   // guards against odd server data (duplicate paths)
                result.Add((k, depth, parent));
                if (depth < 32) Walk(k.Path, depth + 1);
            }
        }
        Walk("", 0);
        // Anything unreachable (cycles, duplicates) still gets listed rather than silently dropped.
        foreach (var f in list)
            if (seen.Add(f.Path)) result.Add((f, 0, ""));
        return result;
    }
}
