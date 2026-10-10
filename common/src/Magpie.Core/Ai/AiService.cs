using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magpie.Core.Mail;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Ai;

public enum AiAvailability
{
    /// <summary>Master switch off (S1): no AI anywhere.</summary>
    MasterOff,
    /// <summary>This feature's switch is off (S2 for the other three).</summary>
    FeatureOff,
    /// <summary>Switched on, but endpoint / model / key missing — show "Set up AI provider" (B5).</summary>
    NotConfigured,
    Ready,
}

public sealed class ThreadForAi
{
    public string Text { get; init; } = "";
    public int Included { get; init; }
    public int Total { get; init; }
    public bool Truncated => Included < Total;
    public string Digest { get; init; } = "";
}

public enum RewriteKind { Shorter, Longer, Friendlier, MoreFormal, FixGrammar, Custom }

/// <summary>
/// Everything that calls a model goes through here, so the toggle rules are enforced in one place:
/// master switch → provider configured → feature switch → consent (cloud providers only).
/// Nothing is ever inserted or sent automatically: every result goes back to the UI as a card to accept.
/// </summary>
public sealed class AiService
{
    private readonly HttpClient _http;
    private readonly Func<AiSettings> _settings;
    private readonly Func<string, string?> _apiKey;

    public const int ThreadBudgetChars = 24_000;

    public AiService(HttpClient http, Func<AiSettings> settings, Func<string?> apiKey)
    {
        _http = http;
        _settings = settings;
        _apiKey = _ => apiKey();
    }

    public AiService(HttpClient http, Func<AiSettings> settings, Func<string, string?> apiKeyFor)
    {
        _http = http;
        _settings = settings;
        _apiKey = apiKeyFor;
    }

    public static bool IsConfigured(AiSettings s, string? key) =>
        !string.IsNullOrWhiteSpace(s.Endpoint) && Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var endpoint)
        && (endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback) && !string.IsNullOrWhiteSpace(s.Model)
        && (!string.IsNullOrWhiteSpace(key) || AiProviderFactory.IsLocalEndpoint(s.Endpoint) || s.Provider == AiProviderKind.Custom);

    public static AiAvailability Availability(AiSettings s, string? key, AiFeature f)
    {
        if (!s.Enabled) return AiAvailability.MasterOff;
        if (!s.FeatureSwitch(f)) return AiAvailability.FeatureOff;
        return IsConfigured(s, key) ? AiAvailability.Ready : AiAvailability.NotConfigured;
    }

    public AiAvailability Availability(AiFeature f)
    {
        var s = _settings().Clone();
        return Availability(s, _apiKey(s.ActiveId), f);
    }

    /// <summary>Should the UI show this feature's control at all? (Visible when switched on, even if not yet configured.)</summary>
    public bool IsVisible(AiFeature f) => Availability(f) is AiAvailability.Ready or AiAvailability.NotConfigured;

    public bool IsLocal => AiProviderFactory.IsLocalEndpoint(_settings().Endpoint);

    public static string ConsentKey(AiFeature f, AiSettings s) =>
        $"{f}@{s.ActiveId}:{(Uri.TryCreate(s.Endpoint, UriKind.Absolute, out var uri) ? uri.AbsoluteUri.TrimEnd('/') : s.Endpoint)}";

    /// <summary>Local models never need consent — nothing leaves the PC.</summary>
    public bool NeedsConsent(AiFeature f)
    {
        var s = _settings();
        return !AiProviderFactory.IsLocalEndpoint(s.Endpoint) && !s.Consents.Contains(ConsentKey(f, s));
    }

    public string ProviderLabel
    {
        get
        {
            var s = _settings();
            var host = AiProviderFactory.Host(s.Endpoint);
            return AiProviderFactory.IsLocalEndpoint(s.Endpoint) ? $"{s.Model} on this PC" : $"{s.Model} at {host}";
        }
    }

    private IAiProvider Require(AiFeature f)
    {
        // Validate and construct from the same snapshot, even if Settings changes during a request.
        var s = _settings().Clone();
        var key = _apiKey(s.ActiveId);
        switch (Availability(s, key, f))
        {
            case AiAvailability.MasterOff: throw new AiException("AI features are turned off in Settings.");
            case AiAvailability.FeatureOff: throw new AiException("This AI feature is turned off in Settings.");
            case AiAvailability.NotConfigured: throw new AiException("Set up an AI provider in Settings → AI features first.");
        }
        if (!AiProviderFactory.IsLocalEndpoint(s.Endpoint) && !s.Consents.Contains(ConsentKey(f, s)))
            throw new AiException("Allow this AI feature to send your text to the selected provider first.");
        return AiProviderFactory.Create(_http, s, key);
    }

    // ───────────────────────── context building ─────────────────────────

    /// <summary>
    /// Plain-text transcript of a conversation for the model: quoted history stripped, newest messages
    /// kept when it is too long (the UI says "summarised the newest N of M").
    /// </summary>
    public static ThreadForAi BuildThread(string subject, IReadOnlyList<(MessageRow row, MessageBody? body)> messages, int budget = ThreadBudgetChars)
    {
        var blocks = new List<string>();
        foreach (var (row, body) in messages)
        {
            var text = body != null ? MimeText.StripQuoted(body.Text) : row.Preview;
            if (string.IsNullOrWhiteSpace(text)) text = row.Preview;
            text = Regex.Replace(text, @"[ \t]+", " ");
            if (text.Length > 6000) text = text[..6000] + " […]";
            blocks.Add($"From: {row.Sender} <{row.FromAddress}>\nDate: {row.Date.ToLocalTime():ddd d MMM yyyy HH:mm}\n\n{text.Trim()}");
        }
        var kept = new List<string>();
        int used = 0;
        for (int i = blocks.Count - 1; i >= 0; i--)
        {
            if (used + blocks[i].Length > budget && kept.Count > 0) break;
            kept.Insert(0, blocks[i]);
            used += blocks[i].Length;
        }
        var sb = new StringBuilder($"Subject: {subject}\n\n");
        sb.Append(string.Join("\n\n---\n\n", kept));
        var text2 = sb.ToString();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text2)))[..16];
        return new ThreadForAi { Text = text2, Included = kept.Count, Total = blocks.Count, Digest = digest };
    }

    // ───────────────────────── features ─────────────────────────

    private const string Rules = "Never invent facts, dates, amounts or commitments that are not in the email. Write in the same language as the email. Plain text only — no markdown headings.";

    public async Task<string> SummariseAsync(ThreadForAi thread, Action<string> onToken, CancellationToken ct)
    {
        var provider = Require(AiFeature.Summarise);
        var system = "You summarise email conversations for a busy reader. " + Rules;
        var prompt = "Summarise this email thread in 2–4 short bullet points (start each with \"• \"). "
                     + "Then, if something is asked of the reader or a decision/deadline is pending, add one final line starting with \"Action: \". "
                     + "Keep it under 90 words.\n\n" + thread.Text;
        return await provider.CompleteAsync(system, new[] { new ChatMessage("user", prompt) }, onToken, 400, ct);
    }

    public async Task<string> DraftAsync(string instruction, string tone, ThreadForAi? context, string myName, Action<string> onToken, CancellationToken ct)
    {
        var provider = Require(AiFeature.Draft);
        var system = "You write email drafts for the user, who will review and edit them before sending. " + Rules
                     + " Output only the email body (greeting, text, sign-off with the user's name). No subject line.";
        var sb = new StringBuilder();
        if (context != null) sb.Append("The user is replying to this conversation:\n\n").Append(context.Text).Append("\n\n");
        sb.Append($"Write the email. Tone: {tone}. The user's name: {myName}.\nWhat the user wants to say: {instruction}");
        return await provider.CompleteAsync(system, new[] { new ChatMessage("user", sb.ToString()) }, onToken, 700, ct);
    }

    public static string RewriteInstruction(RewriteKind kind, string? custom) => kind switch
    {
        RewriteKind.Shorter => "Make it shorter and more direct, keeping every fact.",
        RewriteKind.Longer => "Expand it a little with more helpful detail, without adding new facts.",
        RewriteKind.Friendlier => "Make it warmer and friendlier.",
        RewriteKind.MoreFormal => "Make it more formal and professional.",
        RewriteKind.FixGrammar => "Fix spelling, grammar and punctuation only. Change nothing else.",
        _ => custom ?? "Improve it.",
    };

    public async Task<string> RewriteAsync(string text, RewriteKind kind, string? custom, Action<string> onToken, CancellationToken ct)
    {
        var provider = Require(AiFeature.Rewrite);
        var system = "You rewrite a passage from an email the user is writing. " + Rules + " Output only the rewritten passage.";
        var prompt = RewriteInstruction(kind, custom) + "\n\nPassage:\n" + text;
        return await provider.CompleteAsync(system, new[] { new ChatMessage("user", prompt) }, onToken, Math.Clamp(text.Length / 2 + 200, 200, 1500), ct);
    }

    public async Task<List<string>> SuggestRepliesAsync(ThreadForAi thread, string myName, CancellationToken ct)
    {
        var provider = Require(AiFeature.Replies);
        var system = "You suggest short replies to the latest email in a thread, from the user's point of view. " + Rules;
        var prompt = $"The user is {myName}. Suggest exactly 3 different short replies (each under 25 words, ready to send, no greeting needed) to the latest message. "
                     + "Return them as a JSON array of 3 strings and nothing else.\n\n" + thread.Text;
        var text = await provider.CompleteAsync(system, new[] { new ChatMessage("user", prompt) }, null, 300, ct);
        return ParseReplies(text);
    }

    internal static List<string> ParseReplies(string text)
    {
        var m = Regex.Match(text, @"\[[\s\S]*\]");
        if (m.Success)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<string>>(m.Value);
                if (arr != null) return arr.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToList();
            }
            catch (JsonException) { }
        }
        return text.Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^(\d+[.)]|[-•*])\s*", "").Trim().Trim('"'))
            .Where(l => l.Length is > 1 and < 200)
            .Take(3).ToList();
    }

    public async Task<string> TestAsync(AiSettings candidate, string? key, CancellationToken ct)
    {
        if (!IsConfigured(candidate, key)) throw new AiException("Use HTTPS for a cloud provider, or HTTP for a model on this PC, and supply its model and key.");
        var p = AiProviderFactory.Create(_http, candidate.Clone(), key);
        var reply = await p.CompleteAsync("Reply with exactly: OK", new[] { new ChatMessage("user", "Say OK") }, null, 10, ct);
        return reply.Trim();
    }
}
