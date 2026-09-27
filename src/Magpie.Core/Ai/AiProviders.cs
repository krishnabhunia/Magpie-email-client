using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magpie.Core.Settings;

namespace Magpie.Core.Ai;

public sealed record ChatMessage(string Role, string Content);

public sealed class AiException : Exception
{
    public bool Auth { get; }
    public AiException(string message, bool auth = false, Exception? inner = null) : base(message, inner) { Auth = auth; }
}

/// <summary>A chat model that streams text.</summary>
public interface IAiProvider
{
    /// <summary>Streams the reply; <paramref name="onToken"/> gets each text fragment. Returns the full text.</summary>
    Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, Action<string>? onToken, int maxTokens, CancellationToken ct);
}

internal static class Sse
{
    /// <summary>Reads a text/event-stream, yielding each "data:" payload.</summary>
    public static async IAsyncEnumerable<string> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (line.Length == 0)
            {
                if (data.Length > 0) { yield return data.ToString(); data.Clear(); }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }
        if (data.Length > 0) yield return data.ToString();
    }
}

/// <summary>OpenAI Chat Completions and everything compatible with it: Ollama, LM Studio, vLLM, Groq, OpenRouter…</summary>
public sealed class OpenAiCompatibleProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _endpoint, _model;
    private readonly string? _key;

    public OpenAiCompatibleProvider(HttpClient http, string endpoint, string model, string? apiKey)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _key = apiKey;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, Action<string>? onToken, int maxTokens, CancellationToken ct)
    {
        var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in messages) msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = msgs,
            ["stream"] = true,
            ["temperature"] = 0.4,
            ["max_tokens"] = maxTokens,
        };
        var url = _endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? _endpoint : _endpoint + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(_key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) { throw new AiException($"Can't reach {new Uri(url).Host}: {ex.Message}", inner: ex); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await ErrorAsync(resp, ct);
            var sb = new StringBuilder();
            var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            if (!contentType.Contains("event-stream"))
            {
                // Some servers ignore stream=true.
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                onToken?.Invoke(text);
                return text;
            }
            await foreach (var data in Sse.ReadAsync(stream, ct))
            {
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("error", out var err)) throw new AiException(err.ToString());
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString() ?? "";
                        sb.Append(piece);
                        onToken?.Invoke(piece);
                    }
                }
                catch (JsonException) { /* keep-alive or partial */ }
            }
            return sb.ToString();
        }
    }

    internal static async Task<AiException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = "";
        try { text = await resp.Content.ReadAsStringAsync(ct); } catch { }
        string detail = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var e))
                detail = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? text : e.ToString();
        }
        catch { }
        if (detail.Length > 300) detail = detail[..300] + "…";
        var code = (int)resp.StatusCode;
        var auth = code is 401 or 403;
        var prefix = code switch
        {
            401 => "The API key was rejected",
            403 => "Access denied",
            404 => "Model or endpoint not found",
            429 => "Rate limited or out of credit",
            >= 500 => "The provider had a server error",
            _ => $"Request failed ({code})",
        };
        return new AiException($"{prefix}: {Log.Redact(detail)}", auth);
    }
}

/// <summary>Anthropic Messages API.</summary>
public sealed class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _endpoint, _model, _key;

    public AnthropicProvider(HttpClient http, string endpoint, string model, string apiKey)
    {
        _http = http;
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? "https://api.anthropic.com" : endpoint.TrimEnd('/');
        _model = model;
        _key = apiKey;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, Action<string>? onToken, int maxTokens, CancellationToken ct)
    {
        var msgs = new JsonArray();
        foreach (var m in messages) msgs.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Content });
        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = maxTokens,
            ["system"] = system,
            ["messages"] = msgs,
            ["stream"] = true,
            ["temperature"] = 0.4,
        };
        var url = _endpoint.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ? _endpoint : _endpoint + "/v1/messages";
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", _key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException ex) { throw new AiException($"Can't reach {new Uri(url).Host}: {ex.Message}", inner: ex); }
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await OpenAiCompatibleProvider.ErrorAsync(resp, ct);
            var sb = new StringBuilder();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            await foreach (var data in Sse.ReadAsync(stream, ct))
            {
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type == "content_block_delta" && doc.RootElement.GetProperty("delta").TryGetProperty("text", out var txt))
                    {
                        var piece = txt.GetString() ?? "";
                        sb.Append(piece);
                        onToken?.Invoke(piece);
                    }
                    else if (type == "error")
                        throw new AiException(doc.RootElement.GetProperty("error").TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error");
                    else if (type == "message_stop") break;
                }
                catch (JsonException) { }
            }
            return sb.ToString();
        }
    }
}

public static class AiProviderFactory
{
    public static bool IsLocalEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u)) return false;
        return u.IsLoopback || u.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    public static string Host(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : endpoint;

    public static IAiProvider Create(HttpClient http, AiSettings s, string? apiKey) => s.Provider switch
    {
        AiProviderKind.Anthropic => new AnthropicProvider(http, s.Endpoint, s.Model, apiKey ?? ""),
        _ => new OpenAiCompatibleProvider(http, s.Endpoint, s.Model, apiKey),
    };
}
