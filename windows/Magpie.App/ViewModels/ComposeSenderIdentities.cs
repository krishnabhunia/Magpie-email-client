using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    public ObservableCollection<SenderIdentity> SenderChoices { get; } = new();
    [ObservableProperty] private SenderIdentity? _selectedSender;
    public bool HasSenderAliases => SenderChoices.Count > 1;

    public Account? TemplateSender
    {
        get
        {
            if (From == null) return null;
            var copy = (_e.AccountById(From.Id) ?? From).Clone();
            if (SelectedSender is { Available: true } selected)
            {
                copy.Email = selected.Address;
                copy.DisplayName = selected.Name;
            }
            return copy;
        }
    }

    private bool _refreshingSenderIdentities;

    private void RefreshSenderIdentities(string? address = null)
    {
        var wanted = address ?? SelectedSender?.Address ?? _draft.SenderAddress;
        var account = From == null ? null : _e.AccountById(From.Id) ?? From;
        var choices = account == null ? new List<SenderIdentity>() : SenderIdentities.ForAccount(account).ToList();
        var selected = choices.FirstOrDefault(i => string.Equals(i.Address, wanted, StringComparison.OrdinalIgnoreCase));
        if (selected == null && account != null && !string.IsNullOrWhiteSpace(wanted))
        {
            selected = new SenderIdentity(wanted, "", Available: false);
            choices.Add(selected);
        }
        selected ??= choices.FirstOrDefault();
        var changed = !Equals(SelectedSender, selected);
        if (SenderChoices.SequenceEqual(choices))
        {
            if (changed) SelectedSender = selected;
            return;
        }
        _refreshingSenderIdentities = true;
        try
        {
            // A bound ComboBox briefly clears SelectedItem when its collection is rebuilt.
            // Only the final identity change is an edit; unrelated settings changes keep AI previews.
            SenderChoices.Clear();
            foreach (var identity in choices) SenderChoices.Add(identity);
            SelectedSender = selected;
        }
        finally { _refreshingSenderIdentities = false; }
        OnPropertyChanged(nameof(HasSenderAliases));
        OnPropertyChanged(nameof(TemplateSender));
        if (changed && _constructed) SenderIdentityEdited();
    }

    private void SenderIdentityEdited()
    {
        InvalidateReplyContext();
        InvalidateLegacyAi();
        MarkEdited();
    }

    partial void OnSelectedSenderChanged(SenderIdentity? value)
    {
        OnPropertyChanged(nameof(TemplateSender));
        if (!_constructed || _refreshingSenderIdentities) return;
        SenderIdentityEdited();
    }
}
