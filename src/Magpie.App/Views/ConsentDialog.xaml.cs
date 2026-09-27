using System.Windows;
using Magpie.App.Services;
using Magpie.Core.Ai;
using Magpie.Core.Settings;

namespace Magpie.App.Views;

/// <summary>First-use consent (design B2). Asked once per feature and provider host; never for local models.</summary>
public partial class ConsentDialog : Window
{
    public ConsentDialog() { InitializeComponent(); }

    public static bool Ask(AiFeature feature, int messageCount)
    {
        var e = AppServices.Engine;
        if (!e.Ai.NeedsConsent(feature)) return true;
        var d = new ConsentDialog { Owner = Ui.ActiveWindow };
        (d.Heading.Text, d.Intro.Text, d.WhatSent.Text) = feature switch
        {
            AiFeature.Summarise => ("Summarise with your AI provider?",
                "To summarise, Magpie sends this conversation to the AI provider you set up.",
                $"The text of the conversation ({messageCount} message{(messageCount == 1 ? "" : "s")}) with names, addresses and dates. Quoted history and attachments are not sent."),
            AiFeature.Draft => ("Write drafts with your AI provider?",
                "To write a draft, Magpie sends your instruction to the AI provider you set up.",
                "Your instruction and your name. When you are replying, also the conversation you are replying to (text only, no attachments) — this permission covers both."),
            AiFeature.Rewrite => ("Rewrite text with your AI provider?",
                "To rewrite, Magpie sends the text you selected to the AI provider you set up.",
                "Only the selected text and the kind of change you asked for."),
            _ => ("Suggest replies with your AI provider?",
                "To suggest replies, Magpie sends the conversation you open to the AI provider you set up.",
                "The text of the conversation you are reading (no attachments). This happens each time you open a conversation from a person."),
        };
        d.Where.Text = $"{e.Ai.ProviderLabel}. Its own privacy terms apply.";
        if (d.ShowDialog() != true) return false;
        var s = e.Config.Ai;
        var key = AiService.ConsentKey(feature, s);
        if (!s.Consents.Contains(key)) s.Consents.Add(key);
        e.Settings.Save();
        return true;
    }

    private void OnAllow(object sender, RoutedEventArgs e) => DialogResult = true;
}
