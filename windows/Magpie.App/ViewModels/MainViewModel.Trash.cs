using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.ViewModels;

/// <summary>
/// Design AL1 (option C): in Trash, the conversations that Magpie's auto delete moved there carry a label with the
/// date and time, and a filter over the list shows all of them, only those, or only the others.
/// </summary>
public partial class MainViewModel
{
    /// <summary>Which conversations of Trash the list shows (back to all whenever another view is opened).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrashFilterAll), nameof(TrashFilterAuto), nameof(TrashFilterOther))]
    private TrashReason _trashFilter;

    /// <summary>The filter row is there only when Trash holds something auto delete put there.</summary>
    [ObservableProperty] private bool _showTrashFilter;
    [ObservableProperty] private string _trashAllText = "";
    [ObservableProperty] private string _trashAutoText = "";
    [ObservableProperty] private string _trashOtherText = "";

    public bool TrashFilterAll => TrashFilter == TrashReason.Any;
    public bool TrashFilterAuto => TrashFilter == TrashReason.AutoDeleted;
    public bool TrashFilterOther => TrashFilter == TrashReason.NotAutoDeleted;
    public string TrashAllTip => AutoDeleted.FilterTip(TrashReason.Any);
    public string TrashAutoTip => AutoDeleted.FilterTip(TrashReason.AutoDeleted);
    public string TrashOtherTip => AutoDeleted.FilterTip(TrashReason.NotAutoDeleted);

    /// <summary>Per conversation of the Trash list on screen: its auto-delete note (the latest one) and how many emails it covers.</summary>
    private Dictionary<(string AccountId, string ThreadKey), (TrashNote Latest, int Count)> _autoDeleted = new();
    private bool _trashFilterQuiet;

    private static bool IsTrash(NavItem? nav) => nav is { Kind: NavKind.Role or NavKind.Folder, Role: FolderRole.Trash };

    /// <summary>The filter the list query uses for this view: only Trash has one.</summary>
    private TrashReason TrashReasonFor(NavItem nav) => IsTrash(nav) && !IsPersonView ? TrashFilter : TrashReason.Any;

    /// <summary>"all" · "auto" · "other", from the three choices over the list.</summary>
    [RelayCommand]
    private void SetTrashFilter(string? which)
    {
        TrashFilter = which switch { "auto" => TrashReason.AutoDeleted, "other" => TrashReason.NotAutoDeleted, _ => TrashReason.Any };
    }

    partial void OnTrashFilterChanged(TrashReason value)
    {
        if (_trashFilterQuiet) return;
        Selected = null;
        ClearSelection();
        ReloadList();
    }

    /// <summary>Another view was opened: its list is not filtered (no second reload — the view change reloads).</summary>
    private void ResetTrashFilter()
    {
        if (TrashFilter == TrashReason.Any) return;
        _trashFilterQuiet = true;
        try { TrashFilter = TrashReason.Any; }
        finally { _trashFilterQuiet = false; }
    }

    /// <summary>
    /// Before the list is read: the numbers on the three choices and the notes for the rows. When the last
    /// auto-deleted conversation left Trash while its filter was on, the list goes back to all.
    /// </summary>
    private void PrepareTrashList(NavItem nav)
    {
        if (!IsTrash(nav) || IsPersonView)
        {
            _autoDeleted = new();
            ShowTrashFilter = false;
            return;
        }
        var folders = FolderIdsFor(nav);
        _autoDeleted = _e.Store.AutoDeletedThreads(folders);
        var (all, auto) = _e.Store.TrashCounts(folders);
        ShowTrashFilter = AutoDeleted.ShowFilter(auto);
        TrashAllText = AutoDeleted.FilterLabel(TrashReason.Any, all, auto);
        TrashAutoText = AutoDeleted.FilterLabel(TrashReason.AutoDeleted, all, auto);
        TrashOtherText = AutoDeleted.FilterLabel(TrashReason.NotAutoDeleted, all, auto);
        if (!ShowTrashFilter) ResetTrashFilter();
    }

    /// <summary>The label of one row of Trash ("Auto-deleted · 21 Nov, 18:24") and what pointing at it says; ("", null) for the others.</summary>
    private (string Label, string? Tip) TrashMark(ThreadRow row, DateTimeOffset now) =>
        _autoDeleted.TryGetValue((row.AccountId, row.ThreadKey), out var found)
            ? (AutoDeleted.RowLabel(found.Latest.At, now), AutoDeleted.Tip(found.Latest, found.Count))
            : ("", null);

    /// <summary>What an empty Trash list says while one of the two narrower choices is on; "" otherwise.</summary>
    private string TrashEmptyText(NavItem nav) => IsTrash(nav) ? AutoDeleted.EmptyText(TrashFilter) : "";
}
