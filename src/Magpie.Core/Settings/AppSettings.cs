using System.Text.Json;
using System.Text.Json.Serialization;
using Magpie.Core.Models;

namespace Magpie.Core.Settings;

public enum RemoteImages { Ask = 0, Always = 1, Never = 2 }

public enum AiProviderKind { OpenAI = 0, Anthropic = 1, Ollama = 2, Custom = 3 }

public enum AiFeature { Summarise, Draft, Rewrite, Replies }

/// <summary>
/// The unified AI toggle model (designs S1–S5): one master switch, one provider, four feature switches.
/// A feature is live only when the master is on, the provider is configured, and its own switch is on.
/// </summary>
public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public AiProviderKind Provider { get; set; } = AiProviderKind.OpenAI;
    public string Endpoint { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";

    public bool Summarise { get; set; }
    public bool Draft { get; set; }
    public bool Rewrite { get; set; }
    public bool Replies { get; set; }

    /// <summary>Consents given, as "Feature@host" — changing the provider host asks again.</summary>
    public List<string> Consents { get; set; } = new();

    public bool FeatureSwitch(AiFeature f) => f switch
    {
        AiFeature.Summarise => Summarise,
        AiFeature.Draft => Draft,
        AiFeature.Rewrite => Rewrite,
        AiFeature.Replies => Replies,
        _ => false,
    };

    public void SetFeature(AiFeature f, bool on)
    {
        switch (f)
        {
            case AiFeature.Summarise: Summarise = on; break;
            case AiFeature.Draft: Draft = on; break;
            case AiFeature.Rewrite: Rewrite = on; break;
            case AiFeature.Replies: Replies = on; break;
        }
    }

    public static (string endpoint, string model) Preset(AiProviderKind kind) => kind switch
    {
        AiProviderKind.OpenAI => ("https://api.openai.com/v1", "gpt-4.1-mini"),
        AiProviderKind.Anthropic => ("https://api.anthropic.com", "claude-haiku-4-5"),
        AiProviderKind.Ollama => ("http://localhost:11434/v1", "llama3.1:8b"),
        _ => ("http://localhost:1234/v1", "local-model"),
    };

    public AiSettings Clone()
    {
        var c = (AiSettings)MemberwiseClone();
        c.Consents = new List<string>(Consents);
        return c;
    }
}

public sealed class TagDef
{
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#14606E";
}

public sealed class QuickTemplate
{
    public string Name { get; set; } = "";
    public string Body { get; set; } = "";
}

/// <summary>Which sidebar sections, accounts and folders are open (design Q2). Remembered between runs.</summary>
public sealed class SidebarState
{
    public bool FoldersOpen { get; set; } = true;
    public bool AccountsOpen { get; set; } = true;
    public bool TagsOpen { get; set; } = true;
    /// <summary>Account ids whose folder list is open (accounts start closed).</summary>
    public List<string> OpenAccounts { get; set; } = new();
    /// <summary>"accountId|folderPath" of folders whose subfolders are shown (folders start closed).</summary>
    public List<string> OpenFolders { get; set; } = new();
}

public enum ButtonStyle { IconAndName = 0, IconOnly = 1, NameOnly = 2 }

/// <summary>Folder numbers (design C2): unread / total, unread only, or none.</summary>
public enum CountsMode { UnreadAndTotal = 0, UnreadOnly = 1, Off = 2 }

/// <summary>Light or dark look (design B1). MatchWindows follows the Windows "app mode" setting.</summary>
public enum ThemeMode { MatchWindows = 0, Light = 1, Dark = 2 }

/// <summary>One reading-pane toolbar button (design C3). <see cref="Id"/> is one of <see cref="Appearance.ToolbarIds"/>.</summary>
public sealed class ToolbarButton
{
    public string Id { get; set; } = "";
    public bool Visible { get; set; } = true;
}

/// <summary>Look and toolbar choices (designs C1–C3, S1).</summary>
public sealed class Appearance
{
    /// <summary>Every button the toolbar can show, in default order; the first <see cref="DefaultVisible"/> are on.</summary>
    public static readonly string[] ToolbarIds = { "archive", "delete", "snooze", "remind", "tag", "pin", "move", "unread", "replyall", "forward" };
    public const int DefaultVisible = 7;

    public ThemeMode Theme { get; set; } = ThemeMode.MatchWindows;
    public ButtonStyle ButtonStyle { get; set; } = ButtonStyle.IconAndName;
    public bool Colourful { get; set; } = true;
    public CountsMode Counts { get; set; } = CountsMode.UnreadAndTotal;
    public bool ShowStatusBar { get; set; } = true;
    /// <summary>The message list's right-click menu follows the toolbar's order.</summary>
    public bool MenuFollowsToolbar { get; set; } = true;
    public List<ToolbarButton> Toolbar { get; set; } = DefaultToolbar();
    public FolderHoverSettings FolderHover { get; set; } = new();
    public RowActionsSettings RowActions { get; set; } = new();

    public static List<ToolbarButton> DefaultToolbar() =>
        ToolbarIds.Select((id, i) => new ToolbarButton { Id = id, Visible = i < DefaultVisible }).ToList();

    /// <summary>Drops unknown or repeated ids and appends buttons added in newer versions (hidden).</summary>
    public void Normalise()
    {
        Toolbar ??= DefaultToolbar();
        var seen = new HashSet<string>();
        Toolbar = Toolbar.Where(b => b != null && ToolbarIds.Contains(b.Id) && seen.Add(b.Id)).ToList();
        foreach (var id in ToolbarIds)
            if (seen.Add(id)) Toolbar.Add(new ToolbarButton { Id = id, Visible = false });
        (FolderHover ??= new()).Normalise();
        (RowActions ??= new()).Normalise();
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.MatchWindows;
    }

    public Appearance Clone() => new()
    {
        Theme = Theme, ButtonStyle = ButtonStyle, Colourful = Colourful, Counts = Counts, ShowStatusBar = ShowStatusBar, MenuFollowsToolbar = MenuFollowsToolbar,
        Toolbar = Toolbar.Select(b => new ToolbarButton { Id = b.Id, Visible = b.Visible }).ToList(),
        FolderHover = FolderHover.Clone(), RowActions = RowActions.Clone(),
    };
}

/// <summary>Updates from GitHub Releases (design U1).</summary>
public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public bool AutoDownload { get; set; } = true;
    public bool IncludePrerelease { get; set; }
    /// <summary>"Skip this version": not offered again by automatic checks.</summary>
    public string SkippedVersion { get; set; } = "";
    public DateTimeOffset? LastCheck { get; set; }
}

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1320;
    public double Height { get; set; } = 840;
    public bool Maximized { get; set; }
    public double ListWidth { get; set; } = 400;
    /// <summary>Sidebar width (design H1): 200–420 px; narrower than that snaps to the icon rail.</summary>
    public double SidebarWidth { get; set; } = SidebarDefault;
    /// <summary>The sidebar is the slim icon rail.</summary>
    public bool SidebarRail { get; set; }
    /// <summary>The sidebar is hidden (Ctrl+Shift+B).</summary>
    public bool SidebarHidden { get; set; }

    public const double SidebarDefault = 264, SidebarMin = 200, SidebarMax = 420, RailWidth = 64;

    /// <summary>Snaps a dragged width: below the minimum → rail; otherwise clamped to the allowed range.</summary>
    public static (double width, bool rail) SnapSidebar(double dragged) =>
        dragged < SidebarMin ? (SidebarDefault, true) : (Math.Clamp(dragged, SidebarMin, SidebarMax), false);
}

/// <summary>Folder details on hover (design H2).</summary>
public sealed class FolderHoverSettings
{
    /// <summary>Every line the card can show, in display order.</summary>
    public static readonly string[] LineIds = { "unread", "total", "today", "oldest", "last", "messages", "size", "attach" };
    public static readonly string[] DefaultLines = { "unread", "total", "today", "oldest", "last" };
    public static string NameOf(string id) => id switch
    {
        "unread" => "Unread conversations", "total" => "Total conversations", "today" => "Today", "oldest" => "Oldest unread",
        "last" => "Last received", "messages" => "Messages (every email)", "size" => "Size on server", "attach" => "With attachments", _ => id,
    };

    public bool Enabled { get; set; } = true;
    public List<string> Lines { get; set; } = DefaultLines.ToList();
    /// <summary>Delay before the card appears, ms (300 / 600 / 1000).</summary>
    public int DelayMs { get; set; } = 600;

    public void Normalise()
    {
        Lines ??= DefaultLines.ToList();
        var seen = new HashSet<string>();
        Lines = LineIds.Where(id => Lines.Contains(id) && seen.Add(id)).ToList();
        if (DelayMs is not (300 or 600 or 1000)) DelayMs = 600;
    }

    public FolderHoverSettings Clone() => new() { Enabled = Enabled, Lines = Lines.ToList(), DelayMs = DelayMs };
}

/// <summary>When the action buttons on an email row show (design H3).</summary>
public enum RowActionsMode { OnHover = 0, Always = 1, Never = 2 }

/// <summary>Action buttons on email rows + multi-select (design H3).</summary>
public sealed class RowActionsSettings
{
    /// <summary>Every action a row can offer, in the order they are listed in Settings.</summary>
    public static readonly string[] ActionIds = { "archive", "delete", "snooze", "read", "pin", "remind", "tag", "move", "spam" };
    public static readonly string[] DefaultIds = { "archive", "delete", "snooze", "read", "pin" };
    public const int MaxButtons = 5;

    public RowActionsMode Mode { get; set; } = RowActionsMode.OnHover;
    /// <summary>Chosen buttons in order (at most <see cref="MaxButtons"/>).</summary>
    public List<string> Ids { get; set; } = DefaultIds.ToList();
    /// <summary>Ask before deleting more than this many conversations at once (0 = never ask).</summary>
    public int ConfirmDeleteOver { get; set; } = 10;
    /// <summary>Seconds a bulk archive / delete / move waits before it happens (Undo in the bar).</summary>
    public int BulkUndoSeconds { get; set; } = 8;

    public void Normalise()
    {
        Ids ??= DefaultIds.ToList();
        var seen = new HashSet<string>();
        Ids = Ids.Where(id => id != null && ActionIds.Contains(id) && seen.Add(id)).Take(MaxButtons).ToList();
        ConfirmDeleteOver = Math.Clamp(ConfirmDeleteOver, 0, 1000);
        BulkUndoSeconds = Math.Clamp(BulkUndoSeconds, 0, 30);
    }

    public RowActionsSettings Clone() => new() { Mode = Mode, Ids = Ids.ToList(), ConfirmDeleteOver = ConfirmDeleteOver, BulkUndoSeconds = BulkUndoSeconds };
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<Account> Accounts { get; set; } = new();

    // General
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public int UndoSendSeconds { get; set; } = 10;
    public RemoteImages RemoteImages { get; set; } = RemoteImages.Ask;
    public List<string> TrustedImageSenders { get; set; } = new();
    public bool SmartInbox { get; set; } = true;
    public bool MarkReadOnOpen { get; set; } = true;
    public int SyncIntervalMinutes { get; set; } = 5;

    // Notifications
    public bool Notifications { get; set; } = true;
    /// <summary>Spark-style: only notify for mail from people (not newsletters / notifications).</summary>
    public bool NotifyPeopleOnly { get; set; } = true;
    public bool NotificationSound { get; set; } = true;

    /// <summary>Smart-inbox choices made by the user ("always put this sender in People").</summary>
    public Dictionary<string, Category> SenderCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<TagDef> Tags { get; set; } = new()
    {
        new() { Name = "Important", Color = "#B3261E" },
        new() { Name = "Waiting", Color = "#B45309" },
        new() { Name = "Personal", Color = "#4B3F86" },
        new() { Name = "Work", Color = "#14606E" },
    };

    public List<QuickTemplate> Templates { get; set; } = new()
    {
        new() { Name = "Thanks, received", Body = "Thanks — received. I'll get back to you shortly." },
        new() { Name = "Will check and revert", Body = "Thanks for this. Let me check and get back to you by end of day." },
    };

    // OAuth apps registered by the user (see docs/SIGN-IN-SETUP.md)
    public string GoogleClientId { get; set; } = "";
    public string GoogleClientSecret { get; set; } = "";
    public string MicrosoftClientId { get; set; } = "";

    public AiSettings Ai { get; set; } = new();
    public WindowPlacement Window { get; set; } = new();
    public SidebarState Sidebar { get; set; } = new();
    public Appearance Appearance { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    /// <summary>Rules / filters (design B5), run top to bottom on new Inbox mail.</summary>
    public List<Mail.MailRule> Rules { get; set; } = new();

    [JsonIgnore]
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}

/// <summary>Loads/saves settings.json atomically; a corrupt file is kept as .bad and defaults are used.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    public AppSettings Current { get; private set; } = new();
    public event Action? Changed;

    public SettingsStore(string path) { _path = path; }

    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), AppSettings.Json);
                    Current = s ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Error("settings.json unreadable — using defaults", ex);
                try { File.Copy(_path, _path + ".bad", true); } catch { }
                Current = new AppSettings();
            }
            Normalise(Current);
            return Current;
        }
    }

    internal static void Normalise(AppSettings s)
    {
        s.Accounts ??= new();
        s.Ai ??= new();
        s.Ai.Consents ??= new();
        s.Tags ??= new();
        s.Templates ??= new();
        s.TrustedImageSenders ??= new();
        s.Window ??= new();
        s.Sidebar ??= new();
        s.Sidebar.OpenAccounts ??= new();
        s.Sidebar.OpenFolders ??= new();
        s.Appearance ??= new();
        s.Appearance.Normalise();
        s.Updates ??= new();
        s.Updates.SkippedVersion ??= "";
        s.Rules = Mail.RuleEngine.Normalise(s.Rules);
        s.SenderCategories = new Dictionary<string, Category>(s.SenderCategories ?? new(), StringComparer.OrdinalIgnoreCase);
        s.UndoSendSeconds = Math.Clamp(s.UndoSendSeconds, 0, 30);
        s.SyncIntervalMinutes = Math.Clamp(s.SyncIntervalMinutes, 1, 120);
        if (string.IsNullOrWhiteSpace(s.Ai.Endpoint)) (s.Ai.Endpoint, _) = AiSettings.Preset(s.Ai.Provider);
    }

    /// <summary>Writes settings.json. <paramref name="notify"/> false = UI-state only (sidebar, window size): no Changed event.</summary>
    public void Save(AppSettings? s = null, bool notify = true)
    {
        lock (_gate)
        {
            if (s != null) Current = s;
            Normalise(Current);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, AppSettings.Json));
            File.Move(tmp, _path, true);
        }
        if (notify) Changed?.Invoke();
    }
}
