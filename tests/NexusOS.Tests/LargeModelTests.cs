using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// The large (mixture-of-experts) model: how it is split between graphics
// memory and ordinary memory, and that Nexus always has a model to answer with.
public sealed class LargeModelTests
{
    private const long GiB = MoePlan.GiB;
    private const long Gpt20 = 11_600_000_000;   // gpt-oss-20b, 4-bit

    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "fake_llama_server.py"));

    private static int FreePort() => TestPorts.Next();

    // Requests to the fake llama-server's port go out for real; the standard model (Ollama) stays a stub.
    private sealed class Split(int port, HttpMessageHandler rest) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _real = new(new SocketsHttpHandler());
        private readonly HttpMessageInvoker _rest = new(rest, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            (request.RequestUri!.Port == port ? _real : _rest).SendAsync(request, ct);
    }

    private sealed class RealHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(5) };
    }

    private static StubHandler Standard() =>
        StubHandler.Json(HttpStatusCode.OK, """{"response":"standard answer","message":{"content":"standard answer"}}""");

    [Fact]
    public void No_graphics_card_keeps_the_whole_model_in_memory()
    {
        var split = MoePlan.For(0, Gpt20, 24);
        Assert.Equal((0, 0), (split.GpuLayers, split.CpuMoeLayers));
        Assert.Equal(Gpt20 + GiB, split.MemoryBytes);
        // A built-in GPU's small carve-out is not a graphics card for this.
        Assert.Equal(0, MoePlan.For(2 * GiB, Gpt20, 24).GpuLayers);
    }

    [Fact]
    public void An_8_GB_card_takes_the_shared_part_and_some_experts_and_fits_a_16_GB_computer()
    {
        var split = MoePlan.For(8 * GiB, Gpt20, 24);
        Assert.Equal(MoePlan.AllLayers, split.GpuLayers);
        Assert.InRange(split.CpuMoeLayers, 8, 16);
        // What stays in memory leaves most of 16 GB for the desktop and apps.
        Assert.InRange(split.MemoryBytes, 3 * GiB, 8 * GiB);
        // What goes on the card fits it with the reserve.
        var onCard = Gpt20 - (split.MemoryBytes - GiB);
        Assert.True(onCard <= 8 * GiB - MoePlan.GpuReserve, $"{onCard} on an 8 GiB card");
    }

    [Theory]
    [InlineData(4, 24)]     // a 4 GB card: every expert stays in memory
    [InlineData(6, 16)]
    [InlineData(12, 4)]
    public void More_graphics_memory_means_fewer_experts_in_memory(int vramGiB, int atMost)
    {
        var split = MoePlan.For(vramGiB * GiB, Gpt20, 24);
        Assert.Equal(MoePlan.AllLayers, split.GpuLayers);
        Assert.InRange(split.CpuMoeLayers, 0, atMost);
        if (vramGiB > 4) Assert.True(split.CpuMoeLayers < MoePlan.For((vramGiB - 2) * GiB, Gpt20, 24).CpuMoeLayers);
    }

    [Fact]
    public void A_card_that_holds_it_all_takes_it_all()
    {
        var split = MoePlan.For(16 * GiB, Gpt20, 24);
        Assert.Equal((MoePlan.AllLayers, 0), (split.GpuLayers, split.CpuMoeLayers));
        Assert.Equal(0, MoePlan.WholeLayers(0, Gpt20, 24));
        Assert.InRange(MoePlan.WholeLayers(8 * GiB, Gpt20, 24), 8, 16);
    }

    // Later settings replace earlier ones of the same name.
    private static (string Key, string? Value)[] Settings(string model, int port, params (string Key, string? Value)[] more)
    {
        var all = new Dictionary<string, string?>
        {
            ["NEXUS_LLAMA_SERVER_BIN"] = Fixture, ["NEXUS_LLAMA_MODEL_PATH"] = model, ["NEXUS_LLAMA_PORT"] = port.ToString(),
            ["NEXUS_LARGE_MODEL_NAME"] = "gpt-oss-20b", ["NEXUS_LARGE_MODEL_BYTES"] = Gpt20.ToString(), ["NEXUS_LARGE_MODEL_LAYERS"] = "24",
            ["NEXUS_LARGE_MODEL_VRAM_MIB"] = "8192", ["NEXUS_LARGE_MODEL_AVAILABLE_MIB"] = "12000"
        };
        foreach (var (key, value) in more) all[key] = value;
        return all.Select(pair => (pair.Key, pair.Value)).ToArray();
    }

    [Fact]
    public void The_server_is_started_with_the_split_and_a_missing_file_is_not_an_error_at_startup()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nexus-large-" + Guid.NewGuid().ToString("N") + ".gguf");
        var config = TestConfig.Of(Settings(missing, 18081));
        var large = new LargeModel(config);
        Assert.True(large.Configured);
        Assert.False(large.Downloaded);
        Assert.Contains("not downloaded", large.Blocked(false));
        // Core starts without the file (it is downloaded after installation).
        using var process = new LlamaServerProcess(new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, "{}")), config,
            new InferenceTokenSettings(TestConfig.Of()), large);
        var args = process.Arguments();
        var moe = args[args.ToList().IndexOf("--n-cpu-moe") + 1];
        Assert.Equal(large.Split().CpuMoeLayers.ToString(), moe);
        Assert.Equal("999", args[args.ToList().IndexOf("--n-gpu-layers") + 1]);
    }

    [Fact]
    public void Too_little_free_memory_is_said_with_the_numbers()
    {
        var model = Path.GetTempFileName();
        try
        {
            var large = new LargeModel(TestConfig.Of(Settings(model, 18082, ("NEXUS_LARGE_MODEL_VRAM_MIB", "0"), ("NEXUS_LARGE_MODEL_AVAILABLE_MIB", "6000"))));
            var why = large.Blocked(false);
            Assert.Contains("free memory", why);
            Assert.Contains("Close some apps", why);
            Assert.Null(large.Blocked(true));                      // already running: nothing to check
            Assert.Null(large.Blocked(false, checkMemory: false)); // the router asks before the standard model is unloaded
        }
        finally { File.Delete(model); }
    }

    [Fact]
    public async Task The_large_model_answers_once_chosen_and_the_standard_model_leaves_memory_first()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        var ollama = Standard();
        var port = FreePort();
        using var factory = new CoreApiFactory([.. Settings(model, port, ("NEXUS_LLAMA_REASONING_EFFORT", "low"))]) { AiHandler = new Split(port, ollama) };
        try
        {
            var client = factory.Authorized();
            var before = await client.GetFromJsonAsync<JsonElement>("/model/large");
            Assert.True(before.GetProperty("downloaded").GetBoolean());
            Assert.False(before.GetProperty("selected").GetBoolean());
            using (var standard = await client.PostAsJsonAsync("/chat", new { prompt = "hello" }))
                Assert.Equal("local", (await standard.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());

            using (var use = await client.PostAsJsonAsync("/model/large/use", new { on = true }))
                Assert.Equal(HttpStatusCode.OK, use.StatusCode);
            Assert.Contains(ollama.Bodies, body => body.Contains("\"keep_alive\":0"));   // the standard model is unloaded
            using var large = await client.PostAsJsonAsync("/chat", new { prompt = "hello" });
            var answer = await large.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("llama-cpp", answer.GetProperty("provider").GetString());
            Assert.Contains("moe=", answer.GetProperty("answer").GetString());
            Assert.Contains("effort=low", answer.GetProperty("answer").GetString());
            Assert.True((await client.GetFromJsonAsync<JsonElement>("/model/large")).GetProperty("running").GetBoolean());

            // Back to the standard model: the large one leaves memory.
            using (var off = await client.PostAsJsonAsync("/model/large/use", new { on = false }))
                Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("running").GetBoolean());
        }
        finally { File.Delete(model); }
    }

    [Fact]
    public async Task With_the_large_model_not_downloaded_the_standard_model_answers()
    {
        var missing = Path.Combine(Path.GetTempPath(), "nexus-large-" + Guid.NewGuid().ToString("N") + ".gguf");
        // Preferred on this computer, nothing chosen by the person: large when it can, standard until then.
        using var factory = new CoreApiFactory([.. Settings(missing, FreePort(), ("NEXUS_LARGE_MODEL_PREFERRED", "true"))]);
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/chat", new { prompt = "hello" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("local", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());
        using var use = await client.PostAsJsonAsync("/model/large/use", new { on = true });
        Assert.Equal(HttpStatusCode.Conflict, use.StatusCode);
    }

    [Fact]
    public async Task Preferred_and_downloaded_it_is_used_without_being_chosen_and_too_little_memory_falls_back()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        try
        {
            var port = FreePort();
            using (var factory = new CoreApiFactory([.. Settings(model, port, ("NEXUS_LARGE_MODEL_PREFERRED", "true"))]) { AiHandler = new Split(port, Standard()) })
            {
                using var response = await factory.Authorized().PostAsJsonAsync("/chat", new { prompt = "hello" });
                Assert.Equal("llama-cpp", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());
            }
            using var tight = new CoreApiFactory([.. Settings(model, port, ("NEXUS_LARGE_MODEL_PREFERRED", "true"),
                ("NEXUS_LARGE_MODEL_AVAILABLE_MIB", "2000"))]) { AiHandler = new Split(port, Standard()) };
            var client = tight.Authorized();
            // The first question says why; from then on the standard model answers.
            using (var first = await client.PostAsJsonAsync("/chat", new { prompt = "hello" }))
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
                Assert.Contains("free memory", await first.Content.ReadAsStringAsync());
            }
            using var second = await client.PostAsJsonAsync("/chat", new { prompt = "hello" });
            Assert.Equal("local", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());
            var view = await client.GetFromJsonAsync<JsonElement>("/model/large");
            Assert.Contains("free memory", view.GetProperty("blocked").GetString());
        }
        finally { File.Delete(model); }
    }

    [Fact]
    public async Task A_server_from_before_the_expert_split_gets_whole_layers_instead()
    {
        if (!OperatingSystem.IsLinux()) return;
        var model = Path.GetTempFileName();
        Environment.SetEnvironmentVariable("FAKE_LLAMA_NO_MOE", "1");
        try
        {
            var config = TestConfig.Of(Settings(model, FreePort()));
            var large = new LargeModel(config);
            var http = new RealHttpFactory();
            using var process = new LlamaServerProcess(http, config, new InferenceTokenSettings(TestConfig.Of()), large);
            var provider = new LlamaCppProvider(http, config, process);
            var answer = await provider.AskAsync("hello", InferenceTokenBudgetPlanner.Calculate(4096, 64), CancellationToken.None);
            Assert.DoesNotContain("moe=", answer);
            Assert.Contains($"ngl={MoePlan.WholeLayers(8192L * 1024 * 1024, Gpt20, 24)}", answer);
        }
        finally { Environment.SetEnvironmentVariable("FAKE_LLAMA_NO_MOE", null); File.Delete(model); }
    }
}
