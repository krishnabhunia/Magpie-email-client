using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.ViewModels;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>Shows the actual expanded template before a deliberate, reversible editor insertion.</summary>
public sealed class TemplatePreviewDialog : Window
{
    private readonly ReviewedQuickTemplate _review;
    private readonly CheckBox _replaceSubject;
    private readonly TextBox _resultingSubject;

    public bool ReplaceSubject => _replaceSubject.IsChecked == true;

    public TemplatePreviewDialog(ReviewedQuickTemplate review)
    {
        _review = review;
        Title = "Review template — Magpie";
        Width = 680;
        Height = 570;
        MinWidth = 460;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Window.Dialog");

        var root = new Grid { Margin = new Thickness(20) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto,
                     GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star),
                     GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });

        void Add(UIElement element, int row)
        {
            Grid.SetRow(element, row);
            root.Children.Add(element);
        }

        Add(new TextBlock
        {
            Text = $"Review \"{review.Name}\" before inserting it into the message.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        }, 0);
        Add(new TextBlock
        {
            Text = "Subject after insertion",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
        }, 1);
        _resultingSubject = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 9),
        };
        _resultingSubject.SetResourceReference(StyleProperty, "TextBox.Field");
        AutomationProperties.SetName(_resultingSubject, "Subject after template insertion, read only");
        Add(_resultingSubject, 2);

        _replaceSubject = new CheckBox
        {
            Content = "Replace my current subject with the template subject",
            IsEnabled = review.Expanded.WouldReplaceSubject(review.Subject),
            Margin = new Thickness(0, 0, 0, 13),
        };
        AutomationProperties.SetName(_replaceSubject, "Replace current subject when inserting template");
        _replaceSubject.Checked += (_, _) => UpdateSubject();
        _replaceSubject.Unchecked += (_, _) => UpdateSubject();
        Add(_replaceSubject, 3);
        UpdateSubject();

        Add(new TextBlock
        {
            Text = "Text to insert at the cursor",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5),
        }, 4);
        Add(new TextBlock
        {
            Text = "Placeholders have been filled from the first To recipient, selected sender and current date where available.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        }, 5);
        var body = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Text = review.Expanded.Body,
            Margin = new Thickness(0, 0, 0, 10),
        };
        AutomationProperties.SetName(body, "Expanded template message text, read only");
        Add(body, 6);

        var unresolved = ComposeSafety.UnresolvedQuickTemplateVariables(review.Expanded.Subject + "\n" + review.Expanded.Body);
        var notice = new TextBlock
        {
            Text = unresolved.Count == 0
                ? "The text remains editable after insertion. Nothing is sent automatically."
                : "No value was available for " + string.Join(", ", unresolved)
                  + ". These placeholders remain literal text. Fill them in before sending.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        notice.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        Add(notice, 7);

        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 9, 0) };
        cancel.SetResourceReference(StyleProperty, "Button.Secondary");
        cancel.Click += (_, _) => Close();
        var insert = new Button { Content = "Insert reviewed text", IsDefault = true };
        insert.SetResourceReference(StyleProperty, "Button.Primary");
        insert.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(insert);
        Add(actions, 8);

        Content = root;
    }

    private void UpdateSubject()
    {
        var subject = _review.Expanded.SubjectFor(_review.Subject, ReplaceSubject);
        _resultingSubject.Text = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject;
    }
}
