using System.Net;
using System.Net.Sockets;
using System.Text;
using Magpie.Core.Ai;
using Magpie.Core.Auth;
using Magpie.Core.Models;
using Magpie.Core.Security;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

public sealed class FakeHttp : HttpMessageHandler
{
    public List<(HttpRequestMessage req, string body)> Requests { get; } = new();
    public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.OK);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return Respond(request, body);
    }

    public static HttpResponseMessage Sse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var e in events) sb.Append("data: ").Append(e).Append("\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream") };
    }
}

public class AiToggleTests
{
    private static AiSettings S(bool master, bool sum = false, bool draft = false, bool rew = false, bool rep = false, string endpoint = "https://api.openai.com/v1") =>
        new() { Enabled = master, Summarise = sum, Draft = draft, Rewrite = rew, Replies = rep, Endpoint = endpoint, Model = "m" };

    [Fact]
    public void S1_master_off_hides_every_feature()
    {
        var s = S(false, true, true, true, true);
        foreach (var f in Enum.GetValues<AiFeature>())
            Assert.Equal(AiAvailability.MasterOff, AiService.Availability(s, "key", f));
    }

    [Fact]
    public void S2_only_summarise_on()
    {
        var s = S(true, sum: true);
        Assert.Equal(AiAvailability.Ready, AiService.Availability(s, "key", AiFeature.Summarise));
        Assert.Equal(AiAvailability.FeatureOff, AiService.Availability(s, "key", AiFeature.Draft));
        Assert.Equal(AiAvailability.FeatureOff, AiService.Availability(s, "key", AiFeature.Rewrite));
        Assert.Equal(AiAvailability.FeatureOff, AiService.Availability(s, "key", AiFeature.Replies));
    }

    [Fact]
    public void S3_all_on_but_cloud_without_key_is_not_configured_while_local_needs_no_key()
    {
        var cloud = S(true, true, true, true, true);
        foreach (var f in Enum.GetValues<AiFeature>())
        {
            Assert.Equal(AiAvailability.Ready, AiService.Availability(cloud, "key", f));
            Assert.Equal(AiAvailability.NotConfigured, AiService.Availability(cloud, null, f));
        }
        var local = S(true, true, true, true, true, "http://localhost:11434/v1");
        Assert.Equal(AiAvailability.Ready, AiService.Availability(local, null, AiFeature.Draft));
    }

    [Fact]
    public void Consent_needed_for_cloud_per_feature_and_host_never_for_local()
    {
        var s = S(true, true, true);
        var svc = new AiService(new HttpClient(new FakeHttp()), () => s, () => "k");
        Assert.True(svc.NeedsConsent(AiFeature.Summarise));
        s.Consents.Add(AiService.ConsentKey(AiFeature.Summarise, s));
        Assert.False(svc.NeedsConsent(AiFeature.Summarise));
        Assert.True(svc.NeedsConsent(AiFeature.Draft));
        s.Endpoint = "https://api.anthropic.com"; // new provider → ask again
        Assert.True(svc.NeedsConsent(AiFeature.Summarise));
        s.Endpoint = "http://127.0.0.1:1234/v1";
        Assert.False(svc.NeedsConsent(AiFeature.Draft));
        Assert.True(svc.IsLocal);
    }

    [Fact]
    public async Task Disabled_feature_never_calls_the_network()
    {
        var http = new FakeHttp();
        var s = S(true, sum: true);
        var svc = new AiService(new HttpClient(http), () => s, () => "k");
        var t = AiService.BuildThread("S", new[] { (Rows.Make("A", 1, "t"), (MessageBody?)null) });
        await Assert.ThrowsAsync<AiException>(() => svc.DraftAsync("x", "Friendly", null, "K", _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<AiException>(() => svc.RewriteAsync("x", RewriteKind.Shorter, null, _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<AiException>(() => svc.SuggestRepliesAsync(t, "K", CancellationToken.None));
        s.Enabled = false;
        await Assert.ThrowsAsync<AiException>(() => svc.SummariseAsync(t, _ => { }, CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void Long_threads_keep_newest_and_strip_quotes()
    {
        var msgs = Enumerable.Range(0, 30).Select(i => (Rows.Make("A", 1, "t", date: DateTimeOffset.Now.AddHours(i)),
            (MessageBody?)new MessageBody { Text = $"Message {i} " + new string('x', 2000) + "\nOn Mon, A wrote:\n> old" })).ToList();
        var t = AiService.BuildThread("Big", msgs, budget: 10_000);
        Assert.True(t.Truncated);
        Assert.Equal(30, t.Total);
        Assert.InRange(t.Included, 1, 6);
        Assert.Contains("Message 29", t.Text);
        Assert.DoesNotContain("Message 0 ", t.Text);
        Assert.DoesNotContain("> old", t.Text);
        Assert.Equal(16, t.Digest.Length);
    }

    [Theory]
    [InlineData("[\"Yes, Friday works.\", \"Can we do Monday?\", \"Thanks!\"]", 3)]
    [InlineData("Here you go:\n1. Sure\n2. No thanks\n3. Let me check", 3)]
    [InlineData("```json\n[\"A\",\"B\"]\n```", 2)]
    public void Parses_suggested_replies(string text, int count) => Assert.Equal(count, AiService.ParseReplies(text).Count);
}

public class AiProviderTests
{
    [Fact]
    public async Task OpenAI_compatible_streams_tokens()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => FakeHttp.Sse(
                "{\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
                "{\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}",
                "{\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}",
                "[DONE]"),
        };
        var p = new OpenAiCompatibleProvider(new HttpClient(http), "http://localhost:11434/v1/", "llama", null);
        var tokens = new List<string>();
        var text = await p.CompleteAsync("sys", new[] { new ChatMessage("user", "hi") }, tokens.Add, 50, CancellationToken.None);
        Assert.Equal("Hello", text);
        Assert.Equal(new[] { "Hel", "lo" }, tokens);
        var (req, body) = http.Requests.Single();
        Assert.Equal("http://localhost:11434/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Null(req.Headers.Authorization);
        Assert.Contains("\"stream\":true", body);
        Assert.Contains("\"role\":\"system\"", body);
    }

    [Fact]
    public async Task Anthropic_streams_and_sends_headers()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => FakeHttp.Sse(
                "{\"type\":\"message_start\"}",
                "{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"• Point\"}}",
                "{\"type\":\"message_stop\"}"),
        };
        var p = new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-x", "sk-ant-123");
        var text = await p.CompleteAsync("sys", new[] { new ChatMessage("user", "hi") }, null, 50, CancellationToken.None);
        Assert.Equal("• Point", text);
        var (req, body) = http.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.Equal("sk-ant-123", req.Headers.GetValues("x-api-key").Single());
        Assert.Contains("\"system\":\"sys\"", body);
    }

    [Fact]
    public async Task Errors_are_readable_and_keys_redacted()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("{\"error\":{\"message\":\"Incorrect API key provided: sk-proj-abcdefghijkl\"}}") },
        };
        var p = new OpenAiCompatibleProvider(new HttpClient(http), "https://api.openai.com/v1", "m", "sk-proj-abcdefghijkl");
        var ex = await Assert.ThrowsAsync<AiException>(() => p.CompleteAsync("s", new[] { new ChatMessage("user", "x") }, null, 5, CancellationToken.None));
        Assert.True(ex.Auth);
        Assert.StartsWith("The API key was rejected", ex.Message);
        Assert.DoesNotContain("abcdefghijkl", ex.Message);
    }

    [Fact]
    public async Task Summarise_goes_through_the_service_end_to_end()
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"choices\":[{\"delta\":{\"content\":\"• Decide by Friday\"}}]}", "[DONE]") };
        var s = new AiSettings { Enabled = true, Summarise = true, Endpoint = "https://api.openai.com/v1", Model = "gpt-4.1-mini" };
        var svc = new AiService(new HttpClient(http), () => s, () => "sk-1");
        var t = AiService.BuildThread("Vendor", new[] { (Rows.Make("A", 1, "t"), (MessageBody?)new MessageBody { Text = "We need a call by Friday." }) });
        var sb = new StringBuilder();
        var result = await svc.SummariseAsync(t, x => sb.Append(x), CancellationToken.None);
        Assert.Equal("• Decide by Friday", result);
        Assert.Contains("We need a call by Friday.", http.Requests.Single().body);
        Assert.Equal("Bearer", http.Requests.Single().req.Headers.Authorization!.Scheme);
    }
}

public class OAuthTests
{
    [Fact]
    public void Reads_email_from_id_token()
    {
        string B64(string s) => OAuthService.Base64Url(Encoding.UTF8.GetBytes(s));
        var jwt = B64("{\"alg\":\"none\"}") + "." + B64("{\"email\":\"k@gmail.com\",\"name\":\"Krishna B\"}") + ".sig";
        Assert.Equal(("k@gmail.com", "Krishna B"), OAuthService.ReadIdToken(jwt));
        var ms = B64("{}") + "." + B64("{\"preferred_username\":\"k@outlook.com\"}") + ".";
        Assert.Equal("k@outlook.com", OAuthService.ReadIdToken(ms).email);
    }

    [Fact]
    public async Task Loopback_pkce_flow_end_to_end()
    {
        var http = new FakeHttp
        {
            Respond = (req, body) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":3600}"),
            },
        };
        string? stored = null;
        var svc = new OAuthService(new HttpClient(http), _ => stored, (_, t) => stored = t);
        var cfg = OAuthService.Google("client-1", "secret-1");
        string? authUrl = null;
        var task = svc.SignInAsync(cfg, "k@gmail.com", url => authUrl = url, CancellationToken.None);
        // Simulate the browser following the redirect.
        for (int i = 0; i < 50 && authUrl == null; i++) await Task.Delay(20);
        Assert.NotNull(authUrl);
        var q = System.Web.HttpUtility.ParseQueryString(new Uri(authUrl!).Query);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("k@gmail.com", q["login_hint"]);
        var redirect = new Uri(q["redirect_uri"]!);
        Assert.Equal("127.0.0.1", redirect.Host);
        using (var browser = new HttpClient())
        {
            var page = await browser.GetStringAsync($"{redirect}?code=CODE1&state={q["state"]}");
            Assert.Contains("Signed in to Magpie", page);
        }
        var tokens = await task;
        Assert.Equal("AT", tokens.AccessToken);
        Assert.Equal("RT", tokens.RefreshToken);
        var form = http.Requests.Single().body;
        Assert.Contains("code=CODE1", form);
        Assert.Contains("code_verifier=", form);
        Assert.Contains("client_secret=secret-1", form);

        // Refresh path
        var acc = new Account { Id = "acc1", Kind = AccountKind.Gmail };
        svc.ConfigFor = _ => cfg;
        stored = "RT";
        http.Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"AT2\",\"expires_in\":3600}") };
        Assert.Equal("AT2", await svc.GetAccessTokenAsync(acc, CancellationToken.None));
        Assert.Equal("AT2", await svc.GetAccessTokenAsync(acc, CancellationToken.None)); // cached
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("RT", stored); // Google keeps the refresh token
    }

    [Fact]
    public async Task Revoked_refresh_token_asks_for_sign_in()
    {
        var http = new FakeHttp { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"Token has been expired or revoked.\"}") } };
        var svc = new OAuthService(new HttpClient(http), _ => "RT", (_, _) => { }) { ConfigFor = _ => OAuthService.Microsoft("cid") };
        await Assert.ThrowsAsync<ReauthRequiredException>(() => svc.GetAccessTokenAsync(new Account { Kind = AccountKind.Microsoft }, CancellationToken.None));
        Assert.Contains("scope=", http.Requests.Single().body);
    }

    [Fact]
    public async Task Missing_client_id_explains_setup()
    {
        var svc = new OAuthService(new HttpClient(new FakeHttp()), _ => null, (_, _) => { });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SignInAsync(OAuthService.Microsoft(""), null, _ => { }, CancellationToken.None));
        Assert.Contains("Sign-in apps", ex.Message);
    }
}

public class SettingsAndVaultTests
{
    [Fact]
    public void Vault_roundtrip_and_never_plaintext_on_disk()
    {
        using var dir = new TempDir();
        var v = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        v.Set(SecretVault.PasswordKey("a1"), "hunter2-password");
        v.Set(SecretVault.AiKey, "sk-live-1");
        Assert.DoesNotContain("hunter2", File.ReadAllText(dir.File("secrets.json")));
        var v2 = new SecretVault(dir.File("secrets.json"), new FakeProtector());
        Assert.Equal("hunter2-password", v2.Get(SecretVault.PasswordKey("a1")));
        v2.RemovePrefix("account:a1:");
        Assert.Null(v2.Get(SecretVault.PasswordKey("a1")));
        Assert.Equal("sk-live-1", v2.Get(SecretVault.AiKey));
    }

    [Fact]
    public void Settings_roundtrip_and_corrupt_file_falls_back()
    {
        using var dir = new TempDir();
        var st = new SettingsStore(dir.File("settings.json"));
        st.Load();
        st.Current.Ai.Enabled = true;
        st.Current.Ai.Summarise = true;
        st.Current.UndoSendSeconds = 99;
        st.Current.SenderCategories["News@Shop.com"] = Category.Newsletters;
        st.Save();
        var st2 = new SettingsStore(dir.File("settings.json"));
        var s = st2.Load();
        Assert.True(s.Ai.Enabled && s.Ai.Summarise && !s.Ai.Draft);
        Assert.Equal(30, s.UndoSendSeconds); // clamped
        Assert.True(s.SenderCategories.ContainsKey("news@shop.com"));
        File.WriteAllText(dir.File("settings.json"), "{ not json");
        var s3 = new SettingsStore(dir.File("settings.json")).Load();
        Assert.False(s3.Ai.Enabled);
        Assert.True(File.Exists(dir.File("settings.json.bad")));
    }
}
