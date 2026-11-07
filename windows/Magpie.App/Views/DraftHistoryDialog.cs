using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Magpie.App.Services;
using Magpie.App.ViewModels;
using Magpie.Core.Mail;
using Magpie.Core.Models;

namespace Magpie.App.Views;

/// <summary>Read-only local revision preview. Restore is explicit, checkpointed and never sends mail.</summary>
public sealed class DraftHistoryDialog : Window
{
    private readonly ComposeViewModel _vm;
    private readonly ListBox _versions = new();
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _status = new() { Text = "Loading revisions…", TextWrapping = TextWrapping.Wrap };
    private readonly Button _restore = new() { Content = "Restore selected revision", IsEnabled = false };
    private readonly Button _clear = new() { Content = "Clear history…", IsEnabled = false };
    private bool _busy;
    private int _previewRequest;

    public DraftHistoryDialog(ComposeViewModel vm)
    {
        _vm = vm;
        Title = "Draft history — Magpie";
        Width = 780;
        Height = 690;
        MinWidth = 480;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, "Window.Dialog");
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(190) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var note = new TextBlock
        {
            Text = "On this PC only · Last 30 revisions per draft, up to 30 days.\nSnapshots include attachment contents. Maximum 32 MB per revision and 200 MB total history; older revisions are pruned."
                + (vm.SupportsRichDraftRestore ? "" : "\nThe rich editor is unavailable: you can preview all revisions, but only plain-text revisions can be restored safely."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        root.Children.Add(note);
        Grid.SetRow(_versions, 1);
        _versions.Margin = new Thickness(0, 0, 0, 12);
        AutomationProperties.SetName(_versions, "Saved draft revisions, newest first");
        _versions.SelectionChanged += async (_, _) => await PreviewSelectedAsync();
        root.Children.Add(_versions);
        Grid.SetRow(_preview, 2);
        AutomationProperties.SetName(_preview, "Read-only revision preview");
        root.Children.Add(_preview);
        Grid.SetRow(_status, 3);
        _status.Margin = new Thickness(0, 10, 0, 10);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
        root.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _clear.SetResourceReference(StyleProperty, "Button.Secondary");
        _restore.SetResourceReference(StyleProperty, "Button.Primary");
        _clear.Margin = new Thickness(0, 0, 8, 0);
        _restore.Margin = new Thickness(0, 0, 8, 0);
        var close = new Button { Content = "Close", IsCancel = true };
        close.SetResourceReference(StyleProperty, "Button.Secondary");
        close.Click += (_, _) => Close();
        _clear.Click += async (_, _) => await ClearAsync();
        _restore.Click += async (_, _) => await RestoreAsync();
        actions.Children.Add(_clear);
        actions.Children.Add(_restore);
        actions.Children.Add(close);
        Grid.SetRow(actions, 4);
        root.Children.Add(actions);
        Content = root;
        Loaded += async (_, _) => await LoadAsync();
        Closing += (_, e) => { if (_busy) e.Cancel = true; };
    }

    private DraftRevision? Selected => (_versions.SelectedItem as ListBoxItem)?.Tag as DraftRevision;

    private async Task LoadAsync()
    {
        _busy = true;
        try
        {
            var entries = await _vm.LoadDraftHistoryAsync();
            foreach (var entry in entries)
                _versions.Items.Add(new ListBoxItem
                {
                    Tag = entry,
                    Content = $"{entry.Created.LocalDateTime:g}  ·  {(string.IsNullOrWhiteSpace(entry.Subject) ? "(no subject)" : entry.Subject)}  ·  {HtmlRenderer.FormatSize(entry.SizeBytes)}",
                    ToolTip = entry.ToText,
                });
            _status.Text = entries.Count == 0 ? "No revisions yet. Edits are recorded when the draft is autosaved." : $"{entries.Count} revision(s). Select one to preview its recipients, body and attachments.";
            _clear.IsEnabled = entries.Count > 0;
            if (entries.Count > 0) _versions.SelectedIndex = 0;
        }
        catch (Exception ex) { _status.Text = "Could not load draft history: " + ex.Message; }
        finally { _busy = false; _restore.IsEnabled = Selected != null; }
    }

    private async Task PreviewSelectedAsync()
    {
        var request = ++_previewRequest;
        var selected = Selected;
        _restore.IsEnabled = selected != null && !_busy;
        if (selected == null) { _preview.Text = ""; return; }
        try
        {
            var revision = await _vm.ReadDraftRevisionAsync(selected.Id);
            if (request != _previewRequest) return;
            if (revision == null) { _preview.Text = "This revision is no longer available."; _restore.IsEnabled = false; return; }
            var text = MimeText.HtmlToText(revision.Html);
            if (text.Length > 120_000) text = text[..120_000] + "\n\n[Preview shortened; restoring uses the full saved revision.]";
            var attachments = revision.CarriedParts.Select(p => (p as MimeKit.MimePart)?.FileName ?? "attached message");
            _preview.Text = $"To: {revision.To}\nCc: {revision.Cc}\nBcc: {revision.Bcc}\nReply-To: {revision.ReplyTo}\nSubject: {revision.Subject}\nFormat: {(revision.IsPlainText ? "Plain text" : "Rich text")} · Priority: {revision.Priority}\nRequest read receipt: {(revision.RequestReadReceipt ? "Yes" : "No")}\nAttachments: {string.Join(", ", attachments)}\n\n{text}";
        }
        catch (Exception ex) { if (request == _previewRequest) { _preview.Text = "Preview unavailable: " + ex.Message; _restore.IsEnabled = false; } }
    }

    private async Task RestoreAsync()
    {
        if (_busy || Selected is not { } selected) return;
        if (MessageBox.Show(this, "Restore this revision's recipients, subject, message body and attachments?\n\nThe current content will first be saved as another local revision. Nothing will be sent.",
                "Restore draft revision", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true;
        _restore.IsEnabled = _clear.IsEnabled = false;
        try
        {
            if (!await _vm.RestoreDraftRevisionAsync(selected.Id)) { _status.Text = "The draft cannot be restored while it is closing or sending."; return; }
            _busy = false;
            DialogResult = true;
        }
        catch (Exception ex) { Ui.Error("Restore draft revision", ex.Message, this); }
        finally { _busy = false; _restore.IsEnabled = Selected != null; _clear.IsEnabled = _versions.Items.Count > 0; }
    }

    private async Task ClearAsync()
    {
        if (_busy) return;
        if (MessageBox.Show(this, "Permanently delete the saved revisions for this draft on this PC?\n\nThe open message and saved draft are kept. Subsequent saves or opening Draft history can create a new current snapshot.",
                "Clear draft history", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _busy = true;
        _restore.IsEnabled = _clear.IsEnabled = false;
        try
        {
            await _vm.ClearDraftHistoryAsync();
            _versions.Items.Clear();
            _preview.Text = "";
            _status.Text = "History cleared. The open message was not changed.";
        }
        catch (Exception ex) { Ui.Error("Clear draft history", ex.Message, this); }
        finally { _busy = false; _clear.IsEnabled = _versions.Items.Count > 0; _restore.IsEnabled = Selected != null; }
    }
}
