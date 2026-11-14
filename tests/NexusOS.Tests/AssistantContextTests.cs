using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// What the chat model is told before each question (AssistantContext.cs).
public sealed class AssistantContextTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-assistant-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static StubHandler ChatStub() => StubHandler.Json(HttpStatusCode.OK,
        "{\"message\":{\"content\":\"An answer\"},\"done\":false}\n{\"message\":{\"content\":\"\"},\"done\":true}\n");

    private static JsonElement[] Messages(StubHandler stub, int request)
    {
        using var doc = JsonDocument.Parse(stub.Bodies[request]);
        return doc.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.Clone()).ToArray();
    }

    [Fact]
    public async Task Chat_tells_the_model_what_NexusOS_is_and_about_this_computer()
    {
        var stub = ChatStub();
        File.WriteAllText(Path.Combine(_dir, "apps.json"), """{"terminal":"kitty","files":"thunar"}""");
        using var factory = new CoreApiFactory(("NEXUS_ASSISTANT_NOTES", Path.Combine(_dir, "none")),
            ("NEXUS_DESKTOP_APPS", Path.Combine(_dir, "apps.json"))) { AiHandler = stub };
        using var response = await factory.Authorized().PostAsJsonAsync("/chat/stream", new { prompt = "how do I connect to wi-fi?" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.EndsWith("/api/chat", stub.Requests[0].RequestUri!.ToString());
        using var body = JsonDocument.Parse(stub.Bodies[0]);
        Assert.False(body.RootElement.GetProperty("think").GetBoolean());       // no hidden reasoning before the answer
        var messages = Messages(stub, 0);
        Assert.Equal(["system", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        var system = messages[0].GetProperty("content").GetString()!;
        Assert.StartsWith("You are Nexus, the assistant built into NexusOS", system);
        Assert.Contains("Super+Space: open or close Nexus", system);
        Assert.Contains("This computer", system);
        Assert.Contains("Apps set up: terminal: kitty, files: thunar", system);
        Assert.Equal("how do I connect to wi-fi?", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_persons_notes_are_included_and_symlinks_are_not_followed()
    {
        var notes = Directory.CreateDirectory(Path.Combine(_dir, "notes")).FullName;
        File.WriteAllText(Path.Combine(notes, "a-work.md"), "I work in finance; explain terms simply.");
        File.WriteAllText(Path.Combine(notes, "ignored.json"), "{\"not\":\"a note\"}");
        var secret = Path.Combine(_dir, "secret.txt");
        File.WriteAllText(secret, "do-not-include");
        File.CreateSymbolicLink(Path.Combine(notes, "b-link.md"), secret);
        var stub = ChatStub();
        using var factory = new CoreApiFactory(("NEXUS_ASSISTANT_NOTES", notes)) { AiHandler = stub };
        (await factory.Authorized().PostAsJsonAsync("/chat/stream", new { prompt = "hello" })).EnsureSuccessStatusCode();

        var system = Messages(stub, 0)[0].GetProperty("content").GetString()!;
        Assert.Contains("Notes from the person", system);
        Assert.Contains("I work in finance; explain terms simply.", system);
        Assert.DoesNotContain("do-not-include", system);
        Assert.DoesNotContain("a note", system);
    }

    [Fact]
    public async Task A_follow_up_question_carries_the_earlier_exchange()
    {
        var stub = ChatStub();
        using var factory = new CoreApiFactory(("NEXUS_ASSISTANT_NOTES", Path.Combine(_dir, "none"))) { AiHandler = stub };
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/chat/stream", new { prompt = "what is a generation?" })).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/chat/stream", new { prompt = "how do I go back to one?" })).EnsureSuccessStatusCode();

        var second = Messages(stub, 1);
        Assert.Equal(["system", "user", "assistant", "user"], second.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("what is a generation?", second[1].GetProperty("content").GetString());
        Assert.Equal("An answer", second[2].GetProperty("content").GetString());
        Assert.Equal("how do I go back to one?", second[3].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Json_tasks_keep_their_own_prompt_without_the_guide()
    {
        var stub = StubHandler.Json(HttpStatusCode.OK, """{"response":"{\"apps\":[\"files\"]}"}""");
        using var factory = new CoreApiFactory { AiHandler = stub };
        await factory.Authorized().PostAsJsonAsync("/suggest/model", new { prompt = "organise my photos" });
        Assert.EndsWith("/api/generate", stub.Requests[0].RequestUri!.ToString());
        Assert.DoesNotContain("You are Nexus, the assistant", stub.Bodies[0]);
    }

    [Fact]
    public void The_guide_fits_comfortably_in_the_default_budget()
    {
        var budget = new InferenceTokenSettings(TestConfig.Of()).Budget;
        Assert.True(AssistantContextBuilder.Guide.Length / 3.5 < budget.MaxInputTokens / 3,
            $"The guide is {AssistantContextBuilder.Guide.Length} characters; keep it under a third of the input budget.");
    }
}
