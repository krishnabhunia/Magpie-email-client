namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    private bool _ccRowExpanded;
    private bool _bccRowExpanded;

    // Non-empty fields always remain visible, including invalid/incomplete text. Hiding a row never clears it.
    public bool ShowCcRow => _ccRowExpanded || !string.IsNullOrWhiteSpace(Cc);
    public bool ShowBccRow => _bccRowExpanded || !string.IsNullOrWhiteSpace(Bcc);
    public bool CanHideCcRow => string.IsNullOrWhiteSpace(Cc);
    public bool CanHideBccRow => string.IsNullOrWhiteSpace(Bcc);

    public bool SetRecipientRowVisible(string field, bool visible)
    {
        if (field == "cc")
        {
            if (!visible && !CanHideCcRow) return false;
            _ccRowExpanded = visible;
        }
        else if (field == "bcc")
        {
            if (!visible && !CanHideBccRow) return false;
            _bccRowExpanded = visible;
        }
        else return false;
        NotifyRecipientVisibility();
        return true;
    }

    private void NotifyRecipientVisibility()
    {
        // Once a populated row has opened, keep it open while its last address is being edited/removed.
        // Only the explicit Hide action closes an empty row, so deleting text never steals keyboard focus.
        if (!string.IsNullOrWhiteSpace(Cc)) _ccRowExpanded = true;
        if (!string.IsNullOrWhiteSpace(Bcc)) _bccRowExpanded = true;
        OnPropertyChanged(nameof(ShowCcRow));
        OnPropertyChanged(nameof(ShowBccRow));
        OnPropertyChanged(nameof(CanHideCcRow));
        OnPropertyChanged(nameof(CanHideBccRow));
        // Retain the previous aggregate property for draft recovery and existing integrations.
        ShowCcBcc = ShowCcRow || ShowBccRow;
    }
}
