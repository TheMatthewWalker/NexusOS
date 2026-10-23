using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string category) => new Logger(category, Lines);
    public void Dispose() { }
    public string All => string.Join("\n", Lines);

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            lines.Enqueue($"{category} scope: {state}");
            return null;
        }
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{level} {category}: {formatter(state, exception)} {exception}");
    }
}

// Runs the real Core pipeline (auth, rate limiting, concurrency, providers,
// HttpClient logging) in-process with upstream model servers replaced by stubs.
public sealed class CoreApiFactory : WebApplicationFactory<Program>
{
    private readonly TempState _state = new();
    private readonly Dictionary<string, string?> _settings;
    // IHttpClientFactory caches primary handlers, so tests swap the inner stub.
    private readonly SwitchHandler _ai = new(StubHandler.Json(HttpStatusCode.OK, """{"response":"stub answer"}"""));
    private readonly SwitchHandler _cloud = new(StubHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"cloud answer"}}]}"""));
    public HttpMessageHandler AiHandler { get => _ai.Inner; set => _ai.Inner = value; }
    public HttpMessageHandler CloudHandler { get => _cloud.Inner; set => _cloud.Inner = value; }
    public FakeCredentials Credentials { get; set; } = new("sk-nexus-test-credential-0123456789");
    public CapturedLogs Logs { get; } = new();
    public string StateRoot => _state.Root;

    public CoreApiFactory(params (string Key, string? Value)[] settings)
    {
        _settings = new() { ["NEXUS_STATE_DIR"] = _state.Root, ["NEXUS_API_MODE"] = "tcp" };
        foreach (var (key, value) in settings) _settings[key] = value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(Logs);
        });
        builder.ConfigureServices(services =>
        {
            services.AddHttpClient("ai").ConfigurePrimaryHttpMessageHandler(() => _ai);
            services.AddHttpClient("cloud").ConfigurePrimaryHttpMessageHandler(() => _cloud);
            services.AddSingleton<IProviderCredentialStore>(_ => Credentials);
        });
    }

    public string Token => File.ReadAllText(Path.Combine(_state.Root, "api-token")).Trim();

    public HttpClient Authorized()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _state.Dispose();
    }
}

public sealed class SwitchHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    public HttpMessageHandler Inner { get; set; } = inner;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        new HttpMessageInvoker(Inner, disposeHandler: false).SendAsync(request, ct);
    // The factory disposes primary handlers on rotation; tests own the stubs.
    protected override void Dispose(bool disposing) { }
}

public sealed class CoreApiTests
{
    private sealed class GatedHandler : HttpMessageHandler
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(true); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"response":"released"}""") };
        }
    }

    [Theory]
    [InlineData("GET", "/health")]
    [InlineData("GET", "/history")]
    [InlineData("DELETE", "/history")]
    [InlineData("POST", "/chat")]
    [InlineData("POST", "/chat/stream")]
    [InlineData("POST", "/suggest")]
    [InlineData("POST", "/suggest/model")]
    [InlineData("GET", "/apps")]
    [InlineData("POST", "/guardian/preview")]
    [InlineData("GET", "/inference/status")]
    [InlineData("POST", "/inference/unload")]
    public async Task EveryEndpointRequiresTheBearerToken(string method, string path)
    {
        using var factory = new CoreApiFactory();
        var client = factory.CreateClient();
        using var anonymous = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var wrong = new HttpRequestMessage(new HttpMethod(method), path);
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
        using var rejected = await client.SendAsync(wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
    }

    [Fact]
    public async Task SecondConcurrentChatIsRejectedAndSlotIsReleased()
    {
        var gate = new GatedHandler();
        using var factory = new CoreApiFactory { AiHandler = gate };
        var client = factory.Authorized();
        var first = client.PostAsJsonAsync("/chat", new { prompt = "first" });
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using (var second = await client.PostAsJsonAsync("/chat", new { prompt = "second" }))
            Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        gate.Release.SetResult();
        using (var done = await first) Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        factory.AiHandler = StubHandler.Json(HttpStatusCode.OK, """{"response":"after"}""");
        using var third = await client.PostAsJsonAsync("/chat", new { prompt = "third" });
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task ClientCancellationReachesProviderReleasesSlotAndSavesNothing()
    {
        var gate = new GatedHandler();
        using var factory = new CoreApiFactory { AiHandler = gate };
        var client = factory.Authorized();
        using var cancel = new CancellationTokenSource();
        var pending = client.PostAsJsonAsync("/chat", new { prompt = "abandoned" }, cancel.Token);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(await gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        factory.AiHandler = StubHandler.Json(HttpStatusCode.OK, """{"response":"next"}""");
        using var next = await client.PostAsJsonAsync("/chat", new { prompt = "next" });
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        var history = await client.GetFromJsonAsync<JsonElement>("/history");
        Assert.Equal(["next"], history.EnumerateArray().Select(x => x.GetProperty("prompt").GetString()));
    }

    [Fact]
    public async Task ChatIsRateLimitedAfterTwentyRequestsPerMinute()
    {
        using var factory = new CoreApiFactory(("NEXUS_HISTORY_LIMIT", "0"));
        var client = factory.Authorized();
        for (var i = 0; i < 20; i++)
        {
            using var ok = await client.PostAsJsonAsync("/chat", new { prompt = "p" + i });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("/chat", new { prompt = "over" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankPromptIsRejected(string prompt)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsJsonAsync("/chat", new { prompt });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OverlongPromptIsRejectedWithoutContactingProvider()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK, """{"response":"x"}""");
        using var factory = new CoreApiFactory { AiHandler = stub };
        using var response = await factory.Authorized().PostAsJsonAsync("/chat", new { prompt = new string('a', 32769) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task OversizedProviderResponseIsBadGatewayAndNotSaved()
    {
        var huge = "{\"response\":\"" + new string('x', (int)ModelResponseGuard.MaxResponseBytes) + "\"}";
        using var factory = new CoreApiFactory { AiHandler = StubHandler.Json(HttpStatusCode.OK, huge) };
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/chat", new { prompt = "big" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/history")).GetArrayLength());
    }

    [Fact]
    public async Task CloudWithoutConsentNeverReadsCredentialOrSends()
    {
        var cloud = StubHandler.Json(HttpStatusCode.OK, "{}");
        using var factory = new CoreApiFactory { CloudHandler = cloud };
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/providers/select", new { name = "openai-compatible" })).EnsureSuccessStatusCode();
        using var response = await client.PostAsJsonAsync("/chat", new { prompt = "do not send" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(cloud.Requests);
        Assert.Equal(0, factory.Credentials.Calls);
    }

    [Fact]
    public async Task SecretsPromptsAndAnswersNeverAppearInLogs()
    {
        var prompt = "confidential-prompt-" + Guid.NewGuid().ToString("N");
        var answer = "confidential-answer-" + Guid.NewGuid().ToString("N");
        var cloud = StubHandler.Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"" + answer + "\"}}]}");
        using var factory = new CoreApiFactory(("NEXUS_CLOUD_EGRESS_ALLOWED", "true")) { CloudHandler = cloud };
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/providers/select", new { name = "openai-compatible" })).EnsureSuccessStatusCode();
        using (var ok = await client.PostAsJsonAsync("/chat", new { prompt }))
            Assert.Equal(answer, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("answer").GetString());
        // Failure paths log too; exercise an upstream error that echoes the prompt.
        factory.CloudHandler = StubHandler.Json(HttpStatusCode.InternalServerError, "{\"error\":\"" + prompt + "\"}");
        using (var failed = await client.PostAsJsonAsync("/chat", new { prompt }))
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal($"Bearer {factory.Credentials.GetAsync("", default).Result}", cloud.Requests[0].Headers.Authorization!.ToString());

        var logs = factory.Logs.All;
        Assert.Contains("POST /chat", logs); // Logging was captured at Trace.
        Assert.DoesNotContain(factory.Token, logs);
        Assert.DoesNotContain("sk-nexus-test-credential", logs);
        Assert.DoesNotContain(prompt, logs);
        Assert.DoesNotContain(answer, logs);
    }

    [Fact]
    public async Task InvalidStartupConfigurationStopsCore()
    {
        using var factory = new CoreApiFactory(("NEXUS_MAX_OUTPUT_TOKENS", "999999"));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("token budget", error.ToString());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StatusReportsSelectedProvider()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var local = await client.GetFromJsonAsync<JsonElement>("/inference/status");
        Assert.Equal("local", local.GetProperty("backend").GetString());
        Assert.True(local.GetProperty("unloadSupported").GetBoolean());
        (await client.PostAsJsonAsync("/providers/select", new { name = "openai-compatible" })).EnsureSuccessStatusCode();
        var cloud = await client.GetFromJsonAsync<JsonElement>("/inference/status");
        Assert.Equal(JsonValueKind.Null, cloud.GetProperty("available").ValueKind);
        using var unload = await client.PostAsync("/inference/unload", null);
        Assert.Equal(HttpStatusCode.Conflict, unload.StatusCode);
    }
}
