using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Magpie.Core.Mail;
using Magpie.App.Services;
using MimeKit;

namespace Magpie.App.Views;

/// <summary>
/// Shows reviewed recipients as removable chips when an address field is not being edited.
/// The existing TextBox and contact suggestions remain the source of truth while editing.
/// </summary>
public partial class ComposeWindow
{
    private const string ComposeRecipientDragFormat = "Magpie.Compose.RecipientChip";
    private readonly Guid _recipientDragOwner = Guid.NewGuid();
    private Point? _recipientDragStart;
    private sealed record RecipientChipDrag(Guid Owner, string Source, string Address);

    private string? RecipientField(object sender) =>
        ReferenceEquals(sender, ToBox) || ReferenceEquals(sender, ToChipPanel) ? "to" :
        ReferenceEquals(sender, CcBox) || ReferenceEquals(sender, CcChipPanel) ? "cc" :
        ReferenceEquals(sender, BccBox) || ReferenceEquals(sender, BccChipPanel) ? "bcc" : null;

    private void OnRecipientChipDragStart(object sender, MouseButtonEventArgs e)
    {
        _recipientDragStart = e.GetPosition(this);
    }

    private void OnRecipientChipDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _recipientDragStart is not { } origin
            || sender is not Border { Tag: RecipientChipDrag chip }) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _recipientDragStart = null;
        _vm.ShowCcBcc = true; // Make both drop targets visible before dragging.
        var data = new DataObject(ComposeRecipientDragFormat, chip);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void OnRecipientDragOver(object sender, DragEventArgs e)
    {
        var target = RecipientField(sender);
        var chip = e.Data.GetDataPresent(ComposeRecipientDragFormat)
            ? e.Data.GetData(ComposeRecipientDragFormat) as RecipientChipDrag : null;
        e.Effects = chip is { Owner: var owner } && owner == _recipientDragOwner && target != null && target != chip.Source
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRecipientDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var target = RecipientField(sender);
        if (target == null || !e.Data.GetDataPresent(ComposeRecipientDragFormat)
            || e.Data.GetData(ComposeRecipientDragFormat) is not RecipientChipDrag chip
            || chip.Owner != _recipientDragOwner || chip.Source == target) return;
        var result = ComposeSafety.MoveRecipientBetweenFields(_vm.To, _vm.Cc, _vm.Bcc,
            chip.Address, chip.Source, target);
        if (!result.Moved)
        {
            Ui.Error("Move recipient", "Check the recipient fields for invalid addresses, then try again.", this);
            return;
        }
        _vm.To = result.To;
        _vm.Cc = result.Cc;
        _vm.Bcc = result.Bcc;
        RefreshAllRecipientChips();
    }

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
                Tag = new RecipientChipDrag(_recipientDragOwner, ReferenceEquals(box, ToBox) ? "to" : ReferenceEquals(box, CcBox) ? "cc" : "bcc", address),
                ToolTip = "Drag to To, Cc or Bcc · " + address,
            };
            border.PreviewMouseLeftButtonDown += OnRecipientChipDragStart;
            border.PreviewMouseMove += OnRecipientChipDragMove;
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

    private void OnPickContacts(object sender, RoutedEventArgs e)
    {
        var box = (sender as FrameworkElement)?.Tag switch
        {
            "to" => ToBox,
            "cc" => CcBox,
            "bcc" => BccBox,
            _ => null,
        };
        if (box == null) return;
        var picked = ComposeContactPicker.Pick(this);
        if (picked.Count == 0) return;
        var seen = Composer.ParseAddresses(box.Text).Mailboxes.Select(mailbox => mailbox.Address)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var valid = new List<string>();
        var invalid = 0;
        foreach (var contact in picked)
        {
            if (Composer.InvalidAddresses(contact.Email).Count > 0) { invalid++; continue; }
            if (!seen.Add(contact.Email)) continue;
            valid.Add(new MailboxAddress(contact.Name, contact.Email).ToString());
        }
        if (invalid > 0)
            Ui.Error("Contacts", $"{invalid} contact address(es) are invalid and were not added.", this);
        if (valid.Count == 0) return;
        var existingText = box.Text.Trim().TrimEnd(',', ';');
        box.Text = existingText.Length == 0 ? string.Join(", ", valid) : existingText + ", " + string.Join(", ", valid);
        RefreshRecipientChips(box);
    }
}
