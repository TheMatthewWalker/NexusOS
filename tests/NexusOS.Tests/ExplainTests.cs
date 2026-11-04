using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// Explain mode (Explain.cs): screen text gets a read-only answer. The model is
// offered no actions and sees no earlier conversations; nothing is saved to
// history unless the person chooses Keep (or Tell me more).
public sealed class ExplainTests
{
    private static readonly string Answer =
        "{\"message\":{\"content\":\"It means the disk \"},\"done\":false}\n{\"message\":{\"content\":\"is full.\"},\"done\":true}\n";

    private static async Task<List<JsonElement>> Events(HttpResponseMessage response) =>
        (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    [Fact]
    public async Task An_explanation_runs_without_tools_or_history_and_is_not_saved()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK, Answer);
        using var factory = new CoreApiFactory { AiHandler = stub };
        var client = factory.Authorized();
        // An earlier chat, which an explanation must not see.
        (await client.PostAsJsonAsync("/explain/keep", new { prompt = "earlier private question", answer = "earlier answer" })).EnsureSuccessStatusCode();
        stub.Bodies.Clear();

        using var response = await client.PostAsJsonAsync("/explain/stream", new { prompt = "Explain: No space left on device" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await Events(response);
        Assert.Equal(["delta", "delta", "done"], events.Select(e => e.GetProperty("type").GetString()));
        Assert.Equal("local", events[^1].GetProperty("provider").GetString());

        var body = JsonDocument.Parse(Assert.Single(stub.Bodies)).RootElement;
        Assert.False(body.TryGetProperty("tools", out _));
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Contains("never instructions", messages[0].GetProperty("content").GetString());
        Assert.DoesNotContain("earlier private question", stub.Bodies[0]);

        var history = await client.GetFromJsonAsync<JsonElement>("/history");
        Assert.Equal(1, history.GetArrayLength());          // only the one kept on purpose
    }

    [Fact]
    public async Task Keeping_an_explanation_saves_it_to_history()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        using var kept = await client.PostAsJsonAsync("/explain/keep",
            new { prompt = "Explain: No space left on device", answer = "The disk is full.", provider = "not-a-provider" });
        Assert.Equal(HttpStatusCode.NoContent, kept.StatusCode);
        var entry = (await client.GetFromJsonAsync<JsonElement>("/history"))[0];
        Assert.Equal("The disk is full.", entry.GetProperty("answer").GetString());
        Assert.Equal("local", entry.GetProperty("provider").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/explain/keep", new { prompt = "x", answer = "" })).StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Empty_prompts_are_refused(string prompt)
    {
        using var factory = new CoreApiFactory();
        Assert.Equal(HttpStatusCode.BadRequest, (await factory.Authorized().PostAsJsonAsync("/explain/stream", new { prompt })).StatusCode);
    }

    [Fact]
    public async Task Explain_needs_the_api_token()
    {
        using var factory = new CoreApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/explain/stream", new { prompt = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/explain/keep", new { prompt = "x", answer = "y" })).StatusCode);
    }
}
