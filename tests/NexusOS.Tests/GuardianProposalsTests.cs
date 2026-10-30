using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// The Guardian dialog's flow through Core, against Guardian as an installed
// NexusOS runs it (tests/fixtures/test_guardian_stack.py): the real broker,
// local builder and staging service; only nix, the bootloader and polkit are
// stood in for. Nothing is approved without the (stand-in) password step.
public sealed class GuardianProposalsTests
{
    private static string Fixture => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "fixtures", "test_guardian_stack.py"));
    private const string Valid = """{"description":"Enable printing","changes":[{"option":"services.printing.enable","enabled":true}]}""";

    private sealed class Stack : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "nexus-gs-" + Guid.NewGuid().ToString("N")[..10]);
        private readonly Process _process;
        public CoreApiFactory Factory { get; }

        public Stack(params string[] extra)
        {
            Directory.CreateDirectory(Dir);
            var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { Fixture, "--dir", Dir }.Concat(extra)) start.ArgumentList.Add(arg);
            _process = Process.Start(start)!;
            var ready = _process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(TimeSpan.FromSeconds(30)) || ready.Result != "READY")
                throw new InvalidOperationException("Guardian stack did not start: " + _process.StandardError.ReadToEnd());
            Factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", Path.Combine(Dir, "broker.sock")),
                ("NEXUS_GUARDIAN_BUILDER_SOCKET", Path.Combine(Dir, "builder.sock")),
                ("NEXUS_GUARDIAN_STAGE_SOCKET", Path.Combine(Dir, "stage.sock")))
            {
                AiHandler = StubHandler.Json(HttpStatusCode.OK, "{\"response\":" + JsonSerializer.Serialize(Valid) + "}")
            };
        }

        // What polkit does after the person's password: one approval for one candidate and build
        // (pkexec nexus-guardian-desktop-approve <candidateId> <buildHash>).
        public async Task ApproveWithPassword(string candidateId, string buildHash)
        {
            File.WriteAllText(Path.Combine(Dir, "approve", candidateId), buildHash);
            for (var i = 0; i < 100 && File.Exists(Path.Combine(Dir, "approve", candidateId)); i++) await Task.Delay(50);
            await Task.Delay(100);
        }

        public void Booted(string decision, string evt) =>
            File.AppendAllText(Path.Combine(Dir, "state", "staging.jsonl"),
                JsonSerializer.Serialize(new Dictionary<string, object> { ["event"] = evt, ["decision"] = decision, ["at"] = 1 }) + "\n");

        public void Dispose()
        {
            Factory.Dispose();
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
            _process.Dispose();
            Directory.Delete(Dir, true);
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> WaitFor(HttpClient client, string id, string state)
    {
        for (var i = 0; i < 200; i++)
        {
            var p = await client.GetFromJsonAsync<JsonElement>("/guardian/proposals/" + id);
            if (p.GetProperty("state").GetString() == state) return p;
            await Task.Delay(50);
        }
        throw new TimeoutException("Proposal never reached " + state);
    }

    [Fact]
    public async Task ProposedByNexusApprovedByThePersonStagedAndApplied()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var stack = new Stack();
        var client = stack.Factory.Authorized();

        // Nexus proposes (the chat tool's endpoint): the dialog sees it waiting, nothing submitted.
        var proposed = await Json(await client.PostAsJsonAsync("/guardian/propose", new { request = "I want to print" }));
        Assert.False(proposed.GetProperty("submitted").GetBoolean());
        var id = proposed.GetProperty("proposal").GetProperty("id").GetString()!;
        var list = await client.GetFromJsonAsync<JsonElement>("/guardian/proposals");
        Assert.True(list.GetProperty("attention").GetBoolean());
        var waiting = Assert.Single(list.GetProperty("proposals").EnumerateArray());
        Assert.Equal("waiting", waiting.GetProperty("state").GetString());
        Assert.Equal("nexus", waiting.GetProperty("source").GetString());
        Assert.Equal("I want to print", waiting.GetProperty("request").GetString());
        var item = Assert.Single(waiting.GetProperty("items").EnumerateArray());
        Assert.Equal(("setting", "services.printing.enable", true), (item.GetProperty("kind").GetString(),
            item.GetProperty("name").GetString(), item.GetProperty("after").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("before").ValueKind);
        Assert.Equal(JsonValueKind.Null, waiting.GetProperty("candidateId").ValueKind);

        // The person chooses Approve: submitted, built, tested and signed; today's value is now known.
        var checking = await Json(await client.PostAsync($"/guardian/proposals/{id}/check", null));
        Assert.Equal("checking", checking.GetProperty("state").GetString());
        var ready = await WaitFor(client, id, "ready");
        Assert.False(ready.GetProperty("items")[0].GetProperty("before").GetBoolean());
        Assert.Equal("pass", ready.GetProperty("checks").GetProperty("vm-test").GetString());
        var candidateId = ready.GetProperty("candidateId").GetString()!;
        var attestation = ready.GetProperty("attestation");

        // Without the password step the broker refuses the review, and nothing is lost.
        using (var early = await client.PostAsJsonAsync("/guardian/review", new { candidateId, attestation }))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, early.StatusCode);
        Assert.Equal("ready", (await client.GetFromJsonAsync<JsonElement>("/guardian/proposals/" + id)).GetProperty("state").GetString());

        // What the dialog shows is the broker's record of the checked candidate, not Core's copy.
        var record = await Json(await client.GetAsync($"/guardian/proposals/{id}/describe"));
        Assert.Equal(candidateId, record.GetProperty("candidateId").GetString());
        var buildHash = record.GetProperty("buildHash").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", buildHash);
        var setting = Assert.Single(record.GetProperty("settings").EnumerateArray());
        Assert.Equal(("services.printing.enable", false, true), (setting.GetProperty("option").GetString(),
            setting.GetProperty("before").GetBoolean(), setting.GetProperty("after").GetBoolean()));
        Assert.False(record.TryGetProperty("description", out _));

        // A password approval for another build is refused, so the review still finds none.
        await stack.ApproveWithPassword(candidateId, new string('0', 64));
        using (var swapped = await client.PostAsJsonAsync("/guardian/review", new { candidateId, attestation }))
            Assert.Equal(HttpStatusCode.UnprocessableEntity, swapped.StatusCode);

        // Password (polkit) for the build shown, then the existing review flow, then staging.
        await stack.ApproveWithPassword(candidateId, buildHash);
        var decision = await Json(await client.PostAsJsonAsync("/guardian/review", new { candidateId, attestation }));
        Assert.True(decision.GetProperty("eligible").GetBoolean());
        var hash = decision.GetProperty("decision").GetString()!;
        Assert.Equal("approved", (await client.GetFromJsonAsync<JsonElement>("/guardian/proposals/" + id)).GetProperty("state").GetString());
        var staged = await Json(await client.PostAsJsonAsync("/guardian/stage", new { decision = hash }));
        Assert.Equal("staged", staged.GetProperty("proposal").GetProperty("state").GetString());
        Assert.True((await client.GetFromJsonAsync<JsonElement>("/guardian/proposals")).GetProperty("restartNeeded").GetBoolean());
        // Staged once only.
        using (var again = await client.PostAsJsonAsync("/guardian/stage", new { decision = hash }))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // After the restart the boot check confirms it.
        stack.Booted(hash, "confirmed");
        Assert.Equal("applied", (await client.GetFromJsonAsync<JsonElement>("/guardian/proposals/" + id)).GetProperty("state").GetString());
        var summaries = (await client.GetFromJsonAsync<JsonElement>("/journal?kind=system")).EnumerateArray()
            .Select(x => x.GetProperty("summary").GetString()).ToArray();
        Assert.Contains("A system change is now in use: Enable printing", summaries);
        Assert.Contains("Nexus proposed a system change: Enable printing", summaries);
    }

    [Fact]
    public async Task SecurityUpdatesAreFoundCheckedAndApprovedLikeAnyChange()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var stack = new Stack("--update");
        var client = stack.Factory.Authorized();

        // The root update check left an update: Core proposes it and starts its checks by itself.
        var list = await client.GetFromJsonAsync<JsonElement>("/guardian/proposals");
        var update = Assert.Single(list.GetProperty("proposals").EnumerateArray());
        Assert.Equal(("update", "updates"), (update.GetProperty("kind").GetString(), update.GetProperty("source").GetString()));
        Assert.Matches("^[0-9a-f]{64}$", update.GetProperty("lockHash").GetString()!);
        var id = update.GetProperty("id").GetString()!;
        var ready = await WaitFor(client, id, "ready");
        Assert.StartsWith("Security updates: 4 packages updated, including Linux 6.18.56 → 6.18.60, Firefox 144.0 → 144.0.2",
            ready.GetProperty("title").GetString());
        Assert.Contains("Security updates are ready", ready.GetProperty("message").GetString());
        var linux = ready.GetProperty("items")[0];
        Assert.Equal(("update", "linux", "Linux", "6.18.60"), (linux.GetProperty("kind").GetString(), linux.GetProperty("name").GetString(),
            linux.GetProperty("label").GetString(), linux.GetProperty("after").GetString()));
        list = await client.GetFromJsonAsync<JsonElement>("/guardian/proposals");
        Assert.True(list.GetProperty("updatesReady").GetBoolean());
        Assert.True(list.GetProperty("attention").GetBoolean());

        // The broker's record names the update: nixpkgs before and after, and the package versions.
        var record = await Json(await client.GetAsync($"/guardian/proposals/{id}/describe"));
        Assert.Equal("update", record.GetProperty("kind").GetString());
        var signed = record.GetProperty("update");
        Assert.Equal(new string('a', 40), signed.GetProperty("nixpkgs").GetProperty("before").GetProperty("rev").GetString());
        Assert.Equal(new string('b', 40), signed.GetProperty("nixpkgs").GetProperty("after").GetProperty("rev").GetString());
        Assert.Equal(ready.GetProperty("lockHash").GetString(), signed.GetProperty("lockHash").GetString());
        Assert.Equal("Linux", signed.GetProperty("packages")[0].GetProperty("label").GetString());

        // Approved with the password, reviewed and staged as a trial boot, like any change.
        var candidateId = ready.GetProperty("candidateId").GetString()!;
        await stack.ApproveWithPassword(candidateId, record.GetProperty("buildHash").GetString()!);
        var decision = await Json(await client.PostAsJsonAsync("/guardian/review",
            new { candidateId, attestation = ready.GetProperty("attestation") }));
        Assert.True(decision.GetProperty("eligible").GetBoolean());
        var staged = await Json(await client.PostAsJsonAsync("/guardian/stage", new { decision = decision.GetProperty("decision").GetString() }));
        Assert.Equal("staged", staged.GetProperty("proposal").GetProperty("state").GetString());

        // Nexus can tell the person about it, and the same update is not proposed twice.
        var told = await Json(await client.GetAsync("/guardian/updates"));
        Assert.Equal("available", told.GetProperty("lastCheck").GetProperty("state").GetString());
        Assert.Equal("staged", told.GetProperty("updates").GetProperty("state").GetString());
        Assert.Contains("Linux 6.18.56 → 6.18.60", told.GetProperty("updates").GetProperty("summary").GetString());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/guardian/proposals")).GetProperty("proposals").EnumerateArray());

        // "Check for updates": through the builder (a root path unit starts the check), once in a while.
        var asked = await Json(await client.PostAsync("/guardian/updates/check", null));
        Assert.True(asked.GetProperty("requested").GetBoolean());
        Assert.True(File.Exists(Path.Combine(stack.Dir, "builder", "work", "update-requested")));
        var again = await Json(await client.PostAsync("/guardian/updates/check", null));
        Assert.False(again.GetProperty("requested").GetBoolean());
    }

    [Fact]
    public async Task WithoutGuardianUpdatesAreReportedPlainly()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_BUILDER_SOCKET", "/nonexistent/builder.sock"));
        var client = factory.Authorized();
        var told = await Json(await client.GetAsync("/guardian/updates"));
        Assert.Contains("not reachable", told.GetProperty("guardian").GetString());
        var asked = await Json(await client.PostAsync("/guardian/updates/check", null));
        Assert.False(asked.GetProperty("requested").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/guardian/proposals")).GetProperty("updatesReady").GetBoolean());
    }

    [Fact]
    public async Task FailedBuildCannotBeApprovedAndARolledBackTrialIsReported()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var stack = new Stack("--failing-build");
        var client = stack.Factory.Authorized();
        var created = await Json(await client.PostAsJsonAsync("/guardian/proposals",
            new { description = "Install hello", packages = new[] { "hello" }, source = "user" }));
        var id = created.GetProperty("id").GetString()!;
        await Json(await client.PostAsync($"/guardian/proposals/{id}/check", null));
        var failed = await WaitFor(client, id, "failed");
        Assert.Equal("fail", failed.GetProperty("checks").GetProperty("build").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("attestation").ValueKind);
        Assert.DoesNotContain("simulated", failed.GetProperty("message").GetString());
        var dismissed = await Json(await client.PostAsync($"/guardian/proposals/{id}/dismiss", null));
        Assert.Equal("dismissed", dismissed.GetProperty("state").GetString());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/guardian/proposals")).GetProperty("proposals").EnumerateArray());
    }

    [Fact]
    public async Task DeclineSendsNothingAndTheSamePlanIsNotRepeated()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/broker.sock"));
        var client = factory.Authorized();
        var body = new { description = "Enable printing", changes = new[] { new { option = "services.printing.enable", enabled = true } } };
        var first = await Json(await client.PostAsJsonAsync("/guardian/proposals", body));
        var second = await Json(await client.PostAsJsonAsync("/guardian/proposals", body));
        Assert.Equal(first.GetProperty("id").GetString(), second.GetProperty("id").GetString());
        var declined = await Json(await client.PostAsync($"/guardian/proposals/{first.GetProperty("id").GetString()}/decline", null));
        Assert.Equal("declined", declined.GetProperty("state").GetString());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/guardian/proposals")).GetProperty("attention").GetBoolean());
    }

    [Fact]
    public async Task OnlyACheckedChangeHasABrokerRecordToShow()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/broker.sock"));
        var client = factory.Authorized();
        var p = await Json(await client.PostAsJsonAsync("/guardian/proposals", new { description = "Install hello", packages = new[] { "hello" } }));
        using (var waiting = await client.GetAsync($"/guardian/proposals/{p.GetProperty("id").GetString()}/describe"))
            Assert.Equal(HttpStatusCode.Conflict, waiting.StatusCode);
        using (var unknown = await client.GetAsync("/guardian/proposals/0123456789abcdef/describe"))
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task WithoutGuardianTheCheckFailsPlainly()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/broker.sock"));
        var client = factory.Authorized();
        var p = await Json(await client.PostAsJsonAsync("/guardian/proposals", new { description = "Install hello", packages = new[] { "hello" } }));
        var checkedOut = await Json(await client.PostAsync($"/guardian/proposals/{p.GetProperty("id").GetString()}/check", null));
        Assert.Equal("failed", checkedOut.GetProperty("state").GetString());
        Assert.Contains("not running on this computer", checkedOut.GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("""{"description":"x","changes":[{"option":"imports","enabled":true}]}""")]
    [InlineData("""{"description":"x","packages":["nixpkgs#evil"]}""")]
    [InlineData("""{"description":"x"}""")]
    public async Task InvalidPlansAreNeverRecorded(string json)
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        using var response = await client.PostAsync("/guardian/proposals", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/guardian/proposals")).GetProperty("proposals").EnumerateArray());
    }

    [Theory]
    [InlineData("/guardian/proposals/not-an-id/check")]
    [InlineData("/guardian/proposals/0123456789abcdef/check")]
    public async Task UnknownOrMalformedIdsAreRefused(string path)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsync(path, null);
        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StageNeedsADecisionHash()
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsJsonAsync("/guardian/stage", new { decision = "abc" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void ProposerAcceptsPackagesButStillRejectsEmptyPlans()
    {
        var (plan, _) = GuardianProposer.Parse("""{"description":"Install Blender","changes":[],"packages":["blender"]}""");
        Assert.Equal(["blender"], plan!.Packages!);
        Assert.Throws<ProposalRejectedException>(() => GuardianProposer.Parse("""{"description":"x","changes":[],"packages":[]}"""));
        Assert.Throws<ProposalRejectedException>(() => GuardianProposer.Parse("""{"description":"x","changes":[],"packages":["a;b"]}"""));
        Assert.Throws<ProposalRejectedException>(() => GuardianProposer.Parse("""{"description":"x","changes":[],"packages":[1]}"""));
        Assert.Throws<ProposalRejectedException>(() =>
            GuardianProposer.Parse("""{"description":"x","changes":[],"packages":["a"],"packages":["b"]}"""));
    }
}
