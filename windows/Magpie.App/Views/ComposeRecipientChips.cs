using System.Windows;
using System.Windows.Controls;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>
/// Shows reviewed recipients as removable chips when an address field is not being edited.
/// The existing TextBox and contact suggestions remain the source of truth while editing.
/// </summary>
public partial class ComposeWindow
{
    private void RefreshAllRecipientChips()
    {
        RefreshRecipientChips(ToBox);
        RefreshRecipientChips(CcBox);
        RefreshRecipientChips(BccBox);
    }

    private (WrapPanel panel, Button edit) RecipientParts(TextBox box) =>
        ReferenceEquals(box, ToBox) ? (ToChipPanel, ToEditButton) :
        ReferenceEquals(box, CcBox) ? (CcChipPanel, CcEditButton) :
        (BccChipPanel, BccEditButton);

    private void RefreshRecipientChips(TextBox box)
    {
        var (panel, edit) = RecipientParts(box);
        panel.Children.Clear();
        // Invalid tokens remain visible in the editable field; do not hide or silently discard them.
        if (Composer.InvalidAddresses(box.Text).Count > 0)
        {
            box.ToolTip = "Correct the invalid email addresses shown here before sending.";
            box.Visibility = Visibility.Visible;
            panel.Visibility = Visibility.Collapsed;
            edit.Visibility = Visibility.Collapsed;
            return;
        }
        box.ToolTip = null;
        var recipients = Composer.ParseAddresses(box.Text).Mailboxes.ToList();
        if (recipients.Count == 0)
        {
            box.Visibility = Visibility.Visible;
            panel.Visibility = Visibility.Collapsed;
            edit.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (var recipient in recipients)
        {
            var address = recipient.Address;
            var border = new Border
            {
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(8, 3, 4, 3),
                Margin = new Thickness(0, 0, 5, 4),
            };
            border.SetResourceReference(Border.BackgroundProperty, "Brush.Badge.Local.Background");
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(recipient.Name) ? address : recipient.Name,
                ToolTip = address,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Badge.Local.Text");
            line.Children.Add(label);
            var remove = new Button
            {
                Content = "×", ToolTip = "Remove " + address,
                MinWidth = 20, MinHeight = 20,
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(5, 0, 0, 0),
                BorderThickness = new Thickness(0),
            };
            remove.Click += (_, _) =>
            {
                var remaining = Composer.ParseAddresses(box.Text).Mailboxes
                    .Where(candidate => !candidate.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
                box.Text = Composer.FormatList(remaining);
                RefreshRecipientChips(box);
            };
            line.Children.Add(remove);
            border.Child = line;
            panel.Children.Add(border);
        }
        panel.Visibility = Visibility.Visible;
        edit.Visibility = Visibility.Visible;
        box.Visibility = Visibility.Collapsed;
    }

    private void OnEditRecipients(object sender, RoutedEventArgs e)
    {
        var box = ReferenceEquals(sender, ToEditButton) ? ToBox :
            ReferenceEquals(sender, CcEditButton) ? CcBox : BccBox;
        var (panel, edit) = RecipientParts(box);
        panel.Visibility = Visibility.Collapsed;
        edit.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Visible;
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }
}
