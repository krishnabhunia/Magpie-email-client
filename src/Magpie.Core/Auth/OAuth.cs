using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Magpie.Core.Models;

namespace Magpie.Core.Auth;

public sealed record OAuthConfig(string Name, string AuthorizeUrl, string TokenUrl, string Scopes, string ClientId, string? ClientSecret, string RedirectHost, string ExtraAuthParams);

public sealed class OAuthTokens
{
    public string AccessToken { get; init; } = "";
    public string? RefreshToken { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
}

/// <summary>The saved sign-in no longer works (revoked, expired, password changed) — the user must sign in again.</summary>
public sealed class ReauthRequiredException : Exception
{
    public ReauthRequiredException(string message) : base(message) { }
}

/// <summary>
/// OAuth 2.0 authorization-code flow with PKCE and a loopback redirect (RFC 8252), used for
/// Gmail and Microsoft accounts. A tiny TCP listener on 127.0.0.1/::1 receives the redirect —
/// no admin rights or URL ACL needed. Access tokens are cached in memory; refresh tokens go to the vault.
/// </summary>
public sealed class OAuthService
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _getRefresh;
    private readonly Action<string, string?> _setRefresh;
    private readonly Dictionary<string, OAuthTokens> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Func<AccountKind, OAuthConfig?> ConfigFor { get; set; } = _ => null;

    public OAuthService(HttpClient http, Func<string, string?> getRefresh, Action<string, string?> setRefresh)
    {
        _http = http;
        _getRefresh = getRefresh;
        _setRefresh = setRefresh;
    }

    public static OAuthConfig Google(string clientId, string clientSecret) => new(
        "Google",
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        "https://mail.google.com/ openid email profile",
        clientId, clientSecret, "127.0.0.1",
        "access_type=offline&prompt=consent");

    public static OAuthConfig Microsoft(string clientId) => new(
        "Microsoft",
        "https://login.microsoftonline.com/common/oauth2/v2.0/authorize",
        "https://login.microsoftonline.com/common/oauth2/v2.0/token",
        "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access openid email profile",
        clientId, null, "localhost",
        "prompt=select_account");

    /// <summary>Interactive sign-in. <paramref name="openBrowser"/> is called with the URL to show.</summary>
    public async Task<OAuthTokens> SignInAsync(OAuthConfig cfg, string? loginHint, Action<string> openBrowser, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ClientId))
            throw new InvalidOperationException($"No {cfg.Name} sign-in app is set up yet. Add its client ID in Settings → Accounts → Sign-in apps.");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new LoopbackListener();
        var redirect = $"http://{cfg.RedirectHost}:{listener.Port}/";

        var url = new StringBuilder(cfg.AuthorizeUrl)
            .Append("?response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(cfg.ClientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirect))
            .Append("&scope=").Append(Uri.EscapeDataString(cfg.Scopes))
            .Append("&code_challenge=").Append(challenge)
            .Append("&code_challenge_method=S256")
            .Append("&state=").Append(state);
        if (!string.IsNullOrWhiteSpace(loginHint)) url.Append("&login_hint=").Append(Uri.EscapeDataString(loginHint));
        if (!string.IsNullOrEmpty(cfg.ExtraAuthParams)) url.Append('&').Append(cfg.ExtraAuthParams);

        openBrowser(url.ToString());
        var query = await listener.WaitForRedirectAsync(ct);
        if (query["state"] != state) throw new InvalidOperationException("Sign-in was interrupted (state mismatch). Please try again.");
        if (query["error"] is { } err) throw new InvalidOperationException($"{cfg.Name} sign-in failed: {query["error_description"] ?? err}");
        var code = query["code"] ?? throw new InvalidOperationException("Sign-in did not return a code.");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["client_id"] = cfg.ClientId,
            ["code_verifier"] = verifier,
        };
        if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
        if (cfg.Name == "Microsoft") form["scope"] = cfg.Scopes;
        return await TokenRequestAsync(cfg, form, ct);
    }

    private async Task<OAuthTokens> TokenRequestAsync(OAuthConfig cfg, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await _http.PostAsync(cfg.TokenUrl, new FormUrlEncodedContent(form), ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        var root = doc.RootElement;
        if (!resp.IsSuccessStatusCode)
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : resp.StatusCode.ToString();
            var desc = root.TryGetProperty("error_description", out var ed) ? ed.GetString() : "";
            if (error is "invalid_grant" or "unauthorized_client" or "interaction_required")
                throw new ReauthRequiredException($"{cfg.Name}: {error} {desc}".Trim());
            throw new InvalidOperationException($"{cfg.Name} token request failed: {error} {desc}".Trim());
        }
        var access = root.GetProperty("access_token").GetString() ?? "";
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var expires = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var secs) ? secs : 3600;
        string? email = null, name = null;
        if (root.TryGetProperty("id_token", out var idt) && idt.GetString() is { } jwt)
            (email, name) = ReadIdToken(jwt);
        return new OAuthTokens { AccessToken = access, RefreshToken = refresh, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expires - 60), Email = email, Name = name };
    }

    internal static (string? email, string? name) ReadIdToken(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return (null, null);
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;
            string? Get(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Get("email") ?? Get("preferred_username") ?? Get("upn"), Get("name"));
        }
        catch { return (null, null); }
    }

    /// <summary>Stores the first tokens of a newly added account.</summary>
    public void Remember(string accountId, OAuthTokens t)
    {
        if (!string.IsNullOrEmpty(t.RefreshToken)) _setRefresh(accountId, t.RefreshToken);
        lock (_cache) _cache[accountId] = t;
    }

    public void Forget(string accountId)
    {
        lock (_cache) _cache.Remove(accountId);
    }

    /// <summary>A valid access token, refreshed when needed.</summary>
    public async Task<string> GetAccessTokenAsync(Account a, CancellationToken ct)
    {
        lock (_cache)
            if (_cache.TryGetValue(a.Id, out var t) && t.ExpiresAt > DateTimeOffset.UtcNow) return t.AccessToken;

        await _gate.WaitAsync(ct);
        try
        {
            lock (_cache)
                if (_cache.TryGetValue(a.Id, out var t2) && t2.ExpiresAt > DateTimeOffset.UtcNow) return t2.AccessToken;
            var cfg = ConfigFor(a.Kind) ?? throw new ReauthRequiredException($"No sign-in app configured for {a.Kind}.");
            var refresh = _getRefresh(a.Id);
            if (string.IsNullOrEmpty(refresh)) throw new ReauthRequiredException("This account needs to sign in again.");
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["client_id"] = cfg.ClientId,
            };
            if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;
            if (cfg.Name == "Microsoft") form["scope"] = cfg.Scopes;
            var tokens = await TokenRequestAsync(cfg, form, ct);
            // Microsoft rotates refresh tokens; Google keeps the old one.
            if (!string.IsNullOrEmpty(tokens.RefreshToken) && tokens.RefreshToken != refresh) _setRefresh(a.Id, tokens.RefreshToken);
            lock (_cache) _cache[a.Id] = tokens;
            return tokens.AccessToken;
        }
        finally { _gate.Release(); }
    }

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Minimal HTTP listener for the OAuth redirect on the loopback interfaces (IPv4 and IPv6, same port).</summary>
internal sealed class LoopbackListener : IDisposable
{
    private readonly TcpListener _v4;
    private readonly TcpListener? _v6;
    public int Port { get; }

    public LoopbackListener()
    {
        _v4 = new TcpListener(IPAddress.Loopback, 0);
        _v4.Start();
        Port = ((IPEndPoint)_v4.LocalEndpoint).Port;
        try
        {
            _v6 = new TcpListener(IPAddress.IPv6Loopback, Port);
            _v6.Start();
        }
        catch { _v6 = null; }
    }

    public async Task<System.Collections.Specialized.NameValueCollection> WaitForRedirectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            var tasks = new List<Task<TcpClient>> { _v4.AcceptTcpClientAsync(timeout.Token).AsTask() };
            if (_v6 != null) tasks.Add(_v6.AcceptTcpClientAsync(timeout.Token).AsTask());
            Task<TcpClient> done;
            try { done = await Task.WhenAny(tasks); }
            catch (OperationCanceledException) { throw new TimeoutException("Sign-in timed out."); }
            TcpClient client;
            try { client = await done; }
            catch (OperationCanceledException) { throw new TimeoutException("Sign-in timed out — no response from the browser within 5 minutes."); }
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[16384];
                int read = 0;
                var readCts = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(10));
                try
                {
                    while (read < buffer.Length)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(read), readCts.Token);
                        if (n == 0) break;
                        read += n;
                        if (Encoding.ASCII.GetString(buffer, 0, read).Contains("\r\n\r\n")) break;
                    }
                }
                catch (OperationCanceledException) { continue; }
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var firstLine = request.Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2 || !parts[1].Contains('?'))
                {
                    await Respond(stream, 404, "Not found");
                    continue; // e.g. favicon
                }
                var query = HttpUtility.ParseQueryString(parts[1][(parts[1].IndexOf('?') + 1)..]);
                var ok = query["code"] != null;
                await Respond(stream, 200, ok
                    ? "<h2>Signed in to Magpie</h2><p>You can close this tab and return to Magpie.</p>"
                    : "<h2>Sign-in was not completed</h2><p>Return to Magpie and try again.</p>");
                return query;
            }
        }
    }

    private static async Task Respond(NetworkStream s, int code, string body)
    {
        var html = $"<!doctype html><html><head><meta charset=utf-8><title>Magpie</title></head><body style=\"font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#14181C\">{body}</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        var head = $"HTTP/1.1 {code} {(code == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(bytes);
        await s.FlushAsync();
    }

    public void Dispose()
    {
        try { _v4.Stop(); } catch { }
        try { _v6?.Stop(); } catch { }
    }
}
