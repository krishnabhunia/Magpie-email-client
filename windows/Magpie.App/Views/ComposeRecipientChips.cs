using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Magpie.Core;
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
    private HoverCard? _recipientHover;
    private sealed record RecipientToken(int Start, int Length, string Raw, string Name, string Address, bool IsValid);
    private sealed record RecipientChipDrag(Guid Owner, string Source, string SourceText, RecipientToken Token);

    private TextBox RecipientBox(string field) => field switch { "cc" => CcBox, "bcc" => BccBox, _ => ToBox };

    // Preserve source offsets and raw text. Parsing and reformatting the whole field would silently drop
    // incomplete addresses when the user removes a neighbouring valid chip.
    private static List<RecipientToken> RecipientTokens(string text)
    {
        var result = new List<RecipientToken>();
        var quoted = false;
        var escaped = false;
        var start = 0;
        void Add(int end)
        {
            var first = start;
            while (first < end && char.IsWhiteSpace(text[first])) first++;
            while (end > first && char.IsWhiteSpace(text[end - 1])) end--;
            if (end == first) return;
            var raw = text[first..end];
            var valid = MailboxAddress.TryParse(raw, out var mailbox) && Composer.InvalidAddresses(raw).Count == 0;
            result.Add(new RecipientToken(first, end - first, raw,
                valid ? mailbox!.Name ?? "" : "", valid ? mailbox!.Address : "", valid));
        }
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '"' && !escaped) quoted = !quoted;
            if (!quoted && ch is ',' or ';' or '\r' or '\n') { Add(index); start = index + 1; }
            escaped = ch == '\\' && !escaped;
        }
        Add(text.Length);
        return result;
    }

    private void OnShowRecipientRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string field || !_vm.SetRecipientRowVisible(field, true)) return;
        EditRecipientField(RecipientBox(field));
    }

    private void OnHideRecipientRow(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string field && _vm.SetRecipientRowVisible(field, false))
            _recipientHover?.Close();
    }

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
            || sender is not Border { Tag: RecipientChipDrag chip } || !chip.Token.IsValid) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _recipientDragStart = null;
        _vm.SetRecipientRowVisible("cc", true); // Make both drop targets visible before dragging.
        _vm.SetRecipientRowVisible("bcc", true);
        _recipientHover?.Close();
        var data = new DataObject(ComposeRecipientDragFormat, chip);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void OnRecipientDragOver(object sender, DragEventArgs e)
    {
        var target = RecipientField(sender);
        var chip = e.Data.GetDataPresent(ComposeRecipientDragFormat)
            ? e.Data.GetData(ComposeRecipientDragFormat) as RecipientChipDrag : null;
        e.Effects = chip is { Owner: var owner } && chip.Token.IsValid && owner == _recipientDragOwner && target != null && target != chip.Source
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
        MoveRecipientChip(chip, target);
    }

    private bool MoveRecipientChip(RecipientChipDrag chip, string target)
    {
        if (!chip.Token.IsValid || chip.Owner != _recipientDragOwner || RecipientBox(chip.Source).Text != chip.SourceText) return false;
        var result = ComposeSafety.MoveRecipientBetweenFields(_vm.To, _vm.Cc, _vm.Bcc,
            chip.Token.Address, chip.Source, target);
        if (!result.Moved)
        {
            Ui.Error("Move recipient", "Check the recipient fields for invalid addresses, then try again.", this);
            return false;
        }
        _recipientHover?.Close();
        if (target is "cc" or "bcc") _vm.SetRecipientRowVisible(target, true);
        _vm.To = result.To;
        _vm.Cc = result.Cc;
        _vm.Bcc = result.Bcc;
        RefreshAllRecipientChips();
        return true;
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
        _recipientHover?.Close();
        panel.Children.Clear();
        UpdateRecipientValidation(box);
        var tokens = RecipientTokens(box.Text);
        if (tokens.Count == 0)
        {
            box.Visibility = Visibility.Visible;
            panel.Visibility = Visibility.Collapsed;
            edit.Visibility = Visibility.Collapsed;
            return;
        }
        _recipientHover ??= new HoverCard(BuildRecipientHover);
        var source = RecipientField(box)!;
        var sourceText = box.Text;
        foreach (var token in tokens)
        {
            var chip = new RecipientChipDrag(_recipientDragOwner, source, sourceText, token);
            var border = new Border
            {
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(8, 3, 4, 3),
                Margin = new Thickness(0, 0, 5, 4),
                BorderThickness = new Thickness(1),
                Tag = chip,
            };
            border.PreviewMouseLeftButtonDown += OnRecipientChipDragStart;
            border.PreviewMouseMove += OnRecipientChipDragMove;
            border.SetResourceReference(Border.BackgroundProperty, token.IsValid ? "Brush.Badge.Local.Background" : "Brush.Danger.Subtle");
            border.SetResourceReference(Border.BorderBrushProperty, token.IsValid ? "Brush.Card.Border" : "Brush.Danger");
            AutomationProperties.SetName(border, token.IsValid ? "Recipient " + token.Address : "Invalid recipient " + token.Raw);
            _recipientHover.Attach(border);
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var label = new TextBlock
            {
                Text = token.IsValid ? (string.IsNullOrWhiteSpace(token.Name) ? token.Address : token.Name) : "⚠ " + token.Raw,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, token.IsValid ? "Brush.Badge.Local.Text" : "Brush.Danger");
            var editChip = new Button
            {
                Content = label, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), MinHeight = 22, Tag = chip,
            };
            AutomationProperties.SetName(editChip, token.IsValid ? "Edit recipient " + token.Address : "Correct invalid recipient " + token.Raw);
            AutomationProperties.SetHelpText(editChip, "Activate to edit. Alt+Down opens contact details. Delete removes only this recipient.");
            editChip.Click += (_, _) => EditRecipientChip(chip);
            editChip.PreviewKeyDown += (_, e) =>
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                if (key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)
                { e.Handled = true; _recipientHover.Show(border, focus: true); }
                else if (key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
                { e.Handled = true; RemoveRecipientChip(chip); }
            };
            line.Children.Add(editChip);
            var remove = new Button
            {
                Content = "×", ToolTip = "Remove " + (token.IsValid ? token.Address : token.Raw),
                MinWidth = 20, MinHeight = 20,
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(5, 0, 0, 0),
                BorderThickness = new Thickness(0),
            };
            AutomationProperties.SetName(remove, "Remove recipient " + (token.IsValid ? token.Address : token.Raw));
            remove.Click += (_, _) => RemoveRecipientChip(chip);
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
        EditRecipientField(box);
    }

    private void EditRecipientField(TextBox box, RecipientToken? token = null)
    {
        _recipientHover?.Close();
        var (panel, edit) = RecipientParts(box);
        panel.Visibility = Visibility.Collapsed;
        edit.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Visible;
        if (!box.Focus())
            Dispatcher.BeginInvoke(new Action(() => { if (box.IsVisible) box.Focus(); }), System.Windows.Threading.DispatcherPriority.Input);
        if (token != null) box.Select(token.Start, token.Length);
        else box.CaretIndex = box.Text.Length;
    }

    private void EditRecipientChip(RecipientChipDrag chip)
    {
        var box = RecipientBox(chip.Source);
        EditRecipientField(box, box.Text == chip.SourceText ? chip.Token : null);
    }

    private void RemoveRecipientChip(RecipientChipDrag chip)
    {
        var box = RecipientBox(chip.Source);
        if (box.Text != chip.SourceText) { RefreshRecipientChips(box); return; }
        var tokens = RecipientTokens(box.Text);
        var index = tokens.FindIndex(token => token.Start == chip.Token.Start && token.Length == chip.Token.Length);
        if (index < 0) return;
        var start = chip.Token.Start;
        var end = start + chip.Token.Length;
        if (index + 1 < tokens.Count) end = tokens[index + 1].Start;
        else if (index > 0) start = tokens[index - 1].Start + tokens[index - 1].Length;
        box.Text = box.Text.Remove(start, end - start);
        RefreshRecipientChips(box);
    }

    private void UpdateRecipientValidation(TextBox box)
    {
        var invalid = RecipientTokens(box.Text).Count(token => !token.IsValid);
        if (invalid > 0) box.SetResourceReference(Control.BorderBrushProperty, "Brush.Danger");
        else box.ClearValue(Control.BorderBrushProperty);
        var help = invalid > 0 ? $"{invalid} invalid or incomplete address(es). Correct the red recipients before sending." : null;
        box.ToolTip = help;
        AutomationProperties.SetHelpText(box, help ?? "Type recipient addresses separated by commas, semicolons or newlines.");
    }

    private UIElement? BuildRecipientHover(FrameworkElement target)
    {
        if (target.Tag is not RecipientChipDrag chip) return null;
        var token = chip.Token;
        var content = new StackPanel { MaxWidth = 300 };
        void Line(string text, bool heading = false, bool danger = false)
        {
            var line = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 0, 5),
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, danger ? "Brush.Danger" : heading ? "Brush.Text.Primary" : "Brush.Text.Secondary");
            content.Children.Add(line);
        }
        if (!token.IsValid)
        {
            Line("Invalid or incomplete email address", heading: true, danger: true);
            Line(token.Raw);
            Line("This text is kept exactly as entered. Correct it before sending.");
        }
        else
        {
            Line(string.IsNullOrWhiteSpace(token.Name) ? token.Address : token.Name, heading: true);
            if (!string.IsNullOrWhiteSpace(token.Name)) Line(token.Address);
            try
            {
                var engine = AppServices.Engine;
                var saved = engine.Contacts.All().FirstOrDefault(contact => contact.Emails.Contains(token.Address, StringComparer.OrdinalIgnoreCase));
                var person = engine.Store.MailPeople().FirstOrDefault(contact => contact.Address.Equals(token.Address, StringComparison.OrdinalIgnoreCase));
                Line(saved == null ? "Not saved in contacts" : "Saved contact · " + saved.Display);
                if (!string.IsNullOrWhiteSpace(saved?.Work)) Line(saved.Work);
                if (saved?.Groups.Count > 0) Line("Groups: " + string.Join(", ", saved.Groups));
                if (person.Count > 0) Line($"Local contact frequency: {person.Count}");
            }
            catch (Exception ex)
            {
                Log.Warn("recipient hover details: " + ex.Message);
                Line("Saved contact details are currently unavailable.");
            }
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        void AddAction(string label, Action action, bool enabled = true)
        {
            var button = new Button { Content = label, Margin = new Thickness(0, 0, 8, 3), IsEnabled = enabled };
            button.SetResourceReference(StyleProperty, "Button.Link");
            button.Click += (_, _) => action();
            actions.Children.Add(button);
        }
        AddAction("Edit address", () => EditRecipientChip(chip));
        AddAction("Remove", () => RemoveRecipientChip(chip));
        if (token.IsValid)
        {
            AddAction("Copy address", () =>
            {
                try { Clipboard.SetText(token.Address); _recipientHover?.Close(); }
                catch (Exception ex) { Ui.Error("Copy address", ex.Message, this); }
            });
            var movable = Composer.InvalidAddresses(_vm.To).Count == 0 && Composer.InvalidAddresses(_vm.Cc).Count == 0
                && Composer.InvalidAddresses(_vm.Bcc).Count == 0;
            foreach (var field in new[] { "to", "cc", "bcc" }.Where(field => field != chip.Source))
                AddAction("Move to " + (field == "to" ? "To" : field == "cc" ? "Cc" : "Bcc"), () => MoveRecipientChip(chip, field), movable);
        }
        content.Children.Add(actions);
        return content;
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
