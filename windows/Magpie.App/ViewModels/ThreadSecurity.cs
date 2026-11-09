using Magpie.App.Services;
using Magpie.App.Views;
using Magpie.Core.Security;

namespace Magpie.App.ViewModels;

public partial class ThreadViewModel
{
    private bool _openingSecurity;

    /// <summary>Explicit action only: raw encrypted MIME may be cached, decrypted bodies never enter the store or AI context.</summary>
    public async Task OpenSecurityAsync(long rowId)
    {
        if (_openingSecurity || Messages.FirstOrDefault(m => m.Id == rowId) is not { } row) return;
        _openingSecurity = true;
        SecureMailReadResult? result = null;
        try
        {
            var (_, mime) = await _e.LoadAsync(row, true, CancellationToken.None);
            if (mime == null) throw new MailSecurityException("Download the original message before opening its security details.");
            result = await Task.Run(() => _e.SecureMail.Open(mime, row.AccountId));
            var window = SecureMessageWindow.Create(Ui.ActiveWindow, result);
            window.Show(); result = null; // The secure window owns the in-memory result until it closes.
        }
        catch (Exception ex) { Ui.Error("Message security", ex is MailSecurityException ? ex.Message : "The secure message could not be opened. Its decrypted content was not cached."); }
        finally { result?.Dispose(); _openingSecurity = false; }
    }
}
