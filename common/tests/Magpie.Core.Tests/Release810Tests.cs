using System.Net;
using System.Text.Json;
using Magpie.Core.Ai;
using Magpie.Core.Models;
using Magpie.Core.Settings;

namespace Magpie.Core.Tests;

/// <summary>8.1.0 (design AM1, queue #75): the AI model list comes from the provider, newest first with "New"; requests are
/// shaped to what each model accepts (no temperature for Claude 5.x / Fable, max_completion_tokens for o-series / GPT-5),
/// a refused setting is retried without and remembered.</summary>
public class Release810Tests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    // ── Catalogue: parsing each provider ──

    [Fact]
    public async Task Anthropic_list_follows_pages_and_sends_its_headers()
    {
        var http = new FakeHttp
        {
            Respond = (req, _) => req.RequestUri!.Query.Contains("after_id")
                ? Json("{\"data\":[{\"id\":\"claude-haiku-4-5\",\"display_name\":\"Claude Haiku 4.5\",\"created_at\":\"2025-10-15T00:00:00Z\",\"type\":\"model\"}],\"has_more\":false,\"last_id\":\"claude-haiku-4-5\"}")
                : Json("{\"data\":[{\"id\":\"claude-opus-5-5\",\"display_name\":\"Claude Opus 5.5\",\"created_at\":\"2026-09-01T00:00:00Z\",\"type\":\"model\"},"
                       + "{\"id\":\"claude-sonnet-4-5\",\"display_name\":\"Claude Sonnet 4.5\",\"created_at\":\"2025-09-29T00:00:00Z\",\"type\":\"model\"}],\"has_more\":true,\"last_id\":\"claude-sonnet-4-5\"}"),
        };
        var list = await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.Anthropic, "https://api.anthropic.com", "sk-ant-1", CancellationToken.None);
        Assert.Equal(new[] { "claude-opus-5-5", "claude-haiku-4-5", "claude-sonnet-4-5" }, list.Select(m => m.Id));
        Assert.Equal("Claude Opus 5.5", list[0].Name);
        Assert.Equal(2, http.Requests.Count);
        var (first, _) = http.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/models?limit=100", first.RequestUri!.ToString());
        Assert.Equal("sk-ant-1", first.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", first.Headers.GetValues("anthropic-version").Single());
        Assert.Contains("after_id=claude-sonnet-4-5", http.Requests[1].req.RequestUri!.Query);
    }

    [Fact]
    public async Task OpenAI_list_keeps_chat_models_only_and_names_them()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => Json("{\"object\":\"list\",\"data\":["
                + "{\"id\":\"gpt-4.1-mini\",\"object\":\"model\",\"created\":1744318000,\"owned_by\":\"openai\"},"
                + "{\"id\":\"text-embedding-3-small\",\"object\":\"model\",\"created\":1705948997,\"owned_by\":\"openai\"},"
                + "{\"id\":\"gpt-4o-audio-preview\",\"object\":\"model\",\"created\":1727460000,\"owned_by\":\"openai\"},"
                + "{\"id\":\"whisper-1\",\"object\":\"model\",\"created\":1677532384,\"owned_by\":\"openai\"},"
                + "{\"id\":\"gpt-5\",\"object\":\"model\",\"created\":1754000000,\"owned_by\":\"openai\"},"
                + "{\"id\":\"o4-mini\",\"object\":\"model\",\"created\":1744700000,\"owned_by\":\"openai\"},"
                + "{\"id\":\"gpt-3.5-turbo-instruct\",\"object\":\"model\",\"created\":1692901427,\"owned_by\":\"openai\"},"
                + "{\"id\":\"dall-e-3\",\"object\":\"model\",\"created\":1698785189,\"owned_by\":\"openai\"}]}"),
        };
        var list = await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.OpenAI, "https://api.openai.com/v1", "sk-1", CancellationToken.None);
        Assert.Equal(new[] { "gpt-5", "o4-mini", "gpt-4.1-mini" }, list.Select(m => m.Id));
        Assert.Equal("GPT-4.1 mini", list[2].Name);
        Assert.Equal("o4 mini", list[1].Name);
        Assert.Equal("https://api.openai.com/v1/models", http.Requests.Single().req.RequestUri!.ToString());
        Assert.Equal("Bearer", http.Requests.Single().req.Headers.Authorization!.Scheme);
    }

    [Theory]
    [InlineData("gpt-4o", true)]
    [InlineData("o3", true)]
    [InlineData("chatgpt-4o-latest", true)]
    [InlineData("gpt-4o-realtime-preview", false)]
    [InlineData("gpt-4o-transcribe", false)]
    [InlineData("text-embedding-3-large", false)]
    [InlineData("omni-moderation-latest", false)]
    [InlineData("gpt-image-1", false)]
    public void OpenAI_chat_filter(string id, bool chat) => Assert.Equal(chat, ModelCatalog.IsOpenAiChatModel(id));

    [Fact]
    public async Task Ollama_list_comes_from_api_tags_with_sizes()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => Json("{\"models\":[{\"name\":\"llama3.1:8b\",\"size\":4920753328,\"modified_at\":\"2026-03-01T10:00:00Z\"},"
                + "{\"name\":\"phi4:latest\",\"size\":9053116391,\"modified_at\":\"2026-05-01T10:00:00Z\"}]}"),
        };
        var list = await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.Ollama, "http://localhost:11434/v1", null, CancellationToken.None);
        Assert.Equal("http://localhost:11434/api/tags", http.Requests.Single().req.RequestUri!.ToString());
        Assert.Equal(new[] { "phi4:latest", "llama3.1:8b" }, list.Select(m => m.Id));
        Assert.Equal("llama3.1:8b · 4.6 GB", list[1].Name);
    }

    [Fact]
    public async Task Other_server_lists_OpenAI_shaped_or_says_it_cannot()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("{\"data\":[{\"id\":\"local-model\"},{\"id\":\"my-embedding\"}]}") };
        var list = await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.Custom, "http://localhost:1234/v1", null, CancellationToken.None);
        Assert.Equal(2, list.Count);   // no chat filter for an unknown server

        var none = new FakeHttp { Respond = (_, _) => new HttpResponseMessage(HttpStatusCode.NotFound) };
        var ex = await Assert.ThrowsAsync<AiException>(() => ModelCatalog.ListAsync(new HttpClient(none), AiProviderKind.Custom, "http://localhost:1234/v1", null, CancellationToken.None));
        Assert.Equal("This server doesn't list its models", ex.Message);
    }

    [Fact]
    public async Task A_rejected_key_is_an_auth_error_in_plain_words()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", HttpStatusCode.Unauthorized) };
        var ex = await Assert.ThrowsAsync<AiException>(() => ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.Anthropic, "https://api.anthropic.com", "bad", CancellationToken.None));
        Assert.True(ex.Auth);
        Assert.Equal("Anthropic refused the key (401). Check the key.", ex.Message);
    }

    // ── Catalogue: pure parts ──

    [Fact]
    public void Sorts_newest_first_then_by_id_with_undated_last()
    {
        var list = ModelCatalog.Sort(new[]
        {
            new AiModelInfo { Id = "b-undated" }, new AiModelInfo { Id = "old", Created = Now.AddYears(-1) },
            new AiModelInfo { Id = "a-undated" }, new AiModelInfo { Id = "new", Created = Now },
        });
        Assert.Equal(new[] { "new", "old", "b-undated", "a-undated" }, list.Select(m => m.Id));
    }

    [Fact]
    public void Recommended_is_the_newest_haiku_or_mini_else_the_first()
    {
        var claude = ModelCatalog.Sort(new[]
        {
            new AiModelInfo { Id = "claude-haiku-4-5", Created = Now.AddYears(-1) }, new AiModelInfo { Id = "claude-opus-5-5", Created = Now },
            new AiModelInfo { Id = "claude-haiku-5-5", Created = Now.AddDays(-10) },
        });
        Assert.Equal("claude-haiku-5-5", ModelCatalog.Recommended(AiProviderKind.Anthropic, claude)!.Id);
        var openai = ModelCatalog.Sort(new[] { new AiModelInfo { Id = "gpt-5", Created = Now }, new AiModelInfo { Id = "gpt-5-mini", Created = Now.AddDays(-1) } });
        Assert.Equal("gpt-5-mini", ModelCatalog.Recommended(AiProviderKind.OpenAI, openai)!.Id);
        var other = new[] { new AiModelInfo { Id = "x" }, new AiModelInfo { Id = "y" } };
        Assert.Equal("x", ModelCatalog.Recommended(AiProviderKind.Custom, other)!.Id);
        Assert.Null(ModelCatalog.Recommended(AiProviderKind.Anthropic, Array.Empty<AiModelInfo>()));
    }

    [Theory]
    [InlineData(AiProviderKind.Anthropic, "claude-opus-5-5")]
    [InlineData(AiProviderKind.OpenAI, "gpt-5")]
    [InlineData(AiProviderKind.Ollama, "llama3.1:8b")]
    public void Built_in_lists_are_filled_newest_first(AiProviderKind kind, string first)
    {
        var list = ModelCatalog.BuiltIn(kind);
        Assert.NotEmpty(list);
        Assert.Equal(first, list[0].Id);
        Assert.All(list, m => Assert.False(ModelCatalog.IsNew(m, Now)));
        Assert.Contains(list, m => m.Id == AiSettings.Preset(kind).model);
        Assert.Empty(ModelCatalog.BuiltIn(AiProviderKind.Custom));
    }

    [Fact]
    public void First_list_marks_nothing_new_and_a_later_id_is_new_for_30_days()
    {
        var fetched = new[] { new AiModelInfo { Id = "claude-haiku-4-5" }, new AiModelInfo { Id = "claude-sonnet-4-5" } };
        var first = ModelCatalog.Merge(null, fetched, Now, checkedBefore: false);
        Assert.All(first, m => Assert.False(ModelCatalog.IsNew(m, Now)));

        var later = ModelCatalog.Merge(first, fetched.Append(new AiModelInfo { Id = "claude-opus-5-5" }), Now.AddDays(3), checkedBefore: true);
        Assert.False(ModelCatalog.IsNew(later.Single(m => m.Id == "claude-haiku-4-5"), Now.AddDays(3)));
        var opus = later.Single(m => m.Id == "claude-opus-5-5");
        Assert.Equal(Now.AddDays(3), opus.FirstSeen);
        Assert.True(ModelCatalog.IsNew(opus, Now.AddDays(3)));
        Assert.True(ModelCatalog.IsNew(opus, Now.AddDays(32)));
        Assert.False(ModelCatalog.IsNew(opus, Now.AddDays(34)));

        // The notice offers it unless it is already in use or was dismissed.
        var c = new AiConnection { Model = "claude-haiku-4-5", Models = later, ModelsCheckedAt = Now.AddDays(3) };
        Assert.Equal("claude-opus-5-5", ModelCatalog.NewModelToOffer(c, Now.AddDays(4))!.Id);
        c.DismissedNewModel = "claude-opus-5-5";
        Assert.Null(ModelCatalog.NewModelToOffer(c, Now.AddDays(4)));
        c.DismissedNewModel = "";
        c.Model = "claude-opus-5-5";
        Assert.Null(ModelCatalog.NewModelToOffer(c, Now.AddDays(4)));
    }

    // ── Request shape ──

    [Theory]
    [InlineData(AiProviderKind.Anthropic, "claude-haiku-4-5", true, "max_tokens")]
    [InlineData(AiProviderKind.Anthropic, "claude-opus-4-1", true, "max_tokens")]
    [InlineData(AiProviderKind.Anthropic, "claude-fable-5-1", false, "max_tokens")]
    [InlineData(AiProviderKind.Anthropic, "claude-mythos-1", false, "max_tokens")]
    [InlineData(AiProviderKind.Anthropic, "claude-opus-5-5", false, "max_tokens")]
    [InlineData(AiProviderKind.Anthropic, "claude-sonnet-5-0", false, "max_tokens")]
    [InlineData(AiProviderKind.OpenAI, "gpt-4.1-mini", true, "max_tokens")]
    [InlineData(AiProviderKind.OpenAI, "gpt-4o", true, "max_tokens")]
    [InlineData(AiProviderKind.OpenAI, "o3", false, "max_completion_tokens")]
    [InlineData(AiProviderKind.OpenAI, "o4-mini", false, "max_completion_tokens")]
    [InlineData(AiProviderKind.OpenAI, "gpt-5", false, "max_completion_tokens")]
    [InlineData(AiProviderKind.Ollama, "llama3.1:8b", true, "max_tokens")]
    [InlineData(AiProviderKind.Custom, "local-model", true, "max_tokens")]
    public void Built_in_shape_table(AiProviderKind kind, string model, bool temperature, string field)
    {
        var s = RequestShape.For(kind, model, null, 0.4);
        Assert.Equal(temperature, s.SendTemperature);
        Assert.Equal(field, s.MaxTokensField);
        Assert.Equal(0.4, s.Temperature);
    }

    [Fact]
    public void Learned_refusals_override_the_table_per_model()
    {
        var learned = new[] { "gpt-4.1-mini:temperature", "gpt-4.1-mini:max_tokens", "claude-haiku-4-5:max_tokens" };
        var s = RequestShape.For(AiProviderKind.OpenAI, "gpt-4.1-mini", learned, 0.7);
        Assert.False(s.SendTemperature);
        Assert.Equal("max_completion_tokens", s.MaxTokensField);   // OpenAI: the newer name, not no limit
        var other = RequestShape.For(AiProviderKind.OpenAI, "gpt-4o", learned, 0.7);
        Assert.True(other.SendTemperature);   // another model's refusals don't apply
        var custom = RequestShape.For(AiProviderKind.Ollama, "x", new[] { "x:max_tokens" }, 0.4);
        Assert.Null(custom.MaxTokensField);   // not OpenAI: no limit is sent, Magpie cuts the text itself
        var both = RequestShape.For(AiProviderKind.OpenAI, "gpt-4.1-mini", learned.Append("gpt-4.1-mini:max_completion_tokens"), 0.7);
        Assert.Null(both.MaxTokensField);
        Assert.Equal("claude-opus-5-5:temperature", RequestShape.Key("claude-opus-5-5 ", "temperature"));
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"`temperature` is deprecated for this model.\"}}", "temperature")]
    [InlineData("{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\",\"type\":\"invalid_request_error\",\"param\":\"max_tokens\",\"code\":\"unsupported_parameter\"}}", "max_tokens")]
    [InlineData("{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0.4 with this model. Only the default (1) value is supported.\",\"type\":\"invalid_request_error\",\"param\":\"temperature\",\"code\":\"unsupported_value\"}}", "temperature")]
    [InlineData("{\"error\":{\"message\":\"max_completion_tokens is not supported by this model\",\"type\":\"invalid_request_error\"}}", "max_completion_tokens")]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"messages: at least one message is required\"}}", null)]
    [InlineData("{\"error\":{\"message\":\"The model `gpt-9` does not exist or you do not have access to it.\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":\"model_not_found\"}}", null)]
    [InlineData("not json at all", null)]
    public void Finds_the_refused_parameter_in_a_400(string body, string? expected) => Assert.Equal(expected, RequestShape.RefusedParameter(400, body));

    [Fact]
    public void Only_a_400_names_a_refused_parameter() =>
        Assert.Null(RequestShape.RefusedParameter(401, "{\"error\":{\"message\":\"`temperature` is deprecated\"}}"));

    // ── Providers: retry without the refused setting ──

    private const string TemperatureDeprecated = "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"`temperature` is deprecated for this model.\"}}";

    [Fact]
    public async Task Anthropic_retries_once_without_temperature_and_reports_it()
    {
        var http = new FakeHttp
        {
            Respond = (_, body) => body.Contains("\"temperature\"")
                ? Json(TemperatureDeprecated, HttpStatusCode.BadRequest)
                : FakeHttp.Sse("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello\"}}", "{\"type\":\"message_stop\"}"),
        };
        var refused = new List<string>();
        var shape = RequestShape.For(AiProviderKind.Anthropic, "claude-haiku-4-5", null, 0.4);
        var p = new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-haiku-4-5", "k", shape, refused.Add);
        var text = await p.CompleteAsync("sys", new[] { new ChatMessage("user", "hi") }, null, 50, CancellationToken.None);
        Assert.Equal("Hello", text);
        Assert.Equal(new[] { "temperature" }, refused);
        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("\"temperature\":0.4", http.Requests[0].body);
        Assert.DoesNotContain("temperature", http.Requests[1].body);
        Assert.Contains("\"max_tokens\":50", http.Requests[1].body);
    }

    [Fact]
    public async Task OpenAI_switches_max_tokens_to_max_completion_tokens_when_told_to()
    {
        var http = new FakeHttp
        {
            Respond = (_, body) => body.Contains("\"max_tokens\"")
                ? Json("{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\",\"type\":\"invalid_request_error\",\"param\":\"max_tokens\",\"code\":\"unsupported_parameter\"}}", HttpStatusCode.BadRequest)
                : FakeHttp.Sse("{\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}", "[DONE]"),
        };
        var refused = new List<string>();
        var p = new OpenAiCompatibleProvider(new HttpClient(http), "https://api.openai.com/v1", "gpt-4.1-mini", "k", RequestShape.For(AiProviderKind.OpenAI, "gpt-4.1-mini", null, 0.4), refused.Add);
        Assert.Equal("OK", await p.CompleteAsync("s", new[] { new ChatMessage("user", "x") }, null, 10, CancellationToken.None));
        Assert.Equal(new[] { "max_tokens->max_completion_tokens" }, refused);   // remembered as the replacement, not as "no length field"
        Assert.Equal(2, http.Requests.Count);
        Assert.Contains("\"max_completion_tokens\":10", http.Requests[1].body);
        Assert.DoesNotContain("\"max_tokens\"", http.Requests[1].body);
    }

    [Fact]
    public async Task An_OpenAI_compatible_server_that_asked_for_max_completion_tokens_gets_it_next_time()
    {
        var learned = new[] { "local-model:max_tokens->max_completion_tokens" };
        var custom = RequestShape.For(AiProviderKind.Custom, "local-model", learned, 0.4);
        Assert.Equal("max_completion_tokens", custom.MaxTokensField);   // not null: the server named the field it wants
        Assert.Equal("max_completion_tokens", RequestShape.For(AiProviderKind.OpenAI, "local-model", learned, 0.4).MaxTokensField);
        Assert.Equal("max_tokens", RequestShape.For(AiProviderKind.Anthropic, "local-model", learned, 0.4).MaxTokensField);
        Assert.Null(RequestShape.For(AiProviderKind.Custom, "local-model", new[] { "local-model:max_tokens" }, 0.4).MaxTokensField);   // a plain refusal: no field
        Assert.Equal(("max_tokens", "max_completion_tokens"), RequestShape.Parse("max_tokens->max_completion_tokens"));
        Assert.Equal(("temperature", (string?)null), RequestShape.Parse("temperature"));

        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}", "[DONE]") };
        var p = new OpenAiCompatibleProvider(new HttpClient(http), "http://localhost:1234/v1", "local-model", null, custom);
        await p.CompleteAsync("s", new[] { new ChatMessage("user", "x") }, null, 10, CancellationToken.None);
        Assert.Contains("\"max_completion_tokens\":10", http.Requests.Single().body);
        var r = new AiTestResult("OK", new[] { "temperature", "max_tokens->max_completion_tokens" });
        Assert.Equal(" (sent without temperature, with max_completion_tokens instead of max_tokens)", r.DroppedNote);
    }

    [Fact]
    public async Task A_server_that_always_refuses_gives_up_with_a_plain_message()
    {
        var http = new FakeHttp { Respond = (_, _) => Json(TemperatureDeprecated, HttpStatusCode.BadRequest) };
        var refused = new List<string>();
        var p = new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-haiku-4-5", "k", RequestShape.For(AiProviderKind.Anthropic, "claude-haiku-4-5", null, 0.4), refused.Add);
        var ex = await Assert.ThrowsAsync<AiException>(() => p.CompleteAsync("s", new[] { new ChatMessage("user", "x") }, null, 10, CancellationToken.None));
        Assert.InRange(http.Requests.Count, 2, 4);
        Assert.Equal(new[] { "temperature" }, refused);   // never the same setting twice
        Assert.StartsWith("Request failed (400): `temperature` is deprecated", ex.Message);
    }

    [Fact]
    public async Task A_remembered_refusal_is_applied_next_time_in_one_request()
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}", "{\"type\":\"message_stop\"}") };
        var s = new AiSettings
        {
            Enabled = true, Summarise = true,
            Connections = { new AiConnection { Id = "a", Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-haiku-4-5", Unsupported = { "claude-haiku-4-5:temperature" }, Temperature = 0.9 } },
            ActiveId = "a",
        };
        s.UseActive();
        var saved = 0;
        var svc = new AiService(new HttpClient(http), () => s, id => "sk-ant") { SaveSettings = () => saved++ };
        s.Consents.Add(AiService.ConsentKey(AiFeature.Summarise, s));
        var t = AiService.BuildThread("x", new[] { (Rows.Make("A", 1, "t"), (MessageBody?)new MessageBody { Text = "hello" }) });
        Assert.Equal("Hi", await svc.SummariseAsync(t, _ => { }, CancellationToken.None));
        Assert.Single(http.Requests);
        Assert.DoesNotContain("temperature", http.Requests[0].body);
        Assert.Equal(0, saved);
    }

    [Fact]
    public async Task A_refusal_during_use_is_remembered_on_the_connection_and_saved()
    {
        var http = new FakeHttp
        {
            Respond = (_, body) => body.Contains("\"temperature\"")
                ? Json(TemperatureDeprecated, HttpStatusCode.BadRequest)
                : FakeHttp.Sse("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}", "{\"type\":\"message_stop\"}"),
        };
        var s = new AiSettings
        {
            Enabled = true, Summarise = true,
            Connections = { new AiConnection { Id = "a", Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-opus-4-5" } },
            ActiveId = "a",
        };
        s.UseActive();
        var saved = 0;
        var svc = new AiService(new HttpClient(http), () => s, id => "sk-ant") { SaveSettings = () => saved++ };
        s.Consents.Add(AiService.ConsentKey(AiFeature.Summarise, s));
        var t = AiService.BuildThread("x", new[] { (Rows.Make("A", 1, "t"), (MessageBody?)new MessageBody { Text = "hello" }) });
        Assert.Equal("Hi", await svc.SummariseAsync(t, _ => { }, CancellationToken.None));
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(new[] { "claude-opus-4-5:temperature" }, s.Connections[0].Unsupported);
        Assert.Equal(1, saved);

        // Test connection says what was dropped.
        http.Requests.Clear();
        var result = await svc.TestAsync(s, "sk-ant", CancellationToken.None);
        Assert.Equal("Hi", result.Reply);
        Assert.Empty(result.Dropped);   // already remembered: sent without it in one go
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Test_connection_reports_what_it_had_to_drop()
    {
        var http = new FakeHttp
        {
            Respond = (_, body) => body.Contains("\"temperature\"")
                ? Json(TemperatureDeprecated, HttpStatusCode.BadRequest)
                : FakeHttp.Sse("{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}", "{\"type\":\"message_stop\"}"),
        };
        var live = new AiSettings { Connections = { new AiConnection { Id = "a", Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-fable-5-1" } }, ActiveId = "a" };
        var svc = new AiService(new HttpClient(http), () => live, id => "sk-ant");
        var candidate = new AiSettings { Enabled = true, Connections = { new AiConnection { Id = "a", Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-opus-4-5" } }, ActiveId = "a" };
        candidate.UseActive();
        var r = await svc.TestAsync(candidate, "sk-ant", CancellationToken.None);
        Assert.Equal("OK", r.Reply);
        Assert.Equal(new[] { "temperature" }, r.Dropped);
        Assert.Equal(" (sent without temperature)", r.DroppedNote);
        Assert.Equal(new[] { "claude-opus-4-5:temperature" }, live.Connections[0].Unsupported);   // remembered on the live connection too

        // The editor points at another server than the saved connection: the refusal goes back to the caller only.
        live.Connections[0].Unsupported.Clear();
        var elsewhere = new AiSettings { Enabled = true, Connections = { new AiConnection { Id = "a", Provider = AiProviderKind.Anthropic, Endpoint = "https://gateway.example/anthropic", Model = "claude-opus-4-5" } }, ActiveId = "a" };
        elsewhere.UseActive();
        var r2 = await svc.TestAsync(elsewhere, "sk-ant", CancellationToken.None);
        Assert.Equal(new[] { "temperature" }, r2.Dropped);
        Assert.Empty(live.Connections[0].Unsupported);
    }

    [Fact]
    public async Task Without_a_length_field_the_text_is_cut_by_Magpie()
    {
        var http = new FakeHttp { Respond = (_, _) => FakeHttp.Sse("{\"choices\":[{\"delta\":{\"content\":\"" + new string('a', 3000) + "\"}}]}", "{\"choices\":[{\"delta\":{\"content\":\"more\"}}]}", "[DONE]") };
        var shape = RequestShape.For(AiProviderKind.Custom, "m", new[] { "m:max_tokens" }, 0.4);
        Assert.Null(shape.MaxTokensField);
        var p = new OpenAiCompatibleProvider(new HttpClient(http), "http://localhost:1234/v1", "m", null, shape);
        var text = await p.CompleteAsync("s", new[] { new ChatMessage("user", "x") }, null, 100, CancellationToken.None);
        Assert.DoesNotContain("max_tokens", http.Requests.Single().body);
        Assert.Equal(3000, text.Length);   // stopped after ~4 × 100 characters: the second chunk was never appended
    }

    [Fact]
    public async Task Anthropic_never_learns_a_max_tokens_refusal()
    {
        var http = new FakeHttp
        {
            Respond = (_, _) => Json("{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"max_tokens: 100000 > 64000, which is the maximum allowed. Use 'max_completion_tokens' is not supported.\"}}", HttpStatusCode.BadRequest),
        };
        var refused = new List<string>();
        var p = new AnthropicProvider(new HttpClient(http), "https://api.anthropic.com", "claude-haiku-4-5", "k", RequestShape.For(AiProviderKind.Anthropic, "claude-haiku-4-5", null, 0.4), refused.Add);
        var ex = await Assert.ThrowsAsync<AiException>(() => p.CompleteAsync("s", new[] { new ChatMessage("user", "x") }, null, 100000, CancellationToken.None));
        Assert.Single(http.Requests);   // the field is part of the API: nothing to retry without
        Assert.Empty(refused);
        Assert.StartsWith("Request failed (400)", ex.Message);

        // A learned "max_tokens" entry (from an older build, or a gateway) changes nothing for Anthropic either.
        var shape = RequestShape.For(AiProviderKind.Anthropic, "claude-haiku-4-5", new[] { "claude-haiku-4-5:max_tokens", "claude-haiku-4-5:max_completion_tokens" }, 0.4);
        Assert.Equal("max_tokens", shape.MaxTokensField);
        Assert.True(shape.FixedMaxTokens);
        Assert.Equal(shape, shape.Without("max_tokens", "use max_completion_tokens"));
    }

    [Theory]
    [InlineData("gpt-5-chat-latest", true)]
    [InlineData("gpt-5-chat", true)]
    [InlineData("gpt-5-mini", false)]
    public void GPT5_chat_models_keep_temperature(string model, bool temperature) =>
        Assert.Equal(temperature, RequestShape.For(AiProviderKind.OpenAI, model, null, 0.4).SendTemperature);

    [Theory]
    [InlineData(AiProviderKind.OpenAI, "https://api.openai.com/v1/chat/completions", "https://api.openai.com/v1/models")]
    [InlineData(AiProviderKind.Custom, "http://localhost:1234/v1/chat/completions/", "http://localhost:1234/v1/models")]
    [InlineData(AiProviderKind.Anthropic, "https://api.anthropic.com/v1/messages", "https://api.anthropic.com/v1/models?limit=100")]
    [InlineData(AiProviderKind.Anthropic, "https://api.anthropic.com/v1", "https://api.anthropic.com/v1/models?limit=100")]
    [InlineData(AiProviderKind.Ollama, "http://127.0.0.1:11434/v1/chat/completions", "http://127.0.0.1:11434/api/tags")]
    public async Task A_saved_request_path_is_stripped_before_models_is_added(AiProviderKind kind, string endpoint, string expected)
    {
        var http = new FakeHttp { Respond = (_, _) => Json("{\"data\":[],\"models\":[]}") };
        await ModelCatalog.ListAsync(new HttpClient(http), kind, endpoint, "k", CancellationToken.None);
        Assert.Equal(expected, http.Requests.Single().req.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("http://api.openai.com/v1")]
    [InlineData("http://evil.example/v1")]
    [InlineData("ftp://x")]
    [InlineData("not a url")]
    public async Task The_key_never_goes_to_a_plain_http_or_odd_endpoint(string endpoint)
    {
        var http = new FakeHttp();
        var ex = await Assert.ThrowsAsync<AiException>(() => ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.OpenAI, endpoint, "sk-secret", CancellationToken.None));
        Assert.Equal(ModelCatalog.NeedsHttps, ex.Message);
        Assert.Empty(http.Requests);
        Assert.False(AiService.EndpointAllowed(endpoint));
    }

    [Fact]
    public async Task A_huge_list_keeps_the_newest_500()
    {
        // Mixed order on the wire: even ids are old, odd ids are new.
        var items = string.Join(",", Enumerable.Range(0, 700).Select(i => "{\"id\":\"gpt-" + i + "-" + new string('x', 400) + "\",\"created\":" + (i % 2 == 0 ? 1600000000 + i : 1700000000 + i) + "}"));
        var http = new FakeHttp { Respond = (_, _) => Json("{\"data\":[" + items + "]}") };
        var list = await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.OpenAI, "https://api.openai.com/v1", "k", CancellationToken.None);
        Assert.Equal(ModelCatalog.MaxModels, list.Count);
        Assert.All(list, m => { Assert.True(m.Id.Length <= ModelCatalog.MaxTextLength); Assert.True(m.Name.Length <= ModelCatalog.MaxTextLength); });
        Assert.Equal(350, list.Count(m => m.Created >= DateTimeOffset.FromUnixTimeSeconds(1700000000)));   // every new one is in
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000699), list[0].Created);
        Assert.True(list[^1].Created >= DateTimeOffset.FromUnixTimeSeconds(1600000000 + 400));   // the oldest 200 were dropped
    }

    [Fact]
    public async Task A_remote_Ollama_gets_the_key_as_a_bearer_header()
    {
        var http = new FakeHttp { Respond = (_, _) => Json("{\"models\":[{\"name\":\"llama3.2\"}]}") };
        await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.Ollama, "https://ollama.example/v1", "tok", CancellationToken.None);
        Assert.Equal("https://ollama.example/api/tags", http.Requests.Single().req.RequestUri!.ToString());
        Assert.Equal("Bearer tok", http.Requests.Single().req.Headers.Authorization!.ToString());
        http.Requests.Clear();
        await ModelCatalog.ListAsync(new HttpClient(http), AiProviderKind.Ollama, "http://localhost:11434/v1", null, CancellationToken.None);
        Assert.Null(http.Requests.Single().req.Headers.Authorization);
    }

    // ── Settings round-trip ──

    [Fact]
    public void New_connection_fields_round_trip_and_old_json_normalises()
    {
        var s = new AppSettings();
        s.Ai.Connections.Add(new AiConnection
        {
            Id = "a", Name = "Anthropic", Provider = AiProviderKind.Anthropic, Endpoint = "https://api.anthropic.com", Model = "claude-haiku-4-5",
            Models = { new AiModelInfo { Id = "claude-opus-5-5", Name = "Claude Opus 5.5", Created = Now.AddDays(-2), FirstSeen = Now } },
            ModelsCheckedAt = Now, Unsupported = { "claude-opus-5-5:temperature" }, Temperature = 0.75, DismissedNewModel = "claude-opus-5-5",
        });
        var json = JsonSerializer.Serialize(s, AppSettings.Json);
        var back = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.Json)!;
        SettingsStore.Normalise(back);
        var c = back.Ai.Connections.Single(x => x.Id == "a");
        Assert.Equal("claude-opus-5-5", c.Models.Single().Id);
        Assert.Equal(Now, c.Models.Single().FirstSeen);
        Assert.Equal(Now.AddDays(-2), c.Models.Single().Created);
        Assert.Equal(Now, c.ModelsCheckedAt);
        Assert.Equal(new[] { "claude-opus-5-5:temperature" }, c.Unsupported);
        Assert.Equal(0.75, c.Temperature);
        Assert.Equal("claude-opus-5-5", c.DismissedNewModel);

        var old = JsonSerializer.Deserialize<AppSettings>("{\"Ai\":{\"Enabled\":true,\"Connections\":[{\"Id\":\"main\",\"Provider\":\"OpenAI\",\"Endpoint\":\"https://api.openai.com/v1\",\"Model\":\"gpt-4.1-mini\",\"Models\":null,\"Unsupported\":null,\"Temperature\":7}]}}", AppSettings.Json)!;
        SettingsStore.Normalise(old);
        var m = old.Ai.Connections.Single();
        Assert.Empty(m.Models);
        Assert.Null(m.ModelsCheckedAt);
        Assert.Empty(m.Unsupported);
        Assert.Equal(1, m.Temperature);   // clamped to 0–1
        Assert.Equal("", m.DismissedNewModel);
        var clone = old.Ai.Clone();
        clone.Connections[0].Unsupported.Add("x:y");
        Assert.Empty(old.Ai.Connections[0].Unsupported);   // deep copy
    }
}
