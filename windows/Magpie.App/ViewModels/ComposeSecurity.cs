using Magpie.Core.Security;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    public string[] SecurityFormats { get; } = { "S/MIME", "OpenPGP" };
    public string SecurityFormat
    {
        get => SecureMailService.FormatName(_draft.Security.Format);
        set
        {
            var format = value == "OpenPGP" ? MailSecurityFormat.OpenPgp : MailSecurityFormat.Smime;
            if (_draft.Security.Format == format && !_draft.Security.Invalid) return;
            _draft.Security.Format = format; _draft.Security.Invalid = false; SecurityEdited();
        }
    }
    public bool SignMessage
    {
        get => _draft.Security.Sign;
        set { if (_draft.Security.Sign == value && !_draft.Security.Invalid) return; _draft.Security.Sign = value; _draft.Security.Invalid = false; SecurityEdited(); }
    }
    public bool EncryptMessage
    {
        get => _draft.Security.Encrypt;
        set { if (_draft.Security.Encrypt == value && !_draft.Security.Invalid) return; _draft.Security.Encrypt = value; _draft.Security.Invalid = false; SecurityEdited(); }
    }
    public string SecurityNotice => _draft.Security.Invalid
        ? "This draft has unsupported security settings. Choose a method and review Sign / Encrypt before sending."
        : "Sign protects the body and attachments. Encrypt requires a confirmed key for every recipient and your selected From address. Subject and addresses stay visible. Editable drafts are not encrypted. Send Bcc recipients separate encrypted messages.";
    private void SecurityEdited()
    {
        OnPropertyChanged(nameof(SecurityFormat)); OnPropertyChanged(nameof(SignMessage));
        OnPropertyChanged(nameof(EncryptMessage)); OnPropertyChanged(nameof(SecurityNotice)); MarkEdited();
    }
    private void RestoreSecurity(SecureMailOptions options) { _draft.Security = options.Clone(); SecurityEdited(); }
}
