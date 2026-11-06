using Magpie.Core.Mail;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    private EncodedMessageSize? _encodedMessageSize;

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
            return files + (size == null ? " · full message size updates after autosave or Review"
                : $" · {(size.AtLeast ? "at least " : "")}{HtmlRenderer.FormatSize(size.Bytes)} full message / {HtmlRenderer.FormatSize(ComposeSafety.SuggestedOutgoingLimitBytes(From))} suggested cap");
        }
    }

    /// <summary>Measure the whole current message, serializing beside autosave so MIME streams are never read concurrently.</summary>
    public async Task<EncodedMessageSize> MeasureDraftSizeAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            if (_closed || _queued || Sending) throw new InvalidOperationException("The message is closing or sending.");
            var draft = await BuildDraftAsync(validate: false)
                ?? throw new InvalidOperationException("Choose a sending account first.");
            return await MeasureBuiltDraftSizeAsync(draft);
        }
        finally { _saveGate.Release(); }
    }

    private async Task<EncodedMessageSize> MeasureBuiltDraftSizeAsync(Draft draft)
    {
        var account = From ?? throw new InvalidOperationException("Choose a sending account first.");
        var edits = _edits;
        var size = await Task.Run(() => ComposeSafety.MeasureEncodedMessage(Composer.Build(draft, account)));
        if (_edits == edits)
        {
            _encodedMessageSize = size;
            OnPropertyChanged(nameof(AttachmentSummary));
        }
        return size;
    }
}
