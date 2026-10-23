using System.Net;
using System.Text.Json;
using Magpie.Core.Ai;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

public class Release810Tests
{
    private static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body) };
    private static Task<string> Complete(IAiProvider provider) => provider.CompleteAsync("system", new[] { new ChatMessage("user", "Say OK") }, null, 50, default);
    private static AiSettings Settings(string model = "claude-opus-4-6")
    {
        var settings = new AiSettings { Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = model };
        settings.NormaliseConnections();
        return settings;
    }

    [Fact]
    public async Task Catalog_retains_dates_lifecycle_and_per_model_output_limits()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("""{"data":[{"id":"retired","lifecycle":"retired","max_tokens":null},{"id":"new","created_at":"2026-10-23T00:00:00Z","max_tokens":20},{"id":"old","created_at":"2025-01-01T00:00:00Z","lifecycle":"deprecated"}],"has_more":false}""") };
        var models = await AiModelCatalog.DiscoverAsync(new HttpClient(http), "https://api.anthropic.com", "key", default);
        var s = Settings("new");
        AiModelCatalog.Apply(s.Active!, models, DateTimeOffset.UtcNow);
        Assert.Equal("new", s.Active!.Models[0].Id);
        Assert.True(s.Active.Models[0].IsNew);
        Assert.Equal(20, s.Active.Models[0].MaxOutputTokens);
        Assert.False(s.Active.Models.Single(m => m.Id == "retired").Available);
        Assert.Equal("deprecated", s.Active.Models.Single(m => m.Id == "old").Lifecycle);
        s.Active.Models.ForEach(m => m.Enabled = true);
        Assert.DoesNotContain("retired", AiModelCatalog.Published(s.Active));
        http.Respond = (_, _) => Json("""{"content":[{"type":"text","text":"OK"}]}""");
        await Complete(AiProviderFactory.Create(new HttpClient(http), s, "key"));
        using var request = JsonDocument.Parse(http.Requests.Last().body);
        Assert.Equal(20, request.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public void Refresh_does_not_reset_new_badge_age_or_mutate_discovery_results()
    {
        var s = Settings();
        var models = new[] { new AiModelInfo { Id = "a" } };
        var first = DateTimeOffset.UtcNow.AddDays(-31);
        AiModelCatalog.Apply(s.Active!, models, first);
        AiModelCatalog.Apply(s.Active!, models, DateTimeOffset.UtcNow);
        Assert.Equal(first, s.Active!.Models.Single().FirstSeenAt);
        Assert.False(s.Active.Models.Single().IsNew);
        Assert.Null(models[0].FirstSeenAt);
    }

    [Fact]
    public async Task Explicit_temperature_rejection_retries_once_and_remembers_successful_shape()
    {
        var http = new FakeHttp();
        http.Respond = (_, _) => http.Requests.Count == 1
            ? Json("""{"error":{"message":"temperature is deprecated and not supported","param":"temperature"}}""", 400)
            : Json("""{"content":[{"type":"text","text":"OK"}]}""");
        var learned = new List<string>();
        Assert.Equal("OK", await Complete(AiProviderFactory.Create(new HttpClient(http), Settings(), "key", learned.Add)));
        Assert.Equal(new[] { "temperature" }, learned);
        Assert.Equal(2, http.Requests.Count);
        using var first = JsonDocument.Parse(http.Requests[0].body);
        using var next = JsonDocument.Parse(http.Requests[1].body);
        Assert.True(first.RootElement.TryGetProperty("temperature", out _));
        Assert.False(next.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal(50, next.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("temperature must be between zero and one", "temperature")]
    [InlineData("max_tokens must be positive", "max_tokens")]
    [InlineData("invalid model", "model")]
    public async Task Value_errors_do_not_drop_parameters_or_replay(string message, string parameter)
    {
        var body = JsonSerializer.Serialize(new { error = new { message, param = parameter } });
        var http = new FakeHttp { Respond = (_, _) => Json(body, 400) };
        await Assert.ThrowsAsync<AiException>(() => Complete(AiProviderFactory.Create(new HttpClient(http), Settings(), "key")));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Anthropic_required_max_tokens_is_never_removed()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("""{"error":{"message":"max_tokens unsupported"}}""", 400) };
        await Assert.ThrowsAsync<AiException>(() => Complete(AiProviderFactory.Create(new HttpClient(http), Settings(), "key")));
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task OpenAI_can_request_completion_token_field_before_generation()
    {
        var http = new FakeHttp();
        http.Respond = (_, _) => http.Requests.Count == 1
            ? Json("""{"error":{"message":"Unsupported parameter: 'max_tokens'. Use 'max_completion_tokens' instead.","param":"max_tokens"}}""", 400)
            : Json("""{"choices":[{"message":{"content":"OK"}}]}""");
        await Complete(new OpenAiCompatibleProvider(new HttpClient(http), "https://api.openai.com/v1", "future", "key"));
        using var next = JsonDocument.Parse(http.Requests[1].body);
        Assert.Equal(50, next.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(next.RootElement.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void Learned_parameters_are_scoped_to_model_and_server_and_cloned()
    {
        var s = Settings();
        s.Active!.UnsupportedEndpoint = AiProviderFactory.ParameterScope(s.Endpoint);
        s.Active.Unsupported.Add(RequestShape.Key(s.Model, "temperature"));
        Assert.False(AiProviderFactory.Shape(s).SendTemperature);
        var copy = s.Clone();
        copy.Active!.Unsupported.Clear();
        Assert.Single(s.Active.Unsupported);
        s.Model = s.Active.Model = "claude-sonnet-4-6";
        Assert.True(AiProviderFactory.Shape(s).SendTemperature);
        s.Model = s.Active.Model = "claude-opus-4-6";
        s.Endpoint = s.Active.Endpoint = "https://other.example";
        Assert.True(AiProviderFactory.Shape(s).SendTemperature);
    }

    [Fact]
    public async Task Connection_temperature_is_used_and_roundtrips()
    {
        var s = Settings(); s.Active!.Temperature = 0.8;
        s = JsonSerializer.Deserialize<AiSettings>(JsonSerializer.Serialize(s))!;
        var http = new FakeHttp { Respond = (_, _) => Json("""{"content":[{"type":"text","text":"OK"}]}""") };
        await Complete(AiProviderFactory.Create(new HttpClient(http), s, "key"));
        using var request = JsonDocument.Parse(http.Requests.Single().body);
        Assert.Equal(0.8, request.RootElement.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Failed_parameter_recovery_is_bounded_and_does_not_persist_unverified_changes()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("""{"error":{"message":"temperature not supported"}}""", 400) };
        var learned = new List<string>();
        await Assert.ThrowsAsync<AiException>(() => Complete(AiProviderFactory.Create(new HttpClient(http), Settings(), "key", learned.Add)));
        Assert.Equal(2, http.Requests.Count);
        Assert.Empty(learned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_cannot_echo_credentials_in_error_identifiers(bool stream)
    {
        var http = new FakeHttp { Respond = (_, _) => stream
            ? FakeHttp.Sse("""{"type":"error","error":{"type":"secret-key"},"request_id":"req_secret-key"}""")
            : Json("""{"error":{"type":"secret-key"},"request_id":"req_secret-key"}""", 400) };
        var error = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "secret-key")));
        Assert.DoesNotContain("secret-key", error.ToString());
        Assert.Empty(error.RequestId);
        Assert.Empty(error.ErrorType);
    }

    [Fact]
    public async Task Low_credit_400_explains_billing_without_echoing_provider_message()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("""{"error":{"type":"invalid_request_error","message":"Your credit balance is too low: PRIVATE_EMAIL"}}""", 400) };
        var error = await Assert.ThrowsAsync<AiException>(() => Complete(new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-new", "key")));
        Assert.Equal(AiErrorKind.Billing, error.Kind);
        Assert.Equal(400, error.StatusCode);
        Assert.DoesNotContain("PRIVATE_EMAIL", error.Message);
        Assert.Single(http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_generation_remembers_parameters_only_for_unchanged_connection(bool changeEndpoint)
    {
        var s = Settings(); s.Enabled = s.Rewrite = true;
        s.Consents.Add(AiService.ConsentKey(AiFeature.Rewrite, s));
        var saved = 0;
        var http = new FakeHttp();
        http.Respond = (_, _) =>
        {
            if (http.Requests.Count == 1) return Json("""{"error":{"message":"temperature is not supported"}}""", 400);
            if (changeEndpoint) s.Endpoint = s.Active!.Endpoint = "https://new.example";
            return Json("""{"content":[{"type":"text","text":"OK"}]}""");
        };
        var service = new AiService(new HttpClient(http), () => s, () => "key") { SaveSettings = () => saved++ };
        Assert.Equal("OK", await service.RewriteAsync("test", RewriteKind.Shorter, null, _ => { }, default));
        Assert.Equal(changeEndpoint ? 0 : 1, saved);
        Assert.Equal(!changeEndpoint, s.Active!.Unsupported.Contains("claude-opus-4-6:temperature"));
        if (!changeEndpoint)
        {
            await service.RewriteAsync("again", RewriteKind.Shorter, null, _ => { }, default);
            using var last = JsonDocument.Parse(http.Requests.Last().body);
            Assert.False(last.RootElement.TryGetProperty("temperature", out _));
            Assert.Equal(1, saved);
        }
    }
}
