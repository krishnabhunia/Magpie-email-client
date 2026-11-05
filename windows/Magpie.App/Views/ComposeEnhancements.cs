using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Magpie.App.Services;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>Hybrid CW1 compose controls: Balanced by default, optional Focus and Studio.</summary>
public partial class ComposeWindow
{
    private bool _focusMode;
    private bool _restoreAiRail;
    private bool _restoreAiPanel;

    private void OnToggleFocus(object sender, RoutedEventArgs e)
    {
        _focusMode = !_focusMode;
        if (_focusMode)
        {
            _restoreAiRail = _vm.ShowAiRail;
            _restoreAiPanel = _vm.AiPanelOpen;
            _vm.AiPanelOpen = false;
            _vm.ShowAiRail = false;
            FormattingToolbar.Visibility = Visibility.Collapsed;
            StudioPanel.Visibility = Visibility.Collapsed;
            StudioModeButton.Content = "Studio";
            Editor.MaxWidth = 960;
            Editor.HorizontalAlignment = HorizontalAlignment.Center;
            FocusModeButton.Content = "Exit focus";
        }
        else
        {
            FormattingToolbar.Visibility = Visibility.Visible;
            _vm.ShowAiRail = _restoreAiRail;
            _vm.AiPanelOpen = _restoreAiPanel;
            Editor.MaxWidth = double.PositiveInfinity;
            Editor.HorizontalAlignment = HorizontalAlignment.Stretch;
            FocusModeButton.Content = "Focus";
        }
    }

    private void OnToggleStudio(object sender, RoutedEventArgs e)
    {
        if (_focusMode) OnToggleFocus(sender, e);
        StudioPanel.Visibility = StudioPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        StudioModeButton.Content = StudioPanel.Visibility == Visibility.Visible ? "Close studio" : "Studio";
    }

    private async void OnFontChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null || FontFamilyChoice.SelectedItem is not ComboBoxItem item) return;
        await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('fontName', {JsonSerializer.Serialize(item.Content?.ToString())})");
    }

    private async void OnFontSizeChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null || FontSizeChoice.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Content?.ToString(), out var px)) return;
        // The editor's contenteditable surface uses HTML's seven font-size steps. The
        // per-role typography settings continue to control the exact 18 px default.
        var step = px switch { <= 12 => 2, <= 16 => 3, <= 20 => 4, <= 24 => 5, _ => 6 };
        await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('fontSize', '{step}')");
    }

    private async void OnInsertTable(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        const string html = "<table border=\"1\" cellpadding=\"5\" style=\"border-collapse:collapse\"><tbody><tr><td>Item</td><td>Status</td></tr><tr><td><br></td><td><br></td></tr></tbody></table><p><br></p>";
        await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('insertHTML', {JsonSerializer.Serialize(html)})");
    }

    private async void OnInsertImage(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert picture into email",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif|All files|*.*",
        };
        if (picker.ShowDialog(this) != true) return;
        var extension = System.IO.Path.GetExtension(picker.FileName).ToLowerInvariant();
        var mime = extension switch { ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", _ => "" };
        if (mime.Length == 0) { Ui.Error("Insert picture", "Choose a PNG, JPG or GIF image.", this); return; }
        var file = new FileInfo(picker.FileName);
        if (file.Length > 8 * 1024 * 1024)
        {
            Ui.Error("Insert picture", "This picture is larger than 8 MB. Reduce its size or attach it as a file.", this);
            return;
        }
        try
        {
            var data = "data:" + mime + ";base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName));
            await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('insertImage', {JsonSerializer.Serialize(data)})");
        }
        catch (Exception ex) { Ui.Error("Insert picture", ex.Message, this); }
    }

    private void OnInsertEmoji(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady) return;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        foreach (var emoji in new[] { "🙂", "👍", "✅", "🙏", "📎", "⭐", "❤️", "🎉", "→", "₹" })
        {
            var item = new MenuItem { Header = emoji };
            item.Click += async (_, _) => await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('insertText', {JsonSerializer.Serialize(emoji)})");
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnMoreFormat(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady) return;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        foreach (var (label, command, value) in new (string, string, string?)[]
                 {
                     ("Heading 1", "formatBlock", "h1"), ("Heading 2", "formatBlock", "h2"),
                     ("Block quote", "formatBlock", "blockquote"), ("Horizontal line", "insertHorizontalRule", null),
                     ("Superscript", "superscript", null), ("Subscript", "subscript", null),
                     ("Align right", "justifyRight", null), ("Justify", "justifyFull", null),
                 })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await Editor.CoreWebView2.ExecuteScriptAsync($"fmt({JsonSerializer.Serialize(command)}, {JsonSerializer.Serialize(value)})");
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void OnReview(object sender, RoutedEventArgs e)
    {
        var body = _vm.GetNewText != null ? await _vm.GetNewText() : "";
        var missing = ComposeSafety.MissingFiles(_vm.Attachments.Where(a => !a.Carried).Select(a => a.Path));
        var duplicates = ComposeSafety.DuplicateRecipients(_vm.To, _vm.Cc, _vm.Bcc);
        var encoded = ComposeSafety.EncodedSizeOfExistingFiles(_vm.Attachments.Where(a => !a.Carried).Select(a => a.Path), "");
        var notes = new List<string>
        {
            "Recipients: " + Composer.ParseAddresses(_vm.To + "," + _vm.Cc + "," + _vm.Bcc).Mailboxes.Count(),
            "Attachments: " + _vm.Attachments.Count + ", approximately " + HtmlRenderer.FormatSize(encoded) + " encoded (plus any forwarded files)",
            "Priority: " + _vm.Priority,
        };
        if (missing.Count > 0) notes.Add("MISSING: " + string.Join(", ", missing.Select(Path.GetFileName)));
        if (duplicates.Count > 0) notes.Add("DUPLICATES: " + string.Join(", ", duplicates));
        if (ComposeSafety.MentionsAttachment(body) && _vm.Attachments.Count == 0) notes.Add("Possibly forgotten attachment");
        if (string.IsNullOrWhiteSpace(_vm.Subject)) notes.Add("No subject");
        if (string.IsNullOrWhiteSpace(body)) notes.Add("No new body text");
        MessageBox.Show(this, string.Join("\n\n", notes), "Compose review", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
