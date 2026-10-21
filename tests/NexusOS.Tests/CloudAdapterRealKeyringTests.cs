using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Collection("IsolatedRealSecretService")]
public sealed class CloudAdapterRealKeyringTests
{
    private static bool Enabled => OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("NEXUS_REAL_SECRET_SERVICE_TEST") == "1";

    [Fact]
    public async Task UnlockedRealKeyringCredentialIsUsedInCloudAuthorizationHeader()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("NEXUS_EXPECT_LOCKED_KEYRING") == "1") return;
        var secret = "nexus-live-adapter-" + Guid.NewGuid().ToString("N");
        await RunSecretToolAsync(new[] { "store", "--label=NexusOS adapter test", "app", "nexusos", "provider", "openai-compatible" }, secret + "\n");
        try
        {
            var handler = new RecordingHandler();
            var router = CreateRouter(handler);
            var (answer, provider) = await router.AskAsync("synthetic prompt", CancellationToken.None);
            Assert.Equal("synthetic answer", answer);
            Assert.Equal("openai-compatible", provider);
            Assert.Equal(1, handler.Calls);
            Assert.Equal("Bearer", handler.Authorization?.Scheme);
            Assert.Equal(secret, handler.Authorization?.Parameter);
        }
        finally { await RunSecretToolAsync(new[] { "clear", "app", "nexusos", "provider", "openai-compatible" }); }
    }

    [Fact]
    public async Task MissingRealKeyringSecretDoesNotSendEvenWithPlaintextEnvironmentKey()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("NEXUS_EXPECT_LOCKED_KEYRING") == "1") return;
        // An isolated keyring begins without a provider secret; do not require
        // secret-tool clear to succeed when no matching item exists.
        var previous = Environment.GetEnvironmentVariable("NEXUS_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", "forbidden-environment-fallback");
            var handler = new RecordingHandler();
            var router = CreateRouter(handler);
            await Assert.ThrowsAsync<InvalidOperationException>(() => router.AskAsync("synthetic prompt", CancellationToken.None));
            Assert.Equal(0, handler.Calls);
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", previous); }
    }

    [Fact]
    public async Task LockedRealKeyringDoesNotSendEvenWithPlaintextEnvironmentKey()
    {
        if (!Enabled || Environment.GetEnvironmentVariable("NEXUS_EXPECT_LOCKED_KEYRING") != "1") return;
        var previous = Environment.GetEnvironmentVariable("NEXUS_OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", "forbidden-environment-fallback");
            var handler = new RecordingHandler();
            var router = CreateRouter(handler);
            await Assert.ThrowsAnyAsync<Exception>(() => router.AskAsync("synthetic prompt", CancellationToken.None));
            Assert.Equal(0, handler.Calls);
        }
        finally { Environment.SetEnvironmentVariable("NEXUS_OPENAI_API_KEY", previous); }
    }

    private static ModelRouter CreateRouter(RecordingHandler handler)
    {
        var state = Path.Combine(Path.GetTempPath(), "nexus-adapter-keyring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NEXUS_STATE_DIR"] = state,
            ["NEXUS_CLOUD_EGRESS_ALLOWED"] = "true",
            ["NEXUS_OPENAI_ENDPOINT"] = "https://api.openai.com/v1"
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(new NexusStatePaths(config));
        services.AddSingleton<SqliteStateDatabase>();
        services.AddSingleton<ProviderStore>(sp => new ProviderStore(sp.GetRequiredService<SqliteStateDatabase>()));
        services.AddSingleton<InferenceTokenSettings>();
        services.AddSingleton<IProviderCredentialStore, LinuxSecretToolStore>();
        services.AddSingleton<IHttpClientFactory>(new FixtureHttpClientFactory(handler));
        var provider = services.BuildServiceProvider();
        var selection = provider.GetRequiredService<ProviderStore>();
        Assert.True(selection.Select("openai-compatible"));
        return new ModelRouter(provider.GetRequiredService<IHttpClientFactory>(), selection, config,
            provider.GetRequiredService<IProviderCredentialStore>(), provider.GetRequiredService<InferenceTokenSettings>());
    }

    private sealed class FixtureHttpClientFactory(RecordingHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new HttpClient(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Authorization = request.Headers.Authorization;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"synthetic answer\"}}]}", Encoding.UTF8, "application/json")
            });
        }
    }

    private static async Task RunSecretToolAsync(string[] args, string? input = null)
    {
        var info = new System.Diagnostics.ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info) ?? throw new InvalidOperationException("Cannot launch secret-tool");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (input != null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(output, errors);
        Assert.True(process.ExitCode == 0, "Isolated keyring operation failed; diagnostic output withheld");
    }
}
