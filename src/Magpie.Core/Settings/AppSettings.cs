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

public sealed class WindowPlacement
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1320;
    public double Height { get; set; } = 840;
    public bool Maximized { get; set; }
    public double ListWidth { get; set; } = 400;
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
