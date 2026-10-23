using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Magpie.Core.Ai;

public enum AiErrorKind { Unknown, Authentication, Permission, ModelNotFound, InvalidRequest, RateLimit, Billing, Unavailable, Network, Incomplete }

internal static class AiTransport
{
    private static string Identifier(string? value) => value != null && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]{1,128}\z") ? value : "";

    public static AiException Error(int status, string? type = null, string? requestId = null, TimeSpan? retryAfter = null, bool partial = false, bool billing = false)
    {
        var kind = status switch
        {
            401 => AiErrorKind.Authentication,
            403 => AiErrorKind.Permission,
            404 => AiErrorKind.ModelNotFound,
            402 => AiErrorKind.Billing,
            429 => AiErrorKind.RateLimit,
            >= 500 => AiErrorKind.Unavailable,
            400 or 413 or 422 => AiErrorKind.InvalidRequest,
            _ => AiErrorKind.Unknown,
        };
        if (type == "overloaded_error") kind = AiErrorKind.Unavailable;
        if (type == "rate_limit_error") kind = AiErrorKind.RateLimit;
        if (type == "authentication_error") kind = AiErrorKind.Authentication;
        if (type == "permission_error") kind = AiErrorKind.Permission;
        if (billing || type == "billing_error") kind = AiErrorKind.Billing;
        var message = kind switch
        {
            AiErrorKind.Authentication => "The API key was rejected. Check the key in AI settings.",
            AiErrorKind.Permission => "Access denied. Check this key's provider workspace and model permissions.",
            AiErrorKind.ModelNotFound => "Model or endpoint not found. Refresh models or check the model ID and endpoint.",
            AiErrorKind.Billing => "Check billing and API credits in your provider account.",
            AiErrorKind.RateLimit => "Rate limited or out of credit. Wait before trying again, or check your provider's usage limits.",
            AiErrorKind.Unavailable => "The provider is temporarily unavailable. Try again later.",
            AiErrorKind.InvalidRequest => "The provider rejected this model's request settings. Check the model ID and supported parameters.",
            _ when status is >= 300 and < 400 => "The AI endpoint redirected the request. Enter the final HTTPS endpoint in Settings.",
            _ => "The provider could not complete the request. Check your AI settings.",
        };
        requestId = Identifier(requestId);
        if (requestId.Length > 0) message += " Request: " + requestId + ".";
        if (partial) message += " A partial reply was received; it was not retried.";
        return new AiException(message, kind is AiErrorKind.Authentication or AiErrorKind.Permission)
        {
            Kind = kind,
            StatusCode = status == 0 ? null : status,
            ErrorType = Identifier(type),
            RequestId = requestId,
            RetryAfter = retryAfter,
            Retryable = !partial && kind is AiErrorKind.RateLimit or AiErrorKind.Unavailable,
            PartialResponse = partial
        };
    }

    private static string? SafeIdentifier(string? value, string? key) => !string.IsNullOrEmpty(key) && value?.Contains(key, StringComparison.Ordinal) == true ? null : value;

    public static AiException StreamError(JsonElement root, bool partial, string? key = null)
    {
        var type = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var id = root.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return Error(0, SafeIdentifier(type, key), SafeIdentifier(id, key), partial: partial);
    }

    public static async Task<AiException> ErrorAsync(HttpResponseMessage response, CancellationToken ct, string? key = null)
    {
        string? type = null, id = null;
        var billing = false;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString();
            if ((int)response.StatusCode == 400 && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                billing = m.GetString()?.Contains("credit balance", StringComparison.OrdinalIgnoreCase) == true;
            if (root.TryGetProperty("request_id", out var r) && r.ValueKind == JsonValueKind.String) id = r.GetString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        if (response.Headers.TryGetValues("request-id", out var ids)) id = ids.FirstOrDefault();
        var delay = response.Headers.RetryAfter?.Delta;
        if (delay == null && response.Headers.RetryAfter?.Date is { } date) delay = date - DateTimeOffset.UtcNow;
        return Error((int)response.StatusCode, SafeIdentifier(type, key), SafeIdentifier(id, key), delay < TimeSpan.Zero ? TimeSpan.Zero : delay, billing: billing);
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AiException("The AI provider took too long to respond. Try again when the connection is stable.") { Kind = AiErrorKind.Network }; }
        catch (HttpRequestException)
        { throw new AiException("Can't reach the AI provider. Check your connection and endpoint.") { Kind = AiErrorKind.Network }; }
    }

    // Retry only an explicit 400 rejection of an optional setting, before any reply is streamed.
    public static async Task<(HttpResponseMessage response, RequestShape shape)> SendShapedAsync(
        HttpClient http, RequestShape shape, Func<RequestShape, HttpRequestMessage> build, Action<string>? remember, CancellationToken ct)
    {
        var changes = new HashSet<string>(StringComparer.Ordinal);
        for (var attempt = 0; ; attempt++)
        {
            using var request = build(shape);
            var response = await SendAsync(http, request, ct);
            if (response.IsSuccessStatusCode)
            {
                foreach (var learned in changes) remember?.Invoke(learned);
                return (response, shape);
            }
            string body;
            try { body = await response.Content.ReadAsStringAsync(ct); }
            catch { response.Dispose(); throw; }
            var parameter = RequestShape.RefusedParameter((int)response.StatusCode, body);
            var next = parameter == null ? shape : shape.Without(parameter, body);
            if (attempt < 3 && shape.ChangeTo(next) is { } change && changes.Add(change))
            {
                response.Dispose();
                shape = next;
                continue;
            }
            var key = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.FirstOrDefault() : request.Headers.Authorization?.Parameter;
            try { throw await ErrorAsync(response, ct, key); }
            finally { response.Dispose(); }
        }
    }

    // Discovery is read-only. Generation is never automatically replayed on transient/transport failures.
    public static async Task<HttpResponseMessage> GetModelsAsync(HttpClient http, string url, string key, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", key.Trim());
            request.Headers.Add("anthropic-version", "2023-06-01");
            var response = await SendAsync(http, request, ct);
            if (response.IsSuccessStatusCode) return response;
            AiException error;
            try { error = await ErrorAsync(response, ct, key); }
            finally { response.Dispose(); }
            var delay = error.RetryAfter ?? TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt) + Random.Shared.Next(100));
            if (!error.Retryable || attempt >= 2 || delay > TimeSpan.FromSeconds(5)) throw error;
            await Task.Delay(delay, ct);
        }
    }
}
