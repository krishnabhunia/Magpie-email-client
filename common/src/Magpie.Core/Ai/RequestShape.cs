using System.Text.Json;
using System.Text.RegularExpressions;
using Magpie.Core.Settings;

namespace Magpie.Core.Ai;

/// <summary>
/// Design AM1: which optional request settings a model accepts. Newer models refuse <c>temperature</c>
/// (Claude Fable / Mythos, Claude 5.x, OpenAI's o-series and GPT-5) or want <c>max_completion_tokens</c>
/// instead of <c>max_tokens</c>. The built-in table covers the known ones; what a model refuses at run time is
/// learned (<see cref="AiConnection.Unsupported"/>, entries "model:parameter") and applied next time.
/// </summary>
/// <param name="SendTemperature">Send <c>temperature</c> at all.</param>
/// <param name="Temperature">The value to send (0–1).</param>
/// <param name="MaxTokensField">"max_tokens", "max_completion_tokens", or null = send no length limit (the provider then cuts the text itself).</param>
/// <param name="FixedMaxTokens">The API requires the length field (Anthropic): a 400 naming it is never "learned" and never changes the request.</param>
public sealed record RequestShape(bool SendTemperature, double Temperature, string? MaxTokensField, bool FixedMaxTokens = false)
{
    public const string TemperatureParam = "temperature";
    public const string MaxTokensParam = "max_tokens";
    public const string MaxCompletionTokensParam = "max_completion_tokens";

    /// <summary>The entry kept in <see cref="AiConnection.Unsupported"/> for one model and parameter ("model:temperature"), or a
    /// replacement the server asked for ("model:max_tokens->max_completion_tokens").</summary>
    public static string Key(string model, string param) => $"{model.Trim()}:{param}";

    /// <summary>"max_tokens->max_completion_tokens": the server wants the second name instead of the first.</summary>
    public static string Replacement(string from, string to) => $"{from}->{to}";

    /// <summary>What <paramref name="next"/> dropped or renamed compared with this shape, as the entry to remember — or null when nothing changed.</summary>
    public string? ChangeTo(RequestShape next)
    {
        if (SendTemperature && !next.SendTemperature) return TemperatureParam;
        if (MaxTokensField != next.MaxTokensField && MaxTokensField != null)
            return next.MaxTokensField == null ? MaxTokensField : Replacement(MaxTokensField, next.MaxTokensField);
        return null;
    }

    /// <summary>"max_tokens->max_completion_tokens" → (max_tokens, max_completion_tokens); a plain name → (name, null).</summary>
    public static (string from, string? to) Parse(string entry)
    {
        var i = entry.IndexOf("->", StringComparison.Ordinal);
        return i < 0 ? (entry.Trim(), null) : (entry[..i].Trim(), entry[(i + 2)..].Trim());
    }

    public static RequestShape For(AiProviderKind kind, string model, IEnumerable<string>? unsupported, double temperature)
    {
        model = (model ?? "").Trim();
        var shape = new RequestShape(!NoTemperatureByDefault(kind, model), Math.Clamp(temperature, 0, 1), DefaultMaxTokensField(kind, model), kind == AiProviderKind.Anthropic);
        var prefix = model + ":";
        var entries = (unsupported ?? Array.Empty<string>())
            .Where(u => u.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(u => Parse(u[prefix.Length..])).ToList();
        var refused = new HashSet<string>(entries.Where(e => e.to == null).Select(e => e.from), StringComparer.OrdinalIgnoreCase);
        var replaced = entries.Where(e => e.to != null).ToDictionary(e => e.from, e => e.to!, StringComparer.OrdinalIgnoreCase);
        if (refused.Contains(TemperatureParam)) shape = shape with { SendTemperature = false };
        if (shape.FixedMaxTokens) return shape;
        // A replacement the server asked for ("use max_completion_tokens instead") beats a plain refusal; OpenAI's own newer name is the default replacement.
        if (shape.MaxTokensField == MaxTokensParam && replaced.TryGetValue(MaxTokensParam, out var to) && to is MaxCompletionTokensParam or MaxTokensParam)
            shape = shape with { MaxTokensField = to };
        else if (shape.MaxTokensField == MaxTokensParam && refused.Contains(MaxTokensParam))
            shape = shape with { MaxTokensField = kind == AiProviderKind.OpenAI ? MaxCompletionTokensParam : null };
        if (shape.MaxTokensField == MaxCompletionTokensParam && refused.Contains(MaxCompletionTokensParam))
            shape = shape with { MaxTokensField = null };
        return shape;
    }

    /// <summary>The shape after the server refused <paramref name="param"/>. Refusing <c>max_tokens</c> with a message that
    /// names <c>max_completion_tokens</c> switches the field (OpenAI's newer models) instead of dropping the limit.</summary>
    public RequestShape Without(string param, string? serverMessage = null)
    {
        if (param.Equals(TemperatureParam, StringComparison.OrdinalIgnoreCase)) return this with { SendTemperature = false };
        if (FixedMaxTokens) return this;   // Anthropic: max_tokens is part of the API, the 400 is about its value, not the field
        if (param.Equals(MaxTokensParam, StringComparison.OrdinalIgnoreCase))
        {
            if (MaxTokensField == MaxTokensParam && (serverMessage ?? "").Contains(MaxCompletionTokensParam, StringComparison.OrdinalIgnoreCase))
                return this with { MaxTokensField = MaxCompletionTokensParam };
            return this with { MaxTokensField = MaxTokensField == MaxTokensParam ? null : MaxTokensField };
        }
        if (param.Equals(MaxCompletionTokensParam, StringComparison.OrdinalIgnoreCase))
            return this with { MaxTokensField = MaxTokensField == MaxCompletionTokensParam ? null : MaxTokensField };
        return this;   // top_p and the like: Magpie never sends them
    }

    /// <summary>Models that reject <c>temperature</c> outright (the built-in table).</summary>
    public static bool NoTemperatureByDefault(AiProviderKind kind, string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Length == 0) return false;
        if (kind == AiProviderKind.Anthropic || m.StartsWith("claude"))
        {
            if (m.Contains("fable") || m.Contains("mythos")) return true;
            return ClaudeFamilyVersion(m) >= 5.0;
        }
        if (kind == AiProviderKind.OpenAI || m.StartsWith("gpt-") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4"))
            return m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.StartsWith("gpt-5") && !m.StartsWith("gpt-5-chat");
        return false;
    }

    private static string DefaultMaxTokensField(AiProviderKind kind, string model)
    {
        var m = model.ToLowerInvariant();
        if (kind == AiProviderKind.OpenAI && (m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.StartsWith("gpt-5")))
            return MaxCompletionTokensParam;
        return MaxTokensParam;
    }

    private static readonly Regex ClaudeVersion = new(@"(?:opus|sonnet|haiku)-(\d+)-(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"claude-opus-5-5" → 5.5, "claude-haiku-4-5" → 4.5; 0 when the id has no family version.</summary>
    public static double ClaudeFamilyVersion(string model)
    {
        var m = ClaudeVersion.Match(model);
        if (!m.Success) return 0;
        return double.Parse(m.Groups[1].Value + "." + m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static readonly string[] KnownParams = { MaxCompletionTokensParam, MaxTokensParam, TemperatureParam, "top_p", "top_k" };
    private static readonly Regex RefusalWords = new(@"deprecated|unsupported|not supported|does not support|doesn't support|isn't supported|is not allowed|not allowed|cannot be used|can't be used|invalid parameter|unknown parameter|unrecognized|unrecognised|not permitted", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuotedName = new(@"[`'""]([a-z_]+)[`'""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The request parameter a 400 error names as refused, or null when the error is about something else.</summary>
    public static string? RefusedParameter(int status, string? body)
    {
        if (status != 400 || string.IsNullOrWhiteSpace(body)) return null;
        string message = body, param = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString() ?? body;
                    if (e.TryGetProperty("param", out var p) && p.ValueKind == JsonValueKind.String) param = p.GetString() ?? "";
                }
                else if (e.ValueKind == JsonValueKind.String) message = e.GetString() ?? body;
            }
        }
        catch (JsonException) { }
        if (param.Length > 0 && KnownParams.Contains(param, StringComparer.OrdinalIgnoreCase)) return param.ToLowerInvariant();
        if (!RefusalWords.IsMatch(message)) return null;
        foreach (Match m in QuotedName.Matches(message))
            if (KnownParams.Contains(m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) return m.Groups[1].Value.ToLowerInvariant();
        // No quoted name: the first known parameter the sentence mentions (longest names first, so max_completion_tokens wins over max_tokens).
        foreach (var k in KnownParams)
            if (Regex.IsMatch(message, @"\b" + Regex.Escape(k) + @"\b", RegexOptions.IgnoreCase)) return k;
        return null;
    }
}
