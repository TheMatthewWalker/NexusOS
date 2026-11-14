using Microsoft.Extensions.DependencyInjection;
using Xunit;

// inspect_system: Nexus's read-only look at the computer it runs on.
public sealed class SystemInspectorTests
{
    private sealed class FakeRunner(Dictionary<string, string> outputs) : ICommandRunner
    {
        public List<string> Ran { get; } = [];
        public string? Resolve(string binary) => outputs.ContainsKey(binary) ? "/fake/" + binary : null;
        public Task<CommandResult> RunAsync(string path, IReadOnlyList<string> args, TimeSpan timeout, int outputCap, CancellationToken ct)
        {
            Ran.Add(path + " " + string.Join(' ', args));
            return Task.FromResult(new CommandResult(ProbeStatus.Ok, 0, outputs[Path.GetFileName(path)], false));
        }
    }

    private static string Data(object result) => (string)result.GetType().GetProperty("data")!.GetValue(result)!;

    [Fact]
    public async Task A_topic_runs_only_its_fixed_commands_and_a_query_narrows_the_lines()
    {
        var runner = new FakeRunner(new() { ["ps"] = "PID USER %CPU COMM\n10 matt 91.0 firefox\n11 matt 0.1 kitty\n" });
        var inspector = new SystemInspector(runner, TestConfig.Of());
        var all = Data(await inspector.LookAsync("processes", null, null, CancellationToken.None));
        Assert.Contains("firefox", all);
        Assert.Contains("kitty", all);
        Assert.Equal(["/fake/ps -eo pid,user,pcpu,pmem,etime,comm --sort=-pcpu"], runner.Ran);
        var narrowed = Data(await inspector.LookAsync("processes", "FIRE", null, CancellationToken.None));
        Assert.Equal("10 matt 91.0 firefox", narrowed);
    }

    [Fact]
    public async Task Unknown_topics_are_refused_missing_tools_are_said_and_long_output_is_cut()
    {
        var runner = new FakeRunner(new() { ["free"] = string.Join('\n', Enumerable.Repeat(new string('x', 79), 100)) });
        var inspector = new SystemInspector(runner, TestConfig.Of());
        await Assert.ThrowsAsync<ArgumentException>(() => inspector.LookAsync("rm -rf", null, null, CancellationToken.None));
        var result = await inspector.LookAsync("memory", null, null, CancellationToken.None);
        Assert.Equal(SystemInspector.MaxChars, Data(result).Length);
        Assert.True((bool)result.GetType().GetProperty("truncated")!.GetValue(result)!);
        var none = new SystemInspector(new FakeRunner(new()), TestConfig.Of());
        Assert.Contains("(hyprctl is not installed)", Data(await none.LookAsync("windows", null, null, CancellationToken.None)));
    }

    [Fact]
    public async Task Folders_are_listed_only_inside_the_home_folder_and_never_where_keys_are()
    {
        var home = Directory.CreateTempSubdirectory("nexus-home-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "Documents", "Essays"));
            File.WriteAllText(Path.Combine(home, "Documents", "notes.txt"), "hello");
            File.WriteAllText(Path.Combine(home, "Documents", ".hidden"), "x");
            Directory.CreateDirectory(Path.Combine(home, ".ssh"));
            Directory.CreateSymbolicLink(Path.Combine(home, "out"), "/etc");
            var inspector = new SystemInspector(new FakeRunner(new()), TestConfig.Of(("NEXUS_HOME", home)));
            var listed = Data(await inspector.LookAsync("folder", null, "~/Documents", CancellationToken.None));
            Assert.Contains("Essays/", listed);
            Assert.Contains("notes.txt  (5 B", listed);
            Assert.DoesNotContain(".hidden", listed);
            foreach (var path in new[] { "/etc", "~/.ssh", "~/../other", "~/out", "~/.config/nexusos", "~/missing" })
                await Assert.ThrowsAsync<ArgumentException>(() => inspector.LookAsync("folder", null, path, CancellationToken.None));
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public void The_action_is_a_private_look_up_offered_with_every_question()
    {
        var action = NexusActions.ByName["inspect_system"];
        Assert.Equal(ActionSafety.Read, action.Safety);
        Assert.True(action.Private);                 // never sent to a cloud model
        Assert.Contains("inspect_system", ActionSelection.Always);
        Assert.Contains("inspect_system", AssistantContextBuilder.Guide);
        Assert.Contains("Look before you answer or act", AssistantContextBuilder.Guide);
    }
}
