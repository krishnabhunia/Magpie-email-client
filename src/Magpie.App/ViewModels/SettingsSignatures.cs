using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Magpie.App.ViewModels;

/// <summary>Settings → Signatures &amp; replies (design B6).</summary>
public partial class SettingsViewModel
{
    /// <summary>The account whose signature is in the editor (one chip per account).</summary>
    [ObservableProperty] private EditableAccount? _signatureAccount;
    public ObservableCollection<EditableQuickReply> QuickReplyList { get; } = new();
    public string[] SignatureFonts { get; } = { "Segoe UI", "Arial", "Calibri", "Georgia", "Times New Roman", "Verdana", "Courier New" };
    public string[] SignatureColours { get; } = { "#23282E", "#14606E", "#1D4ED8", "#4B3F86", "#B3261E", "#B45309", "#1B6B2E", "#5A6068" };

    /// <summary>Raised when another account is picked, so the window loads its signature into the editor.</summary>
    public event Action<EditableAccount?>? SignatureAccountChanged;

    private void LoadSignatures()
    {
        SignatureAccount = Accounts.FirstOrDefault();
        foreach (var q in _e.Config.QuickReplies) QuickReplyList.Add(new EditableQuickReply { Text = q });
    }

    partial void OnSignatureAccountChanged(EditableAccount? oldValue, EditableAccount? newValue)
    {
        if (oldValue != null) oldValue.IsSignatureSelected = false;
        if (newValue != null) newValue.IsSignatureSelected = true;
        SignatureAccountChanged?.Invoke(newValue);
    }

    [RelayCommand] private void PickSignatureAccount(EditableAccount? a) { if (a != null) SignatureAccount = a; }

    /// <summary>The editor's content changed (it reports every edit).</summary>
    public void SetSignatureHtml(string html)
    {
        if (SignatureAccount != null) SignatureAccount.SignatureHtml = html;
    }

    [RelayCommand] private void AddQuickReply() => QuickReplyList.Add(new EditableQuickReply { Text = "" });
    [RelayCommand] private void RemoveQuickReply(EditableQuickReply? q) { if (q != null) QuickReplyList.Remove(q); }

    private List<string> QuickRepliesToSave() => QuickReplyList.Select(q => q.Text.Trim()).Where(t => t.Length > 0).Distinct().ToList();
}
