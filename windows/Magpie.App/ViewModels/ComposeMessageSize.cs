using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    private EncodedMessageSize? _encodedMessageSize;

    internal int ReviewVersion => _edits;
    internal bool ReviewSnapshotIsCurrent(int version, Account? account)
        => !_closed && !_queued && !Sending && !RevisionBusy && !AttachmentOperationBusy
            && _edits == version && ReferenceEquals(From, account);

    private void InvalidateMessageSize()
    {
        _encodedMessageSize = null;
        OnPropertyChanged(nameof(AttachmentSummary));
    }

    private string FullMessageSizeSummary
    {
        get
        {
            var count = Attachments.Count;
            var files = count == 0 ? "No file attachments" : $"{count} file{(count == 1 ? "" : "s")}";
            var size = _encodedMessageSize;
            return files + (size == null ? (_draft.Security.Requested ? " · protected message size updates after Review" : " · full message size updates after autosave or Review")
                : $" · {(size.AtLeast ? "at least " : "")}{HtmlRenderer.FormatSize(size.Bytes)} full message / {HtmlRenderer.FormatSize(ComposeSafety.SuggestedOutgoingLimitBytes(From))} suggested cap");
        }
    }

    /// <summary>Measure the whole current message, serializing beside autosave so MIME streams are never read concurrently.</summary>
    public async Task<EncodedMessageSize> MeasureDraftSizeAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_closed || _queued || Sending || RevisionBusy || AttachmentOperationBusy)
                throw new InvalidOperationException("Wait for the current send, attachment or restore operation to finish before reviewing.");
            var version = _edits;
            var account = From;
            var draft = await BuildDraftAsync(validate: false)
                ?? throw new InvalidOperationException("Choose a sending account first.");
            if (!ReviewSnapshotIsCurrent(version, account))
                throw new InvalidOperationException("The message changed while its size was being checked. Review it again.");
            var size = await MeasureBuiltDraftSizeAsync(draft);
            if (!ReviewSnapshotIsCurrent(version, account))
                throw new InvalidOperationException("The message changed while its size was being checked. Review it again.");
            return size;
        }
        finally { _saveGate.Release(); }
    }

    private async Task<EncodedMessageSize> MeasureBuiltDraftSizeAsync(Draft draft)
    {
        var account = From ?? throw new InvalidOperationException("Choose a sending account first.");
        var edits = _edits;
        var size = await Task.Run(() =>
        {
            var outgoing = _e.SecureMail.PrepareOutgoing(Composer.Build(draft, account), account, draft.Security,
                DateTimeOffset.UtcNow, preserveRecall: false);
            return ComposeSafety.MeasureEncodedMessage(outgoing.Message);
        });
        if (_edits == edits)
        {
            _encodedMessageSize = size;
            OnPropertyChanged(nameof(AttachmentSummary));
        }
        return size;
    }
}
