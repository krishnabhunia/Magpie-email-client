using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Mail;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>FR1: full reply drafts are separate from prompt drafting and short suggested replies.</summary>
public partial class ComposeViewModel
{
    private CancellationTokenSource? _replyDraftCts;
    private long _replyDraftRequest;
    private bool _replyAutoEligible;
    private bool _replyAutoStarted;
    private string _replyContextFingerprint = "";
    private string _replyThreadDigest = "";
    private string _legacyAiContext = "";

    /// <summary>The view excludes the protected signature and original quotation from this text.</summary>
    public Func<Task<string>>? GetNewText { get; set; }
    /// <summary>Choose append/replace explicitly when an AI reply would alter existing typed text.</summary>
    public Func<Task<string?>>? ChooseReplyInsertion { get; set; }

    public IReadOnlyList<string> ReplyToneChoices => AiReplySettings.Tones;
    public IReadOnlyList<string> ReplyPurposeChoices => AiReplySettings.Purposes;

    [ObservableProperty] private bool _showReplyAi;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUseReplyDraft))] private bool _replyDraftBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUseReplyDraft))] private string _replyDraftPreview = "";
    [ObservableProperty] private string _replyDraftError = "";
    [ObservableProperty] private string _replyDraftStatus = "";
    [ObservableProperty] private bool _replyDraftNeedsSetup;
    [ObservableProperty] private string _replyTone = "Formal";
    [ObservableProperty] private string _replyPurpose = "Answer the message";
    [ObservableProperty] private string _replyCustomTone = "";
    [ObservableProperty] private string _replyCustomPurpose = "";

    public bool CanUseReplyDraft => !_closed && !_queued && !Sending && !ReplyDraftBusy
        && !string.IsNullOrWhiteSpace(ReplyDraftPreview) && ReplyGateOpen()
        && _replyContextFingerprint == ReplyFingerprint();

    private void InitializeReplyDrafts(string? prefillText)
    {
        var settings = _e.Config.Ai.ReplyDrafts;
        ReplyTone = settings.Tone;
        ReplyPurpose = settings.Purpose;
        ReplyCustomTone = settings.CustomTone;
        ReplyCustomPurpose = settings.CustomPurpose;
        // A restored draft or a suggested-reply prefill already has its own content. Opening it is not a Reply click.
        _replyAutoEligible = IsReply && string.IsNullOrWhiteSpace(prefillText);
    }

    /// <summary>Called once after the rich or fallback editor has loaded the initial signature/quote.</summary>
    public async Task ReplyEditorReadyAsync()
    {
        if (_replyAutoStarted) return;
        _replyAutoStarted = true;
        if (!_replyAutoEligible || !ReplyGateOpen()) return;
        AiPanelOpen = true;
        await GenerateReplyDraft();
    }

    private bool ReplyGateOpen() => !_closed && !_queued && !Sending && IsReply
        && _e.Config.Ai.Enabled && _e.Config.Ai.ReplyDrafts.ShouldGenerate(_draft.Mode);

    // This remains in memory. It is never logged or sent to a model.
    private string ReplyFingerprint(bool includeConsent = true) => JsonSerializer.Serialize(new
    {
        Account = From?.Id, _draft.ThreadKey, _draft.Mode, Subject, To, Cc, Bcc,
        ReplyTone, ReplyPurpose, ReplyCustomTone, ReplyCustomPurpose,
        _e.Config.Ai.Enabled, _e.Config.Ai.ActiveId, _e.Config.Ai.Provider,
        _e.Config.Ai.Endpoint, _e.Config.Ai.Model, Preferences = _e.Config.Ai.ReplyDrafts,
        Consent = includeConsent && _e.Ai.NeedsConsent(AiFeature.ReplyDraft),
    });

    private void RefreshReplyAi()
    {
        ShowReplyAi = IsReply && _e.Config.Ai.ReplyDrafts.ShouldGenerate(_draft.Mode)
            && _e.Ai.IsVisible(AiFeature.ReplyDraft);
        ReplyDraftNeedsSetup = ShowReplyAi && _e.Ai.Availability(AiFeature.ReplyDraft) == AiAvailability.NotConfigured;
        InvalidateReplyContext();
        OnPropertyChanged(nameof(CanUseReplyDraft));
    }

    private void InvalidateReplyContext()
    {
        if (!_constructed || _replyContextFingerprint.Length == 0) return;
        if (_replyContextFingerprint == ReplyFingerprint() && ReplyGateOpen()) return;
        StopReplyDraft(clear: true);
        ReplyDraftStatus = ShowReplyAi ? "Reply context changed. Generate again when you are ready." : "";
    }

    partial void OnFromChanged(Magpie.Core.Models.Account? value)
    {
        if (!_constructed) return;
        InvalidateReplyContext();
        InvalidateLegacyAi();
        MarkEdited();
    }

    private string LegacyAiFingerprint() => JsonSerializer.Serialize(new
    {
        Account = From?.Id, _draft.ThreadKey, Subject, To, Cc, Bcc,
        _e.Config.Ai.Enabled, _e.Config.Ai.ActiveId, _e.Config.Ai.Provider,
        _e.Config.Ai.Endpoint, _e.Config.Ai.Model, _e.Config.Ai.Draft, _e.Config.Ai.Rewrite,
    });

    private void InvalidateLegacyAi()
    {
        if (!_constructed || _legacyAiContext.Length == 0) return;
        var feature = PreviewKind == "rewrite" ? AiFeature.Rewrite : AiFeature.Draft;
        if (_legacyAiContext != LegacyAiFingerprint() || _e.Ai.Availability(feature) != AiAvailability.Ready)
            DiscardPreview();
    }

    partial void OnReplyToneChanged(string value) => InvalidateReplyChoices();
    partial void OnReplyPurposeChanged(string value) => InvalidateReplyChoices();
    partial void OnReplyCustomToneChanged(string value) => InvalidateReplyChoices();
    partial void OnReplyCustomPurposeChanged(string value) => InvalidateReplyChoices();

    private void InvalidateReplyChoices()
    {
        if (!_constructed || _replyContextFingerprint.Length == 0) return;
        StopReplyDraft(clear: true);
        ReplyDraftStatus = "Reply preferences changed. Generate again to use them.";
    }

    private ThreadForAi? BuildReplyContext(AiReplySettings preferences)
    {
        if (!IsReply || string.IsNullOrEmpty(_draft.ThreadKey) || From == null) return null;
        try
        {
            // Never use the previous account's quote/editor body as the model context after From changes.
            var rows = _e.Store.GetThread(From.Id, _draft.ThreadKey).Where(r => r.AccountId == From.Id).ToList();
            if (rows.Count == 0) return null;
            var context = AiService.BuildReplyThread(Subject, rows.Select(r => (r, _e.Store.GetBody(r.Id))).ToList(),
                From.Id, _draft.ThreadKey, preferences.Context, _e.MyAddresses);
            return context.Included > 0 ? context : null;
        }
        catch (ArgumentException)
        {
            // A Sent-only conversation has no received message for the latest-message scope.
            return null;
        }
        catch (Exception ex)
        {
            // Context extraction is optional: a store failure must leave the manual composer usable.
            Log.Warn("AI reply context unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    private bool ReplyThreadStillCurrent() => _replyThreadDigest.Length > 0
        && BuildReplyContext(_e.Config.Ai.ReplyDrafts)?.Digest == _replyThreadDigest;

    private void DiscardChangedReplyThread()
    {
        StopReplyDraft(clear: true);
        ReplyDraftStatus = "The conversation changed. Generate again to include the latest messages.";
    }

    [RelayCommand]
    private async Task GenerateReplyDraft()
    {
        if (!ReplyGateOpen())
        {
            StopReplyDraft(clear: true);
            ReplyDraftStatus = "AI replies are off for this action. You can write your reply normally.";
            return;
        }
        if (_e.Ai.Availability(AiFeature.ReplyDraft) == AiAvailability.NotConfigured)
        {
            StopReplyDraft(clear: true);
            ReplyDraftNeedsSetup = true;
            ReplyDraftStatus = "Set up an AI provider, or continue writing manually.";
            AiPanelOpen = true;
            return;
        }
        var preferences = _e.Config.Ai.ReplyDrafts.Clone();
        preferences.Tone = ReplyTone;
        preferences.Purpose = ReplyPurpose;
        preferences.CustomTone = ReplyCustomTone;
        preferences.CustomPurpose = ReplyCustomPurpose;
        preferences.Normalise();
        if (preferences.ValidationError() is { } validation)
        {
            ReplyDraftError = validation;
            return;
        }
        var context = BuildReplyContext(preferences);
        if (context == null)
        {
            ReplyDraftError = preferences.Context == AiReplySettings.LatestMessageContext
                ? "There is no received message available for this sending account. Choose Current conversation in AI reply settings, or write your reply manually."
                : "The conversation is not available for this sending account. You can write manually, or start a new reply from that account's conversation.";
            return;
        }
        // A modal consent decision may also change account/settings. Verify the same context after it returns.
        var beforeConsent = ReplyFingerprint(includeConsent: false);
        if (_e.Ai.NeedsConsent(AiFeature.ReplyDraft) && !Views.ConsentDialog.Ask(AiFeature.ReplyDraft, context.Included))
        {
            ReplyDraftStatus = "AI was not used. You can write your reply normally.";
            return;
        }
        // Consent itself is expected to change; every other field must still match the selected draft context.
        if (!ReplyGateOpen() || From == null || beforeConsent != ReplyFingerprint(includeConsent: false)) return;
        var freshContext = BuildReplyContext(preferences);
        if (freshContext == null || freshContext.Digest != context.Digest)
        {
            ReplyDraftError = "The conversation changed while you chose. Generate the reply again.";
            return;
        }
        StopReplyDraft(clear: true);
        var request = ++_replyDraftRequest;
        var fingerprint = _replyContextFingerprint = ReplyFingerprint();
        _replyThreadDigest = context.Digest;
        var cts = _replyDraftCts = new CancellationTokenSource();
        var token = cts.Token;
        var name = string.IsNullOrWhiteSpace(From.DisplayName) ? From.Email : From.DisplayName;
        var mode = _draft.Mode;
        var buffer = new StringBuilder();
        var acceptingTokens = true;
        ReplyDraftBusy = true;
        ReplyDraftNeedsSetup = false;
        ReplyDraftError = "";
        ReplyDraftStatus = context.Truncated || context.TextTruncated
            ? $"Using the newest {context.Included} of {context.Total} messages. Attachments are excluded."
            : $"Using {context.Included} message{(context.Included == 1 ? "" : "s")}. Attachments are excluded.";
        AiPanelOpen = true;
        bool Current() => !token.IsCancellationRequested && request == _replyDraftRequest
            && !_closed && !_queued && ReplyGateOpen() && fingerprint == ReplyFingerprint();
        try
        {
            var reply = await _e.Ai.ReplyDraftAsync(context, preferences, name,
                text => Ui.Post(() => { if (acceptingTokens && Current()) { buffer.Append(text); ReplyDraftPreview = buffer.ToString(); } }), token, mode);
            acceptingTokens = false;
            if (Current())
            {
                if (!ReplyThreadStillCurrent()) { DiscardChangedReplyThread(); return; }
                ReplyDraftPreview = reply.Trim();
                if (ReplyDraftPreview.Length == 0) ReplyDraftError = "The provider returned no text. Try again or write your reply manually.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            acceptingTokens = false;
            if (Current())
            {
                ReplyDraftPreview = "";
                ReplyDraftError = ex is AiException ? ex.Message : "The AI provider could not be reached. Your typed reply is kept. Try again or continue manually.";
            }
        }
        finally
        {
            acceptingTokens = false;
            if (request == _replyDraftRequest)
            {
                ReplyDraftBusy = false;
                if (!Current()) { ReplyDraftPreview = ""; _replyContextFingerprint = ""; }
            }
            if (ReferenceEquals(_replyDraftCts, cts)) _replyDraftCts = null;
            cts.Dispose();
        }
    }

    private void StopReplyDraft(bool clear)
    {
        ++_replyDraftRequest;
        _replyDraftCts?.Cancel();
        _replyDraftCts = null;
        ReplyDraftBusy = false;
        if (clear)
        {
            ReplyDraftPreview = "";
            ReplyDraftError = "";
            _replyContextFingerprint = "";
            _replyThreadDigest = "";
        }
    }

    [RelayCommand]
    private void CancelReplyDraft()
    {
        StopReplyDraft(clear: true);
        ReplyDraftStatus = "Generation stopped. Your typed reply is kept.";
    }

    [RelayCommand]
    private void DiscardReplyDraft()
    {
        StopReplyDraft(clear: true);
        ReplyDraftStatus = "AI draft discarded. Your typed reply is kept.";
    }

    [RelayCommand]
    private async Task UseReplyDraft()
    {
        if (!CanUseReplyDraft || EditorCommand == null || GetNewText == null) return;
        var text = ReplyDraftPreview;
        var fingerprint = _replyContextFingerprint;
        string mode;
        try
        {
            if (!ReplyThreadStillCurrent()) { DiscardChangedReplyThread(); return; }
            var typed = await GetNewText();
            if (!CanUseReplyDraft || fingerprint != _replyContextFingerprint) return;
            mode = string.IsNullOrWhiteSpace(typed) ? "replaceBody" : await (ChooseReplyInsertion?.Invoke() ?? Task.FromResult<string?>(null)) ?? "";
            // A modal can outlive a provider/account switch. It must never insert a result from the old context.
            if (mode is not ("replaceBody" or "appendBody") || !CanUseReplyDraft || fingerprint != _replyContextFingerprint) return;
            if (!ReplyThreadStillCurrent()) { DiscardChangedReplyThread(); return; }
            await EditorCommand("replyDraft", JsonSerializer.Serialize(new { text, expectedNewText = typed, mode }));
            MarkEdited();
            StopReplyDraft(clear: true);
            ReplyDraftStatus = "AI draft inserted. Review and edit it before sending.";
        }
        catch (Exception ex) { ReplyDraftError = ex.Message; }
    }
}
