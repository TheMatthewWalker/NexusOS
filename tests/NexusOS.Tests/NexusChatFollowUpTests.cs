using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// The chat's cost and its edges: which actions a question is offered and what
// that costs (ActionSelection.cs), cards with their whole text, Nexus's
// follow-up after "Do it", `nexus actions` from a terminal, tools on
// llama-server (--jinja), and malformed tool calls from the model.
public sealed class NexusChatFollowUpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nexus-chat-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static string Line(object value) => JsonSerializer.Serialize(value) + "\n";
    private static readonly string End = Line(new { message = new { content = "" }, done = true });
    private static string Says(string text) => Line(new { message = new { content = text }, done = false }) + End;
    private static string Calls(string name, object args) =>
        Line(new { message = new { content = "", tool_calls = new[] { new { function = new { name, arguments = args } } } }, done = false }) + End;
    private static string CallsRaw(string functionJson) =>
        "{\"message\":{\"content\":\"\",\"tool_calls\":[{\"function\":" + functionJson + "}]},\"done\":false}\n" + End;

    private static StubHandler Model(params string[] turns)
    {
        var next = 0;
        return new StubHandler((request, _) =>
        {
            var body = turns[Math.Min(next++, turns.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
    }

    private CoreApiFactory Factory(StubHandler model, params (string, string?)[] extra)
    {
        var apps = Directory.CreateDirectory(Path.Combine(_dir, "applications")).FullName;
        File.WriteAllText(Path.Combine(apps, "firefox.desktop"), "[Desktop Entry]\nType=Application\nName=Firefox\nExec=firefox\n");
        var settings = new List<(string, string?)>
        {
            ("NEXUS_ASSISTANT_NOTES", Path.Combine(_dir, "none")), ("NEXUS_APPLICATION_DIRS", apps),
            ("NEXUS_GUARDIAN_SOCKET", "/nonexistent/nexus-guardian.sock"), ("NEXUS_FIXIT_MODEL", "false")
        };
        settings.AddRange(extra);
        return new CoreApiFactory(settings.ToArray()) { AiHandler = model };
    }

    private static async Task<JsonElement[]> Stream(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToArray();
    }

    private static async Task<JsonElement[]> Ask(HttpClient client, string prompt) =>
        await Stream(await client.PostAsJsonAsync("/chat/stream", new { prompt }));

    private static IEnumerable<JsonElement> OfType(JsonElement[] events, string type) =>
        events.Where(e => e.GetProperty("type").GetString() == type);

    private static string Said(JsonElement[] events) =>
        string.Concat(OfType(events, "delta").Select(e => e.GetProperty("text").GetString()));

    private static JsonElement[] Messages(StubHandler stub, int request) =>
        JsonDocument.Parse(stub.Bodies[request]).RootElement.GetProperty("messages").EnumerateArray().Select(x => x.Clone()).ToArray();

    private static string[] ToolNames(StubHandler stub, int request) =>
        JsonDocument.Parse(stub.Bodies[request]).RootElement.TryGetProperty("tools", out var tools)
            ? tools.EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString()!).ToArray() : [];

    // ---- 1. fewer tokens per question ----

    // Includes inspect_system, offered with every question so Nexus can look before it acts.
    public const int SimpleQuestionBudget = 700;

    public static TheoryData<string, string[]> Representative => new()
    {
        { "My wifi is not working", ["diagnose_problem", "open_settings", "get_system_state"] },
        { "Install Spotify", ["install_app", "apt_install"] },
        { "Open the sound settings", ["open_settings", "change_setting"] },
        { "Fix the failing test in my project ~/projects/app", ["start_coding_task"] },
        { "Turn on remote login over SSH", ["change_system_option"] },
        { "Install htop for everyone on this computer", ["install_system_software"] },
        { "Make a picture of a fox in the snow", ["make_image"] },
        { "Every night at 22:00 turn on night light", ["create_automation", "change_setting"] },
        { "Are there security updates?", ["get_system_updates", "check_for_updates"] },
        { "How much memory does this computer have?", ["get_system_state"] },
    };

    [Theory]
    [MemberData(nameof(Representative))]
    public void A_question_is_offered_the_right_actions_within_budget(string question, string[] expected)
    {
        using var factory = Factory(Model(Says("x")));
        var tools = factory.Services.GetRequiredService<ActionRunner>().ToolsFor(ProviderNames.Local, question);
        var names = tools.Select(t => t.Name).ToArray();
        foreach (var name in expected) Assert.Contains(name, names);
        Assert.Contains("get_system_state", names);       // "this computer" is always there
        var tokens = ActionSelection.Tokens(tools);
        Assert.True(tokens <= SimpleQuestionBudget, $"'{question}': {tokens} tokens of tools ({string.Join(", ", names)})");
    }

    [Fact]
    public void A_plain_question_costs_almost_nothing_and_a_typical_one_a_fraction_of_the_list()
    {
        using var factory = Factory(Model(Says("x")));
        var runner = factory.Services.GetRequiredService<ActionRunner>();
        var plain = runner.ToolsFor(ProviderNames.Local, "What is the capital of France?");
        Assert.Equal(["get_system_state", "inspect_system"], plain.Select(t => t.Name));
        Assert.True(ActionSelection.Tokens(plain) < 220, $"{ActionSelection.Tokens(plain)} tokens");
        // Every action at once, as every question used to carry (by this estimate, which counts JSON generously).
        var all = ActionSelection.Tokens(runner.ToolsFor(ProviderNames.Local));
        var typical = Representative.Select(row => ActionSelection.Tokens(runner.ToolsFor(ProviderNames.Local, (string)row[0]))).Average();
        Assert.True(typical < all / 3.0, $"typical {typical:0} of {all} tokens");
    }

    [Fact]
    public void A_short_follow_up_keeps_the_actions_of_the_question_before()
    {
        var names = ActionSelection.NamesFor("yes please, do that", "Is there a Spotify app I could install?");
        Assert.Contains("install_app", names);
        // A full new question does not drag the old groups along.
        Assert.DoesNotContain("install_app", ActionSelection.NamesFor("What is the capital of France and why is it there?",
            "Is there a Spotify app I could install?"));
    }

    [Fact]
    public void Every_action_is_in_a_group_or_always_offered()
    {
        // A question touching every group is offered every action: none is unreachable.
        var everything = ActionSelection.NamesFor(
            "install an app, a web app, a game, dual-boot, remove, try, health, settings, fix it, find files, every day, today, updates, a picture, describe the screen, code, workspace, move in, system, large model")
            .Concat(ActionSelection.FollowOn.Values.SelectMany(x => x)).Distinct();   // apply_fix comes after diagnose_problem
        Assert.Equal(NexusActions.All.Select(a => a.Name).Order(), everything.Order());
        // A plain question gets only "this computer": every action has keywords.
        Assert.Equal(["get_system_state", "inspect_system"], ActionSelection.NamesFor("hello"));
    }

    [Fact]
    public async Task A_look_up_brings_its_next_steps_with_it()
    {
        var stub = Model(Calls("list_installed_apps", new { }), Says("You have Firefox."));
        using var factory = Factory(stub);
        await Ask(factory.Authorized(), "which programs can I open?");
        Assert.DoesNotContain("remove_app", ToolNames(stub, 0));
        Assert.Contains("remove_app", ToolNames(stub, 1));        // after the list, removing one makes sense
    }

    [Fact]
    public async Task The_model_can_still_call_an_action_it_was_not_offered_and_it_is_checked_as_usual()
    {
        var stub = Model(Calls("open_app", new { app = "firefox" }));
        using var factory = Factory(stub);
        var events = await Ask(factory.Authorized(), "hello");
        Assert.DoesNotContain("open_app", ToolNames(stub, 0));
        Assert.Equal("Open Firefox", OfType(events, "action").Single().GetProperty("summary").GetString());
    }

    // ---- 2. cards show the whole text ----

    [Fact]
    public async Task A_card_shows_the_whole_task_not_the_first_80_characters()
    {
        var project = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "nexus-test-project-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var task = "Add a dark mode toggle to the settings page, remember the choice in local storage, " +
                       "and write a test that checks the toggle switches the theme class on the body element.";
            var stub = Model(Calls("start_coding_task", new { folder = project, task }));
            using var factory = Factory(stub);
            var action = OfType(await Ask(factory.Authorized(), "add dark mode to my project"), "action").Single();
            Assert.Equal(task, action.GetProperty("detail").GetString());
            Assert.False(action.GetProperty("detailTruncated").GetBoolean());
            Assert.StartsWith("Ask Code to work in ", action.GetProperty("summary").GetString());
            Assert.DoesNotContain("dark mode", action.GetProperty("summary").GetString());
        }
        finally { Directory.Delete(project, true); }
    }

    [Fact]
    public void A_card_keeps_a_sane_maximum_and_says_it_was_cut()
    {
        var pending = new PendingActions(TimeProvider.System);
        var action = pending.Add(NexusActions.ByName["make_image"], new JsonObject(), "Make a picture", detail: new string('a', 1500));
        var ev = JsonSerializer.SerializeToElement(action.Event());
        Assert.Equal(PendingAction.MaxCardDetail, ev.GetProperty("detail").GetString()!.Length);
        Assert.True(ev.GetProperty("detailTruncated").GetBoolean());
        Assert.Equal(1500, ev.GetProperty("detailLength").GetInt32());
        // History, the journal and the command line get one short line.
        Assert.True(action.Line.Length < 120);
    }

    // ---- 3. the follow-up after "Do it" ----

    [Fact]
    public async Task After_Do_it_Nexus_says_what_happened_in_one_turn_without_tools()
    {
        var stub = Model(Calls("change_setting", new { setting = "night_light", value = "on" }),
            Says("Done: night light is on. Want it on every evening?"));
        using var factory = Factory(stub);
        var client = factory.Authorized();
        var id = OfType(await Ask(client, "turn on night light"), "action").Single().GetProperty("id").GetString()!;
        Assert.Equal("perform", (await (await client.PostAsync($"/actions/{id}/confirm", null)).Content
            .ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());

        // The shell did it and says so; Nexus answers in one short turn.
        var events = await Stream(await client.PostAsJsonAsync($"/actions/{id}/followup", new { ok = true, message = "Done. Undo it in Settings, Timeline." }));
        Assert.Equal("Done: night light is on. Want it on every evening?", Said(events));
        Assert.Equal("done", events[^1].GetProperty("type").GetString());
        Assert.Equal(2, stub.Requests.Count);
        Assert.Empty(ToolNames(stub, 1));                         // no tools: it cannot start a loop
        var told = Messages(stub, 1)[^1].GetProperty("content").GetString()!;
        Assert.Contains("Turn night light on", told);
        Assert.Contains("<<<DATA", told);                          // what happened is data, not instructions
        Assert.Contains("Undo it in Settings", told);

        // Once only, and it is in the conversation for the next question.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/actions/{id}/followup", new { })).StatusCode);
        var history = (await client.GetFromJsonAsync<JsonElement[]>("/history"))!;
        Assert.Contains(history, h => h.GetProperty("answer").GetString()!.Contains("every evening"));
    }

    [Fact]
    public async Task A_failure_is_followed_up_too_and_unconfirmed_offers_are_not()
    {
        var stub = Model(Calls("open_app", new { app = "firefox" }), Says("Firefox did not open."));
        using var factory = Factory(stub);
        var client = factory.Authorized();
        var id = OfType(await Ask(client, "open firefox"), "action").Single().GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/actions/{id}/followup", new { })).StatusCode);
        (await client.PostAsync($"/actions/{id}/confirm", null)).EnsureSuccessStatusCode();
        await Stream(await client.PostAsJsonAsync($"/actions/{id}/followup", new { ok = false, message = "That app is not installed." }));
        var told = Messages(stub, 1)[^1].GetProperty("content").GetString()!;
        Assert.Contains("\"status\":\"failed\"", told);
        Assert.Contains("not installed", told);
    }

    // ---- 4. `nexus actions` from a terminal ----

    [Fact]
    public async Task A_terminal_lists_confirms_and_dismisses_offers()
    {
        // The rule parser's own model call (/api/generate) fails to parse, so the keyword rules make the draft.
        string[] chat = [Calls("create_automation", new { sentence = "At 22:00 every day, turn on night light" }),
            Calls("change_setting", new { setting = "volume", value = 30 }), Says("Saved: it runs every day at 22:00.")];
        var turn = 0;
        var stub = new StubHandler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/api/generate" ? """{"response":"not json"}"""
                : chat[Math.Min(turn++, chat.Length - 1)])
        }));
        using var factory = Factory(stub);
        var client = factory.Authorized();
        await Ask(client, "every night at ten turn on night light");
        await Ask(client, "volume 30");

        var output = new StringWriter();
        Assert.Equal(0, await ActionsCommand.RunAsync(client, ["list"], output, new StringWriter()));
        var listed = output.ToString();
        Assert.Contains("Create an automation: Every day at 22:00", listed);
        Assert.Contains("Set the volume to 30%", listed);
        var pending = (await client.GetFromJsonAsync<JsonElement[]>("/actions/pending"))!;
        var rule = pending.Single(p => p.GetProperty("action").GetString() == "create_automation").GetProperty("id").GetString()!;
        var volume = pending.Single(p => p.GetProperty("action").GetString() == "change_setting").GetProperty("id").GetString()!;

        // Core does this one itself; then Nexus says what happened.
        output = new StringWriter();
        Assert.Equal(0, await ActionsCommand.RunAsync(client, ["confirm", rule], output, new StringWriter()));
        Assert.Contains("Saved.", output.ToString());
        Assert.Contains("Saved: it runs every day at 22:00.", output.ToString());
        Assert.Single((await client.GetFromJsonAsync<JsonElement[]>("/rules"))!);

        // The desktop does this one: without a desktop running, it says so and keeps waiting.
        var error = new StringWriter();
        Assert.Equal(1, await ActionsCommand.RunAsync(client, ["confirm", volume], new StringWriter(), error));
        Assert.Contains("desktop", error.ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/actions/{volume}")).StatusCode);

        // With the desktop collecting, it is handed over once.
        Assert.Empty((await (await client.PostAsync("/actions/desktop/collect", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray());
        output = new StringWriter();
        Assert.Equal(0, await ActionsCommand.RunAsync(client, ["confirm", volume], output, new StringWriter()));
        Assert.Contains("Handed to the desktop: Set the volume to 30%", output.ToString());
        var items = (await (await client.PostAsync("/actions/desktop/collect", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().ToArray();
        var item = Assert.Single(items);
        Assert.Equal("volume.set", item.GetProperty("perform").GetProperty("op").GetString());
        Assert.Empty((await (await client.PostAsync("/actions/desktop/collect", null)).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task A_terminal_can_dismiss_an_offer()
    {
        var stub = Model(Calls("change_setting", new { setting = "blur", value = "off" }));
        using var factory = Factory(stub);
        var client = factory.Authorized();
        var id = OfType(await Ask(client, "turn blur off"), "action").Single().GetProperty("id").GetString()!;
        var output = new StringWriter();
        Assert.Equal(0, await ActionsCommand.RunAsync(client, ["dismiss", id], output, new StringWriter()));
        Assert.Contains("Nothing was done", output.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/actions/{id}")).StatusCode);
        Assert.Equal(1, await ActionsCommand.RunAsync(client, ["confirm", id], new StringWriter(), new StringWriter()));
        Assert.Equal(2, await ActionsCommand.RunAsync(client, ["frobnicate"], new StringWriter(), new StringWriter()));
    }

    // ---- 5. llama.cpp: --jinja, and no tools for a server without it ----

    [Fact]
    public async Task An_unmanaged_server_that_refuses_tools_is_not_asked_again_for_a_while()
    {
        var stub = new StubHandler((request, ct) =>
        {
            var withTools = request.Content!.ReadAsStringAsync(ct).Result.Contains("\"tools\"");
            var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"Plain.\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
            return Task.FromResult(withTools
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":"tools param requires --jinja flag"}""") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) });
        });
        using var factory = Factory(stub);
        var client = factory.Authorized();
        (await client.PostAsJsonAsync("/providers/select", new { name = "llama-cpp" })).EnsureSuccessStatusCode();
        Assert.Equal("Plain.", Said(await Ask(client, "open settings")));
        Assert.Equal("Plain.", Said(await Ask(client, "open settings")));
        Assert.Equal(3, stub.Requests.Count);       // refused once, then asked without tools straight away
    }

    // ---- 6. malformed tool calls ----

    [Fact]
    public void Arguments_that_are_not_valid_text_are_refused_not_thrown()
    {
        var error = Assert.Throws<ActionRejectedException>(() => NexusActions.Validate(NexusActions.ByName["open_app"], "{\"app\":\"\ud800\"}"));
        Assert.True(error.Malformed);
        // Unreadable pieces of a streamed call mark the call instead of failing the stream.
        var parser = new OpenAiTurnParser();
        parser.Feed("""data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"open_app","arguments":"{\"app\":\"\ud800\"}"}}]}}]}""");
        parser.Feed("""data: {"choices":[{"delta":{"tool_calls":["junk",{"index":"x","function":{"name":"get_settings","arguments":7}}]}}]}""");
        Assert.All(parser.Calls, c => Assert.True(c.Malformed));
        Assert.Equal(2, parser.Calls.Count);
        // Echoed back to the server, a malformed call's arguments are an empty object.
        var wire = JsonSerializer.Serialize(ModelTools.OpenAiMessages([new TurnMessage("assistant", "", parser.Calls)]));
        Assert.Contains("\"arguments\":\"{}\"", wire);
    }

    [Fact]
    public async Task A_malformed_call_is_answered_once_and_the_answer_goes_on()
    {
        var stub = Model(CallsRaw("""{"name":"open_app","arguments":"{\"app\":\"\ud800\"}"}"""), Says("Which app did you mean?"));
        using var factory = Factory(stub);
        var events = await Ask(factory.Authorized(), "open the thing");
        Assert.Equal("Which app did you mean?", Said(events));
        Assert.Equal("done", events[^1].GetProperty("type").GetString());
        Assert.Empty(OfType(events, "error"));
        Assert.Contains("malformed", Messages(stub, 1)[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task A_model_that_keeps_sending_malformed_calls_is_made_to_answer()
    {
        var stub = Model(CallsRaw("""{"name":"open_app","arguments":12}"""), CallsRaw("""{"name":"open_app","arguments":"not json"}"""),
            Says("Sorry, I could not do that."));
        using var factory = Factory(stub);
        var events = await Ask(factory.Authorized(), "open an app");
        Assert.Equal(3, stub.Requests.Count);
        Assert.NotEmpty(ToolNames(stub, 1));        // told once, may try again
        Assert.Empty(ToolNames(stub, 2));           // then it has to answer
        Assert.Equal("Sorry, I could not do that.", Said(events));
    }
}

public class ActionSelectionTimeoutTests
{
    [Fact]
    public void A_match_that_runs_out_of_time_offers_the_group_instead_of_failing()
    {
        // (a+)+$ on a long run of a's ending in b backtracks far past 1 ms.
        var slow = new System.Text.RegularExpressions.Regex("(a+)+$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(1));
        Assert.True(ActionSelection.Matches(slow, new string('a', 40) + "b"));
        var quick = new System.Text.RegularExpressions.Regex("x", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        Assert.False(ActionSelection.Matches(quick, "abc"));
    }

    [Fact]
    public void A_very_long_question_still_gets_actions()
    {
        var names = ActionSelection.NamesFor(string.Join(" ", Enumerable.Repeat("please turn on night light and find my files", 2000)));
        Assert.Contains("change_setting", names);
        Assert.Contains("get_system_state", names);
    }
}
