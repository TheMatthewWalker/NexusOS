using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

// The coding agent (CodingAgent.cs): it stays inside the project and away from
// secrets, stages edits until the person accepts them, and only offers to run
// the project's own tests; they run on the person's Run, once, in a sandbox.
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

    // bubblewrap for the sandbox tests: NEXUS_BWRAP, the PATH, or the Nix store. Null when this machine has none.
    private static readonly Lazy<string?> TestBwrap = new(() =>
        CodingAgent.FindBwrap(Environment.GetEnvironmentVariable("NEXUS_BWRAP")) ??
        (Directory.Exists("/nix/store")
            ? Directory.EnumerateDirectories("/nix/store", "*-bubblewrap-*").Select(d => Path.Combine(d, "bin/bwrap")).FirstOrDefault(File.Exists)
            : null));

    private async Task<(HttpClient Client, string Id, List<JsonElement> Events)> FixAndAskForTests(CoreApiFactory factory)
    {
        File.WriteAllText(Path.Combine(_project, "Makefile"), "test:\n\tgrep -q 'a + b' src/app.py && echo '1 passed'\n");
        var client = factory.Authorized();
        var start = await (await client.PostAsJsonAsync("/code/sessions", new { folder = _project })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("make test", start.GetProperty("tests").GetString());
        var id = start.GetProperty("id").GetString()!;
        var events = await Events(await client.PostAsJsonAsync($"/code/sessions/{id}/messages", new { text = "fix and test" }));
        return (client, id, events);
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient client, string session, string run) =>
        client.PostAsJsonAsync($"/code/sessions/{session}/runs/{run}/confirm", new { });

    [Fact]
    public async Task The_agent_only_offers_a_test_run_and_the_persons_Run_runs_it_once_in_the_sandbox()
    {
        var model = Model(
            """{"tool":"replace","path":"src/app.py","old":"return a - b","new":"return a + b"}""",
            """{"tool":"run_tests"}""",
            """{"tool":"done","summary":"fixed"}""");
        using var factory = new CoreApiFactory(("NEXUS_BWRAP", TestBwrap.Value ?? "/nonexistent/bwrap")) { AiHandler = model };
        var (client, id, events) = await FixAndAskForTests(factory);

        // Offered as a card with the exact command; nothing ran.
        var card = events.Single(e => e.TryGetProperty("run", out _)).GetProperty("run");
        Assert.Equal("make test", card.GetProperty("command").GetString());
        Assert.Equal(["src/app.py"], card.GetProperty("files").EnumerateArray().Select(f => f.GetString()));
        Assert.DoesNotContain(events, e => e.TryGetProperty("kind", out var k) && k.GetString() is "pass" or "fail");
        Assert.Contains("You cannot run it yourself", model.Bodies[2]);
        var run = card.GetProperty("id").GetString()!;
        Assert.Equal(32, run.Length);

        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, id, new string('0', 32))).StatusCode);   // a guessed id
        using var first = await Confirm(client, id, run);
        if (TestBwrap.Value is null)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
            Assert.Contains("bubblewrap", await first.Content.ReadAsStringAsync());
        }
        else
        {
            first.EnsureSuccessStatusCode();
            var result = await first.Content.ReadFromJsonAsync<JsonElement>();
            Assert.True(result.GetProperty("passed").GetBoolean(), result.GetProperty("output").GetString());
            Assert.Equal("1 passed", result.GetProperty("summary").GetString());
            Assert.False(result.GetProperty("truncated").GetBoolean());
            // The result reaches the model with the next request.
            await Events(await client.PostAsJsonAsync($"/code/sessions/{id}/messages", new { text = "thanks" }));
            var turns = JsonDocument.Parse(model.Bodies[^1]).RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString());
            Assert.Contains(turns, t => t!.StartsWith("The person ran `make test` in the sandbox. Tests passed.", StringComparison.Ordinal));
        }
        Assert.Contains("a - b", File.ReadAllText(Path.Combine(_project, "src/app.py")));   // the real file is untouched
        // Single use: the same id never runs twice.
        Assert.Equal(HttpStatusCode.Conflict, (await Confirm(client, id, run)).StatusCode);
    }

    [Fact]
    public async Task Without_bubblewrap_nothing_runs()
    {
        var model = Model(
            """{"tool":"replace","path":"src/app.py","old":"return a - b","new":"return a + b"}""",
            """{"tool":"run_tests"}""",
            """{"tool":"done","summary":"fixed"}""");
        using var factory = new CoreApiFactory(("NEXUS_BWRAP", "/nonexistent/bwrap")) { AiHandler = model };
        var (client, id, events) = await FixAndAskForTests(factory);
        var run = events.Single(e => e.TryGetProperty("run", out _)).GetProperty("run").GetProperty("id").GetString()!;
        using var response = await Confirm(client, id, run);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not installed, so they were not run", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_run_offer_is_void_once_the_changes_move_on()
    {
        var model = Model(
            """{"tool":"replace","path":"src/app.py","old":"return a - b","new":"return a + b"}""",
            """{"tool":"run_tests"}""",
            """{"tool":"write","path":"Makefile","content":"test:\n\tcurl https://example.com\n"}""",
            """{"tool":"done","summary":"fixed"}""");
        using var factory = new CoreApiFactory(("NEXUS_BWRAP", TestBwrap.Value ?? "/nonexistent/bwrap")) { AiHandler = model };
        var (client, id, events) = await FixAndAskForTests(factory);
        var run = events.Single(e => e.TryGetProperty("run", out _)).GetProperty("run").GetProperty("id").GetString()!;
        using var response = await Confirm(client, id, run);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not the ones this run was offered for", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_staged_change_to_the_test_setup_is_named_on_the_card()
    {
        var model = Model(
            """{"tool":"write","path":"Makefile","content":"test:\n\techo '1 passed'\n"}""",
            """{"tool":"run_tests"}""",
            """{"tool":"done","summary":"fixed"}""");
        using var factory = new CoreApiFactory { AiHandler = model };
        var (_, _, events) = await FixAndAskForTests(factory);
        var card = events.Single(e => e.TryGetProperty("run", out _)).GetProperty("run");
        Assert.Equal(["Makefile"], card.GetProperty("affects").EnumerateArray().Select(f => f.GetString()));
    }

    [Fact]
    public void The_sandbox_has_no_network_and_binds_nothing_of_the_home_folder_but_the_project()
    {
        var home = _home.TrimEnd('/');
        var argv = CodingAgent.SandboxArgv(_project, "/tmp/nexus-code-copy", ["node_modules"], "make test", home);
        foreach (var flag in new[] { "--unshare-net", "--unshare-pid", "--die-with-parent", "--new-session" }) Assert.Contains(flag, argv);
        Assert.Equal(["--", "sh", "-c", "make test"], argv.TakeLast(4));
        var binds = new[] { "--bind", "--bind-try", "--ro-bind", "--ro-bind-try", "--dev-bind", "--dev-bind-try" };
        for (var i = 0; i < argv.Count - 2; i++)
        {
            if (!binds.Contains(argv[i])) continue;
            var source = argv[i + 1];
            Assert.NotEqual(home, source.TrimEnd('/'));
            Assert.False(source.StartsWith(home + "/", StringComparison.Ordinal) && !source.StartsWith(_project + "/", StringComparison.Ordinal),
                $"{argv[i]} {source} exposes the home folder");
        }
        Assert.Contains(("--ro-bind", "/nix/store"), argv.Zip(argv.Skip(1)));
        Assert.Contains(("--tmpfs", home), argv.Zip(argv.Skip(1)));          // the home folder is an empty tmpfs
        Assert.Contains(("--tmpfs", "/tmp"), argv.Zip(argv.Skip(1)));
        Assert.Contains(("--bind", "/tmp/nexus-code-copy"), argv.Zip(argv.Skip(1)));
        Assert.Contains(("--ro-bind", Path.Combine(_project, "node_modules")), argv.Zip(argv.Skip(1)));
        // Core's own settings (API tokens and the like) do not reach the tests.
        Assert.DoesNotContain(CodingAgent.SandboxEnvironment(home).Keys, k => k.StartsWith("NEXUS", StringComparison.Ordinal));
    }

    // Runs a command in the real sandbox over a copy of the project.
    private async Task<SandboxResult> Sandboxed(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var copy = Directory.CreateTempSubdirectory("nexus-code-test-").FullName;
        try
        {
            var dependencies = CodingAgent.CopyTree(_project, copy);
            var home = _home.TrimEnd('/');
            return await CodingAgent.RunSandboxedAsync(TestBwrap.Value!, CodingAgent.SandboxArgv(_project, copy, dependencies, command, home),
                CodingAgent.SandboxEnvironment(home), timeout, ct);
        }
        finally { Directory.Delete(copy, true); }
    }

    [Fact]
    public async Task In_the_sandbox_there_is_no_network_no_home_folder_and_the_project_is_a_copy()
    {
        if (!OperatingSystem.IsLinux() || TestBwrap.Value is null) return;
        var secret = Path.Combine(_home, "nexus-code-secret-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(secret, "hunter2\n");
        try
        {
            var result = await Sandboxed($"cat '{secret}' 2>/dev/null; echo changed > src/app.py; grep -c : /proc/net/dev; cat src/app.py", TimeSpan.FromSeconds(30));
            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("hunter2", result.Output);
            Assert.Equal(["1", "changed"], result.Output.Trim().Split('\n').TakeLast(2));   // only loopback; the copy changed
            Assert.Contains("a - b", File.ReadAllText(Path.Combine(_project, "src/app.py")));     // the project did not
        }
        finally { File.Delete(secret); }
    }

    [Fact]
    public async Task Output_is_capped_and_says_it_was_truncated()
    {
        if (!OperatingSystem.IsLinux() || TestBwrap.Value is null) return;
        var result = await Sandboxed("head -c 1000000 /dev/zero | tr '\\0' a; echo; echo the-end", TimeSpan.FromSeconds(30));
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.True(result.Output.Length <= CodingAgent.MaxOutputChars + 200, $"{result.Output.Length} characters kept");
        Assert.StartsWith("(output truncated", result.Output);
        Assert.EndsWith("the-end", result.Output.Trim());                                   // the end is what is kept
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task On_timeout_or_stop_every_process_the_tests_started_is_killed(bool timeout)
    {
        if (!OperatingSystem.IsLinux() || TestBwrap.Value is null) return;
        var marker = Random.Shared.Next(100000, 999999).ToString();
        using var stop = new CancellationTokenSource();
        if (!timeout) stop.CancelAfter(TimeSpan.FromSeconds(1));
        // One sleeper leaves the process group (setsid), one is a background child, one is in front.
        var result = await Sandboxed($"(setsid sleep {marker} &); sleep {marker} & sleep {marker}", timeout ? TimeSpan.FromSeconds(1) : TimeSpan.FromMinutes(1), stop.Token);
        Assert.True(timeout == result.TimedOut, $"exit {result.ExitCode}: {result.Output}");
        Assert.Equal(!timeout, result.Stopped);
        Assert.Null(result.ExitCode);
        bool Alive() => Directory.EnumerateDirectories("/proc").Any(d =>
        {
            try { return File.ReadAllText(Path.Combine(d, "cmdline")).Replace('\0', ' ').Contains("sleep " + marker); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        });
        for (var i = 0; i < 20 && Alive(); i++) await Task.Delay(100);
        Assert.False(Alive(), "a sleeper outlived the sandbox");
    }

    [Fact]
    public void A_test_copy_leaves_out_git_dependencies_and_build_output()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".git"));
        File.WriteAllText(Path.Combine(_project, ".git/HEAD"), "ref: x\n");
        Directory.CreateDirectory(Path.Combine(_project, "node_modules/left-pad"));
        File.WriteAllText(Path.Combine(_project, "node_modules/left-pad/index.js"), "x\n");
        Directory.CreateDirectory(Path.Combine(_project, "obj"));
        File.WriteAllText(Path.Combine(_project, "obj/big.bin"), "x\n");
        var copy = Directory.CreateTempSubdirectory("nexus-code-test-").FullName;
        try
        {
            Assert.Equal(["node_modules"], CodingAgent.CopyTree(_project, copy));
            Assert.True(File.Exists(Path.Combine(copy, "src/app.py")));
            Assert.False(Directory.Exists(Path.Combine(copy, ".git")));
            Assert.False(Directory.Exists(Path.Combine(copy, "obj")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(copy, "node_modules")));   // a mount point; shown read-only
        }
        finally { Directory.Delete(copy, true); }
    }

    [Theory]
    [InlineData("~/.ssh")]
    [InlineData("~/.gnupg")]
    [InlineData("~/.aws")]
    [InlineData("~/.config")]
    [InlineData("~/.config/nexusos")]
    [InlineData("~/.local/share/keyrings")]
    [InlineData("~/.local")]
    [InlineData("~/.mozilla/firefox")]
    public async Task Folders_for_keys_passwords_and_settings_cannot_be_opened(string folder)
    {
        Assert.Throws<CodingException>(() => CodingAgent.ProjectRoot(folder));
        using var factory = new CoreApiFactory();
        using var response = await factory.Authorized().PostAsJsonAsync("/code/sessions", new { folder });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void A_project_reached_through_a_link_cannot_be_opened()
    {
        var link = Path.Combine(_home, "nexus-code-link-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateSymbolicLink(link, _project);
        try { Assert.Throws<CodingException>(() => CodingAgent.ProjectRoot(Path.Combine(link, "src"))); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task Files_holding_secrets_and_key_folders_inside_the_project_never_reach_the_model()
    {
        Directory.CreateDirectory(Path.Combine(_project, ".ssh"));
        File.WriteAllText(Path.Combine(_project, ".ssh/config"), "Host x\n  IdentityFile key-in-ssh-dir\n");
        File.WriteAllText(Path.Combine(_project, "src/settings.py"), "DEBUG = True\nAPI_KEY = \"sk-live-abcdefghijklmnopqrstuvwxyz0123\"\n");
        var model = Model(
            """{"tool":"read","path":".ssh/config"}""",
            """{"tool":"read","path":"src/settings.py"}""",
            """{"tool":"search","pattern":"API_KEY"}""",
            """{"tool":"list","path":"."}""",
            """{"tool":"done","summary":"ok"}""");
        using var factory = new CoreApiFactory { AiHandler = model };
        var client = factory.Authorized();
        var id = (await (await client.PostAsJsonAsync("/code/sessions", new { folder = _project })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await Events(await client.PostAsJsonAsync($"/code/sessions/{id}/messages", new { text = "look around" }));
        var all = string.Join("\n", model.Bodies);
        Assert.DoesNotContain("key-in-ssh-dir", all);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123", all);
        Assert.DoesNotContain(".ssh/", all.Split("Result of list")[^1]);
        Assert.Contains("Folders for keys and passwords are off limits.", all);
        Assert.Contains("looks like it holds a key, token or password", all);
        Assert.Contains("No matches.", all);
    }

    [Theory]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEow")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE")]
    [InlineData("aws_secret_access_key=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")]
    [InlineData("GITHUB_TOKEN: ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnop")]
    [InlineData("password = \"hunter22\"")]
    [InlineData("{\"token\": \"abcdef123456\"}")]
    [InlineData("db:\n  passwd: 'correct-horse'")]
    [InlineData("DB_PASSWORD=hunter22")]
    [InlineData("export API_TOKEN=abcdefgh")]
    [InlineData("key = \"sk-ant-api03-abcdefghijklmnopqrstuv\"")]
    public void Secrets_in_text_are_recognised(string text)
    {
        Assert.True(CodingAgent.LooksSecret(text));
        Assert.Contains("[secret hidden]", CodingAgent.Redact(text));
    }

    [Theory]
    [InlineData("password = request.form['password']")]
    [InlineData("token = get_token()")]
    [InlineData("def check_password(pw):\n    return pw == stored")]
    [InlineData("PASSWORD=$DB_PASSWORD")]
    [InlineData("token: \"${TOKEN}\"")]
    [InlineData("return a - b")]
    public void Ordinary_code_is_not_taken_for_a_secret(string text) => Assert.False(CodingAgent.LooksSecret(text));

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.local", true)]
    [InlineData("prod.env", true)]
    [InlineData("id_ed25519", true)]
    [InlineData("id_rsa.pub", true)]
    [InlineData("server.key", true)]
    [InlineData("cert.pem", true)]
    [InlineData(".git-credentials", true)]
    [InlineData("secrets.yaml", true)]
    [InlineData("terraform.tfstate", true)]
    [InlineData("app.py", false)]
    [InlineData("keys.py", false)]
    [InlineData("environment.ts", false)]
    public void Secret_files_are_recognised_by_name(string name, bool secret) => Assert.Equal(secret, CodingAgent.IsSecretName(name));

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
