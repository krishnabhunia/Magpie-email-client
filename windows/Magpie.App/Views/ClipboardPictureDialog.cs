using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Magpie.App.Views;

public enum ClipboardPictureChoice { Cancel, Inline, Attachment }

/// <summary>Explicitly choose the outgoing representation of a pasted picture.</summary>
public sealed class ClipboardPictureDialog : Window
{
    public ClipboardPictureChoice Choice { get; private set; }

    public ClipboardPictureDialog(bool canInsertInline)
    {
        Title = "Paste picture — Magpie";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Window.Dialog");
        var root = new StackPanel { Margin = new Thickness(22) };
        root.Children.Add(new TextBlock
        {
            Text = "How would you like to include this picture?",
            FontSize = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(new TextBlock
        {
            Text = canInsertInline
                ? "Insert it into the message body, or save a PNG copy and attach that file. Nothing will be sent."
                : "Inline pictures need the rich-text editor. You can save a PNG copy and attach it without changing the message body.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18),
        });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        Button Add(string label, ClipboardPictureChoice choice, bool enabled = true)
        {
            var button = new Button { Content = label, IsEnabled = enabled, Margin = new Thickness(0, 0, 8, 0) };
            button.SetResourceReference(StyleProperty, choice == ClipboardPictureChoice.Inline ? "Button.Primary" : "Button.Secondary");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { Choice = choice; DialogResult = choice != ClipboardPictureChoice.Cancel; };
            actions.Children.Add(button);
            return button;
        }
        Add("Insert inline", ClipboardPictureChoice.Inline, canInsertInline);
        Add("Save and attach PNG…", ClipboardPictureChoice.Attachment);
        Add("Cancel", ClipboardPictureChoice.Cancel).IsCancel = true;
        root.Children.Add(actions);
        Content = root;
    }
}
