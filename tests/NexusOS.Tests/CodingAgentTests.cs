using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// The coding agent (CodingAgent.cs): it stays inside the project, stages
// edits until the person accepts them, and runs only the project's own tests.
public sealed class CodingAgentTests : IDisposable
{
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string _project;

    public CodingAgentTests()
    {
        _project = Path.Combine(_home, "nexus-code-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_project, "src"));
        File.WriteAllText(Path.Combine(_project, "src/app.py"), "def add(a, b):\n    return a - b\n");
        File.WriteAllText(Path.Combine(_project, ".env"), "SECRET=hunter2\n");
    }

    public void Dispose() => Directory.Delete(_project, true);

    // A model that answers with the given actions in turn, through Ollama's /api/chat.
    private static StubHandler Model(params string[] actions)
    {
        var turn = 0;
        return new StubHandler((_, _) =>
        {
            var action = actions[Math.Min(turn++, actions.Length - 1)];
            var body = JsonSerializer.Serialize(new { message = new { content = action }, done = true });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
    }

    private static async Task<List<JsonElement>> Events(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
    }

    [Fact]
    public async Task An_edit_is_staged_shown_as_a_diff_and_written_only_when_accepted_and_can_be_undone()
    {
        var model = Model(
            """{"tool":"read","path":"src/app.py"}""",
            """{"tool":"replace","path":"src/app.py","old":"return a - b","new":"return a + b"}""",
            """{"tool":"done","summary":"add subtracted; it adds now."}""");
        using var factory = new CoreApiFactory { AiHandler = model };
        var client = factory.Authorized();
        var start = await (await client.PostAsJsonAsync("/code/sessions", new { folder = _project })).Content.ReadFromJsonAsync<JsonElement>();
        var id = start.GetProperty("id").GetString();

        using var run = await client.PostAsJsonAsync($"/code/sessions/{id}/messages", new { text = "add is wrong, fix it" });
        var events = await Events(run);
        Assert.Equal(["step", "step", "changes", "message", "done"], events.Select(e => e.GetProperty("type").GetString()));
        var file = Assert.Single(events[2].GetProperty("files").EnumerateArray());
        Assert.Equal("src/app.py", file.GetProperty("path").GetString());
        Assert.Equal(1, file.GetProperty("added").GetInt32());
        Assert.Equal(1, file.GetProperty("removed").GetInt32());
        // Staged, not written.
        Assert.Contains("a - b", File.ReadAllText(Path.Combine(_project, "src/app.py")));
        // The model's request asked for JSON.
        Assert.Equal("json", JsonDocument.Parse(model.Bodies[0]).RootElement.GetProperty("format").GetString());

        (await client.PostAsJsonAsync($"/code/sessions/{id}/accept", new { files = (string[]?)null })).EnsureSuccessStatusCode();
        Assert.Contains("a + b", File.ReadAllText(Path.Combine(_project, "src/app.py")));
        (await client.PostAsJsonAsync($"/code/sessions/{id}/undo", new { })).EnsureSuccessStatusCode();
        Assert.Contains("a - b", File.ReadAllText(Path.Combine(_project, "src/app.py")));
    }

    [Fact]
    public async Task Secrets_git_and_paths_outside_the_project_are_refused()
    {
        var model = Model(
            """{"tool":"read","path":".env"}""",
            """{"tool":"read","path":"../../etc/passwd"}""",
            """{"tool":"write","path":".git/config","content":"x"}""",
            """{"tool":"done","summary":"ok"}""");
        using var factory = new CoreApiFactory { AiHandler = model };
        var client = factory.Authorized();
        var id = (await (await client.PostAsJsonAsync("/code/sessions", new { folder = _project })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var events = await Events(await client.PostAsJsonAsync($"/code/sessions/{id}/messages", new { text = "look around" }));
        Assert.DoesNotContain(events, e => e.GetProperty("type").GetString() == "step");
        var all = string.Join("\n", model.Bodies);
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("root:", all);
        Assert.Contains("Secret files (.env, keys) are off limits.", all);
        Assert.Contains("That path is outside the project.", all);
        Assert.False(Directory.Exists(Path.Combine(_project, ".git")));
    }

    [Fact]
    public async Task Only_folders_inside_home_can_be_opened()
    {
        using var factory = new CoreApiFactory();
        var client = factory.Authorized();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/code/sessions", new { folder = "/etc" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/code/sessions", new { folder = _home })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/code/sessions", new { folder = _project })).StatusCode);
    }

    [Fact]
    public async Task Tests_run_on_a_copy_with_the_staged_edits_using_the_projects_own_command()
    {
        File.WriteAllText(Path.Combine(_project, "Makefile"), "test:\n\tgrep -q 'a + b' src/app.py && echo '1 passed'\n");
        var model = Model(
            """{"tool":"replace","path":"src/app.py","old":"return a - b","new":"return a + b"}""",
            """{"tool":"run_tests"}""",
            """{"tool":"done","summary":"fixed"}""");
        using var factory = new CoreApiFactory { AiHandler = model };
        var client = factory.Authorized();
        var start = await (await client.PostAsJsonAsync("/code/sessions", new { folder = _project })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("make test", start.GetProperty("tests").GetString());
        var events = await Events(await client.PostAsJsonAsync($"/code/sessions/{start.GetProperty("id").GetString()}/messages", new { text = "fix and test" }));
        var tests = events.Single(e => e.TryGetProperty("tests", out _)).GetProperty("tests");
        Assert.True(tests.GetProperty("passed").GetBoolean());
        Assert.Equal("1 passed", tests.GetProperty("summary").GetString());
        Assert.Contains("a - b", File.ReadAllText(Path.Combine(_project, "src/app.py")));   // the real file is untouched
    }

    [Fact]
    public void A_reply_with_prose_around_the_action_is_still_understood()
    {
        var action = CodingAgent.ParseAction("Sure! {\"tool\":\"list\",\"path\":\".\"} Let me look.");
        Assert.Equal("list", action!.Value.GetProperty("tool").GetString());
        Assert.Null(CodingAgent.ParseAction("I will list the files."));
    }

    [Fact]
    public void The_diff_marks_added_and_removed_lines_with_context()
    {
        var lines = LineDiff.Compute("a\nb\nc\nd\ne\nf\ng\nh\ni\nj", "a\nb\nc\nd\nE\nf\ng\nh\ni\nj");
        Assert.Equal(["-", "+"], lines.Where(l => l.Mark != " ").Select(l => l.Mark));
        var shown = LineDiff.WithContext(lines, 1);
        Assert.Equal(["d", "e", "E", "f", ""], shown.Select(l => l.Mark == "…" ? "" : l.Text).Concat([""]).Take(5));
    }
}
