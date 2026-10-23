using System.Text.Json;
using System.Text.Json.Serialization;
using Magpie.Core.Settings;

namespace Magpie.Core.Ai;

/// <summary>Cached metadata only. Credentials are held exclusively in SecretVault.</summary>
public sealed class AiModelInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? FirstSeenAt { get; set; }
    public string Lifecycle { get; set; } = "active";
    public int? MaxOutputTokens { get; set; }
    [JsonIgnore] public bool IsNew => FirstSeenAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromDays(30);
    public bool Enabled { get; set; }
    public bool Available { get; set; } = true;
    public AiModelInfo Clone() => (AiModelInfo)MemberwiseClone();
}

public static class AiModelCatalog
{
    public static IReadOnlyList<string> Published(AiConnection connection)
    {
        if (connection.Provider != AiProviderKind.Anthropic) return Array.Empty<string>();
        try
        {
            return connection.CatalogEndpoint == Scope(connection.Endpoint)
                ? connection.Models.Where(m => m.Enabled && m.Available).Select(m => m.Id).Distinct(StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
        }
        catch (AiException) { return Array.Empty<string>(); }
    }

    public static string Scope(string endpoint) => Endpoint(endpoint).AbsoluteUri.TrimEnd('/');

    public static Uri Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new AiException("Use an HTTPS endpoint (or HTTP on this PC), without a password, query or fragment.");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) path = path[..^9];
        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) path = path[..^7];
        if (!path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path += "/v1";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    /// <summary>Commit a complete refresh only; failed pages never replace the last good cache.</summary>
    public static void Apply(AiConnection connection, IReadOnlyList<AiModelInfo> discovered, DateTimeOffset now)
    {
        var scope = Scope(connection.Endpoint);
        var old = connection.CatalogEndpoint == scope ? connection.Models : new List<AiModelInfo>();
        var enabled = old.Where(m => m.Enabled).Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        var next = discovered.DistinctBy(m => m.Id).OrderByDescending(m => m.CreatedAt).Select(m =>
        {
            var model = m.Clone();
            model.Enabled = enabled.Contains(m.Id);
            model.Available = m.Available;
            model.FirstSeenAt = old.FirstOrDefault(o => o.Id == m.Id)?.FirstSeenAt ?? now;
            return model;
        }).ToList();
        next.AddRange(old.Where(m => next.All(n => n.Id != m.Id)).Select(m =>
            Unavailable(m)));
        connection.Models = next;
        connection.CatalogEndpoint = scope;
        connection.ModelsRefreshedAt = now;
    }

    private static AiModelInfo Unavailable(AiModelInfo model) { var copy = model.Clone(); copy.Available = false; return copy; }

    public static async Task<IReadOnlyList<AiModelInfo>> DiscoverAsync(HttpClient http, string endpoint, string key, CancellationToken ct)
    {
        var root = Endpoint(endpoint).AbsoluteUri.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(key)) throw new AiException("Enter your Anthropic API key before refreshing models.", true);
        var result = new List<AiModelInfo>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            var url = root + "/models?limit=100" + (cursor == null ? "" : "&after_id=" + Uri.EscapeDataString(cursor));
            using var response = await AiTransport.GetModelsAsync(http, url, key, ct);
            try
            {
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var data = doc.RootElement.GetProperty("data");
                foreach (var model in data.EnumerateArray())
                {
                    var id = model.GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(id)) throw new JsonException();
                    var name = model.TryGetProperty("display_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : id;
                    var lifecycle = model.TryGetProperty("lifecycle", out var life) && life.ValueKind == JsonValueKind.String ? life.GetString() ?? "active" : "active";
                    var created = model.TryGetProperty("created_at", out var date) && date.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(date.GetString(), out var at) ? at : (DateTimeOffset?)null;
                    int? max = model.TryGetProperty("max_tokens", out var maxValue) && maxValue.ValueKind == JsonValueKind.Number && maxValue.TryGetInt32(out var tokens) && tokens > 0 ? tokens : null;
                    result.Add(new AiModelInfo { Id = id, Name = name ?? id, Lifecycle = lifecycle, Available = lifecycle != "retired", CreatedAt = created, MaxOutputTokens = max });
                }
                if (!doc.RootElement.TryGetProperty("has_more", out var more) || !more.GetBoolean())
                    return result.DistinctBy(m => m.Id).ToArray();
                cursor = doc.RootElement.GetProperty("last_id").GetString();
                if (string.IsNullOrWhiteSpace(cursor) || !cursors.Add(cursor)) throw new JsonException();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            { throw new AiException("The provider returned an unreadable model list. Your saved models have been kept."); }
        }
        throw new AiException("The provider returned too many model pages. Your saved models have been kept.");
    }
}

/// <summary>Conservative defaults for unknown models; never guess that a new model supports sampling.</summary>
public static class AiModelParameters
{
    public static bool AnthropicTemperature(string model) =>
        model.StartsWith("claude-3-", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("claude-3.", StringComparison.OrdinalIgnoreCase)
        || new[] { "claude-opus-4-1", "claude-opus-4-5", "claude-opus-4-6", "claude-sonnet-4-5", "claude-sonnet-4-6", "claude-haiku-4-5" }
            .Any(id => model.Equals(id, StringComparison.OrdinalIgnoreCase) || model.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase));

    public static bool OpenAiReasoning(string model) =>
        new[] { "o1", "o3", "o4", "gpt-5" }.Any(id => model.Equals(id, StringComparison.OrdinalIgnoreCase)
            || model.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith(id + ".", StringComparison.OrdinalIgnoreCase));
}
