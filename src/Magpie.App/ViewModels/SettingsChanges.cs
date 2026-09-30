using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Magpie.App.Services;
using Magpie.Core.Mail;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>
/// Design AP1: Apply and "Apply and Close" are enabled only while something differs from what is saved. The check
/// builds the settings as Apply would save them (nothing written) and compares them with a snapshot taken at load
/// and after each Apply. Every property and list in the window is watched; the check itself runs a moment after
/// the last change, so typing stays smooth.
/// </summary>
public partial class SettingsViewModel
{
    [ObservableProperty] private bool _hasChanges;
    private string _baseline = "";
    private System.Windows.Threading.DispatcherTimer? _changeTimer;
    private readonly HashSet<string> _quiet = new() { nameof(HasChanges), nameof(Page), nameof(SearchText), nameof(TestStatus), nameof(TestOk), nameof(Testing), nameof(SignatureStatus), nameof(MailFolderInfo) };

    private void StartChangeTracking()
    {
        _changeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _changeTimer.Tick += (_, _) => { _changeTimer.Stop(); HasChanges = Snapshot() != _baseline; };
        PropertyChanged += (_, e) => { if (e.PropertyName != null && !_quiet.Contains(e.PropertyName)) NoteChange(); };
        foreach (var list in new INotifyCollectionChanged[] { Tags, Accounts, Templates, HoverLines, RowActions, ToolbarRows, AiConnections, QuickReplyList, RuleList })
            Track(list);
        _baseline = Snapshot();
        HasChanges = false;
    }

    /// <summary>Something outside a bound property changed (the API key box, the signature editor): check again soon.</summary>
    public void NoteChange()
    {
        if (_changeTimer == null) return;
        _changeTimer.Stop();
        _changeTimer.Start();
    }

    private void Track(INotifyCollectionChanged list)
    {
        if (list is IEnumerable<object> items) foreach (var it in items) TrackItem(it);
        list.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null) foreach (var it in e.NewItems) TrackItem(it);
            NoteChange();
        };
    }

    private void TrackItem(object? item)
    {
        if (item is INotifyPropertyChanged n) n.PropertyChanged += (_, _) => NoteChange();
        if (item is EditableRule r) { Track(r.Conditions); Track(r.Actions); }
    }

    /// <summary>The settings as Apply would save them now, as text (plus the things that aren't in settings.json).</summary>
    private string Snapshot()
    {
        var c = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_e.Config, AppSettings.Json), AppSettings.Json) ?? new AppSettings();
        c.SmartInbox = SmartInbox;
        c.MarkReadOnOpen = MarkReadOnOpen;
        c.RemoteImages = RemoteImages;
        c.UndoSendSeconds = UndoSendSeconds;
        c.SyncIntervalMinutes = SyncIntervalMinutes;
        c.CloseToTray = CloseToTray;
        c.Notifications = Notifications;
        c.NotifyPeopleOnly = NotifyPeopleOnly;
        c.NotificationSound = NotificationSound;
        c.Gatekeeper.Enabled = Gatekeeper;
        c.GoogleClientId = GoogleClientId.Trim();
        c.GoogleClientSecret = GoogleClientSecret.Trim();
        c.MicrosoftClientId = MicrosoftClientId.Trim();
        c.Tags = Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new TagDef { Name = t.Name.Trim().Replace(",", " "), Color = t.Color }).DistinctBy(t => t.Name).ToList();
        c.QuickReplies = QuickRepliesToSave();
        c.Templates = Templates.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new QuickTemplate { Name = t.Name.Trim(), Body = t.Body }).ToList();
        c.Ai.Enabled = AiEnabled;
        c.Ai.Summarise = Summarise;
        c.Ai.Draft = Draft;
        c.Ai.Rewrite = Rewrite;
        c.Ai.Replies = Replies;
        FlushAiEditor(SelectedAiConnection);
        c.Ai.Connections = AiConnections.Select(x => x.ToConnection()).ToList();
        c.Ai.ActiveId = (AiConnections.FirstOrDefault(x => x.IsActive) ?? AiConnections.FirstOrDefault())?.Id ?? "";
        c.Appearance = new Appearance
        {
            Theme = Theme, ButtonStyle = ButtonStyle, Colourful = Colourful, Counts = Counts, ShowStatusBar = ShowStatusBar, MenuFollowsToolbar = MenuFollowsToolbar,
            Toolbar = ToolbarRows.Select(r => new ToolbarButton { Id = r.Id, Visible = r.Visible }).ToList(),
            FolderHover = new FolderHoverSettings { Enabled = HoverEnabled, DelayMs = HoverDelay, Lines = HoverLines.Where(l => l.On).Select(l => l.Id).ToList() },
            RowActions = new RowActionsSettings { Mode = RowMode, ConfirmDeleteOver = ConfirmDeleteOver, Ids = RowActions.Where(a => a.On).Select(a => a.Id).ToList(),
                BulkUndoSeconds = c.Appearance.RowActions.BulkUndoSeconds },
        };
        c.Appearance.Normalise();
        c.Updates.AutoUpdate = AutoUpdate;
        c.Updates.AutoCheck = c.Updates.AutoDownload = AutoUpdate;
        c.Updates.IncludePrerelease = IncludePrerelease;
        c.Rules = ProjectRules(new Dictionary<string, MailRule>(_savedRules));
        c.Accounts = Accounts.Select(a => a.ToAccount()).ToList();
        var keys = ApiKeyChanged || AiConnections.Any(x => x.KeyChanged) || _deletedAiConnections.Count > 0;
        return JsonSerializer.Serialize(c, AppSettings.Json) + "|keys:" + keys + "|startup:" + StartWithWindows;
    }
}
