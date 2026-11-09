using System.Net.Http.Headers;
using System.Text.Json;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Security;

namespace Magpie.Core.CloudFiles;

/// <summary>Cloud consent has its own cache and protected refresh-token keys; it never replaces mail consent.</summary>
public sealed class CloudFilesAuthorization : ICloudFileAuthorization, ICloudExistingFileAuthorization
{
    public const string GoogleScope = "https://www.googleapis.com/auth/drive.file";
    // Broader Google consent is only requested when the user explicitly connects existing-file browsing.
    public const string GoogleExistingScope = "https://www.googleapis.com/auth/drive";
    public const string MicrosoftScope = "https://graph.microsoft.com/Files.ReadWrite";
    private readonly HttpClient _http;
    private readonly SecretVault _vault;
    private readonly OAuthService _oauth;
    private readonly OAuthService _existingOAuth;
    private readonly Func<AccountKind, OAuthConfig?> _mailConfig;
    private readonly Func<string, Account?>? _currentAccount;

    public CloudFilesAuthorization(HttpClient http, SecretVault vault, Func<AccountKind, OAuthConfig?> mailConfig,
        Func<string, Account?>? currentAccount = null)
    {
        _http = http;
        _vault = vault;
        _mailConfig = mailConfig;
        _currentAccount = currentAccount;
        _oauth = new OAuthService(http, id => vault.Get(RefreshKey(id)), SaveRefresh)
        {
            ConfigFor = ConfigFor,
        };
        _existingOAuth = new OAuthService(http, id => vault.Get(ExistingRefreshKey(id)), SaveExistingRefresh)
        {
            ConfigFor = ExistingConfigFor,
        };
    }

    public static string RefreshKey(string accountId) => $"account:{accountId}:cloud:refresh";
    private static string BindingKey(string accountId) => $"account:{accountId}:cloud:binding";
    private static string ScopeKey(string accountId) => $"account:{accountId}:cloud:scopes";
    public static string ExistingRefreshKey(string accountId) => $"account:{accountId}:cloud:browse:refresh";
    private static string ExistingBindingKey(string accountId) => $"account:{accountId}:cloud:browse:binding";
    private static string ExistingScopeKey(string accountId) => $"account:{accountId}:cloud:browse:scopes";
    public static string ProviderName(AccountKind kind) => kind == AccountKind.Gmail ? "Google Drive" : "OneDrive";

    public OAuthConfig? ConfigFor(AccountKind kind)
    {
        var cfg = _mailConfig(kind);
        if (cfg == null) return null;
        return kind switch
        {
            AccountKind.Gmail => cfg with { Scopes = GoogleScope + " openid email", ExtraAuthParams = "access_type=offline&prompt=consent" },
            AccountKind.Microsoft => cfg with { Scopes = MicrosoftScope + " https://graph.microsoft.com/User.Read offline_access openid email", ExtraAuthParams = "prompt=select_account" },
            _ => null,
        };
    }

    public OAuthConfig? ExistingConfigFor(AccountKind kind)
    {
        var cfg = _mailConfig(kind);
        if (cfg == null) return null;
        return kind switch
        {
            AccountKind.Gmail => cfg with { Scopes = GoogleExistingScope + " openid email", ExtraAuthParams = "access_type=offline&prompt=consent" },
            AccountKind.Microsoft => cfg with { Scopes = MicrosoftScope + " https://graph.microsoft.com/User.Read offline_access openid email", ExtraAuthParams = "prompt=select_account" },
            _ => null,
        };
    }

    private static string Binding(Account account, OAuthConfig cfg) =>
        JsonSerializer.Serialize(new[] { account.Kind.ToString(), account.Email.Trim().ToLowerInvariant(), cfg.ClientId });

    private void SaveRefresh(string accountId, string? value)
    {
        // A refresh response can arrive after account removal. Never resurrect protected cloud credentials
        // unless the current account and client still own the binding that initiated cloud consent.
        if (_currentAccount != null && (_currentAccount(accountId) is not { } account
            || ConfigFor(account.Kind) is not { } cfg || _vault.Get(BindingKey(accountId)) != Binding(account, cfg)))
            throw new ReauthRequiredException("The cloud account or sign-in app changed. Connect it again.");
        _vault.Set(RefreshKey(accountId), value);
    }

    private void SaveExistingRefresh(string accountId, string? value)
    {
        if (_currentAccount != null && (_currentAccount(accountId) is not { } account
            || ExistingConfigFor(account.Kind) is not { } cfg || _vault.Get(ExistingBindingKey(accountId)) != Binding(account, cfg)))
            throw new ReauthRequiredException("The existing-file account or sign-in app changed. Connect again.");
        _vault.Set(ExistingRefreshKey(accountId), value);
    }

    private bool AccountStillMatches(Account account) => _currentAccount == null
        || _currentAccount(account.Id) is { } current && current.Kind == account.Kind
            && current.Email.Equals(account.Email, StringComparison.OrdinalIgnoreCase);

    public bool IsConnected(Account account) => AccountStillMatches(account) && ConfigFor(account.Kind) is { } cfg
        && _vault.Get(BindingKey(account.Id)) == Binding(account, cfg)
        && !string.IsNullOrWhiteSpace(_vault.Get(RefreshKey(account.Id)));

    public bool IsExistingConnected(Account account) => AccountStillMatches(account) && ExistingConfigFor(account.Kind) is { } cfg
        && _vault.Get(ExistingBindingKey(account.Id)) == Binding(account, cfg)
        && !string.IsNullOrWhiteSpace(_vault.Get(ExistingRefreshKey(account.Id)));

    public async Task SignInExistingAsync(Account account, Action<string> openBrowser, CancellationToken ct)
    {
        if (!AccountStillMatches(account)) throw new InvalidOperationException("The selected account changed. Choose it again.");
        var cfg = ExistingConfigFor(account.Kind) ?? throw new InvalidOperationException("Choose a Google or Microsoft account.");
        var binding = Binding(account, cfg);
        var tokens = await _existingOAuth.SignInAsync(cfg, account.Email, openBrowser, ct);
        EnsureExistingScopes(account.Kind, tokens.Scopes);
        await VerifyIdentityAsync(account, tokens.AccessToken, ct);
        if (string.IsNullOrWhiteSpace(tokens.RefreshToken))
            throw new ReauthRequiredException("Existing-file sign-in did not grant continued access. Connect again.");
        if (!AccountStillMatches(account) || ExistingConfigFor(account.Kind) is not { } current || Binding(account, current) != binding)
            throw new InvalidOperationException("The account or sign-in app changed. Connect again.");
        ct.ThrowIfCancellationRequested();
        _vault.Set(ExistingBindingKey(account.Id), binding);
        _vault.Set(ExistingScopeKey(account.Id), tokens.Scopes);
        _existingOAuth.Remember(account.Id, tokens);
    }

    public async Task<CloudAuthorization> AuthorizeExistingAsync(Account account, CancellationToken ct)
    {
        if (!IsExistingConnected(account))
        {
            _existingOAuth.Forget(account.Id);
            throw new ReauthRequiredException("Connect existing-file browsing for " + account.Email + " before searching or sharing.");
        }
        var token = await _existingOAuth.GetAccessTokenAsync(account, ct);
        var scopes = _existingOAuth.GrantedScopes(account.Id);
        if (string.IsNullOrWhiteSpace(scopes)) scopes = _vault.Get(ExistingScopeKey(account.Id)) ?? "";
        EnsureExistingScopes(account.Kind, scopes);
        var authorization = await VerifyIdentityAsync(account, token, ct);
        if (!IsExistingConnected(account))
        {
            _existingOAuth.Forget(account.Id);
            throw new ReauthRequiredException("The existing-file account changed. Connect again.");
        }
        return authorization;
    }

    public async Task SignInAsync(Account account, Action<string> openBrowser, CancellationToken ct)
    {
        if (!AccountStillMatches(account)) throw new InvalidOperationException("The selected account changed. Close this window and choose the cloud account again.");
        var cfg = ConfigFor(account.Kind) ?? throw new InvalidOperationException("Choose a Google or Microsoft account.");
        var binding = Binding(account, cfg);
        var tokens = await _oauth.SignInAsync(cfg, account.Email, openBrowser, ct);
        EnsureScopes(account.Kind, tokens.Scopes);
        await VerifyIdentityAsync(account, tokens.AccessToken, ct);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new ReauthRequiredException("Cloud sign-in did not grant continued access. Connect again and approve the requested cloud permission.");
        if (!AccountStillMatches(account) || ConfigFor(account.Kind) is not { } current || Binding(account, current) != binding)
            throw new InvalidOperationException("The account or sign-in app changed. Connect the cloud account again.");
        ct.ThrowIfCancellationRequested();
        _vault.Set(BindingKey(account.Id), binding);
        _vault.Set(ScopeKey(account.Id), tokens.Scopes);
        _oauth.Remember(account.Id, tokens);
    }

    public async Task<CloudAuthorization> AuthorizeAsync(Account account, CancellationToken ct)
    {
        if (!IsConnected(account))
        {
            _oauth.Forget(account.Id);
            throw new ReauthRequiredException("Connect " + ProviderName(account.Kind) + " for " + account.Email + " before uploading.");
        }
        var token = await _oauth.GetAccessTokenAsync(account, ct);
        // RFC 6749 permits a refresh response to omit scope when it is unchanged. Use only this exact binding's consent.
        var scopes = _oauth.GrantedScopes(account.Id);
        if (string.IsNullOrWhiteSpace(scopes)) scopes = _vault.Get(ScopeKey(account.Id)) ?? "";
        EnsureScopes(account.Kind, scopes);
        var authorization = await VerifyIdentityAsync(account, token, ct);
        if (!IsConnected(account))
        {
            _oauth.Forget(account.Id);
            throw new ReauthRequiredException("The cloud account or sign-in app changed. Connect it again.");
        }
        return authorization;
    }

    public void Forget(string accountId) { _oauth.Forget(accountId); _existingOAuth.Forget(accountId); }

    internal static void EnsureExistingScopes(AccountKind kind, string scopes)
    {
        if (kind == AccountKind.Gmail && OAuthService.Allows(scopes, GoogleExistingScope)) return;
        if (kind == AccountKind.Microsoft && (OAuthService.Allows(scopes, MicrosoftScope) || OAuthService.Allows(scopes, "Files.ReadWrite"))
            && (OAuthService.Allows(scopes, "https://graph.microsoft.com/User.Read") || OAuthService.Allows(scopes, "User.Read"))) return;
        throw new ReauthRequiredException("Existing-file browse/share permission was not granted. Reconnect and approve the separately requested permission.");
    }

    internal static void EnsureScopes(AccountKind kind, string scopes)
    {
        var allowed = kind == AccountKind.Gmail ? OAuthService.Allows(scopes, GoogleScope)
            : kind == AccountKind.Microsoft && (OAuthService.Allows(scopes, MicrosoftScope) || OAuthService.Allows(scopes, "Files.ReadWrite"))
                && (OAuthService.Allows(scopes, "https://graph.microsoft.com/User.Read") || OAuthService.Allows(scopes, "User.Read"));
        if (!allowed) throw new ReauthRequiredException("Cloud permission was not granted. Connect again and approve access to upload and share files.");
    }

    private async Task<CloudAuthorization> VerifyIdentityAsync(Account account, string token, CancellationToken ct)
    {
        var endpoint = account.Kind == AccountKind.Gmail
            ? "https://openidconnect.googleapis.com/v1/userinfo"
            : "https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ReauthRequiredException("The cloud account could not be verified. Connect it again.");
        using var json = await CloudFilesService.ReadJsonAsync(response, ct);
        var root = json.RootElement;
        static string Field(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        var email = account.Kind == AccountKind.Gmail ? Field(root, "email") : Field(root, "mail");
        var principal = Field(root, "userPrincipalName");
        var id = Field(root, account.Kind == AccountKind.Gmail ? "sub" : "id");
        var matches = email.Equals(account.Email.Trim(), StringComparison.OrdinalIgnoreCase)
            || account.Kind == AccountKind.Microsoft && principal.Equals(account.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        if (account.Kind == AccountKind.Gmail && (!root.TryGetProperty("email_verified", out var verified) || verified.ValueKind != JsonValueKind.True)) matches = false;
        if (!matches || string.IsNullOrWhiteSpace(id))
            throw new ReauthRequiredException("The signed-in cloud account does not match " + account.Email + ". Connect that exact account.");
        return new CloudAuthorization(token, account.Email.Trim(), id);
    }
}
