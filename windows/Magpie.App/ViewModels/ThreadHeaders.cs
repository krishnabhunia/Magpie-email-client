using Magpie.App.Services;
using Magpie.App.Views;

namespace Magpie.App.ViewModels;

public partial class ThreadViewModel
{
    /// <summary>Design HD1: the Headers button of an email's Details card — its header lines in their own window.</summary>
    public void OpenHeaders(long rowId)
    {
        if (Messages.FirstOrDefault(m => m.Id == rowId) is not { } row) return;
        try { HeadersWindow.Open(Ui.ActiveWindow, _e, row); }
        catch (Exception ex) { Ui.Error(Magpie.Core.Mail.HeaderView.Title, "The headers window could not be opened. " + ex.Message); }
    }
}
