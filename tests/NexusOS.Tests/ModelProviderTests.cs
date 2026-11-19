using System.Net;
using System.Text.Json;
using Xunit;

public sealed class ModelProviderTests
{
    private static readonly InferenceTokenBudget Budget = InferenceTokenBudgetPlanner.Calculate(4096, 128);

    [Fact]
    public async Task OllamaSendsBudgetAndReturnsAnswer()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, """{"response":"hi there"}""");
        var provider = new OllamaProvider(new StubHttpFactory(handler), TestConfig.Of(("NEXUS_OLLAMA_MODEL", "m1")));
        Assert.Equal("hi there", await provider.AskAsync("hello", Budget, CancellationToken.None));
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("m1", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(4096, sent.RootElement.GetProperty("options").GetProperty("num_ctx").GetInt32());
        Assert.Equal(128, sent.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal("http://127.0.0.1:11434/api/generate", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task OllamaMissingModelGivesPullHint()
    {
        var provider = new OllamaProvider(new StubHttpFactory(StubHandler.Json(HttpStatusCode.NotFound, "{}")),
            TestConfig.Of(("NEXUS_OLLAMA_MODEL", "qwen2.5:0.5b")));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.Contains("ollama pull qwen2.5:0.5b", error.Message);
    }

    [Theory]
    [InlineData("""{"unexpected":true}""", "unexpected response format")]
    [InlineData("""{"response":42}""", "unexpected response format")]
    [InlineData("""[1,2]""", "unexpected response format")]
    [InlineData("""{"response":""}""", "empty answer")]
    [InlineData("""not json""", "malformed JSON")]
    public async Task MalformedOllamaResponsesAreProviderErrors(string body, string expected)
    {
        var provider = new OllamaProvider(new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, body)), TestConfig.Of());
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    public async Task MalformedLlamaResponsesAreProviderErrors(string body)
    {
        var provider = new LlamaCppProvider(new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, body)), TestConfig.Of());
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task UnreachableProviderIsReportedWithoutInternals()
    {
        var handler = new StubHandler((_, _) => throw new HttpRequestException("connection refused 127.0.0.1:11434 secret"));
        var provider = new OllamaProvider(new StubHttpFactory(handler), TestConfig.Of());
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(502, error.StatusCode);
        Assert.DoesNotContain("secret", error.Message);
        Assert.Contains("unreachable", error.Message);
    }

    [Fact]
    public async Task ProviderTimeoutIsGatewayTimeout()
    {
        var handler = new StubHandler((_, _) => throw new TaskCanceledException("timeout"));
        var provider = new OllamaProvider(new StubHttpFactory(handler), TestConfig.Of());
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(504, error.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 502, "rejected the credential")]
    [InlineData(HttpStatusCode.TooManyRequests, 503, "rate limiting")]
    [InlineData(HttpStatusCode.InternalServerError, 502, "HTTP 500")]
    public async Task UpstreamStatusCodesMapToSafeMessages(HttpStatusCode status, int expected, string text)
    {
        var handler = StubHandler.Json(status, """{"error":"echo of the user prompt"}""");
        var provider = new OpenAiCompatibleProvider(new StubHttpFactory(handler),
            TestConfig.Of(("NEXUS_CLOUD_EGRESS_ALLOWED", "true")), new FakeCredentials("sk-test"));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(expected, error.StatusCode);
        Assert.Contains(text, error.Message);
        Assert.DoesNotContain("echo of the user prompt", error.Message);
    }

    [Fact]
    public async Task CloudRequiresConsentBeforeReadingCredential()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, "{}");
        var credentials = new FakeCredentials("sk-test");
        var provider = new OpenAiCompatibleProvider(new StubHttpFactory(handler), TestConfig.Of(), credentials);
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal(503, error.StatusCode);
        Assert.Equal(0, credentials.Calls);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CloudMissingCredentialFailsWithoutNetwork()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, "{}");
        var provider = new OpenAiCompatibleProvider(new StubHttpFactory(handler),
            TestConfig.Of(("NEXUS_CLOUD_EGRESS_ALLOWED", "true")), new FakeCredentials(null));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Contains("keyring", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CloudSendsBearerCredentialAndBudget()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"cloud answer"}}]}""");
        var provider = new OpenAiCompatibleProvider(new StubHttpFactory(handler),
            TestConfig.Of(("NEXUS_CLOUD_EGRESS_ALLOWED", "true")), new FakeCredentials("sk-test"));
        Assert.Equal("cloud answer", await provider.AskAsync("x", Budget, CancellationToken.None));
        Assert.Equal("Bearer sk-test", handler.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.Requests[0].RequestUri!.ToString());
        using var sent = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(128, sent.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("NEXUS_OLLAMA_URL", "http://10.0.0.5:11434")]
    [InlineData("NEXUS_LLAMA_ENDPOINT", "https://example.org")]
    [InlineData("NEXUS_OPENAI_ENDPOINT", "https://evil.example/v1")]
    public void InvalidEndpointsFailAtConstruction(string key, string value)
    {
        var factory = new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, "{}"));
        var config = TestConfig.Of((key, value));
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new OllamaProvider(factory, config);
            _ = new LlamaCppProvider(factory, config);
            _ = new OpenAiCompatibleProvider(factory, config, new FakeCredentials(null));
        });
    }

    [Fact]
    public async Task StatusProbesUseProviderSpecificEndpoints()
    {
        var handler = StubHandler.Json(HttpStatusCode.OK, "{}");
        var factory = new StubHttpFactory(handler);
        Assert.True(await new OllamaProvider(factory, TestConfig.Of()).IsAvailableAsync(CancellationToken.None));
        Assert.True(await new LlamaCppProvider(factory, TestConfig.Of()).IsAvailableAsync(CancellationToken.None));
        Assert.Null(await new OpenAiCompatibleProvider(factory, TestConfig.Of(), new FakeCredentials(null)).IsAvailableAsync(CancellationToken.None));
        Assert.Equal(["/api/tags", "/health"], handler.Requests.Select(x => x.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task UnmanagedLlamaAndCloudRefuseUnload()
    {
        var factory = new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Equal(409, (await Assert.ThrowsAsync<ProviderException>(() =>
            new LlamaCppProvider(factory, TestConfig.Of()).UnloadAsync(CancellationToken.None))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ProviderException>(() =>
            new OpenAiCompatibleProvider(factory, TestConfig.Of(), new FakeCredentials(null)).UnloadAsync(CancellationToken.None))).StatusCode);
    }

    [Fact]
    public async Task RouterUsesSelectedProvider()
    {
        using var state = new TempState();
        var factory = new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"from llama"}}]}"""));
        var store = new ProviderStore(state.Paths());
        var config = TestConfig.Of();
        using var colibri = new ColibriServer(new Colibri(config), factory, config);
        var router = new ModelRouter([new OllamaProvider(factory, config), new LlamaCppProvider(factory, config),
            new OpenAiCompatibleProvider(factory, config, new FakeCredentials(null)), LlamaCppProvider.Colibri(factory, colibri, null)],
            store, new InferenceTokenSettings(config));
        store.Select("llama-cpp");
        Assert.Equal(("from llama", "llama-cpp"), await router.AskAsync("hi", CancellationToken.None));
    }

    [Fact]
    public void RouterRejectsIncompleteRegistry()
    {
        using var state = new TempState();
        var factory = new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, "{}"));
        Assert.Throws<InvalidOperationException>(() => new ModelRouter([new OllamaProvider(factory, TestConfig.Of())],
            new ProviderStore(state.Paths()), new InferenceTokenSettings(TestConfig.Of())));
    }
}
