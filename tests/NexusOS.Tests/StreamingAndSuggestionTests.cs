using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class StreamingProviderTests
{
    private static readonly InferenceTokenBudget Budget = new(4096, 512, 3584);

    private static StubHandler Stream(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) }));

    private static IConfiguration Config(params (string, string?)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Item1, v.Item2))).Build();

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> pieces)
    {
        var all = new List<string>();
        await foreach (var piece in pieces) all.Add(piece);
        return all;
    }

    [Fact]
    public async Task Ollama_streams_ndjson_pieces_and_asks_for_a_stream()
    {
        var stub = Stream("{\"response\":\"Hel\",\"done\":false}\n{\"response\":\"lo\",\"done\":false}\n{\"response\":\"\",\"done\":true}\n");
        var provider = new OllamaProvider(new StubHttpFactory(stub), Config());
        Assert.Equal(["Hel", "lo"], await Collect(provider.StreamAsync("hi", Budget, CancellationToken.None)));
        using var sent = JsonDocument.Parse(stub.Bodies[0]);
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
        Assert.EndsWith("/api/generate", stub.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Ollama_stops_at_done_and_ignores_anything_after_it()
    {
        var stub = Stream("{\"response\":\"one\",\"done\":true}\nnot json at all\n");
        var provider = new OllamaProvider(new StubHttpFactory(stub), Config());
        Assert.Equal(["one"], await Collect(provider.StreamAsync("hi", Budget, CancellationToken.None)));
    }

    [Fact]
    public async Task LlamaCpp_streams_server_sent_events()
    {
        var stub = Stream(": keep-alive\n\ndata: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}\n\n" +
                          "data: {\"choices\":[{\"delta\":{\"content\":\"Hi \"}}]}\n\n" +
                          "data: {\"choices\":[{\"delta\":{\"content\":\"there\"}}]}\n\ndata: [DONE]\n\n");
        var provider = new LlamaCppProvider(new StubHttpFactory(stub), Config());
        Assert.Equal(["Hi ", "there"], await Collect(provider.StreamAsync("hi", Budget, CancellationToken.None)));
        using var sent = JsonDocument.Parse(stub.Bodies[0]);
        Assert.True(sent.RootElement.GetProperty("stream").GetBoolean());
    }

    [Theory]
    [InlineData("", "empty answer")]
    [InlineData("{\"response\":\"\",\"done\":true}\n", "empty answer")]
    [InlineData("{\"response\": oops}\n", "malformed JSON")]
    [InlineData("[1,2,3]\n", "unexpected response format")]
    public async Task Bad_streams_fail_with_safe_messages(string body, string expected)
    {
        var provider = new OllamaProvider(new StubHttpFactory(Stream(body)), Config());
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(provider.StreamAsync("hi", Budget, CancellationToken.None)));
        Assert.Contains(expected, ex.Message);
        Assert.Equal(502, ex.StatusCode);
    }

    [Fact]
    public async Task An_oversized_stream_is_cut_off()
    {
        var line = "{\"response\":\"" + new string('x', 200_000) + "\",\"done\":false}\n";
        var body = string.Concat(Enumerable.Repeat(line, 25));       // ~5 MB, over the 4 MB response cap
        var provider = new OllamaProvider(new StubHttpFactory(Stream(body)), Config());
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(provider.StreamAsync("hi", Budget, CancellationToken.None)));
        Assert.Contains("exceeded the allowed size", ex.Message);
    }

    [Fact]
    public async Task Upstream_errors_keep_their_actionable_hints()
    {
        var provider = new OllamaProvider(new StubHttpFactory(Stream("nope", HttpStatusCode.NotFound)), Config());
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(provider.StreamAsync("hi", Budget, CancellationToken.None)));
        Assert.Contains("ollama pull", ex.Message);
    }

    [Fact]
    public async Task Cloud_stream_checks_consent_before_reading_the_credential()
    {
        var credentials = new FakeCredentials("sk-test");
        var stub = Stream("data: {\"choices\":[{\"delta\":{\"content\":\"x\"}}]}\n");
        var provider = new OpenAiCompatibleProvider(new StubHttpFactory(stub), Config(), credentials);
        var ex = await Assert.ThrowsAsync<ProviderException>(() => Collect(provider.StreamAsync("secret", Budget, CancellationToken.None)));
        Assert.Contains("disabled", ex.Message);
        Assert.Empty(stub.Requests);
        Assert.Equal(0, credentials.Calls);
    }
}

public sealed class StreamingApiTests
{
    private static async Task<List<JsonElement>> Events(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task Chat_stream_sends_pieces_then_done_and_records_the_full_answer()
    {
        using var factory = new CoreApiFactory
        {
            AiHandler = StubHandler.Json(HttpStatusCode.OK, "{\"message\":{\"content\":\"Good \"},\"done\":false}\n{\"message\":{\"content\":\"evening\"},\"done\":true}\n")
        };
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/chat/stream", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        var events = await Events(response);
        Assert.Equal(["delta", "delta", "done"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("Good evening", string.Concat(events.Where(e => e.GetProperty("type").GetString() == "delta")
            .Select(e => e.GetProperty("text").GetString())));
        Assert.Equal("local", events[^1].GetProperty("provider").GetString());
        var history = await client.GetFromJsonAsync<JsonElement>("/history");
        Assert.Equal("Good evening", history[0].GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Chat_stream_failure_before_any_text_is_a_normal_http_error()
    {
        using var factory = new CoreApiFactory { AiHandler = StubHandler.Json(HttpStatusCode.InternalServerError, "boom secret") };
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/chat/stream", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("boom secret", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/history")).GetArrayLength());
    }

    [Fact]
    public async Task Chat_stream_failure_mid_answer_is_an_error_event_and_is_not_recorded()
    {
        using var factory = new CoreApiFactory
        {
            AiHandler = StubHandler.Json(HttpStatusCode.OK, "{\"message\":{\"content\":\"partial\"},\"done\":false}\n{broken\n")
        };
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/chat/stream", new { prompt = "hello" });
        var events = await Events(response);
        Assert.Equal(["delta", "error"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Contains("malformed JSON", events[1].GetProperty("message").GetString());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/history")).GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Chat_stream_rejects_empty_prompts(string prompt)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsJsonAsync("/chat/stream", new { prompt });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

public sealed class AppSuggestionTests
{
    private static string[] Ids(IEnumerable<AppSuggestion> apps) => apps.Select(a => a.Id).ToArray();

    [Theory]
    [InlineData("help me research sources", new[] { "browser", "files" })]
    [InlineData("debug my python script", new[] { "terminal", "files" })]
    [InlineData("turn the volume down", new[] { "settings" })]
    [InlineData("hello there", new string[0])]
    public void Rules_match_the_spaceui_prototype(string text, string[] expected) =>
        Assert.Equal(expected, Ids(AppCatalogue.FromRules(text)));

    [Fact]
    public void The_catalogue_matches_the_shell_app_ids()
    {
        // The same six roles the shell's orbit slots use (spike/spaceui/shared/suggest.mjs).
        Assert.Equal(["files", "browser", "terminal", "studio", "models", "settings"], AppCatalogue.Roles.Select(r => r.Id));
    }

    [Theory]
    [InlineData("{\"apps\":[\"files\",\"browser\"]}", new[] { "files", "browser" })]
    [InlineData("```json\n{\"apps\":[\"terminal\"]}\n```", new[] { "terminal" })]
    [InlineData("{\"apps\":[]}", new string[0])]
    [InlineData("{\"apps\":[\"files\",\"files\"]}", new[] { "files" })]
    public void Valid_model_output_is_accepted(string output, string[] expected) =>
        Assert.Equal(expected, Ids(AppSuggester.Parse(output)));

    [Theory]
    [InlineData("Sure! Files would help.")]
    [InlineData("{\"apps\":[\"files\"],\"launch\":true}")]
    [InlineData("{\"apps\":[\"rm -rf /\"]}")]
    [InlineData("{\"apps\":[\"Files\"]}")]
    [InlineData("{\"apps\":[\"files\",\"browser\",\"terminal\",\"studio\"]}")]
    [InlineData("{\"apps\":\"files\"}")]
    [InlineData("{\"apps\":[{\"id\":\"files\"}]}")]
    [InlineData("{\"apps\":[\"files\"]}{\"apps\":[\"browser\"]}")]
    public void Hostile_or_malformed_model_output_is_rejected(string output) =>
        Assert.Throws<SuggestionRejectedException>(() => AppSuggester.Parse(output));

    [Fact]
    public async Task Rules_endpoint_answers_without_the_model()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK, "{\"response\":\"should not be called\"}");
        using var factory = new CoreApiFactory { AiHandler = stub };
        var body = await (await factory.Authorized().PostAsJsonAsync("/suggest", new { prompt = "research this" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rules", body.GetProperty("source").GetString());
        Assert.Equal(["browser", "files"], body.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Model_endpoint_uses_valid_model_choices()
    {
        using var factory = new CoreApiFactory { AiHandler = StubHandler.Json(HttpStatusCode.OK, "{\"response\":\"{\\\"apps\\\":[\\\"studio\\\"]}\"}") };
        var body = await (await factory.Authorized().PostAsJsonAsync("/suggest/model", new { prompt = "make a poster" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("model", body.GetProperty("source").GetString());
        Assert.Equal("local", body.GetProperty("provider").GetString());
        Assert.Equal("studio", body.GetProperty("apps")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Model_endpoint_falls_back_to_rules_and_says_so()
    {
        using var factory = new CoreApiFactory { AiHandler = StubHandler.Json(HttpStatusCode.OK, "{\"response\":\"open a terminal and run rm\"}") };
        var body = await (await factory.Authorized().PostAsJsonAsync("/suggest/model", new { prompt = "debug my python" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rules-fallback", body.GetProperty("source").GetString());
        Assert.Equal(["terminal", "files"], body.GetProperty("apps").EnumerateArray().Select(a => a.GetProperty("id").GetString()));
    }

    [Fact]
    public void Request_markers_cannot_be_closed_by_the_user()
    {
        var prompt = AppSuggester.BuildPrompt("x");
        Assert.Contains("<<<REQUEST", prompt);
        Assert.Contains("REQUEST>>>", prompt);
    }
}
