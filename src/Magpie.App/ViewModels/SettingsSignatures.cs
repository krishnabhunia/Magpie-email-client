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
    [ObservableProperty] private string _signatureStatus = "";

    private void LoadSignatures()
    {
        SignatureAccount = Accounts.FirstOrDefault();
        foreach (var q in _e.Config.QuickReplies) QuickReplyList.Add(new EditableQuickReply { Text = q });
    }

    partial void OnSignatureAccountChanged(EditableAccount? oldValue, EditableAccount? newValue)
    {
        if (oldValue != null) oldValue.IsSignatureSelected = false;
        if (newValue != null) newValue.IsSignatureSelected = true;
        SignatureStatus = "";
    }

    /// <summary>Copies the signature set in Gmail for the chosen account (signed in with Google).</summary>
    [RelayCommand]
    private async Task ImportGmailSignature()
    {
        if (SignatureAccount is not { } a) return;
        SignatureStatus = "Getting it from Gmail…";
        try
        {
            var token = await _e.OAuth.GetAccessTokenAsync(a.Original, CancellationToken.None);
            var html = await Magpie.Core.Mail.GmailSignature.FetchAsync(_e.Http, token, a.Email, CancellationToken.None);
            a.SignatureHtml = html;
            SignatureStatus = "✓ Copied from Gmail. Press Save or Apply to keep it.";
        }
        catch (Magpie.Core.Mail.GmailSignature.Problem ex) { SignatureStatus = ex.Message; }
        catch (Exception ex) { SignatureStatus = "Couldn't reach Gmail: " + ex.Message; }
    }

    [RelayCommand] private void PickSignatureAccount(EditableAccount? a) { if (a != null) SignatureAccount = a; }

    /// <summary>The signature editor window closed with Done.</summary>
    public void SetSignatureHtml(string html)
    {
        if (SignatureAccount != null) SignatureAccount.SignatureHtml = html;
    }

    [RelayCommand] private void AddQuickReply() => QuickReplyList.Add(new EditableQuickReply { Text = "" });
    [RelayCommand] private void RemoveQuickReply(EditableQuickReply? q) { if (q != null) QuickReplyList.Remove(q); }

    private List<string> QuickRepliesToSave() => QuickReplyList.Select(q => q.Text.Trim()).Where(t => t.Length > 0).Distinct().ToList();
}
