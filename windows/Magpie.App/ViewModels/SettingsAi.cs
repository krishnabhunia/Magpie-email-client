using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Magpie.App.Services;
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
    public string Summary => $"{AiSettings.ProviderName(Provider)} · {(string.IsNullOrWhiteSpace(Model) ? "no model yet" : Model)}";

    public AiConnection ToConnection() => new() { Id = Id, Name = Name.Trim(), Provider = Provider, Endpoint = Endpoint.Trim(), Model = Model.Trim() };
}

/// <summary>Settings → AI: several AI connections, one in use (design AI2). The editor fields (Provider, Endpoint, Model,
/// the key box) edit the selected connection.</summary>
public partial class SettingsViewModel
{
    public ObservableCollection<AiConnectionItem> AiConnections { get; } = new();
    [ObservableProperty] private AiConnectionItem? _selectedAiConnection;
    private readonly List<string> _deletedAiConnections = new();
    private bool _loadingAiEditor;

    /// <summary>The key box shows <see cref="ApiKey"/> again (another connection was picked).</summary>
    public event Action? ApiKeyReloaded;

    private void LoadAiConnections(AiSettings ai)
    {
        foreach (var c in ai.Connections)
            AiConnections.Add(new AiConnectionItem
            {
                Id = c.Id, Name = c.Name, Provider = c.Provider, Endpoint = c.Endpoint, Model = c.Model,
                IsActive = c.Id == ai.ActiveId, ApiKey = _e.Vault.Get(SecretVault.AiKeyFor(c.Id)) ?? "",
            });
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
        }
        finally { _loadingAiEditor = false; }
        ApiKey = newValue.ApiKey;
        ApiKeyChanged = newValue.KeyChanged;
        TestStatus = newValue.Status;
        TestOk = newValue.Status.StartsWith('✓');
        OnPropertyChanged(nameof(KeyHint));
        ApiKeyReloaded?.Invoke();
    }

    /// <summary>Writes the editor fields back into <paramref name="item"/> (the connection being edited).</summary>
    private void FlushAiEditor(AiConnectionItem? item)
    {
        if (item == null) return;
        item.Provider = Provider;
        item.Endpoint = Endpoint.Trim();
        item.Model = Model.Trim();
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
