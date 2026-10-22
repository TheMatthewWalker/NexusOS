using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class RulesApiTests
{
    private const string Headphones = """
        {"name":"Headphones","createdFrom":"When I plug in my headphones, open Spotify",
         "trigger":{"type":"device.connected","kind":"audio"},
         "actions":[{"type":"app.open","appId":"spotify"},{"type":"setting.set","key":"notifications.volume","value":25}]}
        """;

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Save(HttpClient client, string json = Headphones)
    {
        using var response = await client.PostAsync("/rules", Json(json));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task SaveListPauseResumeDeleteRoundTrip()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var saved = await Save(client);
        var id = saved.GetProperty("id").GetString()!;
        Assert.True(RuleStore.IsValidId(id));
        Assert.True(saved.GetProperty("enabled").GetBoolean());
        Assert.Equal("When an audio device connects, open spotify and set notifications volume to 25%.", saved.GetProperty("sentence").GetString());
        Assert.Equal("When I plug in my headphones, open Spotify", saved.GetProperty("createdFrom").GetString());

        var listed = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/rules")).EnumerateArray());
        Assert.Equal(id, listed.GetProperty("id").GetString());
        Assert.Equal(0, listed.GetProperty("history").GetProperty("runs").GetInt32());

        using (var paused = await client.PostAsync($"/rules/{id}/pause", null))
            Assert.False((await paused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        using (var resumed = await client.PostAsync($"/rules/{id}/resume", null))
            Assert.True((await resumed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        using (var removed = await client.DeleteAsync($"/rules/{id}"))
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/rules")).GetArrayLength());
        using (var again = await client.DeleteAsync($"/rules/{id}"))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/rules/0123456789abcdef/pause", HttpStatusCode.NotFound)]
    [InlineData("POST", "/rules/0123456789abcdef/resume", HttpStatusCode.NotFound)]
    [InlineData("GET", "/rules/0123456789abcdef/history", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "/rules/0123456789abcdef", HttpStatusCode.NotFound)]
    [InlineData("POST", "/rules/..%2F..%2Fetc/pause", HttpStatusCode.BadRequest)]
    [InlineData("DELETE", "/rules/0123456789ABCDEF", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/rules/x/history", HttpStatusCode.BadRequest)]
    public async Task UnknownOrMalformedIdsAreRejected(string method, string path, HttpStatusCode expected)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"setting.set","key":"wifi","value":false}]}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":"x"}],"id":"0123456789abcdef"}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":"x"}],"enabled":"yes"}""")]
    [InlineData("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"notify","text":"x"}],"name":7}""")]
    [InlineData("[]")]
    public async Task InvalidRulesAreNotSaved(string json)
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        using var response = await client.PostAsync("/rules", Json(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>("/rules")).GetArrayLength());
    }

    [Fact]
    public async Task SavingASystemChangeIsRefusedWithTheApprovalMessage()
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsync("/rules",
            Json("""{"trigger":{"type":"power.plugged"},"actions":[{"type":"shell.run","command":"systemctl enable sshd"}]}"""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("system changes go through approval", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ADraftFromParseCanBeSavedAsIs()
    {
        using var factory = new CoreApiFactory { AiHandler = StubHandler.Json(HttpStatusCode.OK, """{"response":"not json"}""") };
        var client = factory.Authorized();
        using var parsed = await client.PostAsJsonAsync("/rules/parse", new { sentence = "When I unplug the charger, set volume to 30" });
        var draft = (await parsed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rule");
        var body = new Dictionary<string, JsonElement>
        {
            ["name"] = draft.GetProperty("name"), ["createdFrom"] = draft.GetProperty("createdFrom"),
            ["trigger"] = draft.GetProperty("trigger"), ["conditions"] = draft.GetProperty("conditions"), ["actions"] = draft.GetProperty("actions"),
        };
        var saved = await Save(client, JsonSerializer.Serialize(body));
        Assert.Equal(draft.GetProperty("sentence").GetString(), saved.GetProperty("sentence").GetString());
    }

    [Fact]
    public async Task EventsReturnTheActionsOfRulesThatFiredAndRecordHistory()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var id = (await Save(client)).GetProperty("id").GetString()!;
        using var response = await client.PostAsJsonAsync("/events", new { type = "device.connected", kind = "audio", name = "Sony WH-1000XM4" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("accepted").GetBoolean());
        var fired = Assert.Single(body.GetProperty("fired").EnumerateArray());
        Assert.Equal(id, fired.GetProperty("ruleId").GetString());
        Assert.Equal("spotify", fired.GetProperty("actions")[0].GetProperty("appId").GetString());
        Assert.Equal(25, fired.GetProperty("actions")[1].GetProperty("value").GetInt32());

        var history = await client.GetFromJsonAsync<JsonElement>($"/rules/{id}/history");
        var run = Assert.Single(history.EnumerateArray());
        Assert.Equal("events-response", run.GetProperty("delivery").GetString());
        Assert.Equal(fired.GetProperty("runId").GetInt64(), run.GetProperty("id").GetInt64());
        var listed = Assert.Single((await client.GetFromJsonAsync<JsonElement>("/rules")).EnumerateArray());
        Assert.Equal(1, listed.GetProperty("history").GetProperty("runs").GetInt32());

        // Paused: the event is accepted but nothing fires.
        (await client.PostAsync($"/rules/{id}/pause", null)).EnsureSuccessStatusCode();
        using var quiet = await client.PostAsJsonAsync("/events", new { type = "device.connected", kind = "audio" });
        Assert.Equal(0, (await quiet.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fired").GetArrayLength());
    }

    [Fact]
    public async Task ErasingHistoryErasesRuleRuns()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var id = (await Save(client)).GetProperty("id").GetString()!;
        (await client.PostAsJsonAsync("/events", new { type = "device.connected", kind = "audio" })).EnsureSuccessStatusCode();
        Assert.Equal(1, (await client.GetFromJsonAsync<JsonElement>($"/rules/{id}/history")).GetArrayLength());
        (await client.DeleteAsync("/history")).EnsureSuccessStatusCode();
        Assert.Equal(0, (await client.GetFromJsonAsync<JsonElement>($"/rules/{id}/history")).GetArrayLength());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/rules")).EnumerateArray());
    }

    [Theory]
    [InlineData("""{"type":"device.connected"}""")]
    [InlineData("""{"type":"device.connected","kind":"audio","name":"x","extra":true}""")]
    [InlineData("""{"type":"time.daily","time":"09:00"}""")]
    [InlineData("""{"type":"shell.run","command":"id"}""")]
    [InlineData("""{"type":"app.opened","appId":"$(id)"}""")]
    [InlineData("""{"type":"network.joined","ssid":""}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task InvalidEventsAreRejected(string json)
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsync("/events", Json(json));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OverlongDeviceNamesAreRejected()
    {
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsJsonAsync("/events",
            new { type = "device.connected", kind = "usb", name = new string('k', RuleSchema.MaxEventNameLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EventsAreRateLimited()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        for (var i = 0; i < RuleEndpoints.EventsPerMinute; i++)
        {
            using var ok = await client.PostAsJsonAsync("/events", new { type = "power.plugged" });
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync("/events", new { type = "power.plugged" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        // Rule management has its own budget and still works.
        using var list = await client.GetAsync("/rules");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    public async Task TimeRulesQueueActionsForTheShell()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeProvider.System.LocalTimeZone);
        var json = $$"""{"trigger":{"type":"time.daily","time":"{{local:HH\:mm}}"},"actions":[{"type":"notify","text":"Stretch"}]}""";
        var id = (await Save(client, json)).GetProperty("id").GetString()!;
        // The hosted timer may already have fired it; either way it is queued once.
        await factory.Services.GetRequiredService<RuleEngine>().TickAsync(default);
        var pending = await client.GetFromJsonAsync<JsonElement>("/rules/pending-actions");
        var item = Assert.Single(pending.GetProperty("items").EnumerateArray());
        Assert.Equal(id, item.GetProperty("ruleId").GetString());
        Assert.Equal("Stretch", item.GetProperty("actions")[0].GetProperty("text").GetString());
        var next = pending.GetProperty("next").GetInt64();
        var after = await client.GetFromJsonAsync<JsonElement>($"/rules/pending-actions?after={next}");
        Assert.Equal(0, after.GetProperty("items").GetArrayLength());
        Assert.Equal("queued", Assert.Single((await client.GetFromJsonAsync<JsonElement>($"/rules/{id}/history")).EnumerateArray())
            .GetProperty("delivery").GetString());
        using var bad = await client.GetAsync("/rules/pending-actions?after=-1");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task HistoryLimitIsValidated()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var id = (await Save(client)).GetProperty("id").GetString()!;
        using var bad = await client.GetAsync($"/rules/{id}/history?limit=0");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using var tooMany = await client.GetAsync($"/rules/{id}/history?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task RuleContentsNeverAppearInLogs()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        var secret = "PrivateNetwork" + Guid.NewGuid().ToString("N")[..8];
        await Save(client, $$"""{"trigger":{"type":"network.joined","ssid":"{{secret}}"},"actions":[{"type":"notify","text":"hi"}]}""");
        (await client.PostAsJsonAsync("/events", new { type = "network.joined", ssid = secret })).EnsureSuccessStatusCode();
        Assert.Contains("POST /events", factory.Logs.All);
        Assert.DoesNotContain(secret, factory.Logs.All);
    }
}
