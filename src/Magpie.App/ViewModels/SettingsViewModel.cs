using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
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
    [ObservableProperty] private string _color;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _syncDays;

    public Account ToAccount()
    {
        var a = Original.Clone();
        a.DisplayName = DisplayName.Trim();
        a.Signature = Signature;
        a.Color = Color;
        a.Enabled = Enabled;
        a.SyncDays = Math.Clamp(SyncDays, 7, 3650);
        return a;
    }

    public bool Changed => DisplayName.Trim() != Original.DisplayName || Signature != Original.Signature || Color != Original.Color
                           || Enabled != Original.Enabled || SyncDays != Original.SyncDays;
}

public partial class EditableTag : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _color = "#14606E";
}

public partial class EditableTemplate : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _body = "";
}

public sealed record Choice<T>(T Value, string Label);

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

    public string Version => "Magpie " + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
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

    [RelayCommand] private void Go(string page) => Page = page;

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
        c.GoogleClientId = GoogleClientId.Trim();
        c.GoogleClientSecret = GoogleClientSecret.Trim();
        c.MicrosoftClientId = MicrosoftClientId.Trim();
        c.Tags = Tags.Where(t => !string.IsNullOrWhiteSpace(t.Name)).Select(t => new TagDef { Name = t.Name.Trim().Replace(",", " "), Color = t.Color }).DistinctBy(t => t.Name).ToList();
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

        var err = StartupRegistration.Set(StartWithWindows);
        if (err != null) Log.Warn("start with Windows: " + err);

        var changedAccounts = Accounts.Where(a => a.Changed).Select(a => a.ToAccount()).ToList();
        _e.Settings.Save();
        foreach (var a in changedAccounts) _e.UpdateAccount(a);
        Saved?.Invoke();
    }
}
