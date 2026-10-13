using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

public partial class MainViewModel
{
    // Scope before querying/pagination. A removed account yields no folders, never an all-account fallback.
    private List<long> SavedViewFolders(NavItem nav)
    {
        if (nav.SavedView is not { } view) return new();
        var ids = view.InboxOnly ? _e.FolderIds(FolderRole.Inbox) : _e.AllMailFolderIds();
        if (view.AccountId == null) return ids;
        var own = _e.Folders(view.AccountId).Select(f => f.Id).ToHashSet();
        return ids.Where(own.Contains).ToList();
    }

    public void OpenWorkspaceView(NavItem nav)
    {
        SearchText = "";
        UnreadOnly = false;
        NavigateCommand.Execute(nav);
    }

    public void SaveWorkspaceView(string name, string query, string? accountId, bool inboxOnly, long? id = null)
    {
        var key = _e.Store.SaveInboxView(name, query, accountId, inboxOnly, id);
        BuildNav();
        var nav = Smart.First(n => n.Kind == NavKind.SavedView && n.FolderId == key);
        OpenWorkspaceView(nav);
    }

    public void RemoveWorkspaceView(long id)
    {
        _e.Store.DeleteInboxView(id);
        BuildNav();
    }
}
