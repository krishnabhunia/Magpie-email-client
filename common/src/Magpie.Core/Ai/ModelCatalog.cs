using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Magpie.Core.Settings;

namespace Magpie.Core.Ai;

/// <summary>One model a provider offers (design AM1). Kept in settings.json with the connection.</summary>
public sealed class AiModelInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>When the provider says the model was released; null when it doesn't say.</summary>
    public DateTimeOffset? Created { get; set; }
    /// <summary>When Magpie first saw this id for the connection (MinValue = known since the first list).</summary>
    public DateTimeOffset FirstSeen { get; set; }

    public AiModelInfo Clone() => (AiModelInfo)MemberwiseClone();
}

/// <summary>
/// Design AM1: asks a provider which models the key can use (Anthropic, OpenAI, Ollama, or any OpenAI-shaped server),
/// sorts them newest first, picks a recommended one, and merges with what was seen before so a new model can be marked "New".
/// </summary>
public static class ModelCatalog
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan NewFor = TimeSpan.FromDays(30);
    private const int MaxPages = 5;

    public const int MaxModels = 500;
    public const int MaxTextLength = 200;
    public const string NeedsHttps = "Model list needs an https endpoint (http only for a server on this PC).";

    public static async Task<IReadOnlyList<AiModelInfo>> ListAsync(HttpClient http, AiProviderKind kind, string endpoint, string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = AiSettings.Preset(kind).endpoint;
        if (!AiService.EndpointAllowed(endpoint)) throw new AiException(NeedsHttps);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            var list = kind switch
            {
                AiProviderKind.Anthropic => await AnthropicAsync(http, endpoint, apiKey, timeout.Token),
                AiProviderKind.Ollama => await OllamaAsync(http, endpoint, apiKey, timeout.Token),
                _ => await OpenAiAsync(http, endpoint, apiKey, kind, timeout.Token),
            };
            return Sort(list).Take(MaxModels).Select(Clip).ToList();   // the newest 500
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"{Who(kind)} didn't answer within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"Can't reach {AiProviderFactory.Host(endpoint)}: {ex.Message}", inner: ex);
        }
    }

    private static string Who(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => "Anthropic", AiProviderKind.OpenAI => "OpenAI", AiProviderKind.Ollama => "Ollama", _ => "The server",
    };

    private static AiModelInfo Clip(AiModelInfo m)
    {
        if (m.Id.Length > MaxTextLength) m.Id = m.Id[..MaxTextLength];
        if (m.Name.Length > MaxTextLength) m.Name = m.Name[..MaxTextLength];
        return m;
    }

    private static readonly string[] RequestPaths = { "/v1/messages", "/chat/completions", "/messages" };

    /// <summary>A saved endpoint may name the request path ("…/v1/messages", "…/v1/chat/completions"): the list lives next to it, not under it.</summary>
    private static string Root(string endpoint)
    {
        var e = (endpoint ?? "").Trim().TrimEnd('/');
        foreach (var path in RequestPaths)
            if (e.EndsWith(path, StringComparison.OrdinalIgnoreCase)) { e = e[..^path.Length]; break; }
        return e;
    }

    /// <summary>"https://api.anthropic.com", ".../v1" or ".../v1/messages" → "https://api.anthropic.com".</summary>
    private static string Base(string endpoint)
    {
        var e = Root(endpoint);
        if (e.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) e = e[..^3];
        return e;
    }

    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, Action<HttpRequestMessage> headers, AiProviderKind kind, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        headers(req);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            throw code switch
            {
                401 or 403 => new AiException($"{Who(kind)} refused the key ({code}). Check the key.", auth: true),
                404 or 405 or 501 => new AiException("This server doesn't list its models"),
                429 => new AiException($"{Who(kind)} is rate limiting the request (429). Try again in a minute."),
                _ => new AiException($"{Who(kind)} couldn't list models ({code}): {Log.Redact(Trim(text))}"),
            };
        }
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { throw new AiException("This server doesn't list its models"); }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "…" : s;

    private static async Task<List<AiModelInfo>> AnthropicAsync(HttpClient http, string endpoint, string? apiKey, CancellationToken ct)
    {
        var baseUrl = Base(endpoint);
        var list = new List<AiModelInfo>();
        string? after = null;
        for (int page = 0; page < MaxPages; page++)
        {
            var url = baseUrl + "/v1/models?limit=100" + (after == null ? "" : "&after_id=" + Uri.EscapeDataString(after));
            using var doc = await GetAsync(http, url, req =>
            {
                req.Headers.Add("x-api-key", apiKey ?? "");
                req.Headers.Add("anthropic-version", "2023-06-01");
            }, AiProviderKind.Anthropic, ct);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) throw new AiException("This server doesn't list its models");
            foreach (var m in data.EnumerateArray())
            {
                var id = Str(m, "id");
                if (id.Length == 0) continue;
                list.Add(new AiModelInfo { Id = id, Name = Str(m, "display_name") is { Length: > 0 } n ? n : id, Created = Date(m, "created_at") });
            }
            var more = root.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
            after = root.TryGetProperty("last_id", out var li) && li.ValueKind == JsonValueKind.String ? li.GetString() : null;
            if (!more || string.IsNullOrEmpty(after)) break;
        }
        return list;
    }

    private static readonly string[] ChatPrefixes = { "gpt-", "o1", "o3", "o4", "chatgpt-" };
    private static readonly string[] NotChat = { "embedding", "whisper", "tts", "dall-e", "realtime", "audio", "transcribe", "moderation", "search", "instruct", "image" };

    /// <summary>OpenAI's list has every model it sells; keep the ones that chat.</summary>
    public static bool IsOpenAiChatModel(string id)
    {
        var m = id.ToLowerInvariant();
        return ChatPrefixes.Any(m.StartsWith) && !NotChat.Any(m.Contains);
    }

    /// <summary>"gpt-4.1-mini" → "GPT-4.1 mini", "chatgpt-4o-latest" → "ChatGPT-4o latest", "o4-mini" → "o4 mini".</summary>
    public static string OpenAiName(string id)
    {
        if (id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase)) return "GPT-" + id[4..].Replace('-', ' ');
        if (id.StartsWith("chatgpt-", StringComparison.OrdinalIgnoreCase)) return "ChatGPT-" + id[8..].Replace('-', ' ');
        return id.Replace('-', ' ');
    }

    private static async Task<List<AiModelInfo>> OpenAiAsync(HttpClient http, string endpoint, string? apiKey, AiProviderKind kind, CancellationToken ct)
    {
        var url = Root(endpoint) + "/models";
        using var doc = await GetAsync(http, url, req =>
        {
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }, kind, ct);
        var root = doc.RootElement;
        JsonElement data;
        if (root.ValueKind == JsonValueKind.Array) data = root;
        else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out data) && data.ValueKind == JsonValueKind.Array) { }
        else throw new AiException("This server doesn't list its models");
        var list = new List<AiModelInfo>();
        foreach (var m in data.EnumerateArray())
        {
            var id = Str(m, "id");
            if (id.Length == 0) continue;
            if (kind == AiProviderKind.OpenAI && !IsOpenAiChatModel(id)) continue;
            DateTimeOffset? created = null;
            if (m.TryGetProperty("created", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt64(out var secs) && secs > 0)
                created = DateTimeOffset.FromUnixTimeSeconds(secs);
            list.Add(new AiModelInfo { Id = id, Name = kind == AiProviderKind.OpenAI ? OpenAiName(id) : id, Created = created });
        }
        return list;
    }

    private static async Task<List<AiModelInfo>> OllamaAsync(HttpClient http, string endpoint, string? apiKey, CancellationToken ct)
    {
        var url = Base(endpoint) + "/api/tags";
        using var doc = await GetAsync(http, url, req =>
        {
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);   // a remote Ollama behind a gateway
        }, AiProviderKind.Ollama, ct);
        if (!doc.RootElement.TryGetProperty("models", out var data) || data.ValueKind != JsonValueKind.Array) throw new AiException("This server doesn't list its models");
        var list = new List<AiModelInfo>();
        foreach (var m in data.EnumerateArray())
        {
            var name = Str(m, "name");
            if (name.Length == 0) name = Str(m, "model");
            if (name.Length == 0) continue;
            var size = m.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var bytes) ? bytes : 0;
            list.Add(new AiModelInfo { Id = name, Name = size > 0 ? $"{name} · {Size(bytes: size)}" : name, Created = Date(m, "modified_at") });
        }
        return list;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    // ───────────────────────── pure parts ─────────────────────────

    /// <summary>Newest first (models without a date last), then by id descending so "claude-opus-5-5" sits above "claude-opus-4-5".</summary>
    public static List<AiModelInfo> Sort(IEnumerable<AiModelInfo> models) =>
        models.OrderByDescending(m => m.Created ?? DateTimeOffset.MinValue).ThenByDescending(m => m.Id, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The model to suggest: Anthropic → the newest Haiku, OpenAI → the newest "mini", otherwise the first in the list.</summary>
    public static AiModelInfo? Recommended(AiProviderKind kind, IReadOnlyList<AiModelInfo> models)
    {
        if (models.Count == 0) return null;
        var word = kind switch { AiProviderKind.Anthropic => "haiku", AiProviderKind.OpenAI => "mini", _ => null };
        return (word == null ? null : models.FirstOrDefault(m => m.Id.Contains(word, StringComparison.OrdinalIgnoreCase))) ?? models[0];
    }

    /// <summary>What the editor shows when there is no key, the request failed and nothing was cached. Newest first.</summary>
    public static IReadOnlyList<AiModelInfo> BuiltIn(AiProviderKind kind) => kind switch
    {
        AiProviderKind.Anthropic => new[]
        {
            M("claude-opus-5-5", "Claude Opus 5.5"), M("claude-sonnet-5-5", "Claude Sonnet 5.5"), M("claude-haiku-5-5", "Claude Haiku 5.5"),
            M("claude-fable-5-1", "Claude Fable 5.1"), M("claude-opus-4-5", "Claude Opus 4.5"), M("claude-sonnet-4-5", "Claude Sonnet 4.5"),
            M("claude-haiku-4-5", "Claude Haiku 4.5"), M("claude-opus-4-1", "Claude Opus 4.1"),
        },
        AiProviderKind.OpenAI => new[]
        {
            M("gpt-5", "GPT-5"), M("gpt-5-mini", "GPT-5 mini"), M("gpt-4.1", "GPT-4.1"), M("gpt-4.1-mini", "GPT-4.1 mini"),
            M("gpt-4o", "GPT-4o"), M("gpt-4o-mini", "GPT-4o mini"), M("o3", "o3"), M("o4-mini", "o4 mini"),
        },
        AiProviderKind.Ollama => new[] { M("llama3.1:8b", "llama3.1:8b"), M("llama3.2", "llama3.2"), M("mistral", "mistral"), M("phi4", "phi4"), M("qwen2.5", "qwen2.5") },
        _ => Array.Empty<AiModelInfo>(),
    };

    private static AiModelInfo M(string id, string name) => new() { Id = id, Name = name, FirstSeen = DateTimeOffset.MinValue };

    /// <summary>
    /// The fetched list with <see cref="AiModelInfo.FirstSeen"/> carried over from <paramref name="previous"/>. An id not seen before
    /// gets FirstSeen = now — unless this is the connection's first list (<paramref name="checkedBefore"/> false), where nothing is "New".
    /// </summary>
    public static List<AiModelInfo> Merge(IEnumerable<AiModelInfo>? previous, IEnumerable<AiModelInfo> fetched, DateTimeOffset now, bool checkedBefore)
    {
        var seen = (previous ?? Array.Empty<AiModelInfo>()).GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().FirstSeen, StringComparer.OrdinalIgnoreCase);
        var result = new List<AiModelInfo>();
        foreach (var m in fetched)
        {
            var copy = m.Clone();
            copy.FirstSeen = seen.TryGetValue(m.Id, out var at) ? at : checkedBefore ? now : DateTimeOffset.MinValue;
            result.Add(copy);
        }
        return result;
    }

    /// <summary>Seen for the first time within the last 30 days (never for a model known since the first list).</summary>
    public static bool IsNew(AiModelInfo m, DateTimeOffset now) => m.FirstSeen > DateTimeOffset.MinValue && now - m.FirstSeen <= NewFor && m.FirstSeen <= now.AddMinutes(5);

    /// <summary>The newest model that is New, isn't the one in use and wasn't dismissed — for the "New from Anthropic: …" notice.</summary>
    public static AiModelInfo? NewModelToOffer(AiConnection c, DateTimeOffset now)
    {
        if (c.Models == null || c.Models.Count == 0) return null;
        return Sort(c.Models).FirstOrDefault(m => IsNew(m, now) && !m.Id.Equals(c.Model, StringComparison.OrdinalIgnoreCase)
                                                   && !m.Id.Equals(c.DismissedNewModel, StringComparison.OrdinalIgnoreCase));
    }
}
