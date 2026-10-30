using System.Collections.ObjectModel;
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

/// <summary>AW1: one selectable chip (an answer, a tone or a formality step).</summary>
public sealed partial class AiChoice : ObservableObject
{
    public AiChoice(string key, string label, string glyph = "", string template = "")
    {
        Key = key;
        Label = label;
        Glyph = glyph;
        Template = template;
    }

    public string Key { get; }
    public string Label { get; }
    public string Glyph { get; }
    public bool HasGlyph => Glyph.Length > 0;
    /// <summary>The words put in "What the reply says" when this answer chip is clicked.</summary>
    public string Template { get; }
    [ObservableProperty] private bool _isSelected;
}

/// <summary>FR1 + AW1: full drafts after Reply / Reply All / Forward, separate from prompt drafting and short suggested replies.
/// The panel sits under the editor (design AW1): what the reply says (Q86), how it sounds and culture &amp; etiquette (Q87),
/// the Forward note (Q85), up to five versions per draft.</summary>
public partial class ComposeViewModel
{
    private CancellationTokenSource? _replyDraftCts;
    private long _replyDraftRequest;
    private bool _replyAutoEligible;
    private bool _replyAutoStarted;
    private string _replyContextFingerprint = "";
    private string _replyThreadDigest = "";
    private string _legacyAiContext = "";
    private bool _suppressChoiceTracking;
    private readonly HashSet<string> _changedChoices = new();
    private readonly List<DraftVersion> _versions = new();
    private int _versionIndex = -1;
    public const int MaxVersions = 5;

    private sealed record DraftVersion(string Body, string Summary, bool NeedsRecipientGreeting);

    /// <summary>The view excludes the protected signature and original quotation from this text.</summary>
    public Func<Task<string>>? GetNewText { get; set; }
    /// <summary>Choose append/replace explicitly when an AI reply would alter existing typed text.</summary>
    public Func<Task<string?>>? ChooseReplyInsertion { get; set; }
    /// <summary>The view moves the keyboard to "What the reply says" (after "Yes, if…" or "Custom").</summary>
    public Action? FocusAnswerBox { get; set; }

    public IReadOnlyList<string> ReplyToneChoices => AiReplySettings.Tones;
    public IReadOnlyList<string> ReplyPurposeChoices => AiReplySettings.Purposes;
    public IReadOnlyList<string> ReplyLengthChoices => AiReplySettings.Lengths;
    public IReadOnlyList<string> ReplyLanguageChoices => AiReplySettings.Languages;
    public IReadOnlyList<string> ReplyEnglishStyleChoices => AiReplySettings.EnglishStyles;
    public IReadOnlyList<string> ReplyEtiquetteChoices => AiReplySettings.Etiquettes;
    public IReadOnlyList<string> ReplyDirectnessChoices => AiReplySettings.Directnesses;
    public IReadOnlyList<string> ReplyGreetingChoices => AiReplySettings.Greetings;
    public IReadOnlyList<string> ReplyAddressChoices => AiReplySettings.Addresses;
    public IReadOnlyList<string> ReplyEmojiChoices => AiReplySettings.EmojiChoices;

    public ObservableCollection<AiChoice> AnswerChoices { get; } = new();
    public ObservableCollection<AiChoice> ToneChips { get; } = new();
    public ObservableCollection<AiChoice> FormalityChips { get; } = new();

    [ObservableProperty] private bool _showReplyAi;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUseReplyDraft))] private bool _replyDraftBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanUseReplyDraft))] private string _replyDraftPreview = "";
    [ObservableProperty] private string _replyDraftError = "";
    [ObservableProperty] private string _replyDraftStatus = "";
    [ObservableProperty] private bool _replyDraftNeedsSetup;
    [ObservableProperty] private string _replyTone = "Professional";
    [ObservableProperty] private string _replyPurpose = "Answer the message";
    [ObservableProperty] private string _replyCustomTone = "";
    [ObservableProperty] private string _replyCustomPurpose = "";

    // ── AW1 ──
    [ObservableProperty] private bool _replyPanelExpanded = true;
    [ObservableProperty] private string _replyStance = "";
    [ObservableProperty] private string _replyAnswer = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasAskLine))] private string _replyAskLine = "";
    [ObservableProperty] private bool _answerIsSuggestion;
    [ObservableProperty] private string _replyFormality = "Formal";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LanguageNote))] private string _replyLength = "Balanced";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(LanguageNote))] private string _replyLanguage = "Match original";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CultureSummary))] private string _replyEnglishStyle = "Indian English";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CultureSummary))] private string _replyEtiquette = "Neutral";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CultureSummary))] private string _replyDirectness = "Balanced";
    [ObservableProperty] private string _replyGreeting = "Auto";
    [ObservableProperty] private string _replyAddress = "Auto";
    [ObservableProperty] private string _replyEmoji = "Never";
    [ObservableProperty] private bool _forwardSummary = true;
    [ObservableProperty] private bool _forwardMentionFiles = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RewriteLabel))] private int _pendingChanges;
    [ObservableProperty] private string _versionLabel = "";
    [ObservableProperty] private string _usedSummary = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVersions))] private bool _canPrevVersion;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVersions))] private bool _canNextVersion;

    public bool IsForward => _draft.Mode == ComposeMode.Forward;
    /// <summary>Reply, Reply All or Forward — the composer modes that can start an AI draft.</summary>
    public bool IsAiDraftMode => IsReply || IsForward;
    private AiFeature DraftFeature => IsForward ? AiFeature.ForwardDraft : AiFeature.ReplyDraft;
    public bool ShowAnswerSection => IsForward || _e.Config.Ai.ReplyDrafts.ShowStance;
    public bool ShowPurpose => IsReply;
    public string PanelTitle => IsForward ? "AI note · review before use" : "AI draft · review before use";
    public string AnswerTitle => IsForward ? "Why you're forwarding" : "What the reply says";
    public string AnswerNote => IsForward ? "Pick one or describe it" : "Magpie's suggestion · change it freely";
    public string AnswerHint => IsForward
        ? "Type it as plainly as you like; Magpie turns it into the note. Ctrl+Enter rewrites."
        : "Pick an answer or type your own, e.g. change \"Yes, I will do it\" to \"No, I will not do it\". Ctrl+Enter rewrites.";
    public bool HasAskLine => ReplyAskLine.Length > 0;
    public string RewriteLabel => PendingChanges > 0 ? $"Rewrite · {PendingChanges} change{(PendingChanges == 1 ? "" : "s")}" : "Rewrite";
    public string CultureSummary => $"{ReplyEnglishStyle} · {(ReplyEtiquette == "Neutral" ? "neutral etiquette" : ReplyEtiquette + " etiquette")} · {ReplyDirectness.ToLowerInvariant()}";
    public bool HasVersions => _versions.Count > 1;
    public string LanguageNote => ReplyLanguage is "Match original" or "English" ? "" : $"The draft is written in {ReplyLanguage}.";
    public string RecipientNote => !IsForward ? "" : AiService.FirstName(To) is { Length: > 0 } n
        ? $"Greeting uses {n}." : "No recipient yet · the greeting is \"Hi,\" and gets the name when you add one.";

    public bool CanUseReplyDraft => !_closed && !_queued && !Sending && !ReplyDraftBusy
        && !string.IsNullOrWhiteSpace(ReplyDraftPreview) && ReplyGateOpen()
        && _replyContextFingerprint == ReplyFingerprint();

    private static readonly (string Key, string Label, string Glyph, string Template)[] ReplyAnswers =
    {
        ("yes", "Yes, I'll do it", "✓", "Yes, I'll do it."),
        ("no", "No, I can't", "✕", "No, I can't do it."),
        ("conditional", "Yes, if…", "≈", "Yes, if "),
        ("details", "Need details", "?", "I need more details before I can answer."),
        ("later", "Later", "⏱", "Yes, but later than asked."),
        ("other", "Custom", "✎", ""),
    };

    private static readonly (string Key, string Label, string Glyph, string Template)[] ForwardReasons =
    {
        ("fyi", "FYI only", "i", ""),
        ("handle", "Please handle", "→", "Please take care of this."),
        ("review", "Please review", "?", "Please review this and tell me what you think."),
        ("intro", "Introduce", "↔", "Introducing you two — please connect."),
        ("escalate", "Escalate", "!", "Flagging this — it needs attention."),
        ("other", "Custom", "✎", ""),
    };

    private void InitializeReplyDrafts(string? prefillText)
    {
        var settings = _e.Config.Ai.ReplyDrafts;
        foreach (var a in IsForward ? ForwardReasons : ReplyAnswers) AnswerChoices.Add(new AiChoice(a.Key, a.Label, a.Glyph, a.Template));
        foreach (var t in AiReplySettings.Tones) ToneChips.Add(new AiChoice(t, t));
        foreach (var f in AiReplySettings.Formalities) FormalityChips.Add(new AiChoice(f, f));
        _suppressChoiceTracking = true;
        ApplyStyle(settings.Style());
        ReplyPurpose = settings.Purpose;
        ReplyCustomPurpose = settings.CustomPurpose;
        if (IsForward) ReplyStance = "fyi";
        // Q87 (optional): the style last used with this person.
        if (IsReply && settings.StyleFor(Composer.ParseAddresses(_draft.To).Mailboxes.FirstOrDefault()?.Address) is { } remembered)
        {
            ApplyStyle(remembered);
            ReplyDraftStatus = "Using the style you last used with this person.";
        }
        _suppressChoiceTracking = false;
        SelectChips();
        // A restored draft or a suggested-reply prefill already has its own content. Opening it is not a Reply click.
        _replyAutoEligible = IsAiDraftMode && string.IsNullOrWhiteSpace(prefillText) && _draft.Mode != ComposeMode.EditDraft;
    }

    private void ApplyStyle(AiStyle s)
    {
        ReplyTone = s.Tone;
        ReplyCustomTone = s.CustomTone;
        ReplyFormality = s.Formality;
        ReplyLength = s.Length;
        ReplyLanguage = s.Language;
        ReplyEnglishStyle = s.EnglishStyle;
        ReplyEtiquette = s.Etiquette;
        ReplyDirectness = s.Directness;
        ReplyGreeting = s.Greeting;
        ReplyAddress = s.Address;
        ReplyEmoji = s.Emoji;
    }

    /// <summary>The style chosen for this draft.</summary>
    public AiStyle CurrentStyle()
    {
        var s = new AiStyle
        {
            Tone = ReplyTone, CustomTone = ReplyCustomTone, Formality = ReplyFormality, Length = ReplyLength, Language = ReplyLanguage,
            EnglishStyle = ReplyEnglishStyle, Etiquette = ReplyEtiquette, Directness = ReplyDirectness, Greeting = ReplyGreeting,
            Address = ReplyAddress, Emoji = ReplyEmoji,
        };
        s.Normalise();
        if (ReplyTone == "Custom" && string.IsNullOrWhiteSpace(ReplyCustomTone)) s.Tone = "Custom";
        return s;
    }

    private void SelectChips()
    {
        foreach (var c in AnswerChoices) c.IsSelected = c.Key == ReplyStance;
        foreach (var c in ToneChips) c.IsSelected = c.Key == ReplyTone;
        foreach (var c in FormalityChips) c.IsSelected = c.Key == ReplyFormality;
    }

    /// <summary>Called once after the rich or fallback editor has loaded the initial signature/quote.</summary>
    public async Task ReplyEditorReadyAsync()
    {
        if (_replyAutoStarted) return;
        _replyAutoStarted = true;
        if (!_replyAutoEligible || !ReplyGateOpen()) return;
        await GenerateReplyDraft();
    }

    private bool ReplyGateOpen() => !_closed && !_queued && !Sending && IsAiDraftMode
        && _e.Config.Ai.Enabled && _e.Config.Ai.ReplyDrafts.ShouldGenerate(_draft.Mode);

    // This remains in memory. It is never logged or sent to a model. Per-draft choices (answer, tone, culture…) are not part
    // of it: a shown version keeps the choices it was written with. A forward note doesn't depend on the recipients (only its
    // greeting does, and that is filled in locally), so To / Cc / Bcc only count for replies.
    private string ReplyFingerprint(bool includeConsent = true)
    {
        var p = _e.Config.Ai.ReplyDrafts;
        return JsonSerializer.Serialize(new
        {
            Account = From?.Id, _draft.ThreadKey, _draft.Mode, _draft.ForwardOfRow, Subject,
            To = IsForward ? "" : To, Cc = IsForward ? "" : Cc, Bcc = IsForward ? "" : Bcc,
            _e.Config.Ai.Enabled, _e.Config.Ai.ActiveId, _e.Config.Ai.Provider, _e.Config.Ai.Endpoint, _e.Config.Ai.Model,
            Gate = p.ShouldGenerate(_draft.Mode), p.Context, p.Instructions, p.ShowStance,
            Consent = includeConsent && _e.Ai.NeedsConsent(DraftFeature),
        });
    }

    private void RefreshReplyAi()
    {
        ShowReplyAi = IsAiDraftMode && _e.Config.Ai.ReplyDrafts.ShouldGenerate(_draft.Mode) && _e.Ai.IsVisible(DraftFeature);
        ReplyDraftNeedsSetup = ShowReplyAi && _e.Ai.Availability(DraftFeature) == AiAvailability.NotConfigured;
        OnPropertyChanged(nameof(ShowAnswerSection));
        InvalidateReplyContext();
        OnPropertyChanged(nameof(CanUseReplyDraft));
    }

    private void InvalidateReplyContext()
    {
        if (!_constructed) return;
        if (IsForward) RefreshForwardGreeting();
        if (_replyContextFingerprint.Length == 0) return;
        if (_replyContextFingerprint == ReplyFingerprint() && ReplyGateOpen()) return;
        StopReplyDraft(clear: true);
        ReplyDraftStatus = ShowReplyAi ? (IsForward ? "The email changed. Rewrite when you are ready." : "Reply context changed. Generate again when you are ready.") : "";
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

    // ── per-draft choices: collected until Rewrite (one request per click) ──

    partial void OnReplyToneChanged(string value) { ChoiceChanged("tone"); foreach (var c in ToneChips) c.IsSelected = c.Key == value; }
    partial void OnReplyPurposeChanged(string value) => ChoiceChanged("purpose");
    partial void OnReplyCustomToneChanged(string value) => ChoiceChanged("tone");
    partial void OnReplyCustomPurposeChanged(string value) => ChoiceChanged("purpose");
    partial void OnReplyFormalityChanged(string value) { ChoiceChanged("formality"); foreach (var c in FormalityChips) c.IsSelected = c.Key == value; }
    partial void OnReplyLengthChanged(string value) => ChoiceChanged("length");
    partial void OnReplyLanguageChanged(string value) => ChoiceChanged("language");
    partial void OnReplyEnglishStyleChanged(string value) => ChoiceChanged("english");
    partial void OnReplyEtiquetteChanged(string value) => ChoiceChanged("etiquette");
    partial void OnReplyDirectnessChanged(string value) => ChoiceChanged("directness");
    partial void OnReplyGreetingChanged(string value) => ChoiceChanged("greeting");
    partial void OnReplyAddressChanged(string value) => ChoiceChanged("address");
    partial void OnReplyEmojiChanged(string value) => ChoiceChanged("emoji");
    partial void OnForwardSummaryChanged(bool value) => ChoiceChanged("summary");
    partial void OnForwardMentionFilesChanged(bool value) => ChoiceChanged("files");

    partial void OnReplyAnswerChanged(string value)
    {
        if (_suppressChoiceTracking || !_constructed) return;
        // Typed words decide the answer: a chip can't contradict them ("Yes" selected, "No, I can't" typed).
        _suppressChoiceTracking = true;
        ReplyStance = string.IsNullOrWhiteSpace(value) ? (IsForward ? "fyi" : "") : "other";
        _suppressChoiceTracking = false;
        AnswerIsSuggestion = false;
        foreach (var c in AnswerChoices) c.IsSelected = c.Key == ReplyStance;
        ChoiceChanged("answer");
    }

    private void ChoiceChanged(string what)
    {
        if (_suppressChoiceTracking || !_constructed) return;
        _changedChoices.Add(what);
        PendingChanges = _versions.Count > 0 || ReplyDraftBusy ? _changedChoices.Count : 0;
    }

    [RelayCommand]
    private async Task PickAnswer(AiChoice? choice)
    {
        if (choice == null) return;
        _suppressChoiceTracking = true;
        ReplyStance = choice.Key;
        ReplyAnswer = choice.Template;
        _suppressChoiceTracking = false;
        AnswerIsSuggestion = false;
        foreach (var c in AnswerChoices) c.IsSelected = c.Key == choice.Key;
        ChoiceChanged("answer");
        // "Yes, if…" and "Custom" need the user's words first; the other answers rewrite at once.
        if (choice.Key is "conditional" or "other") { FocusAnswerBox?.Invoke(); return; }
        await GenerateReplyDraft();
    }

    [RelayCommand] private void PickTone(AiChoice? choice) { if (choice != null) ReplyTone = choice.Key; }
    [RelayCommand] private void PickFormality(AiChoice? choice) { if (choice != null) ReplyFormality = choice.Key; }
    [RelayCommand] private void ToggleReplyPanel() => ReplyPanelExpanded = !ReplyPanelExpanded;

    [RelayCommand]
    private void PreviousVersion() { if (_versionIndex > 0) ShowVersion(_versionIndex - 1); }

    [RelayCommand]
    private void NextVersion() { if (_versionIndex < _versions.Count - 1) ShowVersion(_versionIndex + 1); }

    private void ShowVersion(int index)
    {
        if (index < 0 || index >= _versions.Count) return;
        _versionIndex = index;
        var v = _versions[index];
        ReplyDraftPreview = v.NeedsRecipientGreeting ? AiService.WithRecipientGreeting(v.Body, AiService.FirstName(To)) : v.Body;
        UsedSummary = v.Summary;
        VersionLabel = $"Draft {index + 1} of {_versions.Count}";
        CanPrevVersion = index > 0;
        CanNextVersion = index < _versions.Count - 1;
        OnPropertyChanged(nameof(HasVersions));
    }

    private void RefreshForwardGreeting()
    {
        OnPropertyChanged(nameof(RecipientNote));
        if (!ReplyDraftBusy && _versionIndex >= 0 && _versionIndex < _versions.Count) ShowVersion(_versionIndex);
    }

    private void ClearVersions()
    {
        _versions.Clear();
        _versionIndex = -1;
        VersionLabel = "";
        UsedSummary = "";
        CanPrevVersion = CanNextVersion = false;
        OnPropertyChanged(nameof(HasVersions));
    }

    private string Summarise(AiStyle style, string stance)
    {
        var parts = new List<string>();
        var chip = AnswerChoices.FirstOrDefault(c => c.Key == stance);
        if (chip != null && ShowAnswerSection) parts.Add(chip.Label);
        if (IsReply && ReplyPurpose != "Answer the message") parts.Add(ReplyPurpose == "Custom" ? "Custom purpose" : ReplyPurpose);
        parts.Add(style.Tone == "Custom" ? "Custom tone" : style.Tone);
        parts.Add(style.Formality);
        parts.Add(style.Length);
        if (style.Etiquette != "Neutral") parts.Add(style.Etiquette + " etiquette");
        parts.Add(style.EnglishStyle);
        if (style.Language != "Match original") parts.Add(style.Language);
        return string.Join(" · ", parts);
    }

    private ThreadForAi? BuildReplyContext(AiReplySettings preferences)
    {
        if (!IsAiDraftMode || string.IsNullOrEmpty(_draft.ThreadKey) || From == null) return null;
        try
        {
            // Never use the previous account's quote/editor body as the model context after From changes.
            var rows = _e.Store.GetThread(From.Id, _draft.ThreadKey).Where(r => r.AccountId == From.Id).ToList();
            if (rows.Count == 0) return null;
            if (IsForward)
            {
                // Q85: only the email being forwarded (an older draft without its row id: the newest one in the conversation).
                var row = rows.FirstOrDefault(r => r.Id == _draft.ForwardOfRow) ?? rows.OrderBy(r => r.Date).Last();
                var forwarded = AiService.BuildThread(Subject, new[] { (row, _e.Store.GetBody(row.Id)) });
                return forwarded.Included > 0 ? forwarded : null;
            }
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

    /// <summary>Rewrite (and the first automatic draft): one request with every choice collected so far.</summary>
    [RelayCommand]
    private async Task GenerateReplyDraft()
    {
        if (!ReplyGateOpen())
        {
            StopReplyDraft(clear: true);
            ReplyDraftStatus = IsForward ? "AI notes are off for Forward. You can write your note normally." : "AI replies are off for this action. You can write your reply normally.";
            return;
        }
        var feature = DraftFeature;
        if (_e.Ai.Availability(feature) == AiAvailability.NotConfigured)
        {
            StopReplyDraft(clear: true);
            ReplyDraftNeedsSetup = true;
            ReplyDraftStatus = "Set up an AI provider, or continue writing manually.";
            return;
        }
        var style = CurrentStyle();
        var preferences = _e.Config.Ai.ReplyDrafts.Clone();
        preferences.Apply(style);
        preferences.Purpose = ReplyPurpose;
        preferences.CustomPurpose = ReplyCustomPurpose;
        preferences.ContactStyles = new();
        preferences.Normalise();
        if (ReplyTone == "Custom" && string.IsNullOrWhiteSpace(ReplyCustomTone)) { ReplyDraftError = "Write your custom tone first."; return; }
        if (preferences.ValidationError() is { } validation)
        {
            ReplyDraftError = validation;
            return;
        }
        var context = BuildReplyContext(preferences);
        if (context == null)
        {
            ReplyDraftError = IsForward
                ? "The email being forwarded is not available on this PC for this sending account. You can write the note manually."
                : preferences.Context == AiReplySettings.LatestMessageContext
                ? "There is no received message available for this sending account. Choose Current conversation in AI writing settings, or write your reply manually."
                : "The conversation is not available for this sending account. You can write manually, or start a new reply from that account's conversation.";
            return;
        }
        // A modal consent decision may also change account/settings. Verify the same context after it returns.
        var beforeConsent = ReplyFingerprint(includeConsent: false);
        if (_e.Ai.NeedsConsent(feature) && !Views.ConsentDialog.Ask(feature, context.Included))
        {
            ReplyDraftStatus = IsForward ? "AI was not used. You can write your note normally." : "AI was not used. You can write your reply normally.";
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
        StopReplyDraft(clear: false);
        var answer = ShowAnswerSection ? ReplyAnswer.Trim() : "";
        var stance = ShowAnswerSection ? ReplyStance : "";
        // An empty box lets Magpie suggest the answer (first draft); anything in it — typed, picked or Magpie's own
        // suggestion kept as it is — is the answer every rewrite follows.
        var userChoseAnswer = answer.Length > 0;
        var choices = new AiDraftChoices
        {
            WantAnswerLine = !IsForward && ShowAnswerSection,
            Stance = userChoseAnswer || IsForward ? stance : "",
            Answer = userChoseAnswer ? answer : "",
            ForwardReason = IsForward ? (stance.Length > 0 ? stance : "fyi") : "fyi",
            Summary = ForwardSummary,
            MentionAttachments = ForwardMentionFiles,
            AttachmentNames = Attachments.Where(a => a.Carried).Select(a => a.Path).ToList(),
            RecipientName = IsForward ? AiService.FirstName(To) : "",
        };
        var request = ++_replyDraftRequest;
        var fingerprint = _replyContextFingerprint = ReplyFingerprint();
        _replyThreadDigest = context.Digest;
        var cts = _replyDraftCts = new CancellationTokenSource();
        var token = cts.Token;
        var name = string.IsNullOrWhiteSpace(From.DisplayName) ? From.Email : From.DisplayName;
        var mode = _draft.Mode;
        var buffer = new StringBuilder();
        var acceptingTokens = true;
        var summary = Summarise(style, choices.Stance.Length > 0 ? choices.Stance : stance);
        _changedChoices.Clear();
        PendingChanges = 0;
        ReplyDraftBusy = true;
        ReplyDraftNeedsSetup = false;
        ReplyDraftError = "";
        ReplyPanelExpanded = true;
        ReplyDraftStatus = IsForward ? "Using the email being forwarded. Attachment contents are excluded."
            : context.Truncated || context.TextTruncated
            ? $"Using the newest {context.Included} of {context.Total} messages. Attachments are excluded."
            : $"Using {context.Included} message{(context.Included == 1 ? "" : "s")}. Attachments are excluded.";
        bool Current() => !token.IsCancellationRequested && request == _replyDraftRequest
            && !_closed && !_queued && ReplyGateOpen() && fingerprint == ReplyFingerprint();
        try
        {
            var raw = await _e.Ai.ReplyDraftAsync(context, preferences, name,
                text => Ui.Post(() =>
                {
                    if (!acceptingTokens || !Current()) return;
                    buffer.Append(text);
                    var body = AiService.ParseDraft(buffer.ToString(), complete: false).Body;
                    ReplyDraftPreview = IsForward && choices.RecipientName.Length == 0 ? AiService.WithRecipientGreeting(body, AiService.FirstName(To)) : body;
                }), token, mode, choices);
            acceptingTokens = false;
            if (Current())
            {
                if (!ReplyThreadStillCurrent()) { DiscardChangedReplyThread(); return; }
                var parsed = AiService.ParseDraft(raw);
                if (parsed.Body.Length == 0)
                {
                    ReplyDraftError = "The provider returned no text. Try again or write your reply manually.";
                    ShowVersion(_versionIndex);
                    return;
                }
                if (!IsForward && ShowAnswerSection)
                {
                    ReplyAskLine = parsed.Ask.Length > 0 ? "They ask: " + parsed.Ask : "";
                    if (!userChoseAnswer && parsed.Gist.Length > 0)
                    {
                        // Magpie's suggestion fills the box; the user can change it and Rewrite.
                        _suppressChoiceTracking = true;
                        ReplyAnswer = parsed.Gist;
                        ReplyStance = parsed.Answer;
                        _suppressChoiceTracking = false;
                        AnswerIsSuggestion = true;
                        foreach (var c in AnswerChoices) c.IsSelected = c.Key == ReplyStance;
                        summary = Summarise(style, ReplyStance);
                    }
                }
                _versions.Add(new DraftVersion(parsed.Body, summary, IsForward && choices.RecipientName.Length == 0));
                if (_versions.Count > MaxVersions) _versions.RemoveAt(0);
                ShowVersion(_versions.Count - 1);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            acceptingTokens = false;
            if (Current())
            {
                ReplyDraftError = ex is AiException ? ex.Message : "The AI provider could not be reached. Your typed text is kept. Try again or continue manually.";
                if (_versions.Count > 0) ShowVersion(_versionIndex); else ReplyDraftPreview = "";
            }
        }
        finally
        {
            acceptingTokens = false;
            if (request == _replyDraftRequest)
            {
                ReplyDraftBusy = false;
                if (!Current()) { ReplyDraftPreview = ""; _replyContextFingerprint = ""; ClearVersions(); }
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
            ReplyAskLine = "";
            _replyContextFingerprint = "";
            _replyThreadDigest = "";
            ClearVersions();
        }
    }

    [RelayCommand]
    private void CancelReplyDraft()
    {
        StopReplyDraft(clear: false);
        if (_versions.Count > 0) ShowVersion(_versionIndex); else { ReplyDraftPreview = ""; _replyContextFingerprint = ""; }
        ReplyDraftStatus = "Generation stopped. Your typed text is kept.";
    }

    [RelayCommand]
    private void DiscardReplyDraft()
    {
        StopReplyDraft(clear: true);
        ReplyDraftStatus = "AI draft discarded. Your typed text is kept.";
    }

    /// <summary>"Save these choices as my defaults": the only way per-draft choices change Settings.</summary>
    [RelayCommand]
    private void SaveReplyDefaults()
    {
        var style = CurrentStyle();
        if (style.Tone == "Custom" && string.IsNullOrWhiteSpace(style.CustomTone)) { ReplyDraftError = "Write your custom tone first."; return; }
        var settings = _e.Config.Ai.ReplyDrafts;
        settings.Apply(style);
        if (IsReply && !(ReplyPurpose == "Custom" && string.IsNullOrWhiteSpace(ReplyCustomPurpose)))
        {
            settings.Purpose = ReplyPurpose;
            settings.CustomPurpose = ReplyCustomPurpose;
        }
        settings.Normalise();
        try
        {
            _e.Settings.Save();
            ReplyDraftStatus = "Saved as your defaults for new drafts (Settings → AI writing).";
        }
        catch (Exception ex) { ReplyDraftError = "Couldn't save your defaults: " + ex.Message; }
    }

    private void RememberStyleForRecipient()
    {
        var settings = _e.Config.Ai.ReplyDrafts;
        if (!settings.RememberPerContact) return;
        var address = Composer.ParseAddresses(To).Mailboxes.FirstOrDefault()?.Address;
        var style = CurrentStyle();
        // Only a style that differs from the defaults is worth remembering (or one that updates a remembered style).
        if (style.SameAs(settings.Style()) && settings.StyleFor(address) == null) return;
        try
        {
            if (settings.Remember(address, style, DateTimeOffset.UtcNow)) _e.Settings.Save();
        }
        catch (Exception ex) { Log.Warn("AI writing: couldn't remember the style for a contact: " + ex.GetType().Name); }
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
            // Forward: the greeting may have gained a name since the preview was read.
            text = ReplyDraftPreview;
            await EditorCommand("replyDraft", JsonSerializer.Serialize(new { text, expectedNewText = typed, mode }));
            MarkEdited();
            RememberStyleForRecipient();
            StopReplyDraft(clear: true);
            ReplyDraftStatus = "AI draft inserted. Review and edit it before sending.";
        }
        catch (Exception ex) { ReplyDraftError = ex.Message; }
    }
}
