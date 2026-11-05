using System.Text.Json;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Magpie.Core;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core.Mail;

namespace Magpie.App.Views;

/// <summary>Hybrid CW1 compose controls: Balanced by default, optional Focus and Studio.</summary>
public partial class ComposeWindow
{
    private bool _focusMode;
    private bool _restoreAiRail;
    private bool _restoreAiPanel;
    private bool _studioRestoreAiPanel;

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
            _studioRestoreAiPanel = false;
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
        if (StudioPanel.Visibility == Visibility.Visible)
        {
            StudioPanel.Visibility = Visibility.Collapsed;
            StudioModeButton.Content = "Studio";
            if (_studioRestoreAiPanel) _vm.AiPanelOpen = true;
            _studioRestoreAiPanel = false;
        }
        else
        {
            // Keep the message readable at the minimum window size: show either
            // the AI assistant or the Studio sidebar, never both side by side.
            _studioRestoreAiPanel = _vm.AiPanelOpen;
            _vm.AiPanelOpen = false;
            StudioPanel.Visibility = Visibility.Visible;
            StudioModeButton.Content = "Close studio";
        }
    }

    private void OnAiPanelVisibilityChanged()
    {
        if (!_vm.AiPanelOpen || StudioPanel.Visibility != Visibility.Visible) return;
        StudioPanel.Visibility = Visibility.Collapsed;
        StudioModeButton.Content = "Studio";
        _studioRestoreAiPanel = false;
    }

    private async void OnToggleQuote(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        var folded = await Editor.CoreWebView2.ExecuteScriptAsync("toggleQuoteFold()");
        QuoteToggleButton.Content = folded == "true" ? "Show quoted message" : "Hide quoted message";
    }

    private void ShowFindReplace(bool replace)
    {
        FindReplacePanel.Visibility = Visibility.Visible;
        if (replace) ReplaceControls.Visibility = Visibility.Visible;
        FindReplaceStatus.Text = "";
        FindTextBox.Focus();
        FindTextBox.SelectAll();
    }

    private void HideFindReplace()
    {
        FindReplacePanel.Visibility = Visibility.Collapsed;
        if (_plain) PlainEditor.Focus(); else Editor.Focus();
    }

    private void OnFind(object sender, RoutedEventArgs e) => ShowFindReplace(false);

    private void OnCloseFind(object sender, RoutedEventArgs e) => HideFindReplace();

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        if (FindReplaceStatus != null) FindReplaceStatus.Text = "";
    }

    private async void OnFindInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await FindNextAsync();
    }

    private async void OnFindNext(object sender, RoutedEventArgs e) => await FindNextAsync();

    private async Task FindNextAsync()
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query)) { FindReplaceStatus.Text = "Enter text to find"; return; }
        if (query.Length > 200) { FindReplaceStatus.Text = "Find text must be 200 characters or fewer"; return; }
        var comparison = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (_plain)
        {
            var text = PlainEditor.Text;
            var start = PlainEditor.SelectionStart + PlainEditor.SelectionLength;
            var at = text.IndexOf(query, Math.Min(start, text.Length), comparison);
            if (at < 0) at = text.IndexOf(query, comparison);
            if (at < 0) { FindReplaceStatus.Text = "No matches"; return; }
            var count = 0;
            for (var pos = 0; (pos = text.IndexOf(query, pos, comparison)) >= 0; pos += query.Length) count++;
            var number = 1;
            for (var pos = 0; (pos = text.IndexOf(query, pos, comparison)) >= 0 && pos < at; pos += query.Length) number++;
            PlainEditor.Focus();
            PlainEditor.Select(at, query.Length);
            FindReplaceStatus.Text = $"Match {number} of {count}";
            return;
        }
        if (!_editorReady || Editor.CoreWebView2 == null) { FindReplaceStatus.Text = "Editor is loading"; return; }
        var result = await Editor.CoreWebView2.ExecuteScriptAsync($"findInEditor({JsonSerializer.Serialize(query)}, {(comparison == StringComparison.Ordinal ? "true" : "false")})");
        using var doc = JsonDocument.Parse(result);
        var countRich = doc.RootElement.GetProperty("count").GetInt32();
        var index = doc.RootElement.GetProperty("index").GetInt32();
        FindReplaceStatus.Text = countRich < 0 ? "Too many matches; narrow your search" : countRich == 0 ? "No matches" : $"Match {index} of {countRich}";
    }

    private async void OnReplaceOne(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query)) return;
        var matchCase = FindMatchCase.IsChecked == true;
        if (_plain)
        {
            if (!string.Equals(PlainEditor.SelectedText, query, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                await FindNextAsync();
                return;
            }
            PlainEditor.SelectedText = ReplaceTextBox.Text;
        }
        else if (_editorReady && Editor.CoreWebView2 != null)
        {
            var result = await Editor.CoreWebView2.ExecuteScriptAsync($"replaceFoundText({JsonSerializer.Serialize(query)}, {JsonSerializer.Serialize(ReplaceTextBox.Text)}, {(matchCase ? "true" : "false")})");
            if (result != "true") { await FindNextAsync(); return; }
        }
        await FindNextAsync();
    }

    private async void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        var query = FindTextBox.Text;
        if (string.IsNullOrEmpty(query)) { FindReplaceStatus.Text = "Enter text to replace"; return; }
        if (query.Length > 200) { FindReplaceStatus.Text = "Find text must be 200 characters or fewer"; return; }
        var matchCase = FindMatchCase.IsChecked == true;
        if (_plain)
        {
            var regex = new Regex(Regex.Escape(query), matchCase ? RegexOptions.None : RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(500));
            var count = regex.Matches(PlainEditor.Text).Count;
            if (count > 2000) { FindReplaceStatus.Text = "Too many matches; narrow your search"; return; }
            if (count > 0) PlainEditor.Text = regex.Replace(PlainEditor.Text, _ => ReplaceTextBox.Text);
            FindReplaceStatus.Text = $"Replaced {count} match(es)";
        }
        else if (_editorReady && Editor.CoreWebView2 != null)
        {
            var result = await Editor.CoreWebView2.ExecuteScriptAsync($"replaceAllInEditor({JsonSerializer.Serialize(query)}, {JsonSerializer.Serialize(ReplaceTextBox.Text)}, {(matchCase ? "true" : "false")})");
            var count = int.TryParse(result, out var parsed) ? parsed : -1;
            FindReplaceStatus.Text = count < 0 ? "Too many matches; narrow your search" : $"Replaced {count} match(es)";
        }
    }

    private void OnClearAttachments(object sender, RoutedEventArgs e)
    {
        if (_vm.Attachments.Count == 0) return;
        if (!Ui.Confirm("Remove attachments", "Remove every attached file from this message?")) return;
        foreach (var attachment in _vm.Attachments.ToArray())
            _vm.RemoveAttachmentCommand.Execute(attachment);
    }

    private void OnAttachRecent(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        foreach (var path in RecentComposeFiles.Read())
        {
            var item = new MenuItem { Header = Path.GetFileName(path), ToolTip = path };
            item.Click += (_, _) => _vm.AddAttachments(new[] { path });
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No recent files on this PC", IsEnabled = false });
        menu.IsOpen = true;
    }

    private void OnRenameAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentItem attachment) return;
        var next = TextPromptDialog.Ask(this, "Rename outgoing attachment", "Name shown to the recipient (original file stays unchanged)", attachment.Name);
        if (next == null) return;
        next = next.Trim();
        if (!ComposeSafety.ValidAttachmentName(next))
        {
            Ui.Error("Rename attachment", "Use a filename without slashes, control characters or a trailing dot (up to 160 characters).", this);
            return;
        }
        var originalExtension = Path.GetExtension(attachment.Name);
        if (originalExtension.Length > 0 && !string.Equals(originalExtension, Path.GetExtension(next), StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error("Rename attachment", "Keep the original file extension so recipients can recognise its type.", this);
            return;
        }
        _vm.RenameAttachment(attachment, next);
    }

    private void OnOpenAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Magpie.App.ViewModels.AttachmentItem attachment) return;
        if (attachment.Carried || !File.Exists(attachment.Path))
        {
            Ui.Error("Open attachment", "This file is not available on this computer. Save or attach it again.", this);
            return;
        }
        var ext = Path.GetExtension(attachment.Path).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".pdf" or ".txt" or ".doc"
            or ".docx" or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx"))
        {
            Ui.Error("Open attachment", "For safety, use File Explorer to open this type of file.", this);
            return;
        }
        try { Process.Start(new ProcessStartInfo(attachment.Path) { UseShellExecute = true }); }
        catch (Exception ex) { Ui.Error("Open attachment", ex.Message, this); }
    }

    private void OnPreviewAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentItem attachment) return;
        if (attachment.Carried)
        {
            Ui.Error("Preview attachment", "This forwarded attachment is stored inside the email. Save it as a file to preview it on this computer.", this);
            return;
        }
        if (!File.Exists(attachment.Path))
        {
            Ui.Error("Preview attachment", "The attached file has moved or been deleted. Attach it again to preview it.", this);
            return;
        }
        try
        {
            switch (ComposeSafety.AttachmentPreviewKind(attachment.Path))
            {
                case "image":
                case "pdf":
                    PreviewWindow.Show(this, attachment.Path, attachment.Name);
                    break;
                case "text":
                    ShowTextAttachmentPreview(attachment);
                    break;
                default:
                    Ui.Error("Preview attachment", "Magpie can preview pictures, PDFs and text files. Use Open for other document formats.", this);
                    break;
            }
        }
        catch (Exception ex) { Ui.Error("Preview attachment", ex.Message, this); }
    }

    private void ShowTextAttachmentPreview(AttachmentItem attachment)
    {
        const int maximumCharacters = 200_000;
        using var stream = new FileStream(attachment.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var text = new char[maximumCharacters + 1];
        var length = reader.ReadBlock(text, 0, text.Length);
        var truncated = length > maximumCharacters;
        var content = new string(text, 0, Math.Min(length, maximumCharacters));
        if (truncated) content += "\n\n[Preview stops after 200,000 characters. Open the original file to read more.]";

        var preview = new Window
        {
            Title = attachment.Name + " — Magpie preview",
            Width = 860, Height = 640, MinWidth = 420, MinHeight = 300,
            Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox
            {
                Text = content, IsReadOnly = true, AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 13, Padding = new Thickness(18),
            },
        };
        preview.Show();
    }

    private void OnCompressAttachment(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentItem attachment
            || attachment.Carried || !ComposeSafety.CanCompressImage(attachment.Path)) return;
        if (!File.Exists(attachment.Path))
        {
            Ui.Error("Compress picture", "The original picture is missing. Attach it again first.", this);
            return;
        }
        var pixels = TextPromptDialog.Ask(this, "Resize attachment", "Maximum picture width in pixels (320–4096)", "1600");
        if (pixels == null) return;
        if (!int.TryParse(pixels, out var maxWidth) || maxWidth is < 320 or > 4096)
        {
            Ui.Error("Compress picture", "Enter a width from 320 to 4096 pixels.", this);
            return;
        }
        var jpeg = Path.GetExtension(attachment.Path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || Path.GetExtension(attachment.Path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        var quality = 82;
        if (jpeg)
        {
            var choice = MessageBox.Show(this,
                "Choose the JPEG quality:\n\nYes — Smaller file (quality 65)\nNo — Better picture quality (quality 82)\nCancel — Keep this attachment unchanged",
                "Compress picture", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return;
            quality = choice == MessageBoxResult.Yes ? 65 : 82;
        }
        var targetExtension = jpeg ? ".jpg" : ".png";
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save optimized picture (original will not be overwritten)",
            FileName = Path.GetFileNameWithoutExtension(attachment.Path) + "-optimized" + targetExtension,
            DefaultExt = targetExtension,
            Filter = jpeg ? "JPEG pictures|*.jpg" : "PNG pictures|*.png",
            AddExtension = true,
        };
        if (picker.ShowDialog(this) != true) return;
        if (Path.GetFullPath(picker.FileName).Equals(Path.GetFullPath(attachment.Path), StringComparison.OrdinalIgnoreCase))
        {
            Ui.Error("Compress picture", "Choose a different filename. Magpie never overwrites the original picture.", this);
            return;
        }
        try
        {
            var originalBytes = new FileInfo(attachment.Path).Length;
            if (originalBytes > 32 * 1024 * 1024)
            {
                Ui.Error("Compress picture", "Pictures larger than 32 MB need to be resized in an image editor first.", this);
                return;
            }
            using (var stream = new FileStream(attachment.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);
                var frame = decoder.Frames.First();
                if (frame.PixelWidth == 0 || frame.PixelHeight == 0 || (long)frame.PixelWidth * frame.PixelHeight > 25_000_000)
                {
                    Ui.Error("Compress picture", "This picture is too large to resize safely inside Magpie.", this);
                    return;
                }
                var scale = Math.Min(1.0, (double)maxWidth / frame.PixelWidth);
                BitmapSource bitmap = scale < 1 ? new TransformedBitmap(frame, new ScaleTransform(scale, scale)) : frame;
                BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = quality } : new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = new FileStream(picker.FileName, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(output);
            }
            var optimizedBytes = new FileInfo(picker.FileName).Length;
            var improvement = originalBytes > 0 ? 100.0 * (originalBytes - optimizedBytes) / originalBytes : 0;
            if (optimizedBytes >= originalBytes)
            {
                MessageBox.Show(this,
                    "The optimized copy is not smaller. It was saved, but the original remains attached. Try a smaller width if you wish.",
                    "Compress picture", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(this,
                    $"Original: {HtmlRenderer.FormatSize(originalBytes)}\nOptimized: {HtmlRenderer.FormatSize(optimizedBytes)} ({improvement:0}% smaller)\n\nReplace the attachment in this email with the optimized copy? Your original picture remains on disk.",
                    "Compression complete", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _vm.ReplaceAttachmentWithOptimizedCopy(attachment, picker.FileName);
        }
        catch (Exception ex) { Ui.Error("Compress picture", ex.Message, this); }
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

    private void OnInsertTable(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        var label = new TextBlock { Text = "Choose table size (up to 6 × 6)", Margin = new Thickness(8, 4) };
        menu.Items.Add(new MenuItem { Header = label, IsEnabled = false });
        var grid = new UniformGrid { Columns = 6, Rows = 6, Margin = new Thickness(6) };
        for (var rows = 1; rows <= 6; rows++)
        for (var cols = 1; cols <= 6; cols++)
        {
            var rowCount = rows;
            var columnCount = cols;
            var square = new Button
            {
                Content = "▦", Width = 30, Height = 28, MinHeight = 28,
                ToolTip = $"{rowCount} rows × {columnCount} columns",
                Margin = new Thickness(1), Padding = new Thickness(0),
            };
            square.SetResourceReference(Control.BackgroundProperty, "Brush.Card.Background");
            square.MouseEnter += (_, _) => label.Text = $"{rowCount} rows × {columnCount} columns";
            square.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                await Editor.CoreWebView2.ExecuteScriptAsync($"insertTableGrid({rowCount},{columnCount})");
            };
            grid.Children.Add(square);
        }
        menu.Items.Add(new MenuItem { Header = grid, StaysOpenOnClick = true });
        menu.IsOpen = true;
    }

    private void ShowTableContextMenu()
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        var menu = new ContextMenu { PlacementTarget = Editor, Placement = PlacementMode.MousePoint };
        foreach (var (label, command) in new[]
                 {
                     ("Insert row above", "insertRowBefore"), ("Insert row below", "insertRowAfter"),
                     ("Delete row", "deleteRow"), ("Insert column to left", "insertColumnBefore"),
                     ("Insert column to right", "insertColumnAfter"), ("Delete column", "deleteColumn"),
                     ("Delete table", "deleteTable"),
                 })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                var result = await Editor.CoreWebView2.ExecuteScriptAsync($"editTable({JsonSerializer.Serialize(command)})");
                if (result != "true")
                    Ui.Error("Edit table", "Choose a regular table cell (without merged rows or columns) and try again.", this);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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

    private void OnResizeInlineImage(object sender, RoutedEventArgs e)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };
        foreach (var width in new[] { 25, 50, 75, 100 })
        {
            var item = new MenuItem { Header = $"{width}% of message width" };
            item.Click += async (_, _) =>
            {
                var changed = await Editor.CoreWebView2.ExecuteScriptAsync($"resizeSelectedImage({width})");
                if (changed != "true") Ui.Error("Resize picture", "Click a picture in the message first, then choose its size.", this);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
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
        menu.Items.Add(new Separator());
        var languageMenu = new MenuItem { Header = "Writing language (current paragraph)" };
        foreach (var (label, language) in new[]
                 {
                     ("English", "en"), ("Hindi", "hi"), ("Bengali", "bn"),
                     ("Chinese", "zh-CN"), ("Arabic", "ar"),
                 })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                var applied = await Editor.CoreWebView2.ExecuteScriptAsync($"setWritingLanguage({JsonSerializer.Serialize(language)})");
                if (applied != "true") Ui.Error("Writing language", "Place the cursor inside a paragraph first.", this);
            };
            languageMenu.Items.Add(item);
        }
        menu.Items.Add(languageMenu);
        foreach (var (label, direction) in new[] { ("Left-to-right paragraph", "ltr"), ("Right-to-left paragraph", "rtl") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                var applied = await Editor.CoreWebView2.ExecuteScriptAsync($"setWritingDirection({JsonSerializer.Serialize(direction)})");
                if (applied != "true") Ui.Error("Text direction", "Place the cursor inside a paragraph first.", this);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var pastePlain = new MenuItem { Header = "Paste without formatting" };
        pastePlain.Click += async (_, _) =>
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                var clipText = Clipboard.GetText();
                if (_plain) PlainEditor.SelectedText = clipText;
                else await Editor.CoreWebView2.ExecuteScriptAsync($"fmt('insertText', {JsonSerializer.Serialize(clipText)})");
            }
            catch (Exception ex) { Ui.Error("Paste text", ex.Message, this); }
        };
        menu.Items.Add(pastePlain);
        var find = new MenuItem { Header = "Find text (Ctrl+F)" };
        find.Click += (_, _) => ShowFindReplace(false);
        menu.Items.Add(find);
        var replace = new MenuItem { Header = "Find and replace (Ctrl+H)" };
        replace.Click += (_, _) => ShowFindReplace(true);
        menu.Items.Add(replace);
        var shortcuts = new MenuItem { Header = "Keyboard shortcuts (Ctrl+/)" };
        shortcuts.Click += (_, _) => ShowComposeShortcuts();
        menu.Items.Add(shortcuts);
        menu.IsOpen = true;
    }

    private async void OnReview(object sender, RoutedEventArgs e)
    {
        var body = _vm.GetNewText != null ? await _vm.GetNewText() : "";
        var missing = ComposeSafety.MissingFiles(_vm.Attachments.Where(a => !a.Carried).Select(a => a.Path));
        var duplicates = ComposeSafety.DuplicateRecipients(_vm.To, _vm.Cc, _vm.Bcc);
        var encoded = ComposeSafety.EncodedSizeOfExistingFiles(_vm.Attachments.Where(a => !a.Carried).Select(a => a.Path), "");
        var words = ComposeSafety.WordCount(body);
        var notes = new List<string>
        {
            "Recipients: " + Composer.ParseAddresses(_vm.To + "," + _vm.Cc + "," + _vm.Bcc).Mailboxes.Count(),
            "Visible To/Cc addresses: " + ComposeSafety.VisibleRecipientCount(_vm.To, _vm.Cc)
                + (ComposeSafety.VisibleRecipientCount(_vm.To, _vm.Cc) > 20 ? " (consider Bcc for privacy)" : ""),
            "Attachments: " + _vm.Attachments.Count + ", approximately " + HtmlRenderer.FormatSize(encoded) + " encoded (plus any forwarded files)",
            "Suggested send size for this account: " + HtmlRenderer.FormatSize(ComposeSafety.SuggestedOutgoingLimitBytes(_vm.From)) + " (server limits may differ)",
            $"New writing: {words} words · approximately {ComposeSafety.EstimatedReadingMinutes(words)} minute(s) to read",
            "Priority: " + _vm.Priority,
        };
        if (_vm.WarnExternalRecipients && _vm.From != null)
        {
            var external = ComposeSafety.ExternalRecipients(_vm.From.Email, _vm.To, _vm.Cc, _vm.Bcc);
            if (external.Count > 0) notes.Add("EXTERNAL: " + string.Join(", ", external));
        }
        if (missing.Count > 0) notes.Add("MISSING: " + string.Join(", ", missing.Select(Path.GetFileName)));
        if (duplicates.Count > 0) notes.Add("DUPLICATES: " + string.Join(", ", duplicates));
        if (ComposeSafety.MentionsAttachment(body) && _vm.Attachments.Count == 0) notes.Add("Possibly forgotten attachment");
        if (string.IsNullOrWhiteSpace(_vm.Subject)) notes.Add("No subject");
        if (string.IsNullOrWhiteSpace(body)) notes.Add("No new body text");
        MessageBox.Show(this, string.Join("\n\n", notes), "Compose review", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
