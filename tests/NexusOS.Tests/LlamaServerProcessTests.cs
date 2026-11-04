using System.Net.Sockets;
using Xunit;

public sealed class LlamaServerProcessTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "fake_llama_server.py"));

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool PortOpen(int port)
    {
        try { using var client = new TcpClient(); client.Connect("127.0.0.1", port); return true; }
        catch (SocketException) { return false; }
    }

    [Fact]
    public void UnconfiguredProcessIsInert()
    {
        var process = new LlamaServerProcess(new StubHttpFactory(StubHandler.Json(System.Net.HttpStatusCode.OK, "{}")),
            TestConfig.Of(), new InferenceTokenSettings(TestConfig.Of()));
        Assert.False(process.IsConfigured);
        Assert.False(process.IsRunning);
    }

    [Theory]
    [InlineData("relative/llama-server", "/tmp/model.gguf")]
    [InlineData("/nonexistent/llama-server", "/tmp/model.gguf")]
    public void RejectsInvalidExecutable(string executable, string model) =>
        Assert.Throws<InvalidOperationException>(() => new LlamaServerProcess(
            new StubHttpFactory(StubHandler.Json(System.Net.HttpStatusCode.OK, "{}")),
            TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", executable), ("NEXUS_LLAMA_MODEL_PATH", model)),
            new InferenceTokenSettings(TestConfig.Of())));

    [Theory]
    [InlineData("NEXUS_LLAMA_PORT", "80")]
    [InlineData("NEXUS_LLAMA_GPU_LAYERS", "-1")]
    [InlineData("NEXUS_LLAMA_GPU_LAYERS", "many")]
    public void RejectsInvalidNumericSettings(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => new LlamaServerProcess(
            new StubHttpFactory(StubHandler.Json(System.Net.HttpStatusCode.OK, "{}")),
            TestConfig.Of((key, value)), new InferenceTokenSettings(TestConfig.Of())));

    [Fact]
    public void ArgumentsBindLoopbackAndCarryBudget()
    {
        var model = Path.GetTempFileName();
        try
        {
            var process = new LlamaServerProcess(new StubHttpFactory(StubHandler.Json(System.Net.HttpStatusCode.OK, "{}")),
                TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", Fixture), ("NEXUS_LLAMA_MODEL_PATH", model),
                    ("NEXUS_LLAMA_PORT", "18080"), ("NEXUS_LLAMA_GPU_LAYERS", "12")),
                new InferenceTokenSettings(TestConfig.Of(("NEXUS_CONTEXT_TOKENS", "2048"))));
            // --jinja: llama-server takes tools (Nexus's actions) only with it.
            Assert.Equal(["--model", model, "--host", "127.0.0.1", "--port", "18080", "--ctx-size", "2048", "--n-gpu-layers", "12", "--jinja"],
                process.Arguments());
            Assert.True(process.ToolCalling);
            Assert.Equal("http://127.0.0.1:18080", process.Endpoint);
        }
        finally { File.Delete(model); }
    }

    [Fact]
    public async Task StartsOnDemandAndStopsOnUnload()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        var port = FreePort();
        var config = TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", Fixture), ("NEXUS_LLAMA_MODEL_PATH", model),
            ("NEXUS_LLAMA_PORT", port.ToString()), ("NEXUS_LLAMA_GPU_LAYERS", "3"));
        var factory = new RealHttpFactory();
        using var process = new LlamaServerProcess(factory, config, new InferenceTokenSettings(TestConfig.Of()));
        try
        {
            var provider = new LlamaCppProvider(factory, config, process);
            Assert.True(provider.SupportsUnload);
            Assert.False(process.IsRunning);
            var answer = await provider.AskAsync("hello", InferenceTokenBudgetPlanner.Calculate(4096, 64), CancellationToken.None);
            Assert.Equal("managed ctx=8192 ngl=3: hello", answer);   // the default budget
            Assert.True(process.IsRunning);
            Assert.True(PortOpen(port));

            await provider.UnloadAsync(CancellationToken.None);
            Assert.False(process.IsRunning);
            Assert.False(PortOpen(port));
        }
        finally { File.Delete(model); }
    }

    private static readonly ToolSpec[] OneTool = [NexusActions.ByName["get_system_state"].Tool];

    private static async Task<string> Turn(LlamaCppProvider provider)
    {
        var text = "";
        await foreach (var piece in provider.StreamTurnAsync([new TurnMessage("user", "hello")], OneTool,
                           InferenceTokenBudgetPlanner.Calculate(4096, 64), CancellationToken.None))
            text += piece.Text;
        return text;
    }

    [Fact]
    public async Task TheManagedServerGetsToolsWithJinja()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        var config = TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", Fixture), ("NEXUS_LLAMA_MODEL_PATH", model),
            ("NEXUS_LLAMA_PORT", FreePort().ToString()));
        var factory = new RealHttpFactory();
        using var process = new LlamaServerProcess(factory, config, new InferenceTokenSettings(TestConfig.Of()));
        try
        {
            Assert.Equal("jinja=true tools=1", await Turn(new LlamaCppProvider(factory, config, process)));
            Assert.True(process.ToolCalling);
        }
        finally { await process.StopAsync(CancellationToken.None); File.Delete(model); }
    }

    [Fact]
    public async Task AnOlderServerWithoutJinjaStartsAnywayAndGetsNoTools()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        var config = TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", Fixture), ("NEXUS_LLAMA_MODEL_PATH", model),
            ("NEXUS_LLAMA_PORT", FreePort().ToString()));
        var factory = new RealHttpFactory();
        // The fixture then does not know --jinja and exits on it, as an old llama-server does.
        Environment.SetEnvironmentVariable("FAKE_LLAMA_OLD", "1");
        using var process = new LlamaServerProcess(factory, config, new InferenceTokenSettings(TestConfig.Of()));
        try
        {
            var provider = new LlamaCppProvider(factory, config, process);
            Assert.Equal("jinja=false tools=0", await Turn(provider));
            Assert.False(process.ToolCalling);
            Assert.DoesNotContain("--jinja", process.Arguments());
            Assert.False(provider.ToolsLikely);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FAKE_LLAMA_OLD", null);
            await process.StopAsync(CancellationToken.None);
            File.Delete(model);
        }
    }

    [Fact]
    public void JinjaCanBeTurnedOff()
    {
        var model = Path.GetTempFileName();
        try
        {
            var process = new LlamaServerProcess(new StubHttpFactory(StubHandler.Json(System.Net.HttpStatusCode.OK, "{}")),
                TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", Fixture), ("NEXUS_LLAMA_MODEL_PATH", model), ("NEXUS_LLAMA_JINJA", "false")),
                new InferenceTokenSettings(TestConfig.Of()));
            Assert.DoesNotContain("--jinja", process.Arguments());
            Assert.False(process.ToolCalling);
        }
        finally { File.Delete(model); }
    }

    [Fact]
    public async Task ExitDuringStartupIsReported()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        try
        {
            // /bin/false exits immediately, standing in for a server that crashes on load.
            using var process = new LlamaServerProcess(new RealHttpFactory(),
                TestConfig.Of(("NEXUS_LLAMA_SERVER_BIN", "/bin/false"), ("NEXUS_LLAMA_MODEL_PATH", model),
                    ("NEXUS_LLAMA_PORT", FreePort().ToString())), new InferenceTokenSettings(TestConfig.Of()));
            var error = await Assert.ThrowsAsync<ProviderException>(() => process.EnsureRunningAsync(CancellationToken.None));
            Assert.Equal(503, error.StatusCode);
            Assert.False(process.IsRunning);
        }
        finally { File.Delete(model); }
    }

    private sealed class RealHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
    }
}
