using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

public sealed class ActivityJournalTests
{
    private static long Count(string root, string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "state.sqlite3")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static ActivityJournal Journal(TempState state, params (string, string?)[] config) =>
        new(new SqliteStateDatabase(state.Paths()), state.Config(config));

    private static JournalRecord Setting(string summary, object? before = null) =>
        new(JournalKinds.Setting, "user", summary, before is null ? null : JsonSerializer.SerializeToElement(before));

    [Fact]
    public async Task EntriesRoundTripNewestFirstAndFilterByKindAndSince()
    {
        using var state = new TempState();
        var journal = Journal(state);
        var ct = CancellationToken.None;
        await journal.AppendAsync(Setting("Night light on", new { enabled = false }) with
            { After = JsonSerializer.SerializeToElement(new { enabled = true }), Reversible = true, Ref = "shell:night-light" },
            ct, DateTimeOffset.UtcNow.AddHours(-2));
        await journal.AppendAsync(new JournalRecord(JournalKinds.Device, "firefox", "Firefox used the microphone"), ct);
        var all = await journal.QueryAsync(null, null, 10, ct);
        Assert.Equal(["Firefox used the microphone", "Night light on"], all.Select(x => x.Summary));
        var setting = all[1];
        Assert.Equal(JournalKinds.Setting, setting.Kind);
        Assert.False(setting.Before!.Value.GetProperty("enabled").GetBoolean());
        Assert.True(setting.After!.Value.GetProperty("enabled").GetBoolean());
        Assert.True(setting.Reversible);
        Assert.Equal("shell:night-light", setting.Ref);
        Assert.Equal(TimeSpan.Zero, setting.Time.Offset);
        Assert.Equal("firefox", Assert.Single(await journal.QueryAsync(null, JournalKinds.Device, 10, ct)).Actor);
        Assert.Equal("Firefox used the microphone",
            Assert.Single(await journal.QueryAsync(DateTimeOffset.UtcNow.AddHours(-1), null, 10, ct)).Summary);
        Assert.Single(await journal.QueryAsync(null, null, 1, ct));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public async Task QueryPageIsBounded(int limit)
    {
        using var state = new TempState();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Journal(state).QueryAsync(null, null, limit, CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredEntriesAreHiddenAndPrunedByJournalAndHistoryRetention()
    {
        using var state = new TempState();
        var config = state.Config(("NEXUS_HISTORY_MAX_DAYS", "7"));
        var database = new SqliteStateDatabase(state.Paths());
        var journal = new ActivityJournal(database, config);
        void InsertOld()
        {
            using var db = database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO journal(timestamp,kind,actor,summary) VALUES($t,'device','user','old')";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddDays(-15).ToString("O"));
            cmd.ExecuteNonQuery();
        }
        InsertOld();
        Assert.Empty(await journal.QueryAsync(null, null, 10, CancellationToken.None));
        await journal.AppendAsync(new JournalRecord(JournalKinds.Device, "user", "new"), CancellationToken.None);
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM journal WHERE summary='old'"));

        // The existing history retention pass prunes the journal too.
        InsertOld();
        await new ConversationStore(database, config).AppendAsync(new(DateTimeOffset.UtcNow, "local", "p", "a"), CancellationToken.None);
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM journal WHERE summary='old'"));
        Assert.Equal(1, Count(state.Root, "SELECT COUNT(*) FROM journal"));
    }

    [Fact]
    public async Task FutureDatedEntriesAreHiddenAndPruned()
    {
        using var state = new TempState();
        var journal = Journal(state);
        await journal.AppendAsync(new JournalRecord(JournalKinds.Device, "user", "future"), CancellationToken.None, DateTimeOffset.UtcNow.AddDays(2));
        Assert.Empty(await journal.QueryAsync(null, null, 10, CancellationToken.None));
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM journal"));
    }

    [Fact]
    public async Task ForgetDayLeavesNoEntryBytesAndKeepsOtherDays()
    {
        using var state = new TempState();
        var journal = Journal(state);
        var ct = CancellationToken.None;
        var marker = "forget-me-" + Guid.NewGuid().ToString("N");
        var today = ActivityJournal.Today;
        var startOfToday = DateTimeOffset.Parse(ActivityJournal.DayBounds(today).Start);
        for (var i = 0; i < 20; i++)
            await journal.AppendAsync(Setting(marker + i, new { secret = marker }), ct, startOfToday.AddSeconds(i));
        await journal.AppendAsync(Setting("yesterday"), ct, startOfToday.AddMinutes(-1));
        var file = Path.Combine(state.Root, "state.sqlite3");
        Assert.Contains(marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));

        Assert.Equal(20, await journal.ForgetDayAsync(today, ct));
        Assert.Equal("yesterday", Assert.Single(await journal.QueryAsync(null, null, 50, ct)).Summary);
        foreach (var path in Directory.GetFiles(state.Root, "state.sqlite3*"))
            Assert.DoesNotContain(marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task DaySummaryCountsEgressReadsAndDeviceUses()
    {
        using var state = new TempState();
        var journal = Journal(state);
        var ct = CancellationToken.None;
        await journal.RecordEgressAsync("openai-compatible", "api.example.com", null, ct);
        await journal.RecordEgressAsync("openai-compatible", "api.example.com", null, ct);
        await journal.AppendAsync(new JournalRecord(JournalKinds.AiRead, "nexus", "Read notes.txt"), ct);
        await journal.AppendAsync(new JournalRecord(JournalKinds.Device, "obs", "OBS used the camera"), ct);
        await journal.AppendAsync(Setting("Theme dark"), ct);
        var summary = await journal.SummaryAsync(ActivityJournal.Today, ct);
        Assert.Equal((2L, 1L, 1L), (summary.LeftTheComputer, summary.AiReads, summary.DeviceUses));
        Assert.Equal(0, (await journal.SummaryAsync(ActivityJournal.Today.AddDays(-1), ct)).LeftTheComputer);
    }

    [Fact]
    public async Task ProviderSelectionTimeIsPersisted()
    {
        using var state = new TempState();
        var store = new ProviderStore(state.Paths());
        Assert.Null(store.SelectedAt);
        Assert.True(store.Select("openai-compatible"));
        var at = store.SelectedAt;
        Assert.NotNull(at);
        Assert.Equal(at, new ProviderStore(state.Paths()).SelectedAt);
    }

    // --- API ---

    private static async Task<long> Post(HttpClient client, object body)
    {
        using var response = await client.PostAsJsonAsync("/journal", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ShellRecordsSettingAndUndoReturnsTheOldValueOnce()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var id = await Post(client, new { kind = "setting", summary = "Night light on", before = new { enabled = false },
                                          after = new { enabled = true }, reversible = true, @ref = "shell:night-light" });
        var listed = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/journal")).EnumerateArray());
        Assert.Equal("user", listed.GetProperty("actor").GetString());
        Assert.Equal("shell:night-light", listed.GetProperty("ref").GetString());

        using (var undo = await client.PostAsync($"/journal/{id}/undo", null))
        {
            Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
            var body = await undo.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("restore").GetProperty("enabled").GetBoolean());
            Assert.Equal("shell:night-light", body.GetProperty("ref").GetString());
        }
        using (var again = await client.PostAsync($"/journal/{id}/undo", null))
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var marked = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/journal")).EnumerateArray());
        Assert.Equal(JsonValueKind.String, marked.GetProperty("undoneAt").ValueKind);

        var device = await Post(client, new { kind = "device", actor = "firefox", summary = "Firefox used the microphone" });
        var noBefore = await Post(client, new { kind = "setting", summary = "Wallpaper changed" });
        var irreversible = await Post(client, new { kind = "setting", summary = "Locale", before = "en", reversible = false });
        foreach (var other in new[] { device, noBefore, irreversible })
            using (var refused = await client.PostAsync($"/journal/{other}/undo", null))
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        using var missing = await client.PostAsync("/journal/999999/undo", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    public static TheoryData<string> InvalidPosts => new()
    {
        """{"kind":"egress","summary":"forged"}""",
        """{"kind":"ai-read","summary":"forged"}""",
        """{"kind":"system","summary":"forged"}""",
        """{"kind":"bogus","summary":"x"}""",
        """{"summary":"no kind"}""",
        """{"kind":"setting"}""",
        """{"kind":"setting","summary":"   "}""",
        """{"kind":"setting","summary":"line\nbreak"}""",
        "{\"kind\":\"setting\",\"summary\":\"" + new string('s', 201) + "\"}",
        """{"kind":"setting","summary":"x","actor":"nexus"}""",
        """{"kind":"setting","summary":"x","actor":"two words"}""",
        """{"kind":"setting","summary":"x","ref":""}""",
        "{\"kind\":\"setting\",\"summary\":\"x\",\"before\":\"" + new string('b', 4100) + "\"}",
        "{\"kind\":\"setting\",\"summary\":\"x\",\"after\":\"" + new string('a', 4100) + "\"}",
    };

    [Theory]
    [MemberData(nameof(InvalidPosts))]
    public async Task InvalidOrForgedEntriesAreRejected(string json)
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        using var response = await client.PostAsync("/journal", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/journal")).GetArrayLength());
    }

    [Theory]
    [InlineData("/journal?limit=0")]
    [InlineData("/journal?limit=501")]
    [InlineData("/journal?kind=bogus")]
    [InlineData("/journal?since=yesterday-ish")]
    public async Task ReadQueryIsValidated(string path)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().GetAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/journal")]
    [InlineData("/journal?day=22-10-2026")]
    [InlineData("/journal?day=2026-13-01")]
    public async Task ForgetRequiresAnExactDay(string path)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().DeleteAsync(path);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ForgetTodayThroughTheApiErasesTodaysEntries()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        await Post(client, new { kind = "device", summary = "Camera used" });
        await Post(client, new { kind = "device", summary = "Microphone used" });
        using var response = await client.DeleteAsync($"/journal?day={ActivityJournal.Today:yyyy-MM-dd}");
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("erased").GetInt64());
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/journal")).GetArrayLength());
    }

    [Fact]
    public async Task JournalWritesAreRateLimited()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        for (var i = 0; i < CoreLimits.JournalPerMinute; i++) await Post(client, new { kind = "device", summary = "use " + i });
        using var limited = await client.PostAsJsonAsync("/journal", new { kind = "device", summary = "over" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task CloudRequestIsJournalledWithConsentTimeButNeverThePrompt()
    {
        var prompt = "private-prompt-" + Guid.NewGuid().ToString("N");
        var cloud = StubHandler.Json(HttpStatusCode.OK, """{"choices":[{"message":{"content":"cloud answer"}}]}""");
        using var factory = new CoreApiFactory(("NEXUS_CLOUD_EGRESS_ALLOWED", "true")) { CloudHandler = cloud };
        var client = factory.Authorized();
        // Local answers never leave the machine and are not egress.
        (await client.PostAsJsonAsync("/chat", new { prompt = "local only" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/providers/select", new { name = "openai-compatible" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/chat", new { prompt })).EnsureSuccessStatusCode();
        Assert.Single(cloud.Requests);

        var raw = await client.GetStringAsync("/journal?kind=egress");
        Assert.DoesNotContain(prompt, raw);
        var entry = Assert.Single(JsonDocument.Parse(raw).RootElement.EnumerateArray());
        Assert.Equal("nexus", entry.GetProperty("actor").GetString());
        Assert.Equal("api.openai.com", entry.GetProperty("ref").GetString());
        var after = entry.GetProperty("after");
        Assert.Equal("openai-compatible", after.GetProperty("provider").GetString());
        Assert.True(after.GetProperty("consentAt").GetDateTimeOffset() <= entry.GetProperty("time").GetDateTimeOffset());
        Assert.Equal(1, Count(factory.StateRoot, "SELECT COUNT(*) FROM journal"));
        Assert.Equal(0, Count(factory.StateRoot, $"SELECT COUNT(*) FROM journal WHERE summary LIKE '%{prompt}%' OR after LIKE '%{prompt}%'"));

        var summary = await client.GetFromJsonAsync<JsonElement>("/journal/today/summary");
        Assert.Equal(1, summary.GetProperty("leftTheComputer").GetInt64());
        Assert.Equal(0, summary.GetProperty("aiReads").GetInt64());
        Assert.Equal(0, summary.GetProperty("deviceUses").GetInt64());
    }

    [Fact]
    public async Task RefusedCloudRequestIsNotJournalled()
    {
        var cloud = StubHandler.Json(HttpStatusCode.OK, "{}");
        using var factory = new CoreApiFactory { CloudHandler = cloud };
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/providers/select", new { name = "openai-compatible" })).EnsureSuccessStatusCode();
        using var response = await client.PostAsJsonAsync("/chat", new { prompt = "no consent" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(cloud.Requests);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/journal")).GetArrayLength());
    }

    [Fact]
    public async Task GuardianProposalIsJournalledAsSystemEntry()
    {
        var plan = """{"description":"Enable printing","changes":[{"option":"services.printing.enable","enabled":true}]}""";
        using var factory = new CoreApiFactory
        {
            AiHandler = StubHandler.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { response = plan }))
        };
        var client = factory.Authorized();
        using var proposed = await client.PostAsJsonAsync("/guardian/propose", new { request = "I want to print" });
        var planId = (await proposed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("id").GetString();
        var entry = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/journal?kind=system")).EnumerateArray());
        Assert.Equal("nexus", entry.GetProperty("actor").GetString());
        Assert.Equal("Nexus proposed a system change: Enable printing", entry.GetProperty("summary").GetString());
        Assert.Equal("plan:" + planId, entry.GetProperty("ref").GetString());
    }

    [Fact]
    public async Task UnreachableBrokerRecordsNoGuardianDecision()
    {
        using var factory = new CoreApiFactory(("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"));
        var client = factory.Authorized();
        using var response = await client.PostAsJsonAsync("/guardian/submit",
            new { description = "Enable printing", changes = new[] { new { option = "services.printing.enable", enabled = true } } });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/journal")).GetArrayLength());
    }
}
