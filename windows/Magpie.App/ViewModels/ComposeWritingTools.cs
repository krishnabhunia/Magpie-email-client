using System.Windows.Threading;
using Magpie.Core.Mail;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

public partial class ComposeViewModel
{
    private ComposeWritingStatistics _writingStatistics = ComposeWritingStatistics.Measure("");
    private DispatcherTimer? _writingStatisticsTimer;
    private int _writingStatisticsRequest;
    private bool _writingInsertionBusy;

    /// <summary>Visible new writing in rich mode; all retained text in plain mode, where section boundaries are lost.</summary>
    public Func<Task<string>>? GetWritingText { get; set; }
    public int WritingWordCount => _writingStatistics.Words;
    public int WritingCharacterCount => _writingStatistics.Characters;
    public int WritingReadingMinutes => _writingStatistics.ReadingMinutes;
    public string WritingStatisticsSummary => (IsPlainText ? "All plain text · " : "") + _writingStatistics.Summary;
    public string WritingStatisticsHelp => (IsPlainText
        ? "All plain message text, including any retained signature or quoted email: plain text has no reliable section boundaries. "
        : "New writing only; signatures, quoted email and inline pictures are excluded. ")
        + "Characters include spaces and line breaks; joined emoji and combining marks count as one character. Reading time is an estimate at 200 words per minute.";

    private void ScheduleWritingStatistics()
    {
        if (_closed) return;
        _writingStatisticsRequest++;
        if (_writingStatisticsTimer == null)
        {
            _writingStatisticsTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _writingStatisticsTimer.Tick += OnWritingStatisticsTick;
        }
        _writingStatisticsTimer.Stop();
        _writingStatisticsTimer.Start();
    }

    private async void OnWritingStatisticsTick(object? sender, EventArgs e)
    {
        _writingStatisticsTimer?.Stop();
        await RefreshWritingStatisticsAsync();
    }

    /// <summary>Latest-result-wins; editor disposal/failure must never prevent editing, saving or sending.</summary>
    public async Task RefreshWritingStatisticsAsync()
    {
        if (_closed || GetWritingText == null) return;
        var request = ++_writingStatisticsRequest;
        try
        {
            var text = await GetWritingText();
            if (_closed || request != _writingStatisticsRequest) return;
            _writingStatistics = ComposeWritingStatistics.Measure(text);
            OnPropertyChanged(nameof(WritingWordCount));
            OnPropertyChanged(nameof(WritingCharacterCount));
            OnPropertyChanged(nameof(WritingReadingMinutes));
            OnPropertyChanged(nameof(WritingStatisticsSummary));
        }
        catch (Exception) { /* The editor may close between requesting and receiving its text. Keep the last count. */ }
    }

    private void StopWritingStatistics()
    {
        _writingStatisticsRequest++;
        if (_writingStatisticsTimer == null) return;
        _writingStatisticsTimer.Stop();
        _writingStatisticsTimer.Tick -= OnWritingStatisticsTick;
        _writingStatisticsTimer = null;
    }

    /// <summary>Insert body text at the current selection; fill a blank subject, but never replace one without approval.</summary>
    public Task<bool> InsertTemplateAsync(QuickTemplate template, bool replaceSubject = false, string? expectedSubject = null)
    {
        return InsertWritingTextAsync(async () =>
        {
            var originalSubject = Subject;
            if (expectedSubject != null && !string.Equals(originalSubject, expectedSubject, StringComparison.Ordinal))
            {
                Status = "The subject changed. Review it and choose the template again.";
                return false;
            }
            var expanded = ComposeTemplates.Expand(template, From, To, originalSubject, DateTimeOffset.Now);
            if (!string.IsNullOrEmpty(expanded.Body)) await EditorCommand!("insert", expanded.Body);
            if (_closed || _queued) return false;
            // A user may change the subject while WebView2 is processing the body insertion: never overwrite that newer edit.
            if (string.Equals(Subject, originalSubject, StringComparison.Ordinal)) Subject = expanded.SubjectFor(originalSubject, replaceSubject);
            return true;
        }, "Template inserted. Review the message before sending.");
    }

    /// <summary>Insert a configured canned reply literally; never expand tokens, replace message fields, or send.</summary>
    public Task<bool> InsertQuickReplyAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Status = "This quick reply is empty. Choose another reply or edit it in Settings.";
            return Task.FromResult(false);
        }
        return InsertWritingTextAsync(async () =>
        {
            await EditorCommand!("insertLiteral", text);
            return true;
        }, "Quick reply inserted as editable text. Review the message before sending.");
    }

    private bool CanInsertWritingText()
    {
        if (_closed || _queued)
        {
            Status = "This message is no longer editable.";
            return false;
        }
        if (Sending || RevisionBusy || AttachmentOperationBusy)
        {
            Status = "Wait for the current send, attachment or draft restore operation before inserting text.";
            return false;
        }
        if (EditorCommand == null)
        {
            Status = "The message editor is not ready. Try again when it is ready.";
            return false;
        }
        return true;
    }

    private async Task<bool> InsertWritingTextAsync(Func<Task<bool>> insert, string completedStatus)
    {
        if (!CanInsertWritingText()) return false;
        if (_writingInsertionBusy)
        {
            Status = "Wait for the current text insertion to finish.";
            return false;
        }
        _writingInsertionBusy = true;
        var acquired = false;
        try
        {
            // Serialise with message builds/restores. If Send starts while the editor is inserting,
            // MarkEdited below invalidates that send's snapshot before it can acquire this gate.
            await _saveGate.WaitAsync();
            acquired = true;
            if (!CanInsertWritingText() || !await insert() || _closed || _queued) return false;
            MarkEdited();
            Status = completedStatus;
        }
        finally
        {
            if (acquired) _saveGate.Release();
            _writingInsertionBusy = false;
        }
        await RefreshWritingStatisticsAsync();
        return true;
    }
}
