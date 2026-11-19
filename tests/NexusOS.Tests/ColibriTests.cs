using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// The very large model (colibri): nothing downloads until the person turns it
// on, it downloads into their folder, Nexus answers with it once chosen, and
// until it can answer the standard model does.
public sealed class ColibriTests
{
    private static string FixtureFile(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", name));

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // Requests to the fake colibri server go out for real; the standard model (Ollama) stays a stub.
    private sealed class Split(int port, HttpMessageHandler rest) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _real = new(new SocketsHttpHandler());
        private readonly HttpMessageInvoker _rest = new(rest, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            (request.RequestUri!.Port == port ? _real : _rest).SendAsync(request, ct);
    }

    private static (string Key, string? Value)[] Settings(string dir, int port, string cpu = "1") =>
    [
        ("NEXUS_COLIBRI_BIN", FixtureFile("fake_coli.py")), ("NEXUS_COLIBRI_FETCH", FixtureFile("fake_colibri_fetch.py")),
        ("NEXUS_COLIBRI_DIR", dir), ("NEXUS_COLIBRI_PORT", port.ToString()), ("NEXUS_COLIBRI_CPU_OK", cpu),
        ("NEXUS_COLIBRI_START_TIMEOUT_SECONDS", "20")
    ];

    private static string TempDir() => Directory.CreateTempSubdirectory("nexus-colibri-").FullName;

    private static async Task<JsonElement> WaitDownloaded(HttpClient client)
    {
        for (var i = 0; i < 100; i++)
        {
            var view = await client.GetFromJsonAsync<JsonElement>("/model/colibri");
            if (view.GetProperty("downloaded").GetBoolean()) return view;
            await Task.Delay(100);
        }
        throw new TimeoutException("the fake download did not finish");
    }

    [Fact]
    public async Task Nothing_downloads_until_turned_on_then_Nexus_answers_with_it_and_the_standard_model_leaves_memory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var dir = TempDir();
        var port = FreePort();
        var ollama = StubHandler.Json(HttpStatusCode.OK, """{"response":"standard answer","message":{"content":"standard answer"}}""");
        using var factory = new CoreApiFactory(Settings(dir, port)) { AiHandler = new Split(port, ollama) };
        try
        {
            var client = factory.Authorized();
            var before = await client.GetFromJsonAsync<JsonElement>("/model/colibri");
            Assert.True(before.GetProperty("configured").GetBoolean());
            Assert.False(before.GetProperty("wanted").GetBoolean());
            Assert.False(before.GetProperty("downloaded").GetBoolean());
            Assert.Equal("qwen36-35b", before.GetProperty("models")[0].GetProperty("id").GetString());
            Assert.False(Directory.Exists(Path.Combine(dir, "qwen36-35b")));

            // Using it before it is downloaded is refused, and says why.
            using (var early = await client.PostAsJsonAsync("/model/colibri/use", new { on = true }))
            {
                Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
                Assert.Contains("not downloaded", await early.Content.ReadAsStringAsync());
            }

            using (var want = await client.PostAsJsonAsync("/model/colibri/want", new { on = true }))
                Assert.Equal(HttpStatusCode.OK, want.StatusCode);
            var downloaded = await WaitDownloaded(client);
            Assert.True(downloaded.GetProperty("wanted").GetBoolean());
            Assert.Equal("ready", downloaded.GetProperty("download").GetProperty("state").GetString());

            using (var use = await client.PostAsJsonAsync("/model/colibri/use", new { on = true }))
                Assert.Equal(HttpStatusCode.OK, use.StatusCode);
            Assert.Contains(ollama.Bodies, body => body.Contains("\"keep_alive\":0"));   // the standard model is unloaded
            using var answer = await client.PostAsJsonAsync("/chat", new { prompt = "hello" });
            var reply = await answer.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("colibri", reply.GetProperty("provider").GetString());
            Assert.Equal("colibri qwen36-35b as nexus-colibri", reply.GetProperty("answer").GetString());
            Assert.True((await client.GetFromJsonAsync<JsonElement>("/model/colibri")).GetProperty("running").GetBoolean());

            // Back to the standard model: the very large one leaves memory.
            using (var off = await client.PostAsJsonAsync("/model/colibri/use", new { on = false }))
                Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("running").GetBoolean());
            using var standard = await client.PostAsJsonAsync("/chat", new { prompt = "hello" });
            Assert.Equal("local", (await standard.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());

            // Removing it deletes the download.
            using (var remove = await client.DeleteAsync("/model/colibri"))
                Assert.False((await remove.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("downloaded").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(dir, "qwen36-35b")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Chosen_but_not_downloaded_the_standard_model_answers()
    {
        var dir = TempDir();
        using var factory = new CoreApiFactory(Settings(dir, FreePort()));
        try
        {
            var client = factory.Authorized();
            (await client.PostAsJsonAsync("/providers/select", new { name = "colibri" })).EnsureSuccessStatusCode();
            using var response = await client.PostAsJsonAsync("/chat", new { prompt = "hello" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("local", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("provider").GetString());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task A_processor_without_AVX2_is_told_so_and_nothing_downloads()
    {
        var dir = TempDir();
        using var factory = new CoreApiFactory(Settings(dir, FreePort(), cpu: "0"));
        try
        {
            var client = factory.Authorized();
            using var want = await client.PostAsJsonAsync("/model/colibri/want", new { on = true });
            Assert.Equal(HttpStatusCode.Conflict, want.StatusCode);
            Assert.Contains("AVX2", await want.Content.ReadAsStringAsync());
            Assert.False((await client.GetFromJsonAsync<JsonElement>("/model/colibri")).GetProperty("cpuSupported").GetBoolean());
            Assert.False(File.Exists(Path.Combine(dir, "choice.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("/tmp/x")]
    [InlineData("Qwen BIG")]
    public async Task A_model_name_never_becomes_a_path_outside_its_folder(string model)
    {
        var dir = TempDir();
        using var factory = new CoreApiFactory(Settings(dir, FreePort()));
        try
        {
            using var want = await factory.Authorized().PostAsJsonAsync("/model/colibri/want", new { on = true, model });
            Assert.Equal(HttpStatusCode.BadRequest, want.StatusCode);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_server_is_started_on_loopback_with_the_chosen_model_folder()
    {
        var dir = TempDir();
        try
        {
            var config = TestConfig.Of(Settings(dir, 18436));
            var colibri = new Colibri(config);
            using var server = new ColibriServer(colibri, new StubHttpFactory(StubHandler.Json(HttpStatusCode.OK, "{}")), config);
            Assert.Equal(["serve", "--model", Path.Combine(dir, "qwen36-35b"), "--host", "127.0.0.1", "--port", "18436", "--model-id", "nexus-colibri"],
                server.Arguments());
            Assert.Equal("http://127.0.0.1:18436", server.Endpoint);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Without_the_engine_it_is_not_offered()
    {
        var colibri = new Colibri(TestConfig.Of());
        Assert.False(colibri.Configured);
        Assert.Contains("no very large model", colibri.Blocked(false));
    }
}
