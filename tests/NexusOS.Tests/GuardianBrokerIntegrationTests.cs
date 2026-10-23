using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// Core -> real Python broker over the framed Unix-socket protocol (ADR-002).
public sealed class GuardianBrokerIntegrationTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "test_broker.py"));

    private sealed class Harness : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "nexus-broker-" + Guid.NewGuid().ToString("N")[..12]);
        private readonly Process _process;
        public string Socket => Path.Combine(Directory, "broker.sock");

        public Harness(params string[] extra)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { Fixture, "--dir", Directory }.Concat(extra)) start.ArgumentList.Add(arg);
            _process = Process.Start(start)!;
            var ready = _process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(20)) || ready.Result != "READY")
                throw new InvalidOperationException("Test broker did not start: " + _process.StandardError.ReadToEnd());
        }

        public JsonElement Attestation(string candidateId) =>
            JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, candidateId + ".attestation.json"))).RootElement.Clone();

        public void Dispose()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
            _process.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    private static readonly object Plan = new
    {
        description = "Enable printing",
        changes = new[] { new { option = "services.printing.enable", enabled = true } }
    };

    [Fact]
    public async Task SubmitReviewAndStatusThroughTheBroker()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var harness = new Harness();
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", harness.Socket));
        var client = factory.Authorized();

        var submitted = await (await client.PostAsJsonAsync("/guardian/submit", Plan)).Content.ReadFromJsonAsync<JsonElement>();
        var candidate = submitted.GetProperty("broker").GetProperty("candidate");
        // Same shared schema and ID scheme on both sides of the boundary.
        Assert.Equal(submitted.GetProperty("planId").GetString(), candidate.GetProperty("planId").GetString());
        Assert.Equal("prohibited", candidate.GetProperty("activation").GetString());
        var id = candidate.GetProperty("candidateId").GetString()!;

        var status = await client.GetFromJsonAsync<JsonElement>($"/guardian/candidates/{id}");
        Assert.True(status.GetProperty("approvalPending").GetBoolean());

        using var review = await client.PostAsJsonAsync("/guardian/review", new { candidateId = id, attestation = harness.Attestation(id) });
        var decision = await review.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        Assert.True(decision.GetProperty("eligible").GetBoolean());
        Assert.False(decision.GetProperty("executed").GetBoolean());

        // The single root approval is spent: replaying the review is refused by the broker.
        using var replay = await client.PostAsJsonAsync("/guardian/review", new { candidateId = id, attestation = harness.Attestation(id) });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, replay.StatusCode);
        Assert.Contains("approval", await replay.Content.ReadAsStringAsync());

        // Each relayed step is journalled as reported, newest first.
        var journal = (await client.GetFromJsonAsync<JsonElement>("/journal?kind=system")).EnumerateArray()
            .Select(x => (x.GetProperty("actor").GetString(), x.GetProperty("summary").GetString(), x.GetProperty("ref").GetString()))
            .ToArray();
        Assert.Equal([
            ("guardian", "Guardian refused a review", "candidate:" + id),
            ("guardian", "Guardian approved a change for staging", "candidate:" + id),
            ("user", "Sent to Guardian for approval: Enable printing", "candidate:" + id)
        ], journal);
    }

    [Fact]
    public async Task FailedBuildAttestationIsNotEligible()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var harness = new Harness("--failing-build");
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", harness.Socket));
        var client = factory.Authorized();
        var submitted = await (await client.PostAsJsonAsync("/guardian/submit", Plan)).Content.ReadFromJsonAsync<JsonElement>();
        var id = submitted.GetProperty("broker").GetProperty("candidate").GetProperty("candidateId").GetString()!;
        var decision = await (await client.PostAsJsonAsync("/guardian/review", new { candidateId = id, attestation = harness.Attestation(id) }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(decision.GetProperty("eligible").GetBoolean());
    }

    [Fact]
    public async Task BrokerSilentlyRejectingThisUidIsForbidden()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var harness = new Harness("--allow-uid", "999999");
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", harness.Socket));
        using var response = await factory.Authorized().PostAsJsonAsync("/guardian/submit", Plan);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task MissingBrokerIsServiceUnavailable()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"));
        using var response = await factory.Authorized().PostAsJsonAsync("/guardian/submit", Plan);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("imports")]
    [InlineData("services.openssh.enable; builtins.exec")]
    public async Task InvalidPlansNeverReachTheBroker(string option)
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"));
        using var response = await factory.Authorized().PostAsJsonAsync("/guardian/submit",
            new { description = "x", changes = new[] { new { option, enabled = true } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-hex-not-hex-not-hex!")]
    [InlineData("abc")]
    public async Task MalformedCandidateIdsAreRejectedLocally(string id)
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"));
        using var response = await factory.Authorized().GetAsync($"/guardian/candidates/{Uri.EscapeDataString(id)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
