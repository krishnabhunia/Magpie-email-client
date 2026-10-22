using System.Threading;
using Magpie.Core;
using Magpie.Core.Ai;
using Magpie.Core.Security;
using Magpie.Core.Settings;

namespace Magpie.App.Services;

/// <summary>
/// Design AM1: 30 s after start and then once a day, asks the provider of the AI connection in use which models it has
/// (when it has a key, or is Ollama / a local server), saves the list, and logs a model seen for the first time — Settings → AI
/// then shows the "New from …" notice. Runs off the UI thread; a failure is only logged.
/// </summary>
public sealed class ModelRefreshService : IDisposable
{
    public static readonly TimeSpan FirstAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Every = TimeSpan.FromHours(24);
    private readonly MailEngine _e;
    private Timer? _timer;
    private int _running;

    public ModelRefreshService(MailEngine e) { _e = e; }

    public void Start() => _timer = new Timer(_ => _ = RefreshAsync(), null, FirstAfter, Every);

    /// <summary>One round: the connection in use only. Public so a test or a "Check now" can call it.</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            var ai = _e.Config.Ai;
            if (!ai.Enabled || ai.Active is not { } c) return;
            if (!AiService.EndpointAllowed(c.Endpoint)) return;   // https, or a server on this PC — the key goes nowhere else
            var key = _e.Vault.Get(SecretVault.AiKeyFor(c.Id));
            var local = c.Provider == AiProviderKind.Ollama || AiProviderFactory.IsLocalEndpoint(c.Endpoint);
            if (string.IsNullOrWhiteSpace(key) && !local && c.Provider != AiProviderKind.Custom) return;
            var fetched = await ModelCatalog.ListAsync(_e.Http, c.Provider, c.Endpoint, string.IsNullOrWhiteSpace(key) ? null : key.Trim(), CancellationToken.None);
            var now = DateTimeOffset.UtcNow;
            var merged = ModelCatalog.Merge(c.Models, fetched, now, checkedBefore: c.ModelsCheckedAt != null);
            var live = _e.Config.Ai.Connections.FirstOrDefault(x => x.Id == c.Id);
            if (live == null || live.Provider != c.Provider || live.Endpoint != c.Endpoint) return;   // changed meanwhile
            var fresh = merged.Where(m => m.FirstSeen == now).Select(m => m.Id).ToList();
            live.Models = merged;
            live.ModelsCheckedAt = now;
            _e.Settings.Save(notify: false);
            Log.Info($"AI models: {merged.Count} from {AiSettings.ProviderName(c.Provider)}" + (fresh.Count > 0 ? ", new: " + string.Join(", ", fresh) : ""));
        }
        catch (Exception ex) { Log.Warn("AI model list (daily): " + ex.Message); }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    public void Dispose() => _timer?.Dispose();
}
