using System.Reflection;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

public partial class EditableAccount : ObservableObject
{
    public Account Original { get; }
    public EditableAccount(Account a)
    {
        Original = a;
        _displayName = a.DisplayName;
        _signature = a.Signature;
        _signatureHtml = a.SignatureHtml;
        _signatureOnNew = a.SignatureOnNew;
        _signatureOnReplies = a.SignatureOnReplies;
        _color = a.Color;
        _enabled = a.Enabled;
        _syncDays = a.SyncDays;
    }
    public string Email => Original.Email;
    public string Kind => Original.Kind switch { AccountKind.Gmail => "Gmail", AccountKind.Microsoft => "Outlook / Microsoft 365", _ => "IMAP" }
                          + (Original.Auth == AuthMethod.OAuth2 ? " · signed in with " + (Original.Kind == AccountKind.Gmail ? "Google" : "Microsoft") : " · password");
    public string Servers => $"IMAP {Original.ImapHost}:{Original.ImapPort} · SMTP {Original.SmtpHost}:{Original.SmtpPort}";
    [ObservableProperty] private string _displayName;
    [ObservableProperty] private string _signature;
    /// <summary>Rich signature (design B6), edited in Settings → Signatures &amp; replies.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(SignaturePlain))] private string _signatureHtml;
    [ObservableProperty] private bool _signatureOnNew;
    [ObservableProperty] private bool _signatureOnReplies;
    [ObservableProperty] private bool _isSignatureSelected;
    [ObservableProperty] private string _color;

    /// <summary>Plain-text stand-in for the rich editor when WebView2 isn't available.</summary>
    public string SignaturePlain
    {
        get => MimeText.HtmlToText(SignatureHtml).Trim();
        set => SignatureHtml = string.IsNullOrWhiteSpace(value) ? "" : System.Net.WebUtility.HtmlEncode(value.Trim()).Replace("\r\n", "\n").Replace("\n", "<br>");
    }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _syncDays;

    public Account ToAccount()
    {
        var a = Original.Clone();
        a.DisplayName = DisplayName.Trim();
        a.SignatureHtml = SignatureHtml.Trim();
        // The old plain-text field mirrors the rich one, so it never brings back a signature that was cleared.
        if (a.SignatureHtml != Original.SignatureHtml) a.Signature = MimeText.HtmlToText(a.SignatureHtml).Trim();
        a.SignatureOnNew = SignatureOnNew;
        a.SignatureOnReplies = SignatureOnReplies;
        a.Color = Color;
        a.Enabled = Enabled;
        a.SyncDays = Math.Clamp(SyncDays, 7, 3650);
        return a;
    }

    public bool Changed => DisplayName.Trim() != Original.DisplayName || SignatureHtml.Trim() != Original.SignatureHtml
                           || SignatureOnNew != Original.SignatureOnNew || SignatureOnReplies != Original.SignatureOnReplies || Color != Original.Color
                           || Enabled != Original.Enabled || SyncDays != Original.SyncDays;
}

public partial class EditableTag : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _color = "#14606E";
}

public partial class EditableQuickReply : ObservableObject
{
    [ObservableProperty] private string _text = "";
}

public partial class EditableTemplate : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _body = "";
}

public sealed record Choice<T>(T Value, string Label);

/// <summary>One row of Settings → Toolbar &amp; buttons (design C3).</summary>
public partial class EditableToolbarButton : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name => ToolbarButtonVm.NameOf(Id);
    public string IconKey => ToolbarButtonVm.IconOf(Id);
    public string Shortcut => ToolbarButtonVm.KeyOf(Id) is { Length: > 0 } k ? k : "—";
    public string ShowLabel => "Show " + Name;
    public string UpLabel => "Move " + Name + " up";
    public string DownLabel => "Move " + Name + " down";
    [ObservableProperty] private bool _visible;
}

/// <summary>One line the folder hover card can show (design H2).</summary>
public partial class EditableHoverLine : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name => FolderHoverSettings.NameOf(Id);
    [ObservableProperty] private bool _on;
}

/// <summary>One action a row can offer (design H3): ticked ones show, in this order (at most five).</summary>
public partial class EditableRowAction : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name => ToolbarButtonVm.NameOf(Id);
    public string IconKey => ToolbarButtonVm.IconOf(Id);
    public string UpLabel => "Move " + Name + " up";
    public string DownLabel => "Move " + Name + " down";
    [ObservableProperty] private bool _on;
}

/// <summary>One hit of the Settings search (design SS1).</summary>
public sealed record SettingHit(string Name, string Page, string PageLabel, string Description, string IconKey, string Anchor);

/// <summary>One "Quick setup" button on the AI page (Off · A · B · C · Custom).</summary>
public partial class AiPreset : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary>Settings window. Edits a copy; Save applies everything at once.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly MailEngine _e = AppServices.Engine;

    [ObservableProperty] private string _page = "General";

    // General
    [ObservableProperty] private bool _smartInbox;
    [ObservableProperty] private bool _markReadOnOpen;
    [ObservableProperty] private RemoteImages _remoteImages;
    [ObservableProperty] private int _undoSendSeconds;
    [ObservableProperty] private int _syncIntervalMinutes;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;
    public ObservableCollection<EditableTag> Tags { get; } = new();
    public int[] UndoChoices { get; } = { 0, 5, 10, 20, 30 };
    public int[] IntervalChoices { get; } = { 1, 2, 5, 10, 15, 30, 60 };
    public string[] ColorChoices { get; } = { "#14606E", "#4B3F86", "#B3261E", "#B45309", "#1B6B2E", "#2F5BEA", "#8A5300", "#5A6068" };

    // Gatekeeper (design B7)
    [ObservableProperty] private bool _gatekeeper;
    public ObservableCollection<string> Blocked { get; } = new();
    public bool HasBlocked => Blocked.Count > 0;

    /// <summary>Unblock takes effect at once: future mail from them lands normally (mail already in Spam stays there).</summary>
    [RelayCommand]
    private void Unblock(string? address)
    {
        if (address == null) return;
        _e.Config.Gatekeeper.Blocked.Remove(address);
        _e.Settings.Save(notify: false);
        Blocked.Remove(address);
        OnPropertyChanged(nameof(HasBlocked));
    }

    // Notifications
    [ObservableProperty] private bool _notifications;
    [ObservableProperty] private bool _notifyPeopleOnly;
    [ObservableProperty] private bool _notificationSound;

    // Accounts
    public ObservableCollection<EditableAccount> Accounts { get; } = new();
    [ObservableProperty] private string _googleClientId = "";
    [ObservableProperty] private string _googleClientSecret = "";
    [ObservableProperty] private string _microsoftClientId = "";

    // Templates
    public ObservableCollection<EditableTemplate> Templates { get; } = new();

    // ── AI features (S1–S3) ──
    [ObservableProperty] private bool _aiEnabled;
    [ObservableProperty] private AiProviderKind _provider;
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private bool _summarise;
    [ObservableProperty] private bool _draft;
    [ObservableProperty] private bool _rewrite;
    [ObservableProperty] private bool _replies;
    [ObservableProperty] private string _testStatus = "";
    [ObservableProperty] private bool _testOk;
    [ObservableProperty] private bool _testing;
    public string ApiKey { get; set; } = "";
    public bool ApiKeyChanged { get; set; }

    /// <summary>Quick setup: each preset sets the master switch and the four feature switches.</summary>
    public List<AiPreset> Presets { get; } = new()
    {
        new() { Id = "Off", Name = "Off", Description = "No AI anywhere" },
        new() { Id = "A", Name = "A · Provider only", Description = "Set up and tested, nothing calls it" },
        new() { Id = "B", Name = "B · Summarise", Description = "One button in threads" },
        new() { Id = "C", Name = "C · Full assistant", Description = "Summaries, drafts, rewrite, replies" },
        new() { Id = "Custom", Name = "Custom", Description = "Your own mix of switches" },
    };

    /// <summary>Which preset the current switches match (Custom when none).</summary>
    public string CurrentPreset => AiPresets.Match(AiEnabled, Summarise, Draft, Rewrite, Replies);

    public string PresetSummary => CurrentPreset switch
    {
        "Off" => "Off — no AI controls anywhere and nothing is ever sent to a model.",
        "A" => "Option A — the provider is set up and tested, but nothing in your mail calls it yet. Turn on a feature (or pick B / C) when you are ready.",
        "B" => "Option B — only the Summarise button appears in threads. Compose is untouched.",
        "C" => "Option C — the full assistant: summaries, the compose rail, rewrite chips and suggested replies.",
        _ => "Custom — your own mix of switches. Each feature still asks for consent the first time it sends data to a cloud provider.",
    };

    [RelayCommand]
    private void ApplyPreset(string? id)
    {
        if (id == null || !AiPresets.TryGet(id, out var p)) return;   // "Custom" is only a state, not an action
        AiEnabled = p.Master;
        Summarise = p.Summarise;
        Draft = p.Draft;
        Rewrite = p.Rewrite;
        Replies = p.Replies;
    }

    private void RefreshPreset()
    {
        var cur = CurrentPreset;
        foreach (var p in Presets) p.IsSelected = p.Id == cur;
        OnPropertyChanged(nameof(CurrentPreset));
        OnPropertyChanged(nameof(PresetSummary));
    }

    partial void OnAiEnabledChanged(bool value) => RefreshPreset();
    partial void OnSummariseChanged(bool value) => RefreshPreset();
    partial void OnDraftChanged(bool value) => RefreshPreset();
    partial void OnRewriteChanged(bool value) => RefreshPreset();
    partial void OnRepliesChanged(bool value) => RefreshPreset();
    public List<Choice<AiProviderKind>> Providers { get; } = new()
    {
        new(AiProviderKind.OpenAI, "OpenAI"),
        new(AiProviderKind.Anthropic, "Anthropic (Claude)"),
        new(AiProviderKind.Ollama, "Ollama — local model on this PC"),
        new(AiProviderKind.Custom, "Other OpenAI-compatible endpoint"),
    };
    public bool IsLocal => AiProviderFactory.IsLocalEndpoint(Endpoint);
    public string KeyHint => IsLocal ? "(not needed for local models)" : Provider == AiProviderKind.Custom ? "(if your endpoint needs one)" : "";

    public string Version => "Magpie " + UpdateService.Current;
    public string VersionNumber => UpdateService.Current.ToString();

    // ── About Me (design A1): Krishna's standard block ──
    public string AuthorName => "Krishna Dipayan Bhunia";
    public string FeedbackEmail => "kri.subsc@gmail.com";
    public string LinkedIn => "linkedin.com/in/krishnabhunia";
    public string Facebook => "facebook.com/kdbhunia";
    public string VersionLine => "Magpie · Version " + VersionNumber;
    public string ReleasedLine => "Released on " + ReleaseDate;
    /// <summary>dd-MMM-yyyy from the assembly's ReleaseDate metadata (Directory.Build.props).</summary>
    public static string ReleaseDate
    {
        get
        {
            var raw = typeof(SettingsViewModel).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "ReleaseDate")?.Value;
            return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                ? d.ToString("dd-MMM-yyyy", System.Globalization.CultureInfo.InvariantCulture) : (raw ?? "");
        }
    }
    public static readonly string[] FeedbackKinds = { "New feature request", "Bug found", "Crash / error report", "Feedback", "Other" };

    /// <summary>Opens Magpie's own compose window with the feedback email pre-filled (subject "[Magpie v1.1.2] Bug found", version, device info, recent log lines for bugs).</summary>
    public void ComposeFeedback(string kind)
    {
        var body = new System.Text.StringBuilder();
        body.AppendLine("App: Magpie " + VersionNumber + " (released " + ReleaseDate + ")");
        body.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " x64" : " x86"));
        body.AppendLine("Machine: " + Environment.MachineName + " · .NET " + Environment.Version);
        body.AppendLine("Accounts: " + _e.Accounts.Count);
        if (kind is "Bug found" or "Crash / error report")
        {
            var tail = Log.Tail(20);
            if (tail.Length > 0) { body.AppendLine(); body.AppendLine("Last 20 log lines:"); body.AppendLine(tail); }
        }
        body.AppendLine();
        body.AppendLine("--- Describe below ---");
        body.AppendLine();
        var mailto = "mailto:" + FeedbackEmail + "?subject=" + Uri.EscapeDataString($"[Magpie v{VersionNumber}] {kind}") + "&body=" + Uri.EscapeDataString(body.ToString());
        Views.ComposeWindow.OpenMailto(mailto);
    }

    [RelayCommand] private void OpenLinkedIn() => Ui.OpenExternal("https://www." + LinkedIn);
    [RelayCommand] private void OpenFacebook() => Ui.OpenExternal("https://www." + Facebook);

    // ── Search (design SS1) ──
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSearching), nameof(SearchHeading), nameof(SearchSub))] private string _searchText = "";
    public bool IsSearching => SearchText.Trim().Length > 0;
    public ObservableCollection<SettingHit> SearchHits { get; } = new();
    public string SearchHeading => IsSearching ? $"Results for \"{SearchText.Trim()}\"" : "";
    public string SearchSub => !IsSearching ? "" : SearchHits.Count == 0 ? "Nothing matches. Try another word." : $"{SearchHits.Count} setting{(SearchHits.Count == 1 ? "" : "s")} found — click one to go to it";
    /// <summary>Matches per page, shown as a badge in the page list; "" when not searching or none.</summary>
    [ObservableProperty] private string _hitsGeneral = "", _hitsToolbar = "", _hitsAccounts = "", _hitsAI = "", _hitsRules = "", _hitsSignatures = "", _hitsNotifications = "", _hitsTemplates = "", _hitsKeys = "", _hitsUpdates = "", _hitsAbout = "";

    private static readonly (string Name, string Page, string Desc, string Keys, string Anchor)[] Index =
    {
        ("Smart inbox", "General", "People · Notifications · Newsletters tabs", "smart inbox people newsletters categories tabs", "RowSmartInbox"),
        ("Mark as read when opened", "General", "Otherwise conversations stay unread", "read unread mark open", "RowMarkRead"),
        ("Pictures from the internet", "General", "Block remote images to stop tracking", "images pictures photos tracking privacy remote load", "RowImages"),
        ("Undo send", "General", "How long you can take a message back after pressing Send", "undo send cancel recall seconds send now", "RowUndo"),
        ("Check all folders every", "General", "How often folders are checked for changes", "sync interval minutes check refresh poll", "RowInterval"),
        ("Keep running in the notification area", "General", "Tray icon when the window is closed", "tray close minimise background notification area", "RowTray"),
        ("Start with Windows", "General", "Starts quietly when you sign in", "startup boot login start windows", "RowStartup"),
        ("Tags", "General", "Your tags and their colours", "tags labels colour color", "RowTags"),
        ("Gatekeeper", "General", "New senders wait at the door until you Allow or Block them", "gatekeeper new senders unknown block allow screen spam door blocked unblock", "RowGatekeeper"),
        ("Toolbar buttons", "Toolbar", "Which buttons sit above a conversation, and their order", "buttons toolbar order archive delete snooze show hide", "RowToolbar"),
        ("Button style", "Toolbar", "Icon + name · Icon only · Name only", "buttons style icon name text", "RowButtonStyle"),
        ("Colourful icons", "Toolbar", "Coloured icons everywhere, or plain grey", "colour color icons colourful grey look", "RowLook"),
        ("Show the status bar", "Toolbar", "One row at the bottom of the window", "status bar bottom activity", "RowLook"),
        ("Folder numbers", "Toolbar", "Unread / total · Unread only · Off", "counts numbers unread total badge folder", "RowLook"),
        ("Folder details on hover", "Toolbar", "The card that pops up when you point at a folder", "hover card folder details unread total today oldest size attachments delay", "RowHover"),
        ("Buttons on email rows", "Toolbar", "Hover actions on each row, and which ones", "hover row actions buttons archive delete always never multi select bulk", "RowRowActions"),
        ("Sidebar width", "Toolbar", "Drag the sidebar's edge; narrower snaps to the icon rail (Ctrl+Shift+← / →)", "sidebar width rail narrow resize drag icon", "RowRowActions"),
        ("Accounts", "Accounts", "Name, colour, sync on/off for each account", "account email signature colour password sign in", "RowAccounts"),
        ("Google / Microsoft sign-in apps", "Accounts", "Your own client ID for Google or Microsoft sign-in", "google microsoft client id secret oauth sign in json", "RowSignIn"),
        ("AI quick setup", "AI", "Off · A · B · C · Custom", "ai presets quick setup off custom", "RowAiPresets"),
        ("Enable AI features", "AI", "The master switch: nothing is sent to a model while it is off", "ai enable master switch privacy", "RowAiMaster"),
        ("AI provider", "AI", "OpenAI · Anthropic · Ollama · other; endpoint, key, model", "ai provider key model openai anthropic ollama endpoint api", "RowAiProvider"),
        ("AI features", "AI", "Summarise, drafts, rewrite, suggested replies", "summarise summary draft rewrite replies suggest ai", "RowAiFeatures"),
        ("New-mail notification", "Notifications", "Show a notification for new mail", "notifications alert new mail toast", "RowNotify"),
        ("Only for mail from people", "Notifications", "Stay quiet for newsletters and automatic mail", "notifications people only newsletters quiet", "RowNotifyPeople"),
        ("Notification sound", "Notifications", "Play a sound for new mail", "sound notifications alert new mail", "RowNotifySound"),
        ("Templates", "Templates", "Text you insert often in a new message", "templates canned quick text snippets", "RowTemplates"),
        ("Keyboard shortcuts", "Keys", "Every key Magpie understands", "keyboard keys shortcuts ctrl hotkeys", "RowKeys"),
        ("Check for updates", "Updates", "Check now, download, restart into the new version", "update check download restart version github", "RowUpdateCard"),
        ("Check for updates automatically", "Updates", "At start, then once a day", "update automatic check daily", "RowUpdateSwitches"),
        ("Download updates in the background", "Updates", "Ask before restarting", "update download background", "RowUpdateSwitches"),
        ("Include test versions", "Updates", "Pre-releases", "update prerelease beta test", "RowUpdateSwitches"),
        ("About Me", "About", "Krishna's details, feedback email, LinkedIn, Facebook", "about author krishna feedback bug report email linkedin facebook contact suggestion", "RowAboutMe"),
        ("Data folder", "About", "Where mail, settings and the log live on this PC", "data folder log file appdata storage", "RowData"),
        ("Rules", "Rules", "Sort new mail automatically: move, tag, mark read, pin, snooze, delete", "rules filters sort move automatic organise organize folder tag skip notification", "RowRules"),
        ("Signature", "Signatures", "A signature for each account, with pictures; new messages and replies", "signature sign off logo picture name footer html rich", "RowSignatures"),
        ("Quick replies", "Signatures", "Short answers you send with one click under a conversation", "quick replies canned answers thanks one click send chips", "RowQuickReplies"),
        ("Theme", "Toolbar", "Match Windows · Light · Dark", "dark theme night light mode appearance black white colours colors windows", "RowTheme"),
    };

    private static readonly Dictionary<string, (string Label, string Icon)> Pages = new()
    {
        ["General"] = ("General", "general"), ["Toolbar"] = ("Appearance", "toolbar"), ["Accounts"] = ("Accounts", "accounts"), ["AI"] = ("AI features", "summarise"), ["Rules"] = ("Rules", "rules"), ["Signatures"] = ("Signatures & replies", "signature"),
        ["Notifications"] = ("Notifications", "notifications"), ["Templates"] = ("Templates", "templates"), ["Keys"] = ("Keyboard shortcuts", "keys"),
        ["Updates"] = ("Updates", "update"), ["About"] = ("About", "about"),
    };

    /// <summary>Every setting whose name, description or plain words contain the text; "" finds nothing.</summary>
    public static List<SettingHit> Search(string text)
    {
        var q = (text ?? "").Trim().ToLowerInvariant();
        if (q.Length == 0) return new();
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return Index.Where(i =>
            {
                var hay = (i.Name + " " + i.Desc + " " + i.Keys + " " + Pages[i.Page].Label).ToLowerInvariant();
                return words.All(hay.Contains);
            })
            .Select(i => new SettingHit(i.Name, i.Page, Pages[i.Page].Label, i.Desc, Pages[i.Page].Icon, i.Anchor)).ToList();
    }

    partial void OnSearchTextChanged(string value)
    {
        SearchHits.Clear();
        foreach (var h in Search(value)) SearchHits.Add(h);
        string Badge(string page) => IsSearching ? (SearchHits.Count(h => h.Page == page) is var n && n > 0 ? n.ToString() : "") : "";
        HitsGeneral = Badge("General"); HitsToolbar = Badge("Toolbar"); HitsAccounts = Badge("Accounts"); HitsAI = Badge("AI"); HitsRules = Badge("Rules"); HitsSignatures = Badge("Signatures"); HitsNotifications = Badge("Notifications");
        HitsTemplates = Badge("Templates"); HitsKeys = Badge("Keys"); HitsUpdates = Badge("Updates"); HitsAbout = Badge("About");
        OnPropertyChanged(nameof(SearchSub));
    }

    /// <summary>Raised when a search result is chosen: the window opens that page and flashes the row.</summary>
    public event Action<string>? HighlightRequested;

    [RelayCommand]
    private void GoToHit(SettingHit? hit)
    {
        if (hit == null) return;
        SearchText = "";
        Page = hit.Page;
        HighlightRequested?.Invoke(hit.Anchor);
    }

    // ── Folder details on hover (design H2) ──
    [ObservableProperty] private bool _hoverEnabled;
    [ObservableProperty] private int _hoverDelay;
    public ObservableCollection<EditableHoverLine> HoverLines { get; } = new();
    public int[] HoverDelayChoices { get; } = { 300, 600, 1000 };

    // ── Buttons on email rows (design H3) ──
    [ObservableProperty] private RowActionsMode _rowMode;
    [ObservableProperty] private int _confirmDeleteOver;
    public ObservableCollection<EditableRowAction> RowActions { get; } = new();
    public ObservableCollection<ToolbarButtonVm> RowPreview { get; } = new();
    public List<Choice<RowActionsMode>> RowModes { get; } = new()
    {
        new(RowActionsMode.OnHover, "On hover"), new(RowActionsMode.Always, "Always"), new(RowActionsMode.Never, "Never"),
    };
    public int[] ConfirmChoices { get; } = { 0, 5, 10, 20, 50 };
    [ObservableProperty] private string _rowLimitNote = "";

    private void LoadRowActions(RowActionsSettings r)
    {
        foreach (var a in RowActions) a.PropertyChanged -= OnRowActionChanged;
        RowActions.Clear();
        foreach (var id in r.Ids.Concat(RowActionsSettings.ActionIds.Where(id => !r.Ids.Contains(id))))
        {
            var row = new EditableRowAction { Id = id, On = r.Ids.Contains(id) };
            row.PropertyChanged += OnRowActionChanged;
            RowActions.Add(row);
        }
        RefreshRowPreview();
    }

    private void OnRowActionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EditableRowAction.On)) return;
        // At most five: the sixth tick is undone.
        if (sender is EditableRowAction { On: true } r && RowActions.Count(a => a.On) > RowActionsSettings.MaxButtons)
        {
            r.On = false;
            RowLimitNote = "Up to 5 buttons fit on a row — untick one first.";
        }
        else RowLimitNote = "";
        RefreshRowPreview();
    }

    private void RefreshRowPreview()
    {
        RowPreview.Clear();
        foreach (var a in RowActions.Where(a => a.On)) RowPreview.Add(ToolbarButtonVm.For(a.Id, ButtonStyle.IconOnly));
    }

    [RelayCommand]
    private void MoveRowActionUp(EditableRowAction? row)
    {
        if (row == null) return;
        var i = RowActions.IndexOf(row);
        if (i > 0) { RowActions.Move(i, i - 1); RefreshRowPreview(); }
    }

    [RelayCommand]
    private void MoveRowActionDown(EditableRowAction? row)
    {
        if (row == null) return;
        var i = RowActions.IndexOf(row);
        if (i >= 0 && i < RowActions.Count - 1) { RowActions.Move(i, i + 1); RefreshRowPreview(); }
    }

    [RelayCommand]
    private void SetRowMode(string? mode)
    {
        if (Enum.TryParse<RowActionsMode>(mode, out var v)) RowMode = v;
    }

    // ── Appearance: theme (design B1) ──
    [ObservableProperty] private ThemeMode _theme;
    public bool WindowsIsDark { get; } = ThemeManager.WindowsUsesDark();
    public string MatchWindowsNote => "Windows is set to " + (WindowsIsDark ? "dark" : "light") + " right now. Magpie changes with it.";

    [RelayCommand]
    private void SetTheme(string? mode)
    {
        if (Enum.TryParse<ThemeMode>(mode, out var v)) Theme = v;
    }

    // ── Toolbar & buttons (design C3) ──
    public ObservableCollection<EditableToolbarButton> ToolbarRows { get; } = new();
    public ObservableCollection<ToolbarButtonVm> ToolbarPreview { get; } = new();
    [ObservableProperty] private ButtonStyle _buttonStyle;
    [ObservableProperty] private bool _colourful;
    [ObservableProperty] private CountsMode _counts;
    [ObservableProperty] private bool _showStatusBar;
    [ObservableProperty] private bool _menuFollowsToolbar;
    public List<Choice<ButtonStyle>> ButtonStyles { get; } = new()
    {
        new(ButtonStyle.IconAndName, "Icon + name"),
        new(ButtonStyle.IconOnly, "Icon only (name shows on hover)"),
        new(ButtonStyle.NameOnly, "Name only"),
    };
    public List<Choice<CountsMode>> CountChoices { get; } = new()
    {
        new(CountsMode.UnreadAndTotal, "Unread / total (3 / 10)"),
        new(CountsMode.UnreadOnly, "Unread only (3)"),
        new(CountsMode.Off, "Off"),
    };

    partial void OnButtonStyleChanged(ButtonStyle value) => RefreshPreview();
    partial void OnColourfulChanged(bool value) => RefreshPreview();

    private void LoadToolbar(Appearance a)
    {
        foreach (var r in ToolbarRows) r.PropertyChanged -= OnToolbarRowChanged;
        ToolbarRows.Clear();
        foreach (var b in a.Toolbar)
        {
            var row = new EditableToolbarButton { Id = b.Id, Visible = b.Visible };
            row.PropertyChanged += OnToolbarRowChanged;
            ToolbarRows.Add(row);
        }
        RefreshPreview();
    }

    private void OnToolbarRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        ToolbarPreview.Clear();
        foreach (var r in ToolbarRows.Where(r => r.Visible)) ToolbarPreview.Add(ToolbarButtonVm.For(r.Id, ButtonStyle));
    }

    [RelayCommand]
    private void MoveToolbarUp(EditableToolbarButton? row)
    {
        if (row == null) return;
        var i = ToolbarRows.IndexOf(row);
        if (i > 0) { ToolbarRows.Move(i, i - 1); RefreshPreview(); }
    }

    [RelayCommand]
    private void MoveToolbarDown(EditableToolbarButton? row)
    {
        if (row == null) return;
        var i = ToolbarRows.IndexOf(row);
        if (i >= 0 && i < ToolbarRows.Count - 1) { ToolbarRows.Move(i, i + 1); RefreshPreview(); }
    }

    [RelayCommand]
    private void SetButtonStyle(string? style)
    {
        if (Enum.TryParse<ButtonStyle>(style, out var v)) ButtonStyle = v;
    }

    [RelayCommand]
    private void ResetToolbar()
    {
        var d = new Appearance();
        ButtonStyle = d.ButtonStyle;
        Colourful = d.Colourful;
        Counts = d.Counts;
        ShowStatusBar = d.ShowStatusBar;
        MenuFollowsToolbar = d.MenuFollowsToolbar;
        LoadToolbar(d);
        HoverEnabled = d.FolderHover.Enabled;
        HoverDelay = d.FolderHover.DelayMs;
        foreach (var l in HoverLines) l.On = d.FolderHover.Lines.Contains(l.Id);
        RowMode = d.RowActions.Mode;
        ConfirmDeleteOver = d.RowActions.ConfirmDeleteOver;
        LoadRowActions(d.RowActions);
    }

    // ── Updates (design U1): the live state comes from the update service; the switches save with the rest ──
    public UpdateService? Updates => AppServices.Updates;
    [ObservableProperty] private bool _autoCheckUpdates;
    [ObservableProperty] private bool _autoDownloadUpdates;
    [ObservableProperty] private bool _includePrerelease;
    public string UpdateSource => "Updates come from github.com/" + Core.Updates.UpdateClient.Repo;
    public string DataFolder => _e.Paths.Root;

    public event Action? Saved;

    public SettingsViewModel(string? page)
    {
        var c = _e.Config;
        _page = page ?? "General";
        _smartInbox = c.SmartInbox;
        _markReadOnOpen = c.MarkReadOnOpen;
        _remoteImages = c.RemoteImages;
        _undoSendSeconds = c.UndoSendSeconds;
        _syncIntervalMinutes = c.SyncIntervalMinutes;
        _closeToTray = c.CloseToTray;
        _startWithWindows = StartupRegistration.IsEnabled();
        _notifications = c.Notifications;
        _gatekeeper = c.Gatekeeper.Enabled;
        foreach (var b in c.Gatekeeper.Blocked) Blocked.Add(b);
        _notifyPeopleOnly = c.NotifyPeopleOnly;
        _notificationSound = c.NotificationSound;
        _googleClientId = c.GoogleClientId;
        _googleClientSecret = c.GoogleClientSecret;
        _microsoftClientId = c.MicrosoftClientId;
        foreach (var t in c.Tags) Tags.Add(new EditableTag { Name = t.Name, Color = t.Color });
        foreach (var t in c.Templates) Templates.Add(new EditableTemplate { Name = t.Name, Body = t.Body });
        foreach (var a in c.Accounts) Accounts.Add(new EditableAccount(a));

        var ai = c.Ai;
        _aiEnabled = ai.Enabled;
        _provider = ai.Provider;
        _endpoint = ai.Endpoint;
        _model = ai.Model;
        _summarise = ai.Summarise;
        _draft = ai.Draft;
        _rewrite = ai.Rewrite;
        _replies = ai.Replies;
        ApiKey = _e.Vault.Get(SecretVault.AiKey) ?? "";
        RefreshPreset();

        var ap = c.Appearance;
        _theme = ap.Theme;
        _buttonStyle = ap.ButtonStyle;
        _colourful = ap.Colourful;
        _counts = ap.Counts;
        _showStatusBar = ap.ShowStatusBar;
        _menuFollowsToolbar = ap.MenuFollowsToolbar;
        LoadToolbar(ap);
        LoadRules();
        LoadSignatures();
        _hoverEnabled = ap.FolderHover.Enabled;
        _hoverDelay = ap.FolderHover.DelayMs;
        foreach (var id in FolderHoverSettings.LineIds) HoverLines.Add(new EditableHoverLine { Id = id, On = ap.FolderHover.Lines.Contains(id) });
        _rowMode = ap.RowActions.Mode;
        _confirmDeleteOver = ap.RowActions.ConfirmDeleteOver;
        LoadRowActions(ap.RowActions);
        _autoCheckUpdates = c.Updates.AutoCheck;
        _autoDownloadUpdates = c.Updates.AutoDownload;
        _includePrerelease = c.Updates.IncludePrerelease;
    }

    partial void OnProviderChanged(AiProviderKind value)
    {
        var (endpoint, model) = AiSettings.Preset(value);
        Endpoint = endpoint;
        Model = model;
        TestStatus = "";
        OnPropertyChanged(nameof(KeyHint));
    }

    partial void OnEndpointChanged(string value)
    {
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(KeyHint));
        TestStatus = "";
    }

    [RelayCommand] private void Go(string page) { SearchText = ""; Page = page; }

    [RelayCommand]
    private async Task TestConnection()
    {
        Testing = true;
        TestStatus = "Testing…";
        TestOk = false;
        try
        {
            var candidate = new AiSettings { Enabled = true, Provider = Provider, Endpoint = Endpoint.Trim(), Model = Model.Trim() };
            if (!AiService.IsConfigured(candidate, ApiKey)) throw new AiException("Fill in the endpoint, model and API key first.");
            var reply = await Task.Run(() => _e.Ai.TestAsync(candidate, string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(), CancellationToken.None));
            TestOk = true;
            TestStatus = "✓ Connected" + (string.IsNullOrWhiteSpace(reply) ? "" : $" — model replied \"{(reply.Length > 20 ? reply[..20] : reply)}\"");
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestStatus = ex is AiException ? ex.Message : "Couldn't connect: " + ex.Message;
        }
        finally { Testing = false; }
    }

    [RelayCommand]
    private void ForgetConsents()
    {
        _e.Config.Ai.Consents.Clear();
        _e.Settings.Save();
        TestStatus = "AI permissions cleared — each feature will ask again.";
    }

    [RelayCommand] private void AddTag() => Tags.Add(new EditableTag { Name = "New tag", Color = ColorChoices[Tags.Count % ColorChoices.Length] });
    [RelayCommand] private void RemoveTag(EditableTag? t) { if (t != null) Tags.Remove(t); }
    [RelayCommand] private void AddTemplate() => Templates.Add(new EditableTemplate { Name = "New template", Body = "" });
    [RelayCommand] private void RemoveTemplate(EditableTemplate? t) { if (t != null) Templates.Remove(t); }

    [RelayCommand]
    private void RemoveAccount(EditableAccount? a)
    {
        if (a == null) return;
        if (!Ui.Confirm("Remove account", $"Remove {a.Email} from Magpie?\n\nIts mail stays on the server; Magpie deletes its local copy and saved sign-in.")) return;
        _e.RemoveAccount(a.Original.Id);
        Accounts.Remove(a);
    }

    [RelayCommand] private void OpenDataFolder() => Ui.OpenExternal(_e.Paths.Root);
    [RelayCommand] private void OpenLog() { if (Log.FilePath != null) Ui.OpenExternal(Log.FilePath); }

    /// <summary>Reads a Google "client_secret_….json" (Desktop app) and fills the ID and secret.</summary>
    public string? ImportGoogleJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var node = root.TryGetProperty("installed", out var inst) ? inst : root.TryGetProperty("web", out var web) ? web : root;
            GoogleClientId = node.GetProperty("client_id").GetString() ?? "";
            GoogleClientSecret = node.TryGetProperty("client_secret", out var s) ? s.GetString() ?? "" : "";
            return null;
        }
        catch (Exception ex) { return "That file isn't a Google OAuth client file: " + ex.Message; }
    }

    [RelayCommand]
    private void Save()
    {
        var c = _e.Config;
        c.SmartInbox = SmartInbox;
        c.MarkReadOnOpen = MarkReadOnOpen;
        c.RemoteImages = RemoteImages;
        c.UndoSendSeconds = UndoSendSeconds;
        c.SyncIntervalMinutes = SyncIntervalMinutes;
        c.CloseToTray = CloseToTray;
        c.Notifications = Notifications;
        c.NotifyPeopleOnly = NotifyPeopleOnly;
        c.NotificationSound = NotificationSound;
        var openGate = c.Gatekeeper.Enabled && !Gatekeeper;
        c.Gatekeeper.Enabled = Gatekeeper;
        c.GoogleClientId = GoogleClientId.Trim();
        c.GoogleClientSecret = GoogleClientSecret.Trim();
        c.MicrosoftClientId = MicrosoftClientId.Trim();
        c.Tags = Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new TagDef { Name = t.Name.Trim().Replace(",", " "), Color = t.Color }).DistinctBy(t => t.Name).ToList();
        c.QuickReplies = QuickRepliesToSave();
        c.Templates = Templates.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new QuickTemplate { Name = t.Name.Trim(), Body = t.Body }).ToList();

        var ai = c.Ai;
        ai.Enabled = AiEnabled;
        ai.Provider = Provider;
        ai.Endpoint = Endpoint.Trim();
        ai.Model = Model.Trim();
        ai.Summarise = Summarise;
        ai.Draft = Draft;
        ai.Rewrite = Rewrite;
        ai.Replies = Replies;
        if (ApiKeyChanged) _e.Vault.Set(SecretVault.AiKey, string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim());

        c.Appearance = new Appearance
        {
            Theme = Theme, ButtonStyle = ButtonStyle, Colourful = Colourful, Counts = Counts, ShowStatusBar = ShowStatusBar, MenuFollowsToolbar = MenuFollowsToolbar,
            Toolbar = ToolbarRows.Select(r => new ToolbarButton { Id = r.Id, Visible = r.Visible }).ToList(),
            FolderHover = new FolderHoverSettings { Enabled = HoverEnabled, DelayMs = HoverDelay, Lines = HoverLines.Where(l => l.On).Select(l => l.Id).ToList() },
            RowActions = new RowActionsSettings { Mode = RowMode, ConfirmDeleteOver = ConfirmDeleteOver, Ids = RowActions.Where(a => a.On).Select(a => a.Id).ToList(),
                BulkUndoSeconds = c.Appearance.RowActions.BulkUndoSeconds },
        };
        c.Appearance.Normalise();
        c.Updates.AutoCheck = AutoCheckUpdates;
        c.Updates.AutoDownload = AutoDownloadUpdates;
        c.Updates.IncludePrerelease = IncludePrerelease;

        var err = StartupRegistration.Set(StartWithWindows);
        if (err != null) Log.Warn("start with Windows: " + err);

        CommitRuleEdits();
        var changedAccounts = Accounts.Where(a => a.Changed).Select(a => a.ToAccount()).ToList();
        _e.Settings.Save();
        foreach (var a in changedAccounts) _e.UpdateAccount(a);
        if (openGate) _e.OpenGate();
        Saved?.Invoke();
    }
}
