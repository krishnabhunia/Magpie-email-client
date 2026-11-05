using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Magpie.App.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.App.Views;

/// <summary>
/// Design CW1 (15.0.0): the compose window's toolbar (built from Settings → Appearance → Compose toolbar, folds into More ▾),
/// its menus (Send ▾, Attach ▾, Templates ▾, Signature ▾, Options ▾), find &amp; replace, @mentions, the link and picture
/// bubbles, the table menu, paste handling, Discard and the remembered window size.
/// </summary>
public partial class ComposeWindow
{
    private readonly Dictionary<string, FrameworkElement> _tools = new();
    private readonly List<FrameworkElement> _toolOrder = new();
    private readonly List<string> _overflow = new();
    private ComboBox? _styleBox, _fontBox, _sizeBox;
    private string _textColour = "#B3261E", _highlightColour = "#FFF3A0";
    private Border? _textSwatch, _highlightSwatch;
    private bool _applyingState;
    private string _lastLinkHref = "";
    private bool _findReplaceMode;
    private Popup? _openPopup;

    private static readonly string[] Fonts = { "Arial", "Calibri", "Georgia", "IBM Plex Sans", "Segoe UI", "Times New Roman", "Verdana", "Consolas" };
    private static readonly int[] Sizes = { 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 36 };
    private static readonly (string Id, string Label)[] Styles = { ("p", "Normal text"), ("h1", "Heading 1"), ("h2", "Heading 2"), ("h3", "Heading 3"), ("code", "Code") };

    private ComposeSettings ComposeConfig => AppServices.Engine.Config.Compose;

    // ───────────────────────── the toolbar (B1–B15) ─────────────────────────

    private void BuildToolbar()
    {
        ToolbarPanel.Children.Clear();
        _tools.Clear();
        _toolOrder.Clear();
        var visible = new HashSet<string>(ComposeConfig.Toolbar.Where(b => b.Visible).Select(b => b.Id));
        var order = ComposeConfig.Toolbar.Select(b => b.Id).ToList();
        // Groups follow the default order; buttons inside a group follow the user's order.
        var groups = new List<List<string>>();
        var current = new List<string>();
        foreach (var id in ComposeSettings.ToolbarIds)
        {
            if (id == "|") { if (current.Count > 0) groups.Add(current); current = new List<string>(); }
            else current.Add(id);
        }
        if (current.Count > 0) groups.Add(current);
        var first = true;
        foreach (var group in groups)
        {
            var ids = group.Where(visible.Contains).OrderBy(id => order.IndexOf(id)).ToList();
            if (ids.Count == 0) continue;
            if (!first) Add("|", Separator());
            first = false;
            foreach (var id in ids) Add(id, MakeTool(id));
        }
        ApplyOverflow();
    }

    private void Add(string id, FrameworkElement el)
    {
        el.Tag = id;
        ToolbarPanel.Children.Add(el);
        _toolOrder.Add(el);
        if (id != "|") _tools[id] = el;
    }

    private static Border Separator() => new() { Width = 1, Margin = new Thickness(5, 7, 5, 7), Background = (Brush)Application.Current.FindResource("Brush.Divider") };

    private FrameworkElement MakeTool(string id)
    {
        switch (id)
        {
            case "undo": return IconButton("", "Undo (Ctrl+Z)", () => Fmt("undo"));
            case "redo": return IconButton("", "Redo (Ctrl+Y)", () => Fmt("redo"));
            case "style":
                _styleBox = Combo(Styles.Select(s => s.Label), 112, "Paragraph style");
                _styleBox.SelectionChanged += (_, _) => { if (!_applyingState && _styleBox.SelectedIndex >= 0) Script($"setBlock({JsonSerializer.Serialize(Styles[_styleBox.SelectedIndex].Id)})"); };
                return _styleBox;
            case "font":
                _fontBox = Combo(Fonts, 124, "Font");
                _fontBox.SelectionChanged += (_, _) => { if (!_applyingState && _fontBox.SelectedItem is string f) Fmt("fontName", f); };
                return _fontBox;
            case "size":
                _sizeBox = Combo(Sizes.Select(x => x.ToString()), 58, "Text size (pt)");
                _sizeBox.SelectionChanged += (_, _) => { if (!_applyingState && _sizeBox.SelectedItem is string pt) Script($"setFontSize({Math.Round(int.Parse(pt) * 96.0 / 72)})"); };
                return _sizeBox;
            case "bold": return Toggle("B", FontWeights.Bold, null, "Bold (Ctrl+B)", () => Fmt("bold"));
            case "italic": return Toggle("I", FontWeights.Normal, FontStyles.Italic, "Italic (Ctrl+I)", () => Fmt("italic"));
            case "underline": { var b = Toggle("U", FontWeights.Normal, null, "Underline (Ctrl+U)", () => Fmt("underline")); ((TextBlock)b.Content).TextDecorations = TextDecorations.Underline; return b; }
            case "strike": { var b = Toggle("S", FontWeights.Normal, null, "Strikethrough (Ctrl+Shift+X)", () => Fmt("strikeThrough")); ((TextBlock)b.Content).TextDecorations = TextDecorations.Strikethrough; return b; }
            case "colour": return ColourTool("Text colour", false);
            case "highlight": return ColourTool("Highlight", true);
            case "align": return IconButton("", "Alignment", null, AlignMenu);
            case "bullets": return IconButton("", "Bulleted list (Ctrl+Shift+8)", () => Fmt("insertUnorderedList"));
            case "numbers": return IconButton("", "Numbered list (Ctrl+Shift+7)", () => Fmt("insertOrderedList"));
            case "outdent": return Text("⇤", "Outdent (Ctrl+[)", () => Fmt("outdent"));
            case "indent": return Text("⇥", "Indent (Ctrl+])", () => Fmt("indent"));
            case "quote": return Text("❝", "Quote", () => Fmt("formatBlock", "blockquote"));
            case "line": return Text("—", "Horizontal line", () => Fmt("insertHorizontalRule"));
            case "table": return Text("▦", "Table", null, b => OpenPopup(ComposePopups.TableGrid(b, (r, c) => Script($"insertTable({r},{c})"))));
            case "picture": return IconButton("", "Picture in the message", OnInsertPicture);
            case "emoji": return IconButton("", "Emoji", null, b => OpenPopup(ComposePopups.Emoji(b, ComposeConfig.RecentEmoji, InsertEmoji)));
            case "link": return IconButton("", "Insert link (Ctrl+K)", () => OnLink(this, new RoutedEventArgs()));
            case "clear": return IconButton("", "Clear formatting (Ctrl+\\)", () => Fmt("removeFormat"));
        }
        return Text(id, id, null);
    }

    private static ComboBox Combo(IEnumerable<string> items, double width, string tip)
    {
        var c = new ComboBox { Width = width, MinHeight = 28, Height = 28, FontSize = 12, Margin = new Thickness(1, 0, 1, 0), ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        foreach (var i in items) c.Items.Add(i);
        return c;
    }

    private Button IconButton(string glyph, string tip, Action? click, Action<Button>? open = null)
    {
        var b = new Button { Content = glyph, ToolTip = tip, Width = 30, Height = 30, MinHeight = 30, Margin = new Thickness(0) };
        b.SetResourceReference(StyleProperty, "Button.Icon");
        b.Click += (_, _) => { if (open != null) open(b); else click?.Invoke(); };
        return b;
    }

    private Button Text(string text, string tip, Action? click, Action<Button>? open = null)
    {
        var b = new Button { Content = new TextBlock { Text = text, FontSize = 14, FontFamily = new FontFamily("Segoe UI Symbol, Segoe UI") }, ToolTip = tip, Width = 30, Height = 30, MinHeight = 30, Margin = new Thickness(0) };
        b.SetResourceReference(StyleProperty, "Button.Icon");
        b.FontFamily = new FontFamily("Segoe UI");
        b.Click += (_, _) => { if (open != null) open(b); else click?.Invoke(); };
        return b;
    }

    private Button Toggle(string text, FontWeight weight, FontStyle? style, string tip, Action click)
    {
        var tb = new TextBlock { Text = text, FontSize = 13.5, FontWeight = weight, FontFamily = new FontFamily("Georgia") };
        if (style != null) tb.FontStyle = style.Value;
        var b = new Button { Content = tb, ToolTip = tip, Width = 30, Height = 30, MinHeight = 30, Margin = new Thickness(0) };
        b.SetResourceReference(StyleProperty, "Button.Icon");
        b.FontFamily = new FontFamily("Segoe UI");
        b.Click += (_, _) => click();
        return b;
    }

    private FrameworkElement ColourTool(string tip, bool highlight)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0) };
        var letter = new TextBlock { Text = "A", FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Georgia") };
        var swatch = new Border { Height = 4, Width = 14, CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 1, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Background = ComposePopups.Brush(highlight ? _highlightColour : _textColour) };
        if (highlight) { letter.Background = ComposePopups.Brush("#FFF3A0"); letter.Foreground = ComposePopups.Brush("#23282E"); letter.Padding = new Thickness(2, 0, 2, 0); }
        var inner = new StackPanel { Children = { letter, swatch }, VerticalAlignment = VerticalAlignment.Center };
        var main = new Button { Content = inner, ToolTip = tip + " — click to apply the last colour", Width = 30, Height = 30, MinHeight = 30, Margin = new Thickness(0) };
        main.SetResourceReference(StyleProperty, "Button.Icon");
        main.FontFamily = new FontFamily("Segoe UI");
        main.Click += (_, _) => Fmt(highlight ? "hiliteColor" : "foreColor", highlight ? _highlightColour : _textColour);
        var arrow = new Button { Content = "", ToolTip = tip + " — choose", Width = 14, Height = 30, MinHeight = 30, FontSize = 7, Margin = new Thickness(-2, 0, 0, 0), Padding = new Thickness(0) };
        arrow.SetResourceReference(StyleProperty, "Button.Icon");
        arrow.Click += (_, _) => OpenPopup(ComposePopups.Colours(main, tip, highlight ? _highlightColour : _textColour, hex =>
        {
            if (hex.Length == 0) { Fmt(highlight ? "hiliteColor" : "foreColor", highlight ? "transparent" : "inherit"); return; }
            if (highlight) _highlightColour = hex; else _textColour = hex;
            swatch.Background = ComposePopups.Brush(hex);
            Fmt(highlight ? "hiliteColor" : "foreColor", hex);
        }));
        if (highlight) _highlightSwatch = swatch; else _textSwatch = swatch;
        stack.Children.Add(main);
        stack.Children.Add(arrow);
        return stack;
    }

    private void AlignMenu(Button b)
    {
        var menu = new ContextMenu { PlacementTarget = b, Placement = PlacementMode.Bottom };
        foreach (var (text, cmd, keys) in new[] { ("Align left", "justifyLeft", "Ctrl+Shift+L"), ("Align centre", "justifyCenter", "Ctrl+Shift+E"), ("Align right", "justifyRight", "Ctrl+Shift+R"), ("Justify", "justifyFull", "Ctrl+Shift+J") })
        {
            var mi = new MenuItem { Header = text, InputGestureText = keys };
            mi.Click += (_, _) => Fmt(cmd);
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    private void OpenPopup(Popup p)
    {
        if (_openPopup != null) _openPopup.IsOpen = false;
        _openPopup = p;
        p.Closed += (_, _) => { if (_openPopup == p) _openPopup = null; };
        p.IsOpen = true;
    }

    private void InsertEmoji(string e)
    {
        ComposeConfig.NoteEmoji(e);
        AppServices.Engine.Settings.Save(notify: false);
        Script($"insertEmoji({JsonSerializer.Serialize(e)})");
    }

    private void Fmt(string command, string? value = null)
    {
        if (_plain || !_editorReady) return;
        _ = Editor.CoreWebView2?.ExecuteScriptAsync(value == null ? $"fmt({JsonSerializer.Serialize(command)})" : $"fmt({JsonSerializer.Serialize(command)}, {JsonSerializer.Serialize(value)})");
    }

    private void Script(string js)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return;
        _ = Editor.CoreWebView2.ExecuteScriptAsync(js);
    }

    private async Task<string> ScriptAsync(string js)
    {
        if (_plain || !_editorReady || Editor.CoreWebView2 == null) return "null";
        return await Editor.CoreWebView2.ExecuteScriptAsync(js);
    }

    /// <summary>B15: buttons that don't fit go into More ▾ (right-most first).</summary>
    private void OnToolbarSizeChanged(object sender, SizeChangedEventArgs e) => ApplyOverflow();

    private void ApplyOverflow()
    {
        if (_toolOrder.Count == 0) return;
        var available = ToolbarPanel.ActualWidth > 0 ? ToolbarPanel.ActualWidth : 0;
        if (available <= 0) return;
        _overflow.Clear();
        double used = 0;
        var fits = true;
        foreach (var el in _toolOrder)
        {
            el.Visibility = Visibility.Visible;
            el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            used += el.DesiredSize.Width + el.Margin.Left + el.Margin.Right;
            if (fits && used > available) fits = false;
            if (!fits)
            {
                el.Visibility = Visibility.Collapsed;
                if (el.Tag is string id && id != "|") _overflow.Add(id);
            }
        }
        // A separator left at the end of the visible part is noise.
        var lastVisible = _toolOrder.LastOrDefault(x => x.Visibility == Visibility.Visible);
        if (lastVisible?.Tag is "|") lastVisible.Visibility = Visibility.Collapsed;
    }

    private void OnMoreMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        foreach (var id in _overflow)
        {
            var mi = new MenuItem { Header = ComposeSettings.NameOf(id) };
            var captured = id;
            mi.Click += (_, _) => RunTool(captured, MoreButton);
            menu.Items.Add(mi);
        }
        if (_overflow.Count > 0) menu.Items.Add(new Separator());
        if (!_overflow.Contains("align"))
        {
            foreach (var (text, cmd, keys) in new[] { ("Align left", "justifyLeft", "Ctrl+Shift+L"), ("Align centre", "justifyCenter", "Ctrl+Shift+E"), ("Align right", "justifyRight", "Ctrl+Shift+R"), ("Justify", "justifyFull", "Ctrl+Shift+J") })
            {
                var mi = new MenuItem { Header = text, InputGestureText = keys };
                mi.Click += (_, _) => Fmt(cmd);
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
        }
        var sup = new MenuItem { Header = "Superscript", InputGestureText = "Ctrl+." }; sup.Click += (_, _) => Fmt("superscript"); menu.Items.Add(sup);
        var sub = new MenuItem { Header = "Subscript", InputGestureText = "Ctrl+," }; sub.Click += (_, _) => Fmt("subscript"); menu.Items.Add(sub);
        menu.Items.Add(new Separator());
        var plainPaste = new MenuItem { Header = "Paste as plain text", InputGestureText = "Ctrl+Shift+V" };
        plainPaste.Click += (_, _) => PastePlain();
        menu.Items.Add(plainPaste);
        var plain = new MenuItem { Header = "Plain text message…" };
        plain.Click += (_, _) => _ = SwitchPlainTextAsync(true);
        menu.Items.Add(plain);
        var customise = new MenuItem { Header = "Choose the buttons…" };
        customise.Click += (_, _) => SettingsWindow.Open("Appearance");
        menu.Items.Add(customise);
        menu.IsOpen = true;
    }

    /// <summary>Runs a toolbar button from the More ▾ menu (the button itself is hidden).</summary>
    private void RunTool(string id, Button anchor)
    {
        switch (id)
        {
            case "undo": Fmt("undo"); break;
            case "redo": Fmt("redo"); break;
            case "bold": Fmt("bold"); break;
            case "italic": Fmt("italic"); break;
            case "underline": Fmt("underline"); break;
            case "strike": Fmt("strikeThrough"); break;
            case "colour": OpenPopup(ComposePopups.Colours(anchor, "Text colour", _textColour, hex => { if (hex.Length > 0) _textColour = hex; Fmt("foreColor", hex.Length == 0 ? "inherit" : hex); })); break;
            case "highlight": OpenPopup(ComposePopups.Colours(anchor, "Highlight", _highlightColour, hex => { if (hex.Length > 0) _highlightColour = hex; Fmt("hiliteColor", hex.Length == 0 ? "transparent" : hex); })); break;
            case "align": AlignMenu(anchor); break;
            case "bullets": Fmt("insertUnorderedList"); break;
            case "numbers": Fmt("insertOrderedList"); break;
            case "outdent": Fmt("outdent"); break;
            case "indent": Fmt("indent"); break;
            case "quote": Fmt("formatBlock", "blockquote"); break;
            case "line": Fmt("insertHorizontalRule"); break;
            case "table": OpenPopup(ComposePopups.TableGrid(anchor, (r, c) => Script($"insertTable({r},{c})"))); break;
            case "picture": OnInsertPicture(); break;
            case "emoji": OpenPopup(ComposePopups.Emoji(anchor, ComposeConfig.RecentEmoji, InsertEmoji)); break;
            case "link": OnLink(this, new RoutedEventArgs()); break;
            case "clear": Fmt("removeFormat"); break;
            case "style": case "font": case "size":
                // The drop-downs: a small menu with their choices.
                var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
                if (id == "style") foreach (var (sid, label) in Styles) { var mi = new MenuItem { Header = label }; mi.Click += (_, _) => Script($"setBlock({JsonSerializer.Serialize(sid)})"); menu.Items.Add(mi); }
                else if (id == "font") foreach (var f in Fonts) { var mi = new MenuItem { Header = f }; var ff = f; mi.Click += (_, _) => Fmt("fontName", ff); menu.Items.Add(mi); }
                else foreach (var pt in Sizes) { var mi = new MenuItem { Header = pt + " pt" }; var px = Math.Round(pt * 96.0 / 72); mi.Click += (_, _) => Script($"setFontSize({px})"); menu.Items.Add(mi); }
                menu.IsOpen = true;
                break;
        }
    }

    /// <summary>The editor reported what is at the cursor: light up the matching buttons.</summary>
    private void ApplyEditorState(JsonElement st)
    {
        _applyingState = true;
        try
        {
            bool B(string n) => st.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            void Lit(string id, bool on)
            {
                if (!_tools.TryGetValue(id, out var el) || el is not Button b) return;
                if (on) { b.SetResourceReference(BackgroundProperty, "Brush.Accent.Subtle"); b.SetResourceReference(BorderBrushProperty, "Brush.Accent.Border"); b.BorderThickness = new Thickness(1); }
                else { b.ClearValue(BackgroundProperty); b.ClearValue(BorderBrushProperty); b.ClearValue(BorderThicknessProperty); }
            }
            Lit("bold", B("bold")); Lit("italic", B("italic")); Lit("underline", B("underline")); Lit("strike", B("strike"));
            Lit("bullets", B("ul")); Lit("numbers", B("ol"));
            if (_styleBox != null && st.TryGetProperty("block", out var block))
            {
                var idx = Array.FindIndex(Styles, s => s.Id == block.GetString());
                _styleBox.SelectedIndex = idx < 0 ? 0 : idx;
            }
            if (_fontBox != null && st.TryGetProperty("font", out var font))
            {
                var f = font.GetString() ?? "";
                var match = Fonts.FirstOrDefault(x => x.Equals(f, StringComparison.OrdinalIgnoreCase));
                _fontBox.SelectedItem = match;
                if (match == null) _fontBox.Text = f;
            }
            if (_sizeBox != null && st.TryGetProperty("size", out var size) && size.TryGetInt32(out var px) && px > 0)
            {
                var pt = (int)Math.Round(px * 72.0 / 96);
                _sizeBox.SelectedItem = Sizes.Contains(pt) ? pt.ToString() : null;
                if (!Sizes.Contains(pt)) _sizeBox.Text = pt.ToString();
            }
        }
        finally { _applyingState = false; }
    }

    // ───────────────────────── pictures (B9, C4) ─────────────────────────

    private void OnInsertPicture()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Picture in the message", Filter = "Pictures|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        InsertPictureFile(dlg.FileName);
    }

    private void InsertPictureFile(string path)
    {
        try
        {
            var size = new FileInfo(path).Length;
            if (size > 2 * 1024 * 1024 && !Ui.Confirm("Big picture", $"{Path.GetFileName(path)} is {HtmlRenderer.FormatSize(size)}. Put it in the message anyway? (Attaching it as a file keeps the email smaller for people on phones.)", this)) return;
            Script($"insertPicture({JsonSerializer.Serialize(Composer.PictureHtml(path))})");
            _ = RefreshSizeAsync();
        }
        catch (Exception ex) { Ui.Error("Picture", ex.Message, this); }
    }

    private async Task RefreshSizeAsync()
    {
        try { _vm.RefreshAttachmentSummary(await GetHtmlAsync()); }
        catch (Exception ex) { Log.Warn("size bar: " + ex.Message); }
    }

    private void ShowPictureBubble(JsonElement root)
    {
        if (root.TryGetProperty("src", out var src) && src.GetString() == "set")
        {
            PictureBubble.HorizontalOffset = root.GetProperty("x").GetDouble();
            PictureBubble.VerticalOffset = root.GetProperty("y").GetDouble() + 4;
            PictureBubble.IsOpen = true;
        }
        else PictureBubble.IsOpen = false;
    }

    private void OnPictureSize(object sender, RoutedEventArgs e) { Script($"imgSize({JsonSerializer.Serialize((sender as FrameworkElement)?.Tag as string ?? "original")})"); }
    private void OnPictureRemove(object sender, RoutedEventArgs e) { Script("imgRemove()"); PictureBubble.IsOpen = false; _ = RefreshSizeAsync(); }

    private async void OnPictureAttach(object sender, RoutedEventArgs e)
    {
        try
        {
            var src = JsonSerializer.Deserialize<string>(await ScriptAsync("imgSrc()")) ?? "";
            if (_vm.AttachDataUri(src) == null) { Ui.Error("Attach", "Only a picture that is inside the message can be attached this way.", this); return; }
            Script("imgRemove()");
            PictureBubble.IsOpen = false;
            await RefreshSizeAsync();
        }
        catch (Exception ex) { Log.Warn("picture → attachment: " + ex.Message); }
    }

    /// <summary>C4: a picture was pasted and the setting says Ask (or the setting decided).</summary>
    private void OnPastedPicture(string dataUri)
    {
        var choice = ComposeConfig.PastePicture;
        if (choice == PastePictureChoice.Ask)
        {
            var (id, remember) = ComposeChoiceDialog.Ask(this, "You pasted a picture", "Put it in the message where the cursor is, or attach it as a file?",
                new[] { new ComposeChoiceDialog.Choice("Attach as file", "attach"), new ComposeChoiceDialog.Choice("Put in message", "inline", Primary: true) }, "Always do this");
            if (id == null) return;
            choice = id == "attach" ? PastePictureChoice.AttachFile : PastePictureChoice.InMessage;
            if (remember) { ComposeConfig.PastePicture = choice; AppServices.Engine.Settings.Save(notify: false); }
        }
        if (choice == PastePictureChoice.AttachFile) _vm.AttachDataUri(dataUri);
        else Script($"insertPicture({JsonSerializer.Serialize("<img src=\"" + dataUri + "\" style=\"max-width:100%\">")})");
        _ = RefreshSizeAsync();
    }

    /// <summary>C4: files copied in Explorer and pasted into the message are attached.</summary>
    private void OnPastedFiles()
    {
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            var files = Clipboard.GetFileDropList().Cast<string>().Where(File.Exists).ToList();
            if (files.Count > 0) _vm.AddAttachments(files);
        }
        catch (Exception ex) { Log.Warn("paste files: " + ex.Message); }
    }

    private void PastePlain()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            Script($"insertEmoji({JsonSerializer.Serialize(Clipboard.GetText())})");
        }
        catch (Exception ex) { Log.Warn("paste plain: " + ex.Message); }
    }

    // ───────────────────────── links (B14) ─────────────────────────

    private void ShowLinkBubble(JsonElement root)
    {
        var href = root.TryGetProperty("href", out var h) ? h.GetString() ?? "" : "";
        if (href.Length == 0) { LinkBubble.IsOpen = false; _lastLinkHref = ""; return; }
        _lastLinkHref = href;
        LinkText.Text = href.Length > 60 ? href[..60] + "…" : href;
        LinkOpen.ToolTip = href;
        LinkBubble.HorizontalOffset = root.GetProperty("x").GetDouble();
        LinkBubble.VerticalOffset = root.GetProperty("y").GetDouble() + 4;
        LinkBubble.IsOpen = true;
    }

    private void OnLinkOpen(object sender, RoutedEventArgs e) { if (_lastLinkHref.Length > 0) Ui.OpenExternal(_lastLinkHref); }
    private void OnLinkRemove(object sender, RoutedEventArgs e) { Script("unlinkAt()"); LinkBubble.IsOpen = false; }

    private void OnLinkEdit(object sender, RoutedEventArgs e)
    {
        var url = TextPromptDialog.Ask(this, "Edit link", "Web address", _lastLinkHref);
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https" or "mailto")) { Ui.Error("Edit link", "That doesn't look like a web address.", this); return; }
        Script($"editLink({JsonSerializer.Serialize(u.ToString())})");
        _lastLinkHref = u.ToString();
        LinkText.Text = _lastLinkHref;
    }

    // ───────────────────────── table menu (B8) ─────────────────────────

    private void ShowTableMenu(JsonElement root)
    {
        var menu = new ContextMenu { Placement = PlacementMode.Relative, PlacementTarget = Editor, HorizontalOffset = root.GetProperty("x").GetDouble(), VerticalOffset = root.GetProperty("y").GetDouble() };
        foreach (var (text, cmd) in new[] { ("Insert row above", "rowAbove"), ("Insert row below", "rowBelow"), ("Insert column left", "colLeft"), ("Insert column right", "colRight"), ("", ""), ("Delete row", "delRow"), ("Delete column", "delCol"), ("Delete table", "delTable") })
        {
            if (cmd.Length == 0) { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = text };
            var c = cmd;
            mi.Click += (_, _) => Script($"tableCmd({JsonSerializer.Serialize(c)})");
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // ───────────────────────── find & replace (E1) ─────────────────────────

    private void OpenFind(bool replace)
    {
        if (_plain) { Ui.Error("Find", "Find works in the rich-text editor. Switch back to rich text from More ▾.", this); return; }
        _findReplaceMode = replace;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;
        FindBar.Visibility = Visibility.Visible;
        FindBox.Focus();
        FindBox.SelectAll();
        if (FindBox.Text.Length > 0) OnFindChanged(this, new RoutedEventArgs());
    }

    private void OnFindClose(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindCount.Text = "";
        Script("findClear()");
        Editor.Focus();
    }

    private async void OnFindChanged(object sender, RoutedEventArgs e)
    {
        if (FindBar.Visibility != Visibility.Visible) return;
        try
        {
            var n = JsonSerializer.Deserialize<int>(await ScriptAsync($"findAll({JsonSerializer.Serialize(FindBox.Text)}, {(FindCase.IsChecked == true ? "true" : "false")})"));
            FindCount.Text = FindBox.Text.Length == 0 ? "" : n == 0 ? "No matches" : $"1 of {n}";
        }
        catch (Exception ex) { Log.Warn("find: " + ex.Message); }
    }

    private async void OnFindNext(object sender, RoutedEventArgs e) => await StepFind(1);
    private async void OnFindPrev(object sender, RoutedEventArgs e) => await StepFind(-1);

    private async Task StepFind(int dir)
    {
        try
        {
            var cur = JsonSerializer.Deserialize<int>(await ScriptAsync($"findNext({dir})"));
            if (cur > 0 && FindCount.Text.Contains(" of ")) FindCount.Text = $"{cur} of {FindCount.Text.Split(" of ")[1]}";
        }
        catch (Exception ex) { Log.Warn("find next: " + ex.Message); }
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; _ = StepFind((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1); }
        else if (e.Key == Key.Escape) { e.Handled = true; OnFindClose(this, new RoutedEventArgs()); }
    }

    private void OnReplaceKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnReplaceOne(this, new RoutedEventArgs()); }
        else if (e.Key == Key.Escape) { e.Handled = true; OnFindClose(this, new RoutedEventArgs()); }
    }

    private async void OnReplaceOne(object sender, RoutedEventArgs e)
    {
        try
        {
            var left = JsonSerializer.Deserialize<int>(await ScriptAsync($"replaceOne({JsonSerializer.Serialize(ReplaceBox.Text)})"));
            FindCount.Text = left == 0 ? "No more matches" : $"1 of {left}";
        }
        catch (Exception ex) { Log.Warn("replace: " + ex.Message); }
    }

    private async void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        try
        {
            var n = JsonSerializer.Deserialize<int>(await ScriptAsync($"replaceAll({JsonSerializer.Serialize(ReplaceBox.Text)})"));
            FindCount.Text = n == 0 ? "No matches" : n == 1 ? "Replaced 1" : $"Replaced {n}";
        }
        catch (Exception ex) { Log.Warn("replace all: " + ex.Message); }
    }

    // ───────────────────────── @mention (E6) ─────────────────────────

    private void ShowMentions(JsonElement root)
    {
        var q = root.TryGetProperty("q", out var qv) ? qv.GetString() ?? "" : "";
        if (q.Length < 2) { MentionPopup.IsOpen = false; Script("setMentionOpen(false)"); return; }
        var hits = AppServices.Engine.Contacts.Suggest(q);
        if (hits.Count == 0) { MentionPopup.IsOpen = false; Script("setMentionOpen(false)"); return; }
        Script("setMentionOpen(true)");
        MentionList.ItemsSource = hits;
        MentionList.SelectedIndex = 0;
        MentionPopup.HorizontalOffset = root.GetProperty("x").GetDouble();
        MentionPopup.VerticalOffset = root.GetProperty("y").GetDouble() + 4;
        MentionPopup.IsOpen = true;
    }

    private void OnMentionClick(object sender, MouseButtonEventArgs e) => AcceptMention();

    private void AcceptMention()
    {
        if (MentionList.SelectedItem is not ContactSuggestion c) { MentionPopup.IsOpen = false; Script("setMentionOpen(false)"); return; }
        MentionPopup.IsOpen = false;
        Script($"mention({JsonSerializer.Serialize(c.Label)}, {JsonSerializer.Serialize(c.Address)})");
        if (_vm.AddMention(c.Name, c.Address)) _vm.Status = $"{c.Label} added to To";
    }

    // ───────────────────────── footer menus (D1–D9, C5) ─────────────────────────

    private void OnSendMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        var send = new MenuItem { Header = "Send", InputGestureText = "Ctrl+Enter" };
        send.Click += async (_, _) => await _vm.SendAsync(null, null);
        menu.Items.Add(send);
        if (_vm.CanSendAndArchive)
        {
            var arc = new MenuItem { Header = "Send & Archive", InputGestureText = "Ctrl+Alt+Enter", ToolTip = "Sends, then archives this conversation once the undo window has passed" };
            arc.Click += async (_, _) => await _vm.SendAndArchiveAsync();
            menu.Items.Add(arc);
        }
        var later = new MenuItem { Header = "Send later…" };
        later.Click += (_, _) => OnSendLater(sender, e);
        menu.Items.Add(later);
        menu.IsOpen = true;
    }

    private void OnAttachMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        var files = new MenuItem { Header = "Attach files…", InputGestureText = "Ctrl+Shift+O" };
        files.Click += (_, _) => OnAttach(sender, e);
        menu.Items.Add(files);
        var pic = new MenuItem { Header = "Picture in the message…", IsEnabled = !_plain };
        pic.Click += (_, _) => OnInsertPicture();
        menu.Items.Add(pic);
        var recent = _vm.RecentAttachments;
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Recent", IsEnabled = false });
            foreach (var path in recent)
            {
                var exists = File.Exists(path);
                var mi = new MenuItem { Header = Path.GetFileName(path), IsEnabled = exists, ToolTip = path, InputGestureText = exists ? When(File.GetLastWriteTime(path)) : "missing" };
                var p = path;
                mi.Click += (_, _) => _vm.AddAttachments(new[] { p });
                menu.Items.Add(mi);
            }
        }
        menu.IsOpen = true;
        static string When(DateTime t) => t.Date == DateTime.Today ? "today" : t.Date == DateTime.Today.AddDays(-1) ? "yesterday" : t.ToString("d MMM");
    }

    private void OnSignatureMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        if (_vm.From != null) menu.Items.Add(new MenuItem { Header = "Signature · " + _vm.From.Email, IsEnabled = false });
        foreach (var s in _vm.Signatures)
        {
            var mi = new MenuItem { Header = s.Name + (s.Id.Length > 0 && s.Id == _vm.From?.DefaultSignatureId ? "   (default)" : ""), IsCheckable = true, IsChecked = s.IsCurrent, IsEnabled = !_plain };
            var id = s.Id;
            mi.Click += async (_, _) => await _vm.SelectSignatureAsync(id);
            menu.Items.Add(mi);
        }
        menu.Items.Add(new Separator());
        var edit = new MenuItem { Header = "Edit signatures…" };
        edit.Click += (_, _) => SettingsWindow.Open("Signatures");
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }

    private void OnOptionsMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Top };
        var prio = new MenuItem { Header = "Priority · " + _vm.PriorityLabel };
        foreach (var (text, id) in new[] { ("! High", "High"), ("Normal", "Normal"), ("↓ Low", "Low") })
        {
            var mi = new MenuItem { Header = text, IsCheckable = true, IsChecked = _vm.PriorityLabel == id };
            var which = id;
            mi.Click += (_, _) => _vm.SetPriorityCommand.Execute(which);
            prio.Items.Add(mi);
        }
        menu.Items.Add(prio);
        var receipt = new MenuItem { Header = "Request a read receipt", IsCheckable = true, IsChecked = _vm.ReadReceipt, ToolTip = "Asks the other person's mail app to tell you when the email was opened. Their app decides whether to answer." };
        receipt.Click += (_, _) => _vm.ToggleReadReceiptCommand.Execute(null);
        menu.Items.Add(receipt);
        var plain = new MenuItem { Header = "Plain text message", IsCheckable = true, IsChecked = _vm.PlainText };
        plain.Click += (_, _) => _ = SwitchPlainTextAsync(!_vm.PlainText);
        menu.Items.Add(plain);
        menu.Items.Add(new Separator());
        var save = new MenuItem { Header = "Save draft", InputGestureText = "Ctrl+S" };
        save.Click += (_, _) => OnSaveDraft(sender, e);
        menu.Items.Add(save);
        var template = new MenuItem { Header = "Save this message as a template…" };
        template.Click += (_, _) => OnSaveAsTemplate();
        menu.Items.Add(template);
        var eml = new MenuItem { Header = "Save to file (.eml)…" };
        eml.Click += (_, _) => OnSaveToFile();
        menu.Items.Add(eml);
        menu.Items.Add(new Separator());
        var keys = new MenuItem { Header = "Keyboard shortcuts", InputGestureText = "Ctrl+/" };
        keys.Click += (_, _) => ComposePopups.Shortcuts(this);
        menu.Items.Add(keys);
        menu.IsOpen = true;
    }

    private async void OnSaveAsTemplate()
    {
        var name = TextPromptDialog.Ask(this, "Save as template", "Template name", _vm.Subject);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _vm.SaveAsTemplateAsync(name);
    }

    private async void OnSaveToFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save to file", Filter = "Email message (*.eml)|*.eml", FileName = SafeFileName(_vm.Subject) + ".eml" };
        if (dlg.ShowDialog(this) != true) return;
        try { await _vm.SaveToFileAsync(dlg.FileName); }
        catch (Exception ex) { Ui.Error("Save to file", ex.Message, this); }
    }

    private static string SafeFileName(string s)
    {
        var name = string.IsNullOrWhiteSpace(s) ? "message" : s;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length > 80 ? name[..80] : name;
    }

    /// <summary>D7: the footer's Discard.</summary>
    private async void OnDiscard(object sender, RoutedEventArgs e)
    {
        var (id, _) = ComposeChoiceDialog.Ask(this, "Discard this message?", "It is removed from Drafts and from this PC. This cannot be undone.",
            new[] { new ComposeChoiceDialog.Choice("Keep editing", "keep", Cancel: true), new ComposeChoiceDialog.Choice("Discard", "discard", Danger: true) });
        if (id != "discard") return;
        if (await _vm.DiscardForeverAsync()) { _forceClose = true; Close(); }
    }

    /// <summary>B16: rich text ↔ plain text. The plain editor is the same one used when WebView2 is missing.</summary>
    private async Task SwitchPlainTextAsync(bool plain, bool ask = true)
    {
        if (plain == _vm.PlainText) return;
        if (plain)
        {
            if (ask)
            {
                var (id, _) = ComposeChoiceDialog.Ask(this, "Switch this message to plain text?",
                    "Bold, colours, lists, tables and pictures are removed. Attached files stay. Your signature is kept as text. You can switch back, but the formatting does not come back.",
                    new[] { new ComposeChoiceDialog.Choice("Keep rich text", "keep", Cancel: true), new ComposeChoiceDialog.Choice("Switch to plain text", "plain", Primary: true) });
                if (id != "plain") return;
            }
            string html;
            try { html = await GetHtmlAsync(); } catch { html = _vm.InitialHtml; }
            _vm.PlainText = true;
            _plain = true;
            Editor.Visibility = Visibility.Collapsed;
            PlainEditor.Visibility = Visibility.Visible;
            PlainEditor.Text = MimeText.HtmlToText(html);
            _plainProtectedTail = ProtectedTail(html);   // the signature and quote stay protected in the plain editor too
            PlainEditor.TextChanged -= OnPlainChanged;
            PlainEditor.TextChanged += OnPlainChanged;
            await ApplyEditorTypographyAsync();
            PlainEditor.Focus();
        }
        else
        {
            var text = PlainEditor.Text;
            _vm.PlainText = false;
            _plain = false;
            PlainEditor.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            if (_editorReady && Editor.CoreWebView2 != null)
            {
                await Editor.CoreWebView2.ExecuteScriptAsync($"setHtml({JsonSerializer.Serialize(ComposeViewModel.TextToParagraphs(text))})");
                await ApplyEditorTypographyAsync();
            }
            Editor.Focus();
        }
        _vm.MarkEdited();
    }

    private void OnPlainChanged(object sender, TextChangedEventArgs e) { _vm.MarkEdited(); _vm.UpdateStats(PlainEditor.Text); }

    // ───────────────────────── attachments (C1) ─────────────────────────

    private void OnAttachmentOpen(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button || (sender as FrameworkElement)?.DataContext is not AttachmentItem a || a.Carried) return;
        if (File.Exists(a.Path)) Ui.OpenExternal(a.Path);
    }

    private void OnAttachmentMenu(object sender, ContextMenuEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not AttachmentItem a) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender };
        if (!a.Carried)
        {
            var open = new MenuItem { Header = "Open" }; open.Click += (_, _) => Ui.OpenExternal(a.Path); menu.Items.Add(open);
            var folder = new MenuItem { Header = "Show in folder" }; folder.Click += (_, _) => { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + a.Path + "\""); } catch { } }; menu.Items.Add(folder);
            if (a.IsPicture) { var inline = new MenuItem { Header = "Put in the message instead", IsEnabled = !_plain }; inline.Click += (_, _) => { InsertPictureFile(a.Path); _vm.RemoveAttachmentCommand.Execute(a); }; menu.Items.Add(inline); }
            menu.Items.Add(new Separator());
        }
        var remove = new MenuItem { Header = "Remove" }; remove.Click += (_, _) => _vm.RemoveAttachmentCommand.Execute(a); menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    // ───────────────────────── address book (A4), chips (A1, A3) ─────────────────────────

    private void OnAddressBook(object sender, RoutedEventArgs e)
    {
        var r = AddressBookDialog.Show(this);
        if (r != null) _vm.AddFromAddressBook(r.To, r.Cc, r.Bcc);
    }

    private void WireRecipientBoxes()
    {
        foreach (var box in new[] { ToBox, CcBox, BccBox, ReplyToBox })
        {
            box.Input.TextChanged += OnAddressChanged;
            box.Input.PreviewKeyDown += OnAddressKey;
            box.Input.LostKeyboardFocus += OnAddressLostFocus;
            box.Describe = DescribeAddress;
            box.OpenContactRequested += address => { if (Application.Current.MainWindow?.DataContext is MainViewModel mv) { mv.OpenContact(address); Application.Current.MainWindow.Activate(); } };
            box.MoveRequested += (entry, where) =>
            {
                if (where == "cc") { _vm.ShowCc = true; CcBox.AddChip(entry); }
                else { _vm.ShowBcc = true; BccBox.AddChip(entry); }
            };
        }
    }

    private string? DescribeAddress(string address)
    {
        try
        {
            var hit = AppServices.Engine.Contacts.Suggest(address, 3).FirstOrDefault(s => s.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            var times = hit.Count == 0 ? "" : hit.Count == 1 ? " · you wrote to them once" : $" · you wrote to them {hit.Count} times";
            return (hit.Saved ? "Saved contact" : "From your mail") + times;
        }
        catch { return null; }
    }

    private RecipientBox? BoxOf(TextBox input) => new[] { ToBox, CcBox, BccBox, ReplyToBox }.FirstOrDefault(b => b.Input == input);

    // ───────────────────────── window size (E5) ─────────────────────────

    private void RestorePlacement()
    {
        var c = ComposeConfig;
        if (c.Width >= MinWidth) Width = c.Width;
        if (c.Height >= MinHeight) Height = c.Height;
        if (!double.IsNaN(c.Left) && !double.IsNaN(c.Top))
        {
            var left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(c.Left, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
            var top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(c.Top, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height));
            // Several windows open at once stagger a little so they don't hide each other.
            var others = Application.Current.Windows.OfType<ComposeWindow>().Count(w => w != this && w.IsVisible);
            Left = left + others * 28;
            Top = top + others * 28;
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (c.Maximized) WindowState = WindowState.Maximized;
    }

    private readonly Ui.Debouncer _placementSave = new(TimeSpan.FromSeconds(1));

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => SavePlacementSoon();
    private void OnWindowMoved(object? sender, EventArgs e) => SavePlacementSoon();

    private void SavePlacementSoon()
    {
        if (!IsLoaded) return;
        _placementSave.Run(() =>
        {
            var c = ComposeConfig;
            c.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { c.Left = Left; c.Top = Top; c.Width = Width; c.Height = Height; }
            try { AppServices.Engine.Settings.Save(notify: false); } catch (Exception ex) { Log.Warn("compose placement: " + ex.Message); }
        });
    }

    // ───────────────────────── shortcuts coming from the editor page (E4) ─────────────────────────

    private void OnEditorKey(string key)
    {
        switch (key)
        {
            case "save": OnSaveDraft(this, new RoutedEventArgs()); break;
            case "find": OpenFind(false); break;
            case "replace": OpenFind(true); break;
            case "findclose": FindBar.Visibility = Visibility.Collapsed; FindCount.Text = ""; break;
            case "shortcuts": ComposePopups.Shortcuts(this); break;
            case "attach": OnAttach(this, new RoutedEventArgs()); break;
            case "cc": _vm.ShowCc = true; Dispatcher.BeginInvoke(new Action(() => CcBox.Input.Focus())); break;
            case "bcc": _vm.ShowBcc = true; Dispatcher.BeginInvoke(new Action(() => BccBox.Input.Focus())); break;
            case "assistant": if (_vm.ShowAiRail) _vm.ToggleAiPanelCommand.Execute(null); break;
            case "sendarchive": _ = _vm.SendAndArchiveAsync(); break;
            case "mentionpick": if (MentionPopup.IsOpen) AcceptMention(); break;
            case "mentiondown": if (MentionPopup.IsOpen) MentionList.SelectedIndex = Math.Min(MentionList.Items.Count - 1, MentionList.SelectedIndex + 1); break;
            case "mentionup": if (MentionPopup.IsOpen) MentionList.SelectedIndex = Math.Max(0, MentionList.SelectedIndex - 1); break;
            case "escape":
                if (MentionPopup.IsOpen) { MentionPopup.IsOpen = false; Script("setMentionOpen(false)"); }
                else if (LinkBubble.IsOpen || PictureBubble.IsOpen) { LinkBubble.IsOpen = false; PictureBubble.IsOpen = false; }
                else if (_vm.AiPanelOpen) _vm.AiPanelOpen = false;
                else Close();
                break;
        }
    }

    /// <summary>The questions the view model asks before sending (C2, C3).</summary>
    private Task<bool> AskForgotAttachmentAsync()
    {
        var (id, _) = ComposeChoiceDialog.Ask(this, "Did you mean to attach a file?", "Your message says \"attached\" but there is no attachment.",
            new[] { new ComposeChoiceDialog.Choice("Send anyway", "send"), new ComposeChoiceDialog.Choice("Add a file", "add", Primary: true) });
        if (id == "add") { OnAttach(this, new RoutedEventArgs()); return Task.FromResult(false); }
        return Task.FromResult(id == "send");
    }

    private Task<bool> AskOverLimitAsync(string summary)
    {
        var (id, _) = ComposeChoiceDialog.Ask(this, "The attachments are over the limit", summary + ". The server may refuse the message. Send anyway?",
            new[] { new ComposeChoiceDialog.Choice("Keep editing", "keep", Cancel: true), new ComposeChoiceDialog.Choice("Send anyway", "send", Primary: true) });
        return Task.FromResult(id == "send");
    }
}
