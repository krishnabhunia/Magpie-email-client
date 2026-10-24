using CommunityToolkit.Mvvm.ComponentModel;
using Magpie.App.Services;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>FR1: staged preferences for full drafts triggered by Reply/Reply All.</summary>
public partial class SettingsViewModel
{
    [ObservableProperty] private bool _replyDraftsEnabled;
    [ObservableProperty] private bool _replyOnReply = true;
    [ObservableProperty] private bool _replyOnReplyAll = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultTone = "Formal";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultPurpose = "Answer the message";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReplySettingsExample))] private string _replyDefaultLength = "Balanced";
    [ObservableProperty] private string _replyDefaultLanguage = "Match original";
    [ObservableProperty] private string _replyDefaultContext = AiReplySettings.ConversationContext;
    [ObservableProperty] private string _replyInstructions = "";
    [ObservableProperty] private string _replyDefaultCustomTone = "";
    [ObservableProperty] private string _replyDefaultCustomPurpose = "";

    public IReadOnlyList<string> ReplyToneOptions => AiReplySettings.Tones;
    public IReadOnlyList<string> ReplyPurposeOptions => AiReplySettings.Purposes;
    public IReadOnlyList<string> ReplyLengthOptions => AiReplySettings.Lengths;
    public IReadOnlyList<string> ReplyLanguageOptions => AiReplySettings.Languages;
    public IReadOnlyList<string> ReplyContextOptions => AiReplySettings.Contexts;

    // A local illustrative sample only. Editing staged preferences never sends a model request.
    public string ReplySettingsExample
    {
        get
        {
            var greeting = ReplyDefaultTone is "Informal" or "Friendly" or "Warm" ? "Hi Alex," : "Hello Alex,";
            var body = ReplyDefaultPurpose switch
            {
                "Make a request" => "Could you please share the agenda before the meeting?",
                "Give an instruction / order" => "Please share the agenda before the meeting so everyone can prepare.",
                "Follow up" => "I am following up on the agenda. Please let me know when it is available.",
                "Say thank you" => "Thank you for sending the agenda. I appreciate your help with the preparations.",
                "Apologise" => "I am sorry for the delay in replying. Thank you for your patience.",
                "Decline politely" => "Thank you for the invitation. I am unable to attend on this occasion.",
                "Custom" => "Your reply will follow the purpose and instructions you provide.",
                _ => "Thank you for sharing the agenda. I will review the details before replying further.",
            };
            return ReplyDefaultLength == "Short" ? body : greeting + "\n\n" + body
                + (ReplyDefaultLength == "Detailed" ? "\n\nPlease let me know if there are any other details I should review." : "");
        }
    }

    private void LoadAiReplySettings(AiReplySettings settings)
    {
        ReplyDraftsEnabled = settings.Enabled;
        ReplyOnReply = settings.OnReply;
        ReplyOnReplyAll = settings.OnReplyAll;
        ReplyDefaultTone = settings.Tone;
        ReplyDefaultPurpose = settings.Purpose;
        ReplyDefaultLength = settings.Length;
        ReplyDefaultLanguage = settings.Language;
        ReplyDefaultContext = settings.Context;
        ReplyInstructions = settings.Instructions;
        ReplyDefaultCustomTone = settings.CustomTone;
        ReplyDefaultCustomPurpose = settings.CustomPurpose;
    }

    private AiReplySettings AiRepliesToSave() => new()
    {
        Enabled = ReplyDraftsEnabled, OnReply = ReplyOnReply, OnReplyAll = ReplyOnReplyAll,
        Tone = ReplyDefaultTone, Purpose = ReplyDefaultPurpose, Length = ReplyDefaultLength,
        Language = ReplyDefaultLanguage, Context = ReplyDefaultContext, Instructions = ReplyInstructions.Trim(),
        CustomTone = ReplyDefaultCustomTone.Trim(), CustomPurpose = ReplyDefaultCustomPurpose.Trim(),
    };

    private bool ValidateAiReplySettings()
    {
        if (!ReplyDraftsEnabled) return true;
        var settings = AiRepliesToSave();
        settings.Normalise();
        if (settings.ValidationError() is not { } error) return true;
        Ui.Error("AI reply settings", error);
        GoTo("AiReplies");
        return false;
    }
}
