using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core.Ai;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>FR1 + AW1: staged preferences for full drafts triggered by Reply / Reply All / Forward (Settings → AI writing).</summary>
public partial class SettingsViewModel
{
    [ObservableProperty] private bool _replyDraftsEnabled;
    [ObservableProperty] private bool _replyOnReply = true;
    [ObservableProperty] private bool _replyOnReplyAll = true;
    [ObservableProperty] private bool _replyOnForward;
    [ObservableProperty] private bool _replyShowStance = true;
    [ObservableProperty] private bool _replyRememberPerContact;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultTone = "Professional";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultPurpose = "Answer the message";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultLength = "Balanced";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultLanguage = "Match original";
    [ObservableProperty] private string _replyDefaultContext = AiReplySettings.ConversationContext;
    [ObservableProperty] private string _replyInstructions = "";
    [ObservableProperty] private string _replyDefaultCustomTone = "";
    [ObservableProperty] private string _replyDefaultCustomPurpose = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultFormality = "Formal";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultEnglishStyle = "Indian English";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultEtiquette = "Neutral";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultDirectness = "Balanced";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultGreeting = "Auto";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultAddress = "Auto";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultEmoji = "Never";

    public IReadOnlyList<string> ReplyToneOptions => AiReplySettings.Tones;
    public IReadOnlyList<string> ReplyPurposeOptions => AiReplySettings.Purposes;
    public IReadOnlyList<string> ReplyLengthOptions => AiReplySettings.Lengths;
    public IReadOnlyList<string> ReplyLanguageOptions => AiReplySettings.Languages;
    public IReadOnlyList<string> ReplyContextOptions => AiReplySettings.Contexts;
    public IReadOnlyList<string> ReplyFormalityOptions => AiReplySettings.Formalities;
    public IReadOnlyList<string> ReplyEnglishStyleOptions => AiReplySettings.EnglishStyles;
    public IReadOnlyList<string> ReplyEtiquetteOptions => AiReplySettings.Etiquettes;
    public IReadOnlyList<string> ReplyDirectnessOptions => AiReplySettings.Directnesses;
    public IReadOnlyList<string> ReplyGreetingOptions => AiReplySettings.Greetings;
    public IReadOnlyList<string> ReplyAddressOptions => AiReplySettings.Addresses;
    public IReadOnlyList<string> ReplyEmojiOptions => AiReplySettings.EmojiChoices;

    /// <summary>AW1: the five-step formality scale, shown as segments.</summary>
    public ObservableCollection<AiChoice> DefaultFormalityChips { get; } = new(AiReplySettings.Formalities.Select(f => new AiChoice(f, f)));

    [RelayCommand] private void PickDefaultFormality(AiChoice? choice) { if (choice != null) ReplyDefaultFormality = choice.Key; }

    partial void OnReplyDefaultFormalityChanged(string value) { foreach (var c in DefaultFormalityChips) c.IsSelected = c.Key == value; }

    // A local illustrative sample only. Editing staged preferences never sends a model request.
    public string ReplySettingsExample => AiWritingExample.Build(AiRepliesToSave());

    private void LoadAiReplySettings(AiReplySettings settings)
    {
        ReplyDraftsEnabled = settings.Enabled;
        ReplyOnReply = settings.OnReply;
        ReplyOnReplyAll = settings.OnReplyAll;
        ReplyOnForward = settings.OnForward;
        ReplyShowStance = settings.ShowStance;
        ReplyRememberPerContact = settings.RememberPerContact;
        ReplyDefaultTone = settings.Tone;
        ReplyDefaultPurpose = settings.Purpose;
        ReplyDefaultLength = settings.Length;
        ReplyDefaultLanguage = settings.Language;
        ReplyDefaultContext = settings.Context;
        ReplyInstructions = settings.Instructions;
        ReplyDefaultCustomTone = settings.CustomTone;
        ReplyDefaultCustomPurpose = settings.CustomPurpose;
        ReplyDefaultFormality = settings.Formality;
        ReplyDefaultEnglishStyle = settings.EnglishStyle;
        ReplyDefaultEtiquette = settings.Etiquette;
        ReplyDefaultDirectness = settings.Directness;
        ReplyDefaultGreeting = settings.Greeting;
        ReplyDefaultAddress = settings.Address;
        ReplyDefaultEmoji = settings.Emoji;
        OnReplyDefaultFormalityChanged(ReplyDefaultFormality);
    }

    private AiReplySettings AiRepliesToSave() => new()
    {
        Enabled = ReplyDraftsEnabled, OnReply = ReplyOnReply, OnReplyAll = ReplyOnReplyAll, OnForward = ReplyOnForward,
        ShowStance = ReplyShowStance, RememberPerContact = ReplyRememberPerContact,
        Tone = ReplyDefaultTone, Purpose = ReplyDefaultPurpose, Length = ReplyDefaultLength,
        Language = ReplyDefaultLanguage, Context = ReplyDefaultContext, Instructions = ReplyInstructions.Trim(),
        CustomTone = ReplyDefaultCustomTone.Trim(), CustomPurpose = ReplyDefaultCustomPurpose.Trim(),
        Formality = ReplyDefaultFormality, EnglishStyle = ReplyDefaultEnglishStyle, Etiquette = ReplyDefaultEtiquette,
        Directness = ReplyDefaultDirectness, Greeting = ReplyDefaultGreeting, Address = ReplyDefaultAddress, Emoji = ReplyDefaultEmoji,
        // Remembered per-contact styles are written by the composer as they come; Apply keeps whatever is stored now.
        // Turning the switch off forgets every remembered style.
        ContactStyles = !ReplyRememberPerContact ? new() : (_e.Config.Ai.ReplyDrafts?.ContactStyles ?? new()).ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.OrdinalIgnoreCase),
    };

    private bool ValidateAiReplySettings()
    {
        if (!ReplyDraftsEnabled) return true;
        var settings = AiRepliesToSave();
        settings.Normalise();
        if (settings.ValidationError() is not { } error) return true;
        Ui.Error("AI writing", error);
        GoTo("AiReplies");
        return false;
    }
}
