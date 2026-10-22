using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

// POST /try/keep turns a trialled app into a normal Guardian install proposal.
public sealed class TryKeepTests
{
    // Records every framed request and answers like a broker that accepted a submit.
    private sealed class RecordingBroker : IDisposable
    {
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly CancellationTokenSource _stop = new();
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "nexus-try-" + Guid.NewGuid().ToString("N")[..12]);
        public string Socket => Path.Combine(Directory, "broker.sock");
        public List<JsonElement> Requests { get; } = [];

        public RecordingBroker()
        {
            System.IO.Directory.CreateDirectory(Directory);
            _listener.Bind(new UnixDomainSocketEndPoint(Socket));
            _listener.Listen(4);
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptAsync(_stop.Token);
                var header = new byte[4];
                await ReadExact(client, header);
                var body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
                await ReadExact(client, body);
                lock (Requests) Requests.Add(JsonDocument.Parse(body).RootElement.Clone());
                var reply = Encoding.UTF8.GetBytes("""{"ok":true,"candidate":{"candidateId":"aaaaaaaaaaaaaaaaaaaaaaaa"},"approval":"required","executed":false}""");
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)reply.Length);
                await client.SendAsync(header);
                await client.SendAsync(reply);
            }
        }

        private static async Task ReadExact(Socket socket, byte[] buffer)
        {
            for (var read = 0; read < buffer.Length;)
            {
                var count = await socket.ReceiveAsync(buffer.AsMemory(read));
                if (count == 0) throw new EndOfStreamException();
                read += count;
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task KeepRequiresTheBearerToken(string? token)
    {
        using var broker = new RecordingBroker();
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", broker.Socket));
        var client = factory.CreateClient();
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 64));
        using var response = await client.PostAsJsonAsync("/try/keep", new { attr = "hello" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task KeepSubmitsOneInstallProposalAndNothingElse()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var broker = new RecordingBroker();
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", broker.Socket));
        using var response = await factory.Authorized().PostAsJsonAsync("/try/keep", new { attr = "hello" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("installed").GetBoolean());
        Assert.False(body.GetProperty("broker").GetProperty("executed").GetBoolean());
        Assert.Equal(GuardianPlanner.Install("hello").Id, body.GetProperty("planId").GetString());

        // Exactly one broker operation, a submit: Core never reviews, approves or stages.
        var request = Assert.Single(broker.Requests);
        Assert.Equal("submit", request.GetProperty("operation").GetString());
        var payload = request.GetProperty("payload");
        Assert.Equal("""[{"package":"hello"}]""", payload.GetProperty("changes").GetRawText());
        Assert.StartsWith("Install hello", payload.GetProperty("description").GetString());
    }

    [Fact]
    public async Task KeepBuildsTheSamePlanAsAnInstallRequest()
    {
        using var factory = new CoreApiFactory();
        var preview = await (await factory.Authorized().PostAsJsonAsync("/guardian/preview",
            new { description = "Install vlc", packages = new[] { "vlc" } })).Content.ReadFromJsonAsync<JsonElement>();
        var plan = GuardianPlanner.Install("vlc");
        Assert.Equal(plan.Id, preview.GetProperty("id").GetString());
        Assert.Equal("{ pkgs, ... }:\n{\n  environment.systemPackages = [ pkgs.vlc ];\n}\n", plan.NixPreview);
        Assert.Equal("review", plan.RiskLevel);
        Assert.Empty(plan.Changes);
    }

    [Theory]
    [InlineData("nixpkgs#hello")]
    [InlineData("github:evil/flake#pkg")]
    [InlineData("/nix/store/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-hello")]
    [InlineData("../hello")]
    [InlineData("hello;id")]
    [InlineData("hello $(id)")]
    [InlineData("hello\n")]
    [InlineData("\"hello\"")]
    [InlineData("let")]
    [InlineData("a.b.c.d.e")]
    [InlineData("")]
    [InlineData(null)]
    public async Task InvalidAttrsNeverReachTheBroker(string? attr)
    {
        using var broker = new RecordingBroker();
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", broker.Socket));
        using var response = await factory.Authorized().PostAsJsonAsync("/try/keep", new { attr });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(broker.Requests);
    }

    [Fact]
    public async Task MissingBrokerIsServiceUnavailable()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"));
        using var response = await factory.Authorized().PostAsJsonAsync("/try/keep", new { attr = "hello" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // Against the real Python broker: the candidate is an approval-gated
    // overlay adding the package, and activation stays prohibited.
    [Fact]
    public async Task RealBrokerRecordsAnApprovalGatedCandidate()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "test_broker.py"));
        var dir = Path.Combine(Path.GetTempPath(), "nexus-broker-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { fixture, "--dir", dir }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        try
        {
            var ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(20)) || ready.Result != "READY")
                throw new InvalidOperationException("Test broker did not start: " + process.StandardError.ReadToEnd());
            using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", Path.Combine(dir, "broker.sock")));
            var client = factory.Authorized();
            using var response = await client.PostAsJsonAsync("/try/keep", new { attr = "hello" });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            var candidate = body.GetProperty("broker").GetProperty("candidate");
            Assert.Equal(body.GetProperty("planId").GetString(), candidate.GetProperty("planId").GetString());
            Assert.Equal("prohibited", candidate.GetProperty("activation").GetString());
            Assert.Contains("environment.systemPackages = [ pkgs.hello ];", candidate.GetProperty("nixModule").GetString());
            Assert.Equal("required", body.GetProperty("broker").GetProperty("approval").GetString());
            var status = await client.GetFromJsonAsync<JsonElement>($"/guardian/candidates/{candidate.GetProperty("candidateId").GetString()}");
            Assert.Equal(JsonValueKind.Null, status.GetProperty("decision").ValueKind);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            Directory.Delete(dir, true);
        }
    }
}
