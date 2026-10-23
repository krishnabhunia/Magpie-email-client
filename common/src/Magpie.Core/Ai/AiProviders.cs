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
    public AiErrorKind Kind { get; init; }
    public int? StatusCode { get; init; }
    public string ErrorType { get; init; } = "";
    public string RequestId { get; init; } = "";
    public TimeSpan? RetryAfter { get; init; }
    public bool Retryable { get; init; }
    public bool PartialResponse { get; init; }
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
    private readonly RequestShape _shape;
    private readonly Action<string>? _remember;

    public OpenAiCompatibleProvider(HttpClient http, string endpoint, string model, string? apiKey, RequestShape? shape = null, Action<string>? remember = null)
    {
        _http = http;
        if (!AiService.IsConfigured(new AiSettings { Provider = AiProviderKind.Custom, Endpoint = endpoint, Model = model }, apiKey))
            throw new AiException("Use an HTTPS endpoint (or HTTP on this PC), without a password, query or fragment, and supply a model.");
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _key = apiKey;
        _shape = shape ?? RequestShape.For(AiProviderKind.OpenAI, model, null, AiConnection.DefaultTemperature);
        _remember = remember;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, Action<string>? onToken, int maxTokens, CancellationToken ct)
    {
        HttpRequestMessage Build(RequestShape shape)
        {
            var msgs = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            foreach (var m in messages) msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            var body = new JsonObject
            {
                ["model"] = _model,
                ["messages"] = msgs,
                ["stream"] = true,
            };
            if (shape.SendTemperature) body["temperature"] = shape.Temperature;
            if (shape.MaxTokensField != null) body[shape.MaxTokensField] = maxTokens;
            var url = _endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? _endpoint : _endpoint + "/chat/completions";
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(_key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return req;
        }
        var (resp, used) = await AiTransport.SendShapedAsync(_http, _shape, Build, _remember, ct);
        var limit = used.MaxTokensField == null ? Math.Max(200, maxTokens * 4) : int.MaxValue;
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
                if (text.Length > limit) text = text[..limit];
                onToken?.Invoke(text);
                return text;
            }
            await foreach (var data in Sse.ReadAsync(stream, ct))
            {
                if (data == "[DONE]") break;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.TryGetProperty("error", out _)) throw AiTransport.StreamError(doc.RootElement, sb.Length > 0, _key);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() == 0) continue;
                    if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString() ?? "";
                        if (piece.Length > limit - sb.Length) piece = piece[..(limit - sb.Length)];
                        sb.Append(piece);
                        onToken?.Invoke(piece);
                        if (sb.Length >= limit) break;
                    }
                }
                catch (JsonException) { /* keep-alive or partial */ }
            }
            return sb.ToString();
        }
    }

    internal static async Task<AiException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
        => await AiTransport.ErrorAsync(resp, ct);
}

/// <summary>Anthropic Messages API.</summary>
public sealed class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string _endpoint, _model, _key;
    private readonly int? _maxOutputTokens;
    private readonly RequestShape _shape;
    private readonly Action<string>? _remember;

    public AnthropicProvider(HttpClient http, string endpoint, string model, string apiKey, RequestShape? shape = null, Action<string>? remember = null, int? maxOutputTokens = null)
    {
        _http = http;
        _endpoint = AiModelCatalog.Scope(string.IsNullOrWhiteSpace(endpoint) ? "https://api.anthropic.com" : endpoint);
        _model = model;
        _key = apiKey;
        _maxOutputTokens = maxOutputTokens;
        _shape = shape ?? RequestShape.For(AiProviderKind.Anthropic, model, null, AiConnection.DefaultTemperature);
        _remember = remember;
    }

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatMessage> messages, Action<string>? onToken, int maxTokens, CancellationToken ct)
    {
        HttpRequestMessage Build(RequestShape shape)
        {
            var msgs = new JsonArray();
            foreach (var m in messages) msgs.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Content });
            var body = new JsonObject
            {
                ["model"] = _model,
                ["max_tokens"] = _maxOutputTokens is > 0 ? Math.Min(maxTokens, _maxOutputTokens.Value) : maxTokens,
                ["system"] = system,
                ["messages"] = msgs,
                ["stream"] = true,
            };
            if (shape.SendTemperature) body["temperature"] = shape.Temperature;
            var url = _endpoint + "/messages";
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("x-api-key", _key);
            req.Headers.Add("anthropic-version", "2023-06-01");
            return req;
        }
        var (resp, _) = await AiTransport.SendShapedAsync(_http, _shape, Build, _remember, ct);
        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await OpenAiCompatibleProvider.ErrorAsync(resp, ct);
            var sb = new StringBuilder();
            var stopped = false;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            if (resp.Content.Headers.ContentType?.MediaType?.Contains("event-stream") != true)
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("error", out _)) throw AiTransport.StreamError(doc.RootElement, false, _key);
                    foreach (var block in doc.RootElement.GetProperty("content").EnumerateArray())
                        if (block.TryGetProperty("type", out var kind) && kind.GetString() == "text" && block.TryGetProperty("text", out var text))
                            sb.Append(text.GetString());
                    var result = sb.ToString();
                    onToken?.Invoke(result);
                    return result;
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                { throw new AiException("The provider returned an unreadable reply."); }
            }
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
                        throw AiTransport.StreamError(doc.RootElement, sb.Length > 0, _key);
                    else if (type == "message_stop") { stopped = true; break; }
                }
                catch (JsonException) { }
            }
            ct.ThrowIfCancellationRequested();
            if (!stopped) throw new AiException("The provider stopped before finishing the reply. Review any partial text before trying again.")
            { Kind = AiErrorKind.Incomplete, PartialResponse = sb.Length > 0 };
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

    public static string ParameterScope(string endpoint) => new Uri(endpoint.Trim()).AbsoluteUri.TrimEnd('/');

    public static RequestShape Shape(AiSettings s)
    {
        var c = s.Active;
        var same = c != null && c.Provider == s.Provider && c.Model == s.Model && c.Endpoint == s.Endpoint;
        return RequestShape.For(s.Provider, s.Model,
            same && c!.UnsupportedEndpoint == ParameterScope(s.Endpoint) ? c.Unsupported : null,
            same ? c!.Temperature : AiConnection.DefaultTemperature);
    }

    public static IAiProvider Create(HttpClient http, AiSettings s, string? apiKey, Action<string>? remember = null)
    {
        var shape = Shape(s);
        return s.Provider switch
        {
            AiProviderKind.Anthropic => new AnthropicProvider(http, s.Endpoint, s.Model, apiKey ?? "", shape, remember,
                s.Active is { } c && c.CatalogEndpoint == AiModelCatalog.Scope(s.Endpoint) ? c.Models.FirstOrDefault(m => m.Id == s.Model && m.Available)?.MaxOutputTokens : null),
            _ => new OpenAiCompatibleProvider(http, s.Endpoint, s.Model, apiKey, shape, remember),
        };
    }
}
