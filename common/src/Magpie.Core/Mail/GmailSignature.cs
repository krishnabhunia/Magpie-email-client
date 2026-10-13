using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Magpie.Core.Mail;

/// <summary>
/// Reads the signature set in Gmail (Settings → See all settings → Signature) through the Gmail API's "send as"
/// settings, for accounts signed in with Google. The mail scope Magpie already asks for covers it; the Gmail API must
/// be switched on in the Google Cloud project that holds Magpie's sign-in app.
/// </summary>
public static class GmailSignature
{
    public const string Url = "https://gmail.googleapis.com/gmail/v1/users/me/settings/sendAs";

    public sealed class Problem : Exception
    {
        public Problem(string message) : base(message) { }
    }

    public static async Task<string> FetchAsync(HttpClient http, string accessToken, string email, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (res.StatusCode == HttpStatusCode.Forbidden && body.Contains("SERVICE_DISABLED", StringComparison.Ordinal) || body.Contains("has not been used in project", StringComparison.Ordinal))
            throw new Problem("The Gmail API is off in your Google Cloud project (the one with Magpie's sign-in app). Switch it on there (APIs & Services → Library → Gmail API → Enable), wait a minute, then try again.");
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new Problem("Google didn't allow reading your Gmail settings. Sign in to this account again (Settings → Accounts), then try again.");
        if (!res.IsSuccessStatusCode) throw new Problem($"Google answered {(int)res.StatusCode}. Try again later.");
        return Pick(body, email) ?? throw new Problem("There's no signature set in Gmail for " + email + ".");
    }

    /// <summary>The signature HTML for <paramref name="email"/> from a sendAs list (the primary address if none matches); null when empty.</summary>
    public static string? Pick(string json, string email)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("sendAs", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        JsonElement? match = null, primary = null;
        foreach (var s in list.EnumerateArray())
        {
            var addr = s.TryGetProperty("sendAsEmail", out var a) ? a.GetString() ?? "" : "";
            if (addr.Equals(email, StringComparison.OrdinalIgnoreCase)) match = s;
            if (s.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True) primary = s;
        }
        var chosen = match ?? primary;
        if (chosen is not { } c || !c.TryGetProperty("signature", out var sig)) return null;
        var html = sig.GetString()?.Trim();
        return string.IsNullOrEmpty(html) ? null : html;
    }
}
