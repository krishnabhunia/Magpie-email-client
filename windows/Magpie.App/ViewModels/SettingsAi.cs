using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Security;
using Magpie.Core.Settings;

namespace Magpie.App.ViewModels;

/// <summary>One AI connection in Settings → AI (design AI2).</summary>
public partial class AiConnectionItem : ObservableObject
{
    public string Id { get; init; } = AiConnection.NewId();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _name = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private AiProviderKind _provider;
    [ObservableProperty] private string _endpoint = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _model = "";
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Result of the last "Test connection" for this one ("✓ Connected", or why not).</summary>
    [ObservableProperty] private string _status = "";
    public string ApiKey { get; set; } = "";
    public bool KeyChanged { get; set; }
    /// <summary>Design AM1: creativity (temperature) for this connection.</summary>
    [ObservableProperty] private double _temperature = AiConnection.DefaultTemperature;
    /// <summary>Design AM1: the provider's list (newest first), when it was fetched, what the models refused, the dismissed notice.</summary>
    public List<AiModelInfo> Models { get; set; } = new();
    public DateTimeOffset? ModelsCheckedAt { get; set; }
    public List<string> Unsupported { get; set; } = new();
    public string DismissedNewModel { get; set; } = "";
    public string Summary => $"{AiSettings.ProviderName(Provider)} · {(string.IsNullOrWhiteSpace(Model) ? "no model yet" : Model)}";

    public AiConnection ToConnection() => new()
    {
        Id = Id, Name = Name.Trim(), Provider = Provider, Endpoint = Endpoint.Trim(), Model = Model.Trim(), Temperature = Math.Round(Temperature, 2),
        Models = Models.Select(m => m.Clone()).ToList(), ModelsCheckedAt = ModelsCheckedAt, Unsupported = Unsupported.ToList(), DismissedNewModel = DismissedNewModel,
    };
}

/// <summary>One row of the model list (design AM1).</summary>
public partial class AiModelItem : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsNew { get; init; }
    public bool IsRecommended { get; init; }
    [ObservableProperty] private bool _isInUse;
    /// <summary>The name, or the id when they are the same thing.</summary>
    public string Display => Name;
    public bool ShowId => !string.Equals(Name, Id, StringComparison.Ordinal);
}

/// <summary>Settings → AI: several AI connections, one in use (design AI2). The editor fields (Provider, Endpoint, Model,
/// the key box, the model list and creativity — design AM1) edit the selected connection.</summary>
public partial class SettingsViewModel
{
    public ObservableCollection<AiConnectionItem> AiConnections { get; } = new();
    [ObservableProperty] private AiConnectionItem? _selectedAiConnection;
    private readonly List<string> _deletedAiConnections = new();
    private bool _loadingAiEditor;

    /// <summary>The key box shows <see cref="ApiKey"/> again (another connection was picked).</summary>
    public event Action? ApiKeyReloaded;

    // ── Design AM1: the model list ──
    public ObservableCollection<AiModelItem> Models { get; } = new();
    /// <summary>"From Anthropic · 7 models · checked 2 min ago" or "Built-in list (8 models). Add your API key to see every model your account can use."</summary>
    [ObservableProperty] private string _modelsHeader = "";
    /// <summary>Why the list couldn't be refreshed, or "" — coloured by <see cref="ModelsOk"/>.</summary>
    [ObservableProperty] private string _modelsStatus = "";
    [ObservableProperty] private bool _modelsOk = true;
    [ObservableProperty] private bool _modelsBusy;
    [ObservableProperty] private double _temperature = AiConnection.DefaultTemperature;
    /// <summary>False when the model in the box ignores temperature (the slider is shown disabled with a note).</summary>
    [ObservableProperty] private bool _temperatureUsed = true;
    [ObservableProperty] private string _temperatureNote = "";
    /// <summary>"New from Anthropic: Claude Opus 5.5. Your connection keeps using Claude Haiku 4.5." or "" (notice at the top of the AI page).</summary>
    [ObservableProperty] private string _newModelNotice = "";
    /// <summary>"Server (advanced)" open — by itself for "Other endpoint", where the address is the point.</summary>
    [ObservableProperty] private bool _showServer;
    private AiModelInfo? _offeredModel;
    /// <summary>Set while <see cref="Models"/> is rebuilt: WPF blanks the editable box's text when its selected item leaves the list, and that must not count as the user clearing the model.</summary>
    private bool _rebuildingModels;
    private System.Windows.Threading.DispatcherTimer? _keyTimer, _endpointTimer;
    /// <summary>The endpoint a pending refresh was armed for: the tick is dropped when the box changed since.</summary>
    private string _armedEndpoint = "";
    /// <summary>The key each connection's list was fetched with, so re-showing the same key doesn't ask again.</summary>
    private readonly Dictionary<string, string> _listedKey = new();
    private CancellationTokenSource? _modelsCts;
    public static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeyDebounce = TimeSpan.FromMilliseconds(800);
    public string TemperatureText => Temperature.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void LoadAiConnections(AiSettings ai)
    {
        foreach (var c in ai.Connections)
            AiConnections.Add(new AiConnectionItem
            {
                Id = c.Id, Name = c.Name, Provider = c.Provider, Endpoint = c.Endpoint, Model = c.Model, Temperature = c.Temperature,
                Models = c.Models.Select(m => m.Clone()).ToList(), ModelsCheckedAt = c.ModelsCheckedAt, Unsupported = c.Unsupported.ToList(), DismissedNewModel = c.DismissedNewModel,
                IsActive = c.Id == ai.ActiveId, ApiKey = _e.Vault.Get(SecretVault.AiKeyFor(c.Id)) ?? "",
            });
        foreach (var c in AiConnections) if (c.ModelsCheckedAt != null) _listedKey[c.Id] = c.ApiKey;
        SelectedAiConnection = AiConnections.FirstOrDefault(c => c.IsActive) ?? AiConnections.FirstOrDefault();
    }

    partial void OnSelectedAiConnectionChanging(AiConnectionItem? oldValue, AiConnectionItem? newValue) => FlushAiEditor(oldValue);

    partial void OnSelectedAiConnectionChanged(AiConnectionItem? oldValue, AiConnectionItem? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue == null) return;
        newValue.IsSelected = true;
        _loadingAiEditor = true;
        try
        {
            Provider = newValue.Provider;   // without _loadingAiEditor this would reset endpoint and model to the provider's defaults
            Endpoint = newValue.Endpoint;
            Model = newValue.Model;
            Temperature = newValue.Temperature;
        }
        finally { _loadingAiEditor = false; }
        ApiKey = newValue.ApiKey;
        ApiKeyChanged = newValue.KeyChanged;
        TestStatus = newValue.Status;
        TestOk = newValue.Status.StartsWith('✓');
        OnPropertyChanged(nameof(KeyHint));
        ApiKeyReloaded?.Invoke();
        _modelsCts?.Cancel();
        ModelsBusy = false;
        ModelsStatus = "";
        if (newValue.Provider == AiProviderKind.Custom) ShowServer = true;
        ShowModels(newValue);
        if (Page == "AI" && ListIsStale(newValue)) _ = RefreshModelsAsync(newValue, silent: true);
    }

    /// <summary>Writes the editor fields back into <paramref name="item"/> (the connection being edited).</summary>
    private void FlushAiEditor(AiConnectionItem? item)
    {
        if (item == null) return;
        item.Provider = Provider;
        item.Endpoint = Endpoint.Trim();
        item.Model = Model.Trim();
        item.Temperature = Temperature;
        if (ApiKeyChanged) { item.ApiKey = ApiKey; item.KeyChanged = true; }
    }

    [RelayCommand] private void EditAiConnection(AiConnectionItem? item) { if (item != null) SelectedAiConnection = item; }

    [RelayCommand]
    private void UseAiConnection(AiConnectionItem? item)
    {
        if (item == null) return;
        foreach (var c in AiConnections) c.IsActive = c == item;
    }

    [RelayCommand]
    private void AddAiConnection()
    {
        var (endpoint, model) = AiSettings.Preset(AiProviderKind.OpenAI);
        var item = new AiConnectionItem { Name = "New connection", Provider = AiProviderKind.OpenAI, Endpoint = endpoint, Model = model };
        AiConnections.Add(item);
        if (!AiConnections.Any(c => c.IsActive)) item.IsActive = true;
        SelectedAiConnection = item;
    }

    [RelayCommand]
    private void DeleteAiConnection(AiConnectionItem? item)
    {
        if (item == null) return;
        if (!Ui.Confirm("Delete this AI connection?", $"\"{item.Name}\" and its API key are removed from Magpie when you save.")) return;
        var index = AiConnections.IndexOf(item);
        AiConnections.Remove(item);
        _deletedAiConnections.Add(item.Id);
        if (AiConnections.Count == 0) AddAiConnection();
        if (item.IsActive) AiConnections[0].IsActive = true;
        if (SelectedAiConnection == item || SelectedAiConnection == null)
        {
            _selectedAiConnection = null;   // don't write the editor into the deleted one
            SelectedAiConnection = AiConnections[Math.Clamp(index, 0, AiConnections.Count - 1)];
        }
    }

    private void SaveAiConnections(AiSettings ai)
    {
        FlushAiEditor(SelectedAiConnection);
        foreach (var item in AiConnections)
        {
            // Same server as saved: refusals learned while Settings was open (AiService.Remember) stay, and a daily list refresh
            // that landed meanwhile (ModelRefreshService) is not overwritten. Another server: the editor's fresh state wins.
            var live = ai.Connections.FirstOrDefault(c => c.Id == item.Id);
            if (live == null || live.Provider != item.Provider || live.Endpoint != item.Endpoint) continue;
            item.Unsupported = item.Unsupported.Union(live.Unsupported, StringComparer.OrdinalIgnoreCase).ToList();
            if (live.ModelsCheckedAt != null && (item.ModelsCheckedAt == null || live.ModelsCheckedAt > item.ModelsCheckedAt))
            {
                item.Models = live.Models.Select(m => m.Clone()).ToList();
                item.ModelsCheckedAt = live.ModelsCheckedAt;
            }
        }
        ai.Connections = AiConnections.Select(c => c.ToConnection()).ToList();
        ai.ActiveId = (AiConnections.FirstOrDefault(c => c.IsActive) ?? AiConnections.First()).Id;
        foreach (var c in AiConnections.Where(c => c.KeyChanged))
        {
            _e.Vault.Set(SecretVault.AiKeyFor(c.Id), string.IsNullOrWhiteSpace(c.ApiKey) ? null : c.ApiKey.Trim());
            c.KeyChanged = false;
        }
        foreach (var id in _deletedAiConnections) _e.Vault.Set(SecretVault.AiKeyFor(id), null);
        _deletedAiConnections.Clear();
        ApiKeyChanged = false;
        ai.NormaliseConnections();
    }

    // ───────────────────────── design AM1: the model list ─────────────────────────

    private static bool ListIsStale(AiConnectionItem c) => c.ModelsCheckedAt == null || DateTimeOffset.UtcNow - c.ModelsCheckedAt.Value > RefreshAfter;

    /// <summary>Can the provider be asked at all? A key for the cloud ones; Ollama and other local servers need none.</summary>
    private bool CanListModels(AiConnectionItem c) =>
        c.Provider == AiProviderKind.Ollama || AiProviderFactory.IsLocalEndpoint(Endpoint.Trim()) || c.Provider == AiProviderKind.Custom || !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>Fills <see cref="Models"/> from the connection's list (or the built-in one), with the pills, header and notice.</summary>
    private void ShowModels(AiConnectionItem c)
    {
        var now = DateTimeOffset.UtcNow;
        var fromProvider = c.ModelsCheckedAt != null;   // fetched (even if the answer was "none") — never fetched or forgotten → built-in list
        var list = fromProvider ? ModelCatalog.Sort(c.Models) : ModelCatalog.BuiltIn(c.Provider).ToList();
        var recommended = ModelCatalog.Recommended(c.Provider, list)?.Id;
        var model = Model.Trim();
        var keep = Model;   // an editable ComboBox blanks its text when the selected item leaves the list
        _rebuildingModels = true;
        try
        {
            Models.Clear();
            foreach (var m in list)
                Models.Add(new AiModelItem
                {
                    Id = m.Id, Name = m.Name, IsNew = fromProvider && ModelCatalog.IsNew(m, now),
                    IsRecommended = m.Id == recommended, IsInUse = m.Id.Equals(model, StringComparison.OrdinalIgnoreCase),
                });
            if (Model != keep) Model = keep;
        }
        finally { _rebuildingModels = false; }
        var who = ProviderShortName(c.Provider);
        if (fromProvider && list.Count == 0)
            ModelsHeader = (c.Provider == AiProviderKind.Ollama ? "Ollama has no models yet — pull one, then Refresh." : $"Your key can use no models at {who}.") + $" Checked {Ago(c.ModelsCheckedAt, now)}.";
        else if (fromProvider)
            ModelsHeader = $"From {who} · {list.Count} model{(list.Count == 1 ? "" : "s")} · checked {Ago(c.ModelsCheckedAt, now)}";
        else if (list.Count == 0)
            ModelsHeader = CanListModels(c) ? "No list yet — Refresh asks the server which models it has." : "Type the model id your server expects.";
        else
            ModelsHeader = $"Built-in list ({list.Count} models). " + (c.Provider == AiProviderKind.Ollama ? "Start Ollama and Refresh to see the models on this PC." : "Add your API key to see every model your account can use.");
        RefreshTemperatureUsed(c);
        RefreshNewModelNotice(c);
    }

    private static string ProviderShortName(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => "Anthropic", AiProviderKind.OpenAI => "OpenAI", AiProviderKind.Ollama => "Ollama", _ => "the server",
    };

    private static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at == null) return "never";
        var d = now - at.Value;
        if (d < TimeSpan.FromMinutes(1)) return "just now";
        if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min ago";
        if (d < TimeSpan.FromDays(1)) return $"{(int)d.TotalHours} h ago";
        return $"{(int)d.TotalDays} day{((int)d.TotalDays == 1 ? "" : "s")} ago";
    }

    private void RefreshTemperatureUsed(AiConnectionItem c)
    {
        var model = Model.Trim();
        var shape = RequestShape.For(Provider, model, c.Unsupported, Temperature);
        TemperatureUsed = model.Length == 0 || shape.SendTemperature;
        var name = Models.FirstOrDefault(m => m.Id.Equals(model, StringComparison.OrdinalIgnoreCase))?.Name ?? model;
        TemperatureNote = TemperatureUsed ? "" : $"{name} chooses its own creativity; this setting is kept for models that accept it.";
    }

    private void RefreshNewModelNotice(AiConnectionItem c)
    {
        var conn = c.ToConnection();
        conn.Model = Model.Trim();
        _offeredModel = ModelCatalog.NewModelToOffer(conn, DateTimeOffset.UtcNow);
        if (_offeredModel == null) { NewModelNotice = ""; return; }
        var current = c.Models.FirstOrDefault(m => m.Id.Equals(conn.Model, StringComparison.OrdinalIgnoreCase))?.Name ?? conn.Model;
        NewModelNotice = $"New from {ProviderShortName(c.Provider)}: {_offeredModel.Name}. Your connection keeps using {current}.";
    }

    [RelayCommand]
    private void SwitchToNewModel()
    {
        if (_offeredModel == null) return;
        Model = _offeredModel.Id;
        NewModelNotice = "";
    }

    [RelayCommand]
    private void DismissNewModel()
    {
        if (SelectedAiConnection is not { } c || _offeredModel == null) return;
        c.DismissedNewModel = _offeredModel.Id;
        if (_e.Config.Ai.Connections.FirstOrDefault(x => x.Id == c.Id) is { } live) { live.DismissedNewModel = c.DismissedNewModel; _e.Settings.Save(notify: false); }
        NewModelNotice = "";
        _offeredModel = null;
        ShowModels(c);
    }

    [RelayCommand]
    private Task RefreshModels()
    {
        if (SelectedAiConnection is not { } c) return Task.CompletedTask;
        ClearUnsupported(c);
        RefreshTemperatureUsed(c);
        return RefreshModelsAsync(c, silent: false);
    }

    /// <summary>Asks the provider for its models and shows them. <paramref name="silent"/>: a background refresh (page opened, key typed) — a failure
    /// is shown only when the user asked, or when the key was refused.</summary>
    private async Task RefreshModelsAsync(AiConnectionItem c, bool silent)
    {
        if (!AiService.EndpointAllowed(Endpoint.Trim()))   // the key goes nowhere but an https server, or a server on this PC
        {
            if (!silent) { ModelsOk = false; ModelsStatus = ModelCatalog.NeedsHttps; }
            return;
        }
        if (!CanListModels(c))
        {
            if (!silent) { ModelsOk = false; ModelsStatus = "Add the API key first — the list needs it."; }
            return;
        }
        _modelsCts?.Cancel();
        var cts = _modelsCts = new CancellationTokenSource();
        ModelsBusy = true;
        ModelsOk = true;
        ModelsStatus = silent ? "" : "Asking " + ProviderShortName(c.Provider) + "…";
        var provider = Provider;
        var endpoint = Endpoint.Trim();
        var key = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        _listedKey[c.Id] = ApiKey;
        try
        {
            var fetched = await Task.Run(() => ModelCatalog.ListAsync(_e.Http, provider, endpoint, key, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            var now = DateTimeOffset.UtcNow;
            c.Models = ModelCatalog.Merge(c.Models, fetched, now, checkedBefore: c.ModelsCheckedAt != null);
            c.ModelsCheckedAt = now;
            // The list isn't a change the user made: keep it even if Settings is closed without saving — but only when it was fetched
            // with the saved key (a key typed and then cancelled must not leave its list on the saved connection).
            var savedKey = (_e.Vault.Get(SecretVault.AiKeyFor(c.Id)) ?? "").Trim();
            if (_e.Config.Ai.Connections.FirstOrDefault(x => x.Id == c.Id) is { } live && live.Provider == provider && live.Endpoint == endpoint && (key ?? "") == savedKey)
            {
                live.Models = c.Models.Select(m => m.Clone()).ToList();
                live.ModelsCheckedAt = now;
                _e.Settings.Save(notify: false);
            }
            if (SelectedAiConnection == c) ShowModels(c);
            ModelsStatus = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            var message = ex is AiException ? ex.Message : "Couldn't get the list: " + ex.Message;
            Log.Warn("AI model list: " + message);
            if (!silent || ex is AiException { Auth: true })
            {
                ModelsOk = false;
                ModelsStatus = message + (c.Models.Count > 0 ? " Showing the last list." : Models.Count > 0 ? " Showing the built-in list." : "");
            }
        }
        finally
        {
            if (_modelsCts == cts) { ModelsBusy = false; _modelsCts = null; }
        }
    }

    /// <summary>The key box changed: ask the provider again 800 ms after typing stops.</summary>
    public void NoteApiKeyTyped()
    {
        _keyTimer ??= MakeTimer(KeyDebounce, () =>
        {
            if (Endpoint.Trim() != _armedEndpoint) return;   // the address changed since: nothing automatic
            if (SelectedAiConnection is { } c && (ApiKey != _listedKey.GetValueOrDefault(c.Id) || ListIsStale(c))) _ = RefreshModelsAsync(c, silent: true);
        });
        _armedEndpoint = Endpoint.Trim();
        _keyTimer.Stop();
        _keyTimer.Start();
    }

    private static System.Windows.Threading.DispatcherTimer MakeTimer(TimeSpan after, Action tick)
    {
        var t = new System.Windows.Threading.DispatcherTimer { Interval = after };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    /// <summary>The provider or endpoint changed: the old list, and what that server refused, are for another server.
    /// <paramref name="refresh"/> (provider change only): ask the new provider shortly. An endpoint being typed never triggers a
    /// request — the key would go to a half-typed host; the user clicks Refresh or Test when done.</summary>
    private void ForgetModelList(bool refresh)
    {
        if (SelectedAiConnection is not { } c) return;
        c.Models = new();
        c.ModelsCheckedAt = null;
        c.DismissedNewModel = "";
        c.Unsupported = new();
        ShowModels(c);
        CancelPendingRefresh();   // an edited address: the key goes nowhere until Refresh or Test is clicked
        if (!refresh) return;
        _endpointTimer ??= MakeTimer(KeyDebounce, () =>
        {
            if (Endpoint.Trim() != _armedEndpoint) return;   // the address was edited after the provider change
            if (SelectedAiConnection is { } x) _ = RefreshModelsAsync(x, silent: true);
        });
        _armedEndpoint = Endpoint.Trim();
        _endpointTimer.Stop();
        _endpointTimer.Start();
    }

    private void CancelPendingRefresh()
    {
        _keyTimer?.Stop();
        _endpointTimer?.Stop();
        _modelsCts?.Cancel();
        ModelsBusy = false;
    }

    /// <summary>Refresh was clicked: what the server refused before is forgotten too (it may be another server, or fixed).</summary>
    private void ClearUnsupported(AiConnectionItem c)
    {
        c.Unsupported = new();
        if (_e.Config.Ai.Connections.FirstOrDefault(x => x.Id == c.Id) is { } live && live.Unsupported.Count > 0)
        {
            live.Unsupported = new();
            _e.Settings.Save(notify: false);
        }
    }

    partial void OnModelChanged(string value)
    {
        if (_rebuildingModels) return;
        TestStatus = "";
        if (SelectedAiConnection is not { } c) return;
        var model = value.Trim();
        foreach (var m in Models) m.IsInUse = m.Id.Equals(model, StringComparison.OrdinalIgnoreCase);
        RefreshTemperatureUsed(c);
        RefreshNewModelNotice(c);
    }

    partial void OnTemperatureChanged(double value)
    {
        OnPropertyChanged(nameof(TemperatureText));
        if (SelectedAiConnection is { } c) c.Temperature = value;
    }

    /// <summary>Settings → AI opened: refresh a list older than an hour.</summary>
    private void OnAiPageOpened()
    {
        if (SelectedAiConnection is { } c && ListIsStale(c)) _ = RefreshModelsAsync(c, silent: true);
    }

    /// <summary>The window closed: no late results into a dead view.</summary>
    private void StopModelRefresh()
    {
        _modelsCts?.Cancel();
        _keyTimer?.Stop();
        _endpointTimer?.Stop();
    }
}
