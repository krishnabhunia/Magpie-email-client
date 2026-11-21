using CommunityToolkit.Mvvm.ComponentModel;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Storage;

namespace Magpie.App.ViewModels;

/// <summary>
/// Design AL1: the open conversation is in Trash because Magpie's auto delete moved it there — the banner above it
/// (when, the rule, where it was, Restore / Edit rule) and the note on each such email.
/// </summary>
public partial class ThreadViewModel
{
    [ObservableProperty] private bool _showAutoDeleted;
    [ObservableProperty] private string _autoDeletedWhen = "";
    [ObservableProperty] private string _autoDeletedDetails = "";
    [ObservableProperty] private string _autoDeletedMany = "";
    [ObservableProperty] private string _autoDeletedRestoreTip = "";
    [ObservableProperty] private string _autoDeletedRuleId = "";
    [ObservableProperty] private bool _canEditAutoDeletedRule;
    public string AutoDeletedTitle => AutoDeleted.Title;

    /// <summary>The auto-delete note of each email of these that sits in Trash, by row id; empty for a conversation elsewhere.</summary>
    private Dictionary<long, TrashNote> AutoDeletedNotes(string accountId, IEnumerable<MessageRow> rows)
    {
        var trash = _e.Folders(accountId).Where(f => f.Role == FolderRole.Trash).Select(f => f.Id).ToHashSet();
        var inTrash = rows.Where(r => trash.Contains(r.FolderId) && r.MessageId.Length > 0).ToList();
        if (inTrash.Count == 0) return new();
        var notes = _e.Store.TrashNotes(accountId, inTrash.Select(r => r.MessageId));
        return inTrash.Where(r => notes.ContainsKey(r.MessageId)).ToDictionary(r => r.Id, r => notes[r.MessageId]);
    }

    /// <summary>The banner for the open conversation: its latest auto-deleted email speaks for it.</summary>
    public void RefreshAutoDeleted()
    {
        Dictionary<long, TrashNote> notes;
        try { notes = HasThread ? AutoDeletedNotes(AccountId, Messages) : new(); }
        catch (Exception ex) { Magpie.Core.Log.Warn("auto-deleted note: " + ex.Message); notes = new(); }
        ShowAutoDeleted = notes.Count > 0;
        if (!ShowAutoDeleted)
        {
            AutoDeletedWhen = AutoDeletedDetails = AutoDeletedMany = AutoDeletedRestoreTip = AutoDeletedRuleId = "";
            CanEditAutoDeletedRule = false;
            return;
        }
        var (rowId, latest) = notes.OrderByDescending(n => n.Value.At).Select(n => (n.Key, n.Value)).First();
        var arrived = Messages.FirstOrDefault(m => m.Id == rowId)?.Date;
        var folder = _e.Folders(AccountId).FirstOrDefault(f => f.Id == latest.FolderId);
        var folderName = folder == null ? null : PersonView.FolderLabel(folder.Role, folder.Path);
        AutoDeletedWhen = AutoDeleted.When(latest.At);
        AutoDeletedDetails = AutoDeleted.Details(latest, arrived, folderName);
        AutoDeletedMany = AutoDeleted.ManyLine(notes.Count);
        AutoDeletedRestoreTip = AutoDeleted.RestoreTip(folderName);
        AutoDeletedRuleId = latest.RuleId;
        CanEditAutoDeletedRule = latest.RuleId.Length > 0 && _e.AutoDeleteRules().Any(r => r.Id == latest.RuleId);
    }
}
