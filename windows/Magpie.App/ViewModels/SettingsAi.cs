using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
using Magpie.Core.Security;
using Magpie.Core.Settings;
using Magpie.Core.Ai;

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
    public double Temperature { get; set; } = AiConnection.DefaultTemperature;
    public List<string> Unsupported { get; set; } = new();
    public string UnsupportedEndpoint { get; set; } = "";
    public List<AiModelInfo> Models { get; set; } = new();
    public string CatalogEndpoint { get; set; } = "";
    public DateTimeOffset? ModelsRefreshedAt { get; set; }
    public string Summary => $"{AiSettings.ProviderName(Provider)} · {(string.IsNullOrWhiteSpace(Model) ? "no model yet" : Model)}";

    public AiConnection ToConnection() => new()
    {
        Id = Id, Name = Name.Trim(), Provider = Provider, Endpoint = Endpoint.Trim(), Model = Model.Trim(),
        Temperature = Temperature, Unsupported = new(Unsupported), UnsupportedEndpoint = UnsupportedEndpoint,
        Models = Models.Select(m => m.Clone()).ToList(), CatalogEndpoint = CatalogEndpoint, ModelsRefreshedAt = ModelsRefreshedAt,
    };
}

public partial class AiModelItem : ObservableObject
{
    public AiModelInfo Info { get; init; } = new();
    public string Label => $"{Info.Name} · {Info.Id}" + (Info.Available ? "" : " · unavailable");
    public bool Available => Info.Available;
    public string Badge => !Info.Available ? "Unavailable" : Info.IsNew ? "New" : Info.Lifecycle == "deprecated" ? "Deprecated" : "";
    [ObservableProperty] private bool _enabled;
    public Action? Changed { get; init; }
    partial void OnEnabledChanged(bool value) { Info.Enabled = value; Changed?.Invoke(); }
}

/// <summary>Settings → AI: several AI connections, one in use (design AI2). The editor fields (Provider, Endpoint, Model,
/// the key box) edit the selected connection.</summary>
public partial class SettingsViewModel
{
    public ObservableCollection<AiConnectionItem> AiConnections { get; } = new();
    [ObservableProperty] private AiConnectionItem? _selectedAiConnection;
    private readonly List<string> _deletedAiConnections = new();
    private bool _loadingAiEditor;
    private CancellationTokenSource? _aiOperation;
    public ObservableCollection<AiModelItem> DiscoveredAiModels { get; } = new();
    public ObservableCollection<string> PublishedAiModels { get; } = new();
    [ObservableProperty] private double _temperature = AiConnection.DefaultTemperature;
    public bool TemperatureUsed
    {
        get
        {
            var item = SelectedAiConnection;
            return RequestShape.For(Provider, Model,
                item?.UnsupportedEndpoint == SafeParameterScope() ? item.Unsupported : null, Temperature).SendTemperature;
        }
    }
    private string SafeParameterScope() => Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : "";
    public string TemperatureHint => TemperatureUsed ? "Creativity (0–1)" : "Creativity — not used by this model";
    partial void OnTemperatureChanged(double value)
    {
        if (!_loadingAiEditor)
        {
            CancelAiOperation(); TestOk = false; TestStatus = "";
            if (SelectedAiConnection is { } item) item.Status = "";
        }
    }
    private void UpdateTemperature() { OnPropertyChanged(nameof(TemperatureUsed)); OnPropertyChanged(nameof(TemperatureHint)); }
    public bool IsAnthropic => Provider == AiProviderKind.Anthropic;
    public string? PublishedAiModel
    {
        get => PublishedAiModels.Contains(Model) ? Model : null;
        set { if (value != null) Model = value; }
    }
    public string ModelsStatus => SelectedAiConnection?.ModelsRefreshedAt is { } at
        ? $"Last refreshed {at.ToLocalTime():g}. Select models for the dropdown, then Apply to save."
        : "Refresh models, select those to show in the dropdown, then Apply to save.";

    private void CancelAiOperation() { _aiOperation?.Cancel(); }
    [RelayCommand] private void CancelAiRequest() => CancelAiOperation();

    public void AiCredentialsChanged()
    {
        CancelAiOperation();
        TestOk = false;
        TestStatus = "";
        if (SelectedAiConnection is { } item)
        {
            item.Status = "";
            item.CatalogEndpoint = "";
            item.ModelsRefreshedAt = null;
        }
        ReloadAiModels();
        NoteChange();
    }

    private void ReloadAiModels()
    {
        DiscoveredAiModels.Clear();
        var item = SelectedAiConnection;
        if (item != null && IsAnthropic)
        {
            string scope;
            try { scope = AiModelCatalog.Scope(Endpoint); } catch (AiException) { scope = ""; }
            if (scope.Length > 0 && item.CatalogEndpoint == scope)
                foreach (var model in item.Models)
                {
                    var row = new AiModelItem { Info = model, Enabled = model.Enabled, Changed = () => { ReloadPublishedModels(); NoteChange(); } };
                    DiscoveredAiModels.Add(row);
                }
        }
        ReloadPublishedModels();
        OnPropertyChanged(nameof(ModelsStatus));
    }

    private void ReloadPublishedModels()
    {
        PublishedAiModels.Clear();
        foreach (var row in DiscoveredAiModels.Where(m => m.Enabled && m.Available)) PublishedAiModels.Add(row.Info.Id);
        OnPropertyChanged(nameof(PublishedAiModel));
    }

    partial void OnModelChanged(string value)
    {
        if (!_loadingAiEditor) { CancelAiOperation(); TestOk = false; TestStatus = ""; if (SelectedAiConnection is { } item) item.Status = ""; }
        OnPropertyChanged(nameof(PublishedAiModel));
        UpdateTemperature();
    }

    private (AiConnectionItem? item, AiProviderKind provider, string endpoint, string model, string key) AiEditorIdentity() =>
        (SelectedAiConnection, Provider, Endpoint.Trim(), Model.Trim(), ApiKey.Trim());

    [RelayCommand(CanExecute = nameof(CanRefreshAiModels))]
    private async Task RefreshAiModels()
    {
        var identity = AiEditorIdentity();
        if (identity.item == null) return;
        using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        _aiOperation = operation;
        Testing = true;
        TestOk = false;
        TestStatus = "Refreshing models…";
        try
        {
            var models = await _e.Ai.DiscoverModelsAsync(identity.endpoint, identity.key, operation.Token);
            if (operation.IsCancellationRequested || identity != AiEditorIdentity()) return;
            var connection = identity.item.ToConnection();
            connection.Endpoint = identity.endpoint;
            AiModelCatalog.Apply(connection, models, DateTimeOffset.UtcNow);
            identity.item.Models = connection.Models;
            identity.item.CatalogEndpoint = connection.CatalogEndpoint;
            identity.item.ModelsRefreshedAt = connection.ModelsRefreshedAt;
            ReloadAiModels();
            TestStatus = models.Count == 0 ? "No models are available for this key. Your selected model has been kept." : $"Found {models.Count} models. Choose which to show below.";
            NoteChange();
        }
        catch (OperationCanceledException) { if (identity == AiEditorIdentity()) TestStatus = "Model refresh cancelled or timed out. Your saved list has been kept."; }
        catch (Exception ex) { if (identity == AiEditorIdentity()) TestStatus = ex is AiException ? ex.Message : "Couldn't refresh models. Your saved list has been kept."; }
        finally { if (ReferenceEquals(_aiOperation, operation)) { _aiOperation = null; Testing = false; } }
    }

    private bool CanRefreshAiModels() => IsAnthropic && !Testing;
    private bool CanTestAiConnection() => !Testing;
    partial void OnTestingChanged(bool value)
    {
        RefreshAiModelsCommand.NotifyCanExecuteChanged();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The key box shows <see cref="ApiKey"/> again (another connection was picked).</summary>
    public event Action? ApiKeyReloaded;

    private void LoadAiConnections(AiSettings ai)
    {
        foreach (var c in ai.Connections)
            AiConnections.Add(new AiConnectionItem
            {
                Id = c.Id, Name = c.Name, Provider = c.Provider, Endpoint = c.Endpoint, Model = c.Model,
                IsActive = c.Id == ai.ActiveId, ApiKey = _e.Vault.Get(SecretVault.AiKeyFor(c.Id)) ?? "",
                Temperature = c.Temperature, Unsupported = new(c.Unsupported), UnsupportedEndpoint = c.UnsupportedEndpoint,
                Models = c.Models.Select(m => m.Clone()).ToList(), CatalogEndpoint = c.CatalogEndpoint, ModelsRefreshedAt = c.ModelsRefreshedAt,
            });
        SelectedAiConnection = AiConnections.FirstOrDefault(c => c.IsActive) ?? AiConnections.FirstOrDefault();
    }

    partial void OnSelectedAiConnectionChanging(AiConnectionItem? oldValue, AiConnectionItem? newValue)
    { CancelAiOperation(); FlushAiEditor(oldValue); }

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
        OnPropertyChanged(nameof(IsAnthropic));
        RefreshAiModelsCommand.NotifyCanExecuteChanged();
        ReloadAiModels();
        UpdateTemperature();
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
        // Keep parameter refusals learned by generation while this editor was open.
        foreach (var item in AiConnections)
        {
            var saved = ai.Connections.FirstOrDefault(c => c.Id == item.Id && c.Endpoint == item.Endpoint && c.Provider == item.Provider);
            if (saved != null && Uri.TryCreate(item.Endpoint, UriKind.Absolute, out var server)
                && saved.UnsupportedEndpoint == server.AbsoluteUri.TrimEnd('/'))
            {
                if (item.UnsupportedEndpoint != saved.UnsupportedEndpoint) item.Unsupported.Clear();
                item.UnsupportedEndpoint = saved.UnsupportedEndpoint;
                item.Unsupported = item.Unsupported.Concat(saved.Unsupported).Distinct().ToList();
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
}
