using System.Net;
using System.Text.Json;
using Magpie.Core.Ai;
using Magpie.Core.Security;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

public class AiDiscoveryTests
{
    private static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static readonly ChatMessage[] Messages = { new("user", "hello") };
    private static Task<string> Complete(IAiProvider provider, Action<string>? token = null, CancellationToken ct = default) =>
        provider.CompleteAsync("system", Messages, token, 50, ct);

    [Theory]
    [InlineData("https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/")]
    [InlineData("https://api.anthropic.com/v1")]
    [InlineData("https://api.anthropic.com/v1/")]
    [InlineData("https://api.anthropic.com/v1/messages")]
    public async Task Discovery_paginates_and_uses_only_credentials_not_email(string endpoint)
    {
        var http = new FakeHttp();
        http.Respond = (_, _) => http.Requests.Count == 1
            ? Json("""{"data":[{"id":"claude-a","display_name":"Claude A"}],"has_more":true,"last_id":"a +&?"}""")
            : Json("""{"data":[{"id":"claude-a"},{"id":"claude-b","display_name":"Claude B"}],"has_more":false}""");
        var models = await AiModelCatalog.DiscoverAsync(new HttpClient(http), endpoint, "secret", default);
        Assert.Equal(new[] { "claude-a", "claude-b" }, models.Select(m => m.Id));
        Assert.Equal("Claude A", models[0].Name);
        Assert.All(models, m => Assert.False(m.Enabled));
        Assert.All(http.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.req.Method);
            Assert.Equal("api.anthropic.com", r.req.RequestUri!.Host);
            Assert.Equal("/v1/models", r.req.RequestUri.AbsolutePath);
            Assert.Equal("secret", r.req.Headers.GetValues("x-api-key").Single());
            Assert.Equal("2023-06-01", r.req.Headers.GetValues("anthropic-version").Single());
            Assert.Empty(r.body);
            Assert.Null(r.req.Headers.Authorization);
        });
        Assert.Contains("after_id=a%20%2B%26%3F", http.Requests[1].req.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://cloud.example")]
    [InlineData("https://user:pass@cloud.example")]
    [InlineData("https://cloud.example?api_key=secret")]
    [InlineData("https://cloud.example#fragment")]
    [InlineData("file:///tmp/key")]
    [InlineData("not a url")]
    public async Task Unsafe_discovery_endpoints_fail_before_network(string endpoint)
    {
        var http = new FakeHttp();
        await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), endpoint, "secret", default));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("http://localhost:5000/proxy/v1/messages", "http://localhost:5000/proxy/v1")]
    [InlineData("http://127.0.0.1:5000", "http://127.0.0.1:5000/v1")]
    [InlineData("https://proxy.example/anthropic/v1/models/", "https://proxy.example/anthropic/v1")]
    public void Endpoint_normalisation_preserves_proxy_paths(string endpoint, string expected) => Assert.Equal(expected, AiModelCatalog.Scope(endpoint));

    [Fact]
    public async Task Missing_key_never_calls_network()
    {
        var http = new FakeHttp();
        await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", " ", default));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"data\":{}}")]
    [InlineData("{\"data\":[{\"id\":null}]}")]
    [InlineData("{\"data\":[],\"has_more\":true,\"last_id\":null}")]
    public async Task Malformed_or_incomplete_catalog_fails_cleanly(string body)
    {
        var http = new FakeHttp { Respond = (_, _) => Json(body) };
        var ex = await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "secret", default));
        Assert.Contains("saved models have been kept", ex.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Repeated_cursor_stops_instead_of_looping_forever()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("""{"data":[],"has_more":true,"last_id":"same"}""") };
        await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "secret", default));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Failed_second_page_leaves_cache_untouched()
    {
        var connection = Connection();
        var before = JsonSerializer.Serialize(connection);
        var http = new FakeHttp();
        http.Respond = (_, _) => http.Requests.Count == 1
            ? Json("""{"data":[{"id":"new"}],"has_more":true,"last_id":"new"}""") : Json("{}", 401);
        await Assert.ThrowsAsync<AiException>(async () =>
            AiModelCatalog.Apply(connection, await AiModelCatalog.DiscoverAsync(new HttpClient(http), connection.Endpoint, "secret", default), DateTimeOffset.UtcNow));
        Assert.Equal(before, JsonSerializer.Serialize(connection));
    }

    private static AiConnection Connection() => new()
    {
        Id = "c", Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "manual-alias",
        CatalogEndpoint = "https://api.anthropic.com/v1", Models = new()
        {
            new() { Id = "a", Name = "A", Enabled = true }, new() { Id = "b", Enabled = false }, new() { Id = "retired", Enabled = true },
        },
    };

    [Fact]
    public void Refresh_preserves_choices_and_manual_model_and_marks_missing_models_unavailable()
    {
        var c = Connection();
        var now = DateTimeOffset.UtcNow;
        AiModelCatalog.Apply(c, new[] { new AiModelInfo { Id = "b" }, new AiModelInfo { Id = "a", Name = "Renamed" }, new AiModelInfo { Id = "new" } }, now);
        Assert.Equal("manual-alias", c.Model);
        Assert.Equal(now, c.ModelsRefreshedAt);
        Assert.Equal(new[] { "a" }, AiModelCatalog.Published(c));
        Assert.Equal("Renamed", c.Models.Single(m => m.Id == "a").Name);
        Assert.False(c.Models.Single(m => m.Id == "retired").Available);
        Assert.True(c.Models.Single(m => m.Id == "retired").Enabled);
        Assert.False(c.Models.Single(m => m.Id == "new").Enabled);
        AiModelCatalog.Apply(c, new[] { new AiModelInfo { Id = "retired" } }, now);
        Assert.Equal(new[] { "retired" }, AiModelCatalog.Published(c));
    }

    [Fact]
    public void Catalog_is_scoped_to_endpoint_provider_and_connection()
    {
        var c = Connection();
        var other = c.Clone();
        other.Endpoint = "https://another.example";
        Assert.Empty(AiModelCatalog.Published(other));
        AiModelCatalog.Apply(other, new[] { new AiModelInfo { Id = "a" } }, DateTimeOffset.UtcNow);
        Assert.False(other.Models[0].Enabled);
        Assert.True(c.Models[0].Enabled);
        other.Provider = AiProviderKind.OpenAI;
        Assert.Empty(AiModelCatalog.Published(other));
        c.Endpoint = "malformed";
        Assert.Empty(AiModelCatalog.Published(c));
    }

    [Fact]
    public void Settings_migrate_roundtrip_and_clone_metadata_without_secrets()
    {
        var old = JsonSerializer.Deserialize<AiSettings>("""{"Provider":1,"Endpoint":"https://api.anthropic.com","Model":"legacy"}""")!;
        old.NormaliseConnections();
        Assert.Equal(AiConnection.FirstId, old.ActiveId);
        Assert.Equal("legacy", old.Active!.Model);
        Assert.Empty(old.Active.Models);
        old.Connections.Add(Connection());
        var clone = old.Clone();
        clone.Connections[1].Models[0].Enabled = false;
        Assert.True(old.Connections[1].Models[0].Enabled);
        var json = JsonSerializer.Serialize(old);
        var copy = JsonSerializer.Deserialize<AiSettings>(json)!;
        Assert.Equal(new[] { "a", "retired" }, AiModelCatalog.Published(copy.Connections[1]));
        Assert.DoesNotContain("ApiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(SecretVault.AiKey, SecretVault.AiKeyFor(old.ActiveId));
        Assert.NotEqual(SecretVault.AiKeyFor("c"), SecretVault.AiKeyFor("other"));
    }

    [Theory]
    [InlineData("claude-opus-4-7", false)]
    [InlineData("claude-opus-4-8", false)]
    [InlineData("claude-sonnet-5-5", false)]
    [InlineData("claude-mythos-preview", false)]
    [InlineData("claude-new-model", false)]
    [InlineData("claude-opus-4-6", true)]
    [InlineData("claude-sonnet-4-5-20250929", true)]
    [InlineData("claude-haiku-4-5", true)]
    [InlineData("claude-3-7-sonnet-20250219", true)]
    public async Task Claude_parameters_use_conservative_model_specific_defaults(string model, bool temperature)
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"type\":\"message_stop\"}") };
        await Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com/v1/", model, "secret"));
        using var body = JsonDocument.Parse(http.Requests.Single().body);
        Assert.Equal(temperature, body.RootElement.TryGetProperty("temperature", out _));
        Assert.False(body.RootElement.TryGetProperty("top_p", out _));
        Assert.False(body.RootElement.TryGetProperty("top_k", out _));
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
        Assert.Equal(50, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("/v1/messages", http.Requests[0].req.RequestUri!.AbsolutePath);
    }

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5.1", true)]
    [InlineData("o3-mini", true)]
    [InlineData("o4-mini", true)]
    [InlineData("gpt-4.1-mini", false)]
    [InlineData("llama3.1:8b", false)]
    public async Task OpenAI_reasoning_and_existing_local_parameters_remain_compatible(string model, bool reasoning)
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("[DONE]") };
        await Complete(new OpenAiCompatibleProvider(new HttpClient(http), "http://localhost:11434/v1", model, null));
        using var body = JsonDocument.Parse(http.Requests[0].body);
        Assert.Equal(!reasoning, body.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(!reasoning, body.RootElement.TryGetProperty("max_tokens", out _));
        Assert.Equal(reasoning, body.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Theory]
    [InlineData(400, AiErrorKind.InvalidRequest)]
    [InlineData(401, AiErrorKind.Authentication)]
    [InlineData(402, AiErrorKind.Billing)]
    [InlineData(403, AiErrorKind.Permission)]
    [InlineData(404, AiErrorKind.ModelNotFound)]
    [InlineData(413, AiErrorKind.InvalidRequest)]
    [InlineData(429, AiErrorKind.RateLimit)]
    [InlineData(500, AiErrorKind.Unavailable)]
    [InlineData(529, AiErrorKind.Unavailable)]
    public async Task Structured_generation_errors_are_safe_and_never_auto_replayed(int status, AiErrorKind kind)
    {
        var http = new FakeHttp { Respond = (_, _) =>
        {
            var result = Json("""{"error":{"type":"api_error","message":"secret key PRIVATE_MAIL_TEXT"},"request_id":"req_123"}""", status);
            result.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(20));
            return result;
        } };
        var ex = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret key")));
        Assert.Equal(kind, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
        Assert.Equal("req_123", ex.RequestId);
        Assert.Equal(TimeSpan.FromSeconds(20), ex.RetryAfter);
        Assert.Equal(status is 401 or 403, ex.Auth);
        Assert.Equal(status is 429 or >= 500, ex.Retryable);
        Assert.DoesNotContain("secret key", ex.ToString());
        Assert.DoesNotContain("PRIVATE_MAIL_TEXT", ex.ToString());
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData("<html>PRIVATE_MAIL_TEXT secret</html>")]
    [InlineData("[]")]
    [InlineData("{\"error\":\"secret\",\"request_id\":\"private text with spaces\"}")]
    public async Task Untrusted_error_bodies_are_not_echoed(string body)
    {
        var http = new FakeHttp { Respond = (_, _) => Json(body, 400) };
        var ex = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret")));
        Assert.DoesNotContain("secret", ex.ToString());
        Assert.DoesNotContain("PRIVATE_MAIL_TEXT", ex.ToString());
        Assert.Empty(ex.RequestId);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(529)]
    public async Task Discovery_retries_transient_rejections_at_most_twice(int status)
    {
        var http = new FakeHttp { Respond = (_, _) =>
        {
            var r = Json("{}", status);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        } };
        await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "secret", default));
        Assert.Equal(3, http.Requests.Count);
    }

    [Fact]
    public async Task Discovery_honours_long_retry_after_without_waiting_or_retrying_early()
    {
        var http = new FakeHttp { Respond = (_, _) =>
        {
            var r = Json("{}", 429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return r;
        } };
        await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "secret", default));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Discovery_recovers_from_transient_failure()
    {
        var http = new FakeHttp();
        http.Respond = (_, _) =>
        {
            var r = http.Requests.Count == 1 ? Json("{}", 503) : Json("{\"data\":[],\"has_more\":false}");
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        };
        Assert.Empty(await AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "secret", default));
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task Stream_error_after_tokens_is_structured_and_never_replayed()
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse(
            """{"type":"content_block_delta","delta":{"text":"partial"}}""",
            """{"type":"error","error":{"type":"overloaded_error","message":"secret"}}""") };
        var tokens = new List<string>();
        var ex = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret"), tokens.Add));
        Assert.Equal(new[] { "partial" }, tokens);
        Assert.Equal(AiErrorKind.Unavailable, ex.Kind);
        Assert.True(ex.PartialResponse);
        Assert.False(ex.Retryable);
        Assert.DoesNotContain("secret", ex.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Truncated_stream_is_not_reported_as_success()
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("""{"type":"content_block_delta","delta":{"text":"partial"}}""") };
        var ex = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret")));
        Assert.Equal(AiErrorKind.Incomplete, ex.Kind);
        Assert.True(ex.PartialResponse);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Non_streaming_anthropic_reply_is_supported()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("""{"content":[{"type":"thinking","thinking":"hidden"},{"type":"text","text":"OK"}]}""") };
        var tokens = new List<string>();
        Assert.Equal("OK", await Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com/v1/messages", "claude-new", "secret"), tokens.Add));
        Assert.Equal(new[] { "OK" }, tokens);
    }

    [Fact]
    public async Task Cancellation_is_propagated_and_not_retried()
    {
        var http = new FakeHttp { Respond = (_, _) => throw new OperationCanceledException() };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret"), ct: cts.Token));
        Assert.True(http.Requests.Count <= 1);
    }

    [Fact]
    public async Task Transport_failure_does_not_expose_key_or_replay_generation()
    {
        var http = new FakeHttp { Respond = (_, _) => throw new HttpRequestException("secret PRIVATE_MAIL_TEXT") };
        var ex = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret")));
        Assert.Equal(AiErrorKind.Network, ex.Kind);
        Assert.DoesNotContain("secret", ex.ToString());
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Test_connection_uses_candidate_without_mutating_settings_or_sending_mail()
    {
        var settings = new AiSettings { Provider = AiProviderKind.OpenAI, Endpoint = "https://api.openai.com/v1", Model = "existing" };
        var before = JsonSerializer.Serialize(settings);
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"type\":\"content_block_delta\",\"delta\":{\"text\":\"OK\"}}", "{\"type\":\"message_stop\"}") };
        var service = new AiService(new HttpClient(http), () => settings, _ => "saved-key");
        await service.TestAsync(new AiSettings { Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-new" }, "candidate-key", default);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Contains("Say OK", http.Requests.Single().body);
        Assert.Equal("candidate-key", http.Requests[0].req.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task Empty_test_reply_does_not_report_connected()
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"type\":\"message_stop\"}") };
        var service = new AiService(new HttpClient(http), () => new AiSettings(), () => null);
        var ex = await Assert.ThrowsAsync<AiException>(() => service.TestAsync(new AiSettings
            { Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-new" }, "key", default));
        Assert.Contains("empty test reply", ex.Message);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Discovery_never_retries_permanent_errors(int status)
    {
        var http = new FakeHttp { Respond = (_, _) => Json("{}", status) };
        await Assert.ThrowsAsync<AiException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "key", default));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Cancellation_interrupts_discovery_backoff()
    {
        using var cts = new CancellationTokenSource();
        var http = new FakeHttp { Respond = (_, _) =>
        {
            cts.Cancel();
            var result = Json("{}", 503);
            result.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
            return result;
        } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "key", cts.Token));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Timeout_is_readable_and_generation_is_not_replayed()
    {
        var http = new FakeHttp { Respond = (_, _) => throw new TaskCanceledException("key PRIVATE_MAIL_TEXT") };
        var ex = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "key")));
        Assert.Contains("too long", ex.Message);
        Assert.DoesNotContain("PRIVATE_MAIL_TEXT", ex.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Engine_does_not_follow_redirects_with_api_keys()
    {
        var portProbe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        var endpoint = $"http://127.0.0.1:{port}";
        listener.Prefixes.Add(endpoint + "/");
        listener.Start();
        var requests = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var context = await listener.GetContextAsync();
                    Interlocked.Increment(ref requests);
                    if (context.Request.Url!.AbsolutePath == "/redirected")
                    {
                        var bytes = System.Text.Encoding.UTF8.GetBytes("{\"data\":[]}");
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }
                    else { context.Response.StatusCode = 302; context.Response.RedirectLocation = endpoint + "/redirected"; }
                    context.Response.Close();
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        });
        try
        {
            using var dir = new TempDir();
            using var engine = new MailEngine(new AppPaths(dir.Path), new FakeProtector());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ex = await Assert.ThrowsAsync<AiException>(() => engine.Ai.DiscoverModelsAsync(endpoint, "synthetic-secret", cts.Token));
            Assert.Contains("redirected", ex.Message);
            Assert.Equal(1, requests);
        }
        finally { listener.Stop(); await server; }
    }
}
