using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The Nexus coding agent (design: "Coding agent A · Conversation and diff
// side by side"). The person picks a project folder in their home and asks
// for a change; the selected model works through it one action at a time:
//   list, read, search      look around the project (never outside it)
//   write, replace          stage an edit: nothing on disk changes yet
//   run_tests               ask the person to run the project's own test
//                           command (detected here, never chosen by the
//                           model): a card with the exact command and Run
//   done                    explain what it did
// The person sees every step, then the diff, and accepts or rejects it.
// Accepting writes the files and can be undone while the session is open.
// The agent never runs anything itself: a test run happens only when the
// person clicks Run on that card (a single-use id, for exactly the staged
// edits it was offered with), on a copy of the project with the edits, inside
// a bubblewrap sandbox with no network and no home folder beyond the project.
// The agent never reads secrets (.env files, keys, files holding tokens or
// passwords), never opens folders that hold them (~/.ssh, ~/.gnupg, …) and
// cannot reach past the project folder.
public sealed class CodingSession
{
    public required string Id { get; init; }
    public required string Root { get; init; }
    public string? TestCommand { get; init; }
    public TestProposal? Proposal { get; set; }                                             // the run offered to the person, if any
    public CancellationTokenSource? TestRun { get; set; }                                   // the run in progress, for Stop
    public List<ChatMessage> Turns { get; } = [];
    public Dictionary<string, string> Staged { get; } = new(StringComparer.Ordinal);       // relative path -> new content
    public Dictionary<string, string?> Original { get; } = new(StringComparer.Ordinal);    // before staging (null = new file)
    public Dictionary<string, string?> Undo { get; } = new(StringComparer.Ordinal);        // after accept
    public SemaphoreSlim Busy { get; } = new(1, 1);
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
}

// A test run offered to the person: what runs, on which staged edits.
public sealed record TestProposal(string Id, string Command, string[] Files, string[] Affects, string Fingerprint, DateTimeOffset Created);

public sealed record SandboxResult(int? ExitCode, string Output, bool Truncated, bool TimedOut, bool Stopped);

public sealed partial class CodingAgent(ModelRouter router, ActivityJournal journal, IConfiguration config, ILogger<CodingAgent> log)
{
    public const int MaxSteps = 24, MaxFileBytes = 64 * 1024, MaxReadChars = 24000, MaxResultChars = 6000, MaxSessions = 8;
    public const int MaxOutputChars = 256 * 1024, MaxCopyFiles = 20000;
    public const long MaxCopyBytes = 256L << 20;
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5), ProposalLifetime = TimeSpan.FromHours(1);
    private static readonly string[] Skipped = [".git", "node_modules", ".venv", "venv", "__pycache__", "target", "bin", "obj", "dist", "build", ".next", ".cache", "result"];
    // Not copied for a test run: version control, and caches or build output the tests make again.
    private static readonly string[] NotCopied = [".git", "__pycache__", ".pytest_cache", ".mypy_cache", ".ruff_cache", ".tox", ".cache", ".next", "obj", "target", "result"];
    // Installed dependencies: shown to the tests read-only from the project instead of copied, and never edited.
    private static readonly string[] Dependencies = ["node_modules", ".venv", "venv"];
    // In the home folder: keys, passwords, browser profiles and Nexus's own state. No project is, is inside or holds one of these.
    private static readonly string[] Sensitive =
        [".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".password-store", ".pki", ".mozilla", ".thunderbird",
         ".config/nexusos", ".config/gh", ".config/gcloud", ".config/chromium", ".config/google-chrome", ".config/BraveSoftware",
         ".config/Signal", ".local/share/keyrings", ".local/share/nexusos", ".local/state/nexusos"];
    // Inside a project, folders by these names are never entered.
    private static readonly string[] SecretDirs = [".ssh", ".gnupg", ".aws", ".password-store", "keyrings"];
    private readonly ConcurrentDictionary<string, CodingSession> _sessions = new();
    public string? Bwrap { get; } = FindBwrap(config["NEXUS_BWRAP"]);

    // ---- sessions ----
    // A folder inside the home folder: not the home folder itself, not through a link, and not a place for keys or settings.
    public static string ProjectRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.Contains('\0')) throw new CodingException("Choose a project folder inside your home folder.");
        var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var expanded = folder.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, folder[2..]) : folder == "~" ? home : folder;
        var root = Path.GetFullPath(expanded).TrimEnd('/');
        if (!(root + "/").StartsWith(home + "/", StringComparison.Ordinal) || root == home)
            throw new CodingException("Choose a project folder inside your home folder.");
        var rel = Path.GetRelativePath(home, root);
        if (Sensitive.Any(d => rel == d || rel.StartsWith(d + "/", StringComparison.Ordinal) || d.StartsWith(rel + "/", StringComparison.Ordinal)))
            throw new CodingException("That folder holds keys, passwords or settings, so Code does not open it. Choose a project folder.");
        if (!Directory.Exists(root)) throw new CodingException("That folder does not exist.");
        for (var p = root; p.Length > home.Length; p = Path.GetDirectoryName(p)!)
            if (new DirectoryInfo(p).LinkTarget is not null) throw new CodingException("The project folder cannot be, or be inside, a symbolic link.");
        return root;
    }

    public CodingSession Start(string folder)
    {
        var root = ProjectRoot(folder);
        foreach (var old in _sessions.Values.OrderBy(s => s.LastUsed).Take(Math.Max(0, _sessions.Count - MaxSessions + 1)))
            _sessions.TryRemove(old.Id, out _);
        var session = new CodingSession { Id = Guid.NewGuid().ToString("N")[..12], Root = root, TestCommand = DetectTests(root) };
        _sessions[session.Id] = session;
        return session;
    }

    public CodingSession Get(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : throw new CodingException("That coding session has ended. Open the folder again.", 404);

    // ---- the agent loop ----
    public async IAsyncEnumerable<object> RunAsync(CodingSession session, string request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!await session.Busy.WaitAsync(0, ct)) { yield return new { type = "error", message = "The agent is still working on the last request." }; yield break; }
        try
        {
            session.LastUsed = DateTimeOffset.UtcNow;
            session.Turns.Add(new ChatMessage("user", "Request: " + request));
            var read = new HashSet<string>(StringComparer.Ordinal);
            for (var step = 0; step < MaxSteps; step++)
            {
                var context = new ChatContext(SystemPrompt(session), session.Turns.Take(session.Turns.Count - 1).ToList(), Json: true);
                string answer;
                string? failure = null;
                try { answer = (await router.AskAsync(session.Turns[^1].Content, ct, context)).Answer; }
                catch (ProviderException ex) { answer = ""; failure = ex.Message; }
                if (failure is not null) { yield return new { type = "error", message = failure }; yield break; }

                var action = ParseAction(answer);
                session.Turns.Add(new ChatMessage("assistant", action is null ? answer : JsonSerializer.Serialize(action.Value)));
                if (action is null)
                {
                    session.Turns.Add(new ChatMessage("user", "That was not one JSON action. Reply with exactly one JSON object, for example {\"tool\":\"list\",\"path\":\".\"}."));
                    continue;
                }
                var (tool, result, ev) = Act(session, action.Value, read);
                if (ev is not null) yield return ev;
                if (tool == "done")
                {
                    if (read.Count > 0)
                        await journal.AppendAsync(new JournalRecord(JournalKinds.AiRead, "nexus",
                            Summary($"Coding agent read {read.Count} file{(read.Count == 1 ? "" : "s")} in {Display(session.Root)}")), ct);
                    yield return Changes(session);
                    yield return new { type = "message", text = result };
                    yield return new { type = "done" };
                    yield break;
                }
                session.Turns.Add(new ChatMessage("user", "Result of " + tool + ":\n" + Truncate(result, MaxResultChars)));
            }
            yield return Changes(session);
            yield return new { type = "message", text = $"I stopped after {MaxSteps} steps. The changes so far are on the right; ask me to continue if they are not finished." };
            yield return new { type = "done" };
        }
        finally { session.Busy.Release(); }
    }

    private (string Tool, string Result, object? Event) Act(CodingSession session, JsonElement action, HashSet<string> read)
    {
        var tool = Str(action, "tool");
        try
        {
            switch (tool)
            {
                case "list":
                {
                    var dir = Resolve(session, Str(action, "path") is { Length: > 0 } p ? p : ".", mustExist: true);
                    var entries = Tree(session.Root, dir, 2, 300);
                    return (tool, entries, new { type = "step", kind = "read", title = "Looked at", detail = Rel(session, dir) is "" ? "the project" : Rel(session, dir) });
                }
                case "read":
                {
                    var path = Resolve(session, Str(action, "path"), mustExist: false);
                    var rel = Rel(session, path);
                    var text = session.Staged.TryGetValue(rel, out var staged) ? staged : ReadText(path);
                    read.Add(rel);
                    var numbered = string.Join("\n", text.Split('\n').Select((l, i) => $"{i + 1,5} {l}"));
                    return (tool, Truncate(numbered, MaxReadChars), new { type = "step", kind = "read", title = "Read", detail = rel });
                }
                case "search":
                {
                    var pattern = Str(action, "pattern");
                    if (pattern.Length is 0 or > 200) throw new CodingException("Search for 1-200 characters.");
                    var hits = Search(session, pattern, 60);
                    return (tool, hits.Length == 0 ? "No matches." : hits, new { type = "step", kind = "search", title = "Searched for", detail = pattern });
                }
                case "write":
                {
                    var path = Resolve(session, Str(action, "path"), mustExist: false);
                    var content = Str(action, "content");
                    if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes) throw new CodingException("That file is too large to write.");
                    Stage(session, path, content);
                    return (tool, "Staged " + Rel(session, path) + ".", new { type = "step", kind = "edit", title = "Edited", detail = Rel(session, path) });
                }
                case "replace":
                {
                    var path = Resolve(session, Str(action, "path"), mustExist: false);
                    var rel = Rel(session, path);
                    var current = session.Staged.TryGetValue(rel, out var staged) ? staged : ReadText(path);
                    var old = Str(action, "old");
                    var count = old.Length == 0 ? 0 : CountOf(current, old);
                    if (count != 1) throw new CodingException(count == 0 ? "The old text was not found; read the file again and copy it exactly." : $"The old text appears {count} times; include more lines so it is unique.");
                    Stage(session, path, current.Replace(old, Str(action, "new")));
                    return (tool, "Staged the change to " + rel + ".", new { type = "step", kind = "edit", title = "Edited", detail = rel });
                }
                case "run_tests":
                {
                    if (session.TestCommand is null)
                        return (tool, "This project has no test command NexusOS recognises (package.json test script, pytest, dotnet, cargo, go, make test). Do not try to run tests.",
                            new { type = "step", kind = "tests", title = "No tests", detail = "none found in this project" });
                    // Only offered: the person sees the exact command and decides. Nothing runs here.
                    var run = Propose(session);
                    return (tool, $"The person has been asked to run `{run.Command}` on your staged changes. You cannot run it yourself and do not know the result, " +
                                  "so do not say the tests pass. Finish with done; if they run it, the result comes in their next message.",
                        new { type = "step", kind = "tests", title = "Asks to run", detail = run.Command, run = Card(run) });
                }
                case "done":
                    return (tool, Str(action, "summary") is { Length: > 0 } s ? s : "Done.", null);
                default:
                    throw new CodingException($"Unknown tool '{tool}'. Use list, read, search, write, replace, run_tests or done.");
            }
        }
        catch (CodingException ex)
        {
            return (tool, "Error: " + ex.Message, null);
        }
    }

    // ---- accept, reject, undo ----
    public object Changes(CodingSession session) => new
    {
        type = "changes",
        files = session.Staged.Keys.Order(StringComparer.Ordinal).Select(rel =>
        {
            var before = session.Original.GetValueOrDefault(rel) ?? "";
            var lines = LineDiff.Compute(before, session.Staged[rel]);
            return new
            {
                path = rel, created = session.Original.GetValueOrDefault(rel) is null,
                added = lines.Count(l => l.Mark == "+"), removed = lines.Count(l => l.Mark == "-"),
                diff = LineDiff.WithContext(lines, 3).Select(l => new { a = l.A, b = l.B, mark = l.Mark, text = l.Text })
            };
        }).ToArray()
    };

    public async Task<object> AcceptAsync(CodingSession session, string[]? only, CancellationToken ct)
    {
        await session.Busy.WaitAsync(ct);
        try
        {
            var files = session.Staged.Keys.Where(k => only is null || only.Contains(k)).ToList();
            session.Undo.Clear();
            foreach (var rel in files)
            {
                var path = Resolve(session, rel, mustExist: false);
                session.Undo[rel] = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, session.Staged[rel], ct);
                session.Staged.Remove(rel);
                session.Original.Remove(rel);
            }
            if (files.Count > 0)
                await journal.AppendAsync(new JournalRecord(JournalKinds.App, "user",
                    Summary($"Accepted the coding agent's changes to {files.Count} file{(files.Count == 1 ? "" : "s")} in {Display(session.Root)}"),
                    Ref: "code:" + session.Id), ct);
            return new { type = "accepted", files };
        }
        finally { session.Busy.Release(); }
    }

    public object Reject(CodingSession session)
    {
        session.Staged.Clear();
        session.Original.Clear();
        session.Turns.Add(new ChatMessage("user", "The person rejected all staged changes; the files are as they were."));
        return new { type = "rejected" };
    }

    public async Task<object> UndoAsync(CodingSession session, CancellationToken ct)
    {
        if (session.Undo.Count == 0) throw new CodingException("There is nothing to undo.", 409);
        foreach (var (rel, before) in session.Undo)
        {
            var path = Resolve(session, rel, mustExist: false);
            if (before is null) File.Delete(path); else await File.WriteAllTextAsync(path, before, ct);
        }
        var files = session.Undo.Keys.ToArray();
        session.Undo.Clear();
        return new { type = "undone", files };
    }

    // ---- test runs: offered by the agent, run only on the person's click ----
    public TestProposal Propose(CodingSession session)
    {
        var files = session.Staged.Keys.Order(StringComparer.Ordinal).ToArray();
        var run = new TestProposal(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), session.TestCommand!, files,
            files.Where(f => RunnerFile().IsMatch(Path.GetFileName(f))).ToArray(), Fingerprint(session, files), DateTimeOffset.UtcNow);
        lock (session) session.Proposal = run;     // a new offer replaces the last one
        return run;
    }

    public static object Card(TestProposal run) => new { id = run.Id, command = run.Command, files = run.Files, affects = run.Affects };

    public async Task<object> ConfirmRunAsync(CodingSession session, string runId, CancellationToken ct)
    {
        if (!await session.Busy.WaitAsync(0, ct)) throw new CodingException("The agent is still working; run the tests when it has finished.", 409);
        try
        {
            TestProposal? run;
            lock (session)
            {
                // Single use: the id is gone from here on, whatever happens next.
                run = session.Proposal is { } offered && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(offered.Id), Encoding.ASCII.GetBytes(runId)) ? offered : null;
                if (run is not null) session.Proposal = null;
            }
            if (run is null || DateTimeOffset.UtcNow - run.Created > ProposalLifetime)
                throw new CodingException("That test run is no longer offered (it was used, replaced or is too old). Ask the agent to run the tests again.", 409);
            if (Fingerprint(session, run.Files) != run.Fingerprint)
                throw new CodingException("The changes are not the ones this run was offered for. Ask the agent to run the tests again.", 409);
            if (Bwrap is null)
                throw new CodingException("Tests run only in a sandbox, and bubblewrap (bwrap) is not installed, so they were not run.", 503);
            session.LastUsed = DateTimeOffset.UtcNow;
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            session.TestRun = stop;
            SandboxResult result;
            var copy = Directory.CreateTempSubdirectory("nexus-code-").FullName;
            try
            {
                var dependencies = CopyTree(session.Root, copy);
                foreach (var (rel, content) in session.Staged)
                {
                    var path = Path.Combine(copy, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, ct);
                }
                var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                result = await RunSandboxedAsync(Bwrap, SandboxArgv(session.Root, copy, dependencies, run.Command, home),
                    SandboxEnvironment(home), TestTimeout, stop.Token);
            }
            finally
            {
                session.TestRun = null;
                try { Directory.Delete(copy, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning("Could not remove {Copy}: {Message}", copy, ex.Message); }
            }
            var passed = result.ExitCode == 0;
            var summary = result.TimedOut ? $"did not finish within {TestTimeout.TotalMinutes} minutes" : result.Stopped ? "stopped" : TestSummary(result.Output, passed);
            var outcome = result.TimedOut ? $"The tests did not finish within {TestTimeout.TotalMinutes} minutes and were stopped."
                : result.Stopped ? "The person stopped the tests." : passed ? "Tests passed." : "Tests failed.";
            session.Turns.Add(new ChatMessage("user", $"The person ran `{run.Command}` in the sandbox. {outcome}\n" + Tail(Redact(result.Output), MaxResultChars)));
            await journal.AppendAsync(new JournalRecord(JournalKinds.App, "user",
                Summary($"Ran the tests ({run.Command}) for {Display(session.Root)} in a sandbox: {summary}"), Ref: "code:" + session.Id), CancellationToken.None);
            return new { type = "test-result", passed, summary, output = result.Output, truncated = result.Truncated, timedOut = result.TimedOut, stopped = result.Stopped };
        }
        finally { session.Busy.Release(); }
    }

    public object DismissRun(CodingSession session, string runId)
    {
        lock (session)
            if (session.Proposal?.Id == runId)
            {
                session.Proposal = null;
                session.Turns.Add(new ChatMessage("user", "The person chose not to run the tests."));
            }
        return new { type = "run-dismissed" };
    }

    public object StopRun(CodingSession session)
    {
        session.TestRun?.Cancel();
        return new { type = "run-stopping" };
    }

    // What the tests would run on: each staged file (or, once accepted, the file on disk). Changes after the offer void it.
    private static string Fingerprint(CodingSession s, string[] offered)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var rel in offered.Union(s.Staged.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(rel + "\0"));
            if (s.Staged.TryGetValue(rel, out var staged)) hash.AppendData(Encoding.UTF8.GetBytes(staged));
            else
            {
                var path = Path.Combine(s.Root, rel);
                try { hash.AppendData(File.Exists(path) && new FileInfo(path).Length <= MaxFileBytes ? File.ReadAllBytes(path) : Encoding.UTF8.GetBytes("\u0001absent")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { hash.AppendData(Encoding.UTF8.GetBytes("\u0001unreadable")); }
            }
            hash.AppendData("\0"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ---- the prompt ----
    private static string SystemPrompt(CodingSession s) => $$"""
        You are the Nexus coding agent in NexusOS. You change code in one project folder at the request of the person.
        Project: {{Display(s.Root)}}
        Tests: {{s.TestCommand ?? "none found"}}

        Work one action at a time. Reply with exactly one JSON object and nothing else:
        {"tool":"list","path":"."}                              list a folder (two levels)
        {"tool":"read","path":"src/app.py"}                     read a file (lines are numbered)
        {"tool":"search","pattern":"send_alert"}                 find text in the project
        {"tool":"replace","path":"src/app.py","old":"exact existing lines","new":"replacement lines"}
        {"tool":"write","path":"tests/test_app.py","content":"whole file"}   create or rewrite a file
        {"tool":"run_tests"}                                     ask the person to run the project's tests on your staged changes
        {"tool":"done","summary":"two or three plain sentences on what you changed and why"}

        Rules:
        - Look before you change: read every file you edit. Keep changes small and in the project's style.
        - Prefer replace for edits; "old" must match the file exactly, once.
        - Your edits are staged: the person reviews the diff and accepts or rejects it. Never claim a change is saved.
        - You never run anything yourself. run_tests shows the person the test command with a Run button; they decide.
          If the project has tests, ask after editing, then finish with done. The result, if they run it, comes in their
          next message; fix failures you caused then. Never say tests pass unless that message says so.
        - You cannot run other commands, install packages or change the system. Say so in your summary if the task needs it.
        - Paths are relative to the project. Files outside it, .git, .env files, keys and files holding passwords or tokens are off limits.
        - If the request is unclear, reply with done and ask your question in the summary.
        """;

    // ---- helpers ----
    public static JsonElement? ParseAction(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;
        // The first complete JSON object in the reply (models sometimes add prose around it).
        for (var end = text.LastIndexOf('}'); end > start; end = text.LastIndexOf('}', end - 1))
        {
            try
            {
                using var doc = JsonDocument.Parse(text[start..(end + 1)]);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("tool", out _))
                    return doc.RootElement.Clone();
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // A path inside the project, never through .git, secrets, or a symbolic link out of it.
    public static string Resolve(CodingSession s, string relative, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\0')) throw new CodingException("A path is needed.");
        var full = Path.GetFullPath(Path.Combine(s.Root, relative.TrimStart('/')));
        if (full != s.Root && !full.StartsWith(s.Root + "/", StringComparison.Ordinal)) throw new CodingException("That path is outside the project.");
        var rel = Path.GetRelativePath(s.Root, full);
        var parts = rel.Split('/');
        if (parts.Contains(".git")) throw new CodingException(".git is off limits.");
        if (parts.Any(SecretDirs.Contains)) throw new CodingException("Folders for keys and passwords are off limits.");
        var name = parts[^1];
        if (IsSecretName(name)) throw new CodingException("Secret files (.env, keys) are off limits.");
        for (var p = full; p.Length > s.Root.Length; p = Path.GetDirectoryName(p)!)
            if ((File.Exists(p) || Directory.Exists(p)) && new FileInfo(p).LinkTarget is not null)
                throw new CodingException("Symbolic links are not followed.");
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new CodingException("Not found: " + rel);
        return full;
    }

    // Files that hold secrets by their name: .env files, SSH and TLS keys, keystores, credential and token files.
    [GeneratedRegex(@"^(\.env(\..*)?|.+\.env|id_(rsa|dsa|ecdsa|ed25519)(_sk)?(\.pub)?|authorized_keys|known_hosts|.*\.(pem|key|p12|pfx|jks|keystore|kdbx|gpg|asc|ppk|tfstate|tfvars|secret)|\.netrc|\.npmrc|\.pypirc|\.pgpass|\.htpasswd|\.git-credentials|\.dockercfg|credentials(\.json)?|secrets?\.(json|ya?ml|toml|ini|env))$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();
    public static bool IsSecretName(string name) => SecretName().IsMatch(name);

    // Text that holds a secret, wherever it is: private keys, cloud and GitHub tokens, and passwords or tokens
    // written out as values (`password = "…"`, `"token": "…"`, `API_TOKEN=…` in env and ini files).
    [GeneratedRegex("""
        -----BEGIN\ [A-Z0-9\ ]*PRIVATE\ KEY[A-Z\ ]*-----(?:[\s\S]*?-----END\ [A-Z0-9\ ]*PRIVATE\ KEY[A-Z\ ]*-----|[\s\S]*\z)
        | \b(?:AKIA|ASIA)[0-9A-Z]{16}\b
        | (?i:aws_secret_access_key)\s*[=:]\s*\S{20,}
        | \bgh[pousr]_[A-Za-z0-9]{36,}\b | \bgithub_pat_[A-Za-z0-9_]{22,} | \bglpat-[A-Za-z0-9_\-]{20,}
        | \bxox[abprs]-[A-Za-z0-9\-]{10,} | \bsk-(?:ant-|proj-)?[A-Za-z0-9_\-]{20,} | \bAIza[0-9A-Za-z_\-]{35}\b
        | (?i:\b(?:password|passwd|passphrase|secret|token|api[_-]?key|access[_-]?key|auth[_-]?token|client[_-]?secret|private[_-]?key))["']?\s*[:=]\s*["'](?![$<{%])[^"'\s]{6,}["']
        | ^(?:export\ +)?[A-Z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|API_?KEY)[A-Z0-9_]*=(?![$"'{(])\S{6,}
        """, RegexOptions.Multiline | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex SecretText();
    public static bool LooksSecret(string text) => SecretText().IsMatch(text);
    public static string Redact(string text) => SecretText().Replace(text, "[secret hidden]");

    // Files that decide what a test command does; the Run card names them when the agent changed one.
    [GeneratedRegex(@"^(Makefile|GNUmakefile|.*\.mk|package\.json|pyproject\.toml|setup\.(py|cfg)|tox\.ini|pytest\.ini|conftest\.py|noxfile\.py|Cargo\.toml|build\.rs|\.cargo|go\.mod|.*\.(csproj|fsproj|sln|props|targets)|(jest|vitest|vite|babel|webpack)\.config\.\w+|\.(babel|mocha)rc.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex RunnerFile();

    private static string Rel(CodingSession s, string full) => full == s.Root ? "" : Path.GetRelativePath(s.Root, full);

    private static string ReadText(string path)
    {
        if (!File.Exists(path)) throw new CodingException("Not found. To create it, use write.");
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new CodingException($"That file is too large to read ({info.Length / 1024} KB).");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Contains((byte)0)) throw new CodingException("That is a binary file.");
        var text = Encoding.UTF8.GetString(bytes);
        if (LooksSecret(text)) throw new CodingException("That file looks like it holds a key, token or password, so it is off limits.");
        return text;
    }

    private static void Stage(CodingSession s, string full, string content)
    {
        var rel = Rel(s, full);
        if (rel.Split('/').Any(Dependencies.Contains)) throw new CodingException("Installed dependencies (node_modules, .venv) are not edited.");
        if (!s.Original.ContainsKey(rel)) s.Original[rel] = File.Exists(full) ? ReadText(full) : null;
        s.Staged[rel] = content;
    }

    private static string Tree(string root, string dir, int depth, int max)
    {
        var lines = new List<string>();
        void Walk(string d, int level)
        {
            if (lines.Count >= max) return;
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(d).Order(StringComparer.Ordinal).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (var e in entries)
            {
                if (lines.Count >= max) { lines.Add("…"); return; }
                var name = Path.GetFileName(e);
                var isDir = Directory.Exists(e) && new FileInfo(e).LinkTarget is null;
                if (isDir && (Skipped.Contains(name) || SecretDirs.Contains(name))) continue;
                lines.Add(new string(' ', (depth - level) * 2) + Path.GetRelativePath(root, e) + (isDir ? "/" : ""));
                if (isDir && level > 1) Walk(e, level - 1);
            }
        }
        Walk(dir, depth);
        return lines.Count == 0 ? "(empty)" : string.Join("\n", lines);
    }

    private static string Search(CodingSession s, string pattern, int max)
    {
        var hits = new List<string>();
        foreach (var file in Files(s.Root))
        {
            var rel = Path.GetRelativePath(s.Root, file);
            if (IsSecretName(Path.GetFileName(file))) continue;
            string text;
            try { if (new FileInfo(file).Length > MaxFileBytes) continue; text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (text.Contains('\0') || LooksSecret(text)) continue;
            if (s.Staged.TryGetValue(rel, out var staged)) text = staged;
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length && hits.Count < max; i++)
                if (lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase)) hits.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            if (hits.Count >= max) break;
        }
        return string.Join("\n", hits);
    }

    private static IEnumerable<string> Files(string root)
    {
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(dir).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if (new FileInfo(e).LinkTarget is not null) continue;
                if (Directory.Exists(e)) { if (!Skipped.Contains(Path.GetFileName(e)) && !SecretDirs.Contains(Path.GetFileName(e))) stack.Push(e); }
                else yield return e;
            }
        }
    }

    // The project's own test command, from what is in the folder. The model never chooses a command.
    public static string? DetectTests(string root)
    {
        bool Has(string name) => File.Exists(Path.Combine(root, name));
        if (Has("package.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
                if (doc.RootElement.TryGetProperty("scripts", out var scripts) && scripts.TryGetProperty("test", out _)) return "npm test";
            }
            catch (JsonException) { }
        }
        if (Has("Cargo.toml")) return "cargo test";
        if (Has("go.mod")) return "go test ./...";
        if (Directory.EnumerateFiles(root, "*.sln").Any() || Directory.EnumerateFiles(root, "*.csproj").Any()) return "dotnet test";
        if (Has("pytest.ini") || Has("pyproject.toml") || Has("setup.cfg") || Has("tox.ini") || Directory.Exists(Path.Combine(root, "tests")) && Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.py").Any())
            return "python3 -m pytest -q";
        if (Has("Makefile") && Regex.IsMatch(File.ReadAllText(Path.Combine(root, "Makefile")), @"^test:", RegexOptions.Multiline)) return "make test";
        return null;
    }

    // bubblewrap, from NEXUS_BWRAP (the Nix module sets it) or the PATH. Without it tests do not run at all.
    public static string? FindBwrap(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/run/current-system/sw/bin"]);
        return dirs.Select(d => Path.Combine(d, "bwrap")).FirstOrDefault(File.Exists);
    }

    // The sandbox for a test run. The copy of the project (with the staged edits) is mounted read-write where the
    // project is, installed dependencies read-only from the project, the system's programs read-only; the rest of
    // the home folder is an empty tmpfs, /tmp is private and there is no network. Its own PID namespace and
    // --die-with-parent mean that when bwrap is killed every process the tests started goes with it.
    public static List<string> SandboxArgv(string project, string copy, IEnumerable<string> dependencies, string command, string home)
    {
        var argv = new List<string>
        {
            "--die-with-parent", "--new-session",
            "--unshare-user-try", "--unshare-ipc", "--unshare-pid", "--unshare-uts", "--unshare-cgroup-try", "--unshare-net",
            "--ro-bind", "/nix/store", "/nix/store",
        };
        foreach (var path in new[] { "/run/current-system", "/usr", "/bin", "/lib", "/lib64", "/etc/static", "/etc/profiles",
                                     "/etc/passwd", "/etc/group", "/etc/nsswitch.conf", "/etc/localtime" })
            argv.AddRange(["--ro-bind-try", path, path]);
        argv.AddRange(["--proc", "/proc", "--dev", "/dev", "--tmpfs", "/tmp", "--tmpfs", home, "--bind", copy, project]);
        foreach (var dependency in dependencies)
            argv.AddRange(["--ro-bind", Path.Combine(project, dependency), Path.Combine(project, dependency)]);
        argv.AddRange(["--chdir", project, "--", "sh", "-c", command]);
        return argv;
    }

    // Only what a test command needs: no API tokens or other settings from Core's own environment.
    public static Dictionary<string, string> SandboxEnvironment(string home)
    {
        var user = Environment.UserName;
        return new()
        {
            ["PATH"] = $"/run/current-system/sw/bin:/etc/profiles/per-user/{user}/bin:/usr/local/bin:/usr/bin:/bin",
            ["HOME"] = home, ["USER"] = user, ["TMPDIR"] = "/tmp", ["LANG"] = "C.UTF-8", ["TERM"] = "dumb", ["CI"] = "1", ["NO_COLOR"] = "1",
        };
    }

    // Runs bwrap with a time limit, keeping the last MaxOutputChars of stdout and stderr together.
    // On timeout or Stop the whole sandbox is killed, and the run waits for it to be gone.
    public static async Task<SandboxResult> RunSandboxedAsync(string bwrap, IEnumerable<string> argv, IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(bwrap)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        foreach (var arg in argv) start.ArgumentList.Add(arg);
        start.Environment.Clear();
        foreach (var (key, value) in environment) start.Environment[key] = value;
        // --die-with-parent is a parent-death signal, which Linux sends when the *thread* that started bwrap ends.
        // Thread-pool threads come and go, so bwrap is started from a thread of its own that waits for it.
        var started = new TaskCompletionSource<Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() =>
        {
            Process p;
            try { p = Process.Start(start)!; }
            catch (Exception ex) { started.SetException(ex); return; }
            started.SetResult(p);
            try { p.WaitForExit(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }) { IsBackground = true, Name = "nexus-code-sandbox" }.Start();
        using var process = await started.Task;
        process.StandardInput.Close();
        var output = new CappedOutput(MaxOutputChars);
        var pumps = Task.WhenAll(output.PumpAsync(process.StandardOutput), output.PumpAsync(process.StandardError));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        bool timedOut = false, stopped = false;
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            stopped = ct.IsCancellationRequested;
            timedOut = !stopped;
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
        }
        // The pipes close once nothing in the sandbox is left; if something still holds them, kill again and move on.
        try { await pumps.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (TimeoutException) { Kill(process); }
        return new SandboxResult(timedOut || stopped ? null : process.ExitCode, output.ToString(), output.Truncated, timedOut, stopped);
    }

    private static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }     // already gone
    }

    // Keeps the tail of the output; drops the front in large steps so a flood of output costs little.
    private sealed class CappedOutput(int max)
    {
        private readonly StringBuilder _text = new();
        private long _dropped;
        public bool Truncated { get { lock (_text) return _dropped > 0; } }

        public async Task PumpAsync(StreamReader reader)
        {
            var buffer = new char[8192];
            int n;
            while ((n = await reader.ReadAsync(buffer)) > 0)
                lock (_text)
                {
                    _text.Append(buffer, 0, n);
                    if (_text.Length > 2 * max) { var cut = _text.Length - max; _text.Remove(0, cut); _dropped += cut; }
                }
        }

        public override string ToString()
        {
            lock (_text)
            {
                if (_text.Length > max) { var cut = _text.Length - max; _text.Remove(0, cut); _dropped += cut; }
                return _dropped == 0 ? _text.ToString()
                    : $"(output truncated: the first {(_dropped + 1023) / 1024} KiB are not shown, only the last {max / 1024} KiB)\n" + _text;
            }
        }
    }

    // Copies the project for a test run, without .git, caches and build output, never following links. Installed
    // dependencies are not copied: they are returned, to be shown read-only from the project. At most
    // MaxCopyBytes and MaxCopyFiles.
    public static List<string> CopyTree(string from, string to)
    {
        long bytes = 0, files = 0;
        var dependencies = new List<string>();
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
            {
                var rel = Path.GetRelativePath(from, entry);
                var name = Path.GetFileName(entry);
                var info = new FileInfo(entry);
                if (info.LinkTarget is not null) continue;
                if (Directory.Exists(entry))
                {
                    if (NotCopied.Contains(name) || SecretDirs.Contains(name)) continue;
                    Directory.CreateDirectory(Path.Combine(to, rel));
                    if (Dependencies.Contains(name)) dependencies.Add(rel);
                    else stack.Push(entry);
                    continue;
                }
                bytes += info.Length;
                if (++files > MaxCopyFiles || bytes > MaxCopyBytes)
                    throw new CodingException($"The project is too large to copy for a test run (over {MaxCopyBytes >> 20} MB or {MaxCopyFiles} files, " +
                                              "not counting .git, node_modules, .venv and build output).", 413);
                File.Copy(entry, Path.Combine(to, rel));
            }
        }
        return dependencies;
    }

    public static string TestSummary(string output, bool passed)
    {
        // The last line that reads like a summary ("42 passed", "Tests: 3 failed, 39 passed", "ok ...").
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => Regex.IsMatch(l, @"\d+\s+(passed|failed|tests?|ok)\b|test result:|Passed!|Failed!", RegexOptions.IgnoreCase));
        return line is null ? (passed ? "passed" : "failed") : Truncate(line, 120);
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n… (cut)";
    private static string Tail(string s, int max) => s.Length <= max ? s : "(…)\n" + s[^max..];
    private static string Summary(string s) => s.Length <= ActivityJournal.MaxSummaryLength ? s : s[..(ActivityJournal.MaxSummaryLength - 1)] + "…";
    public static string Display(string root)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return root.StartsWith(home + "/", StringComparison.Ordinal) ? "~/" + root[(home.Length + 1)..] : root;
    }
}

public sealed class CodingException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

// A line diff (longest common subsequence) for the diff pane.
public static class LineDiff
{
    public sealed record Line(string A, string B, string Mark, string Text);
    public const int MaxLines = 4000;

    public static List<Line> Compute(string before, string after)
    {
        var a = before.Length == 0 ? [] : before.Split('\n');
        var b = after.Split('\n');
        if (a.Length > MaxLines || b.Length > MaxLines)
            return [.. a.Select((t, i) => new Line((i + 1).ToString(), "", "-", t)), .. b.Select((t, i) => new Line("", (i + 1).ToString(), "+", t))];
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var lines = new List<Line>();
        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { lines.Add(new((x + 1).ToString(), (y + 1).ToString(), " ", a[x])); x++; y++; }
            // Removed lines come before the lines that replace them.
            else if (x < a.Length && (y == b.Length || lcs[x + 1, y] >= lcs[x, y + 1])) { lines.Add(new((x + 1).ToString(), "", "-", a[x])); x++; }
            else { lines.Add(new("", (y + 1).ToString(), "+", b[y])); y++; }
        }
        return lines;
    }

    // Changed lines with a few lines of context around them; long unchanged runs become "…".
    public static List<Line> WithContext(List<Line> lines, int context)
    {
        var keep = new bool[lines.Count];
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Mark != " ")
                for (var k = Math.Max(0, i - context); k <= Math.Min(lines.Count - 1, i + context); k++) keep[k] = true;
        var result = new List<Line>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (keep[i]) result.Add(lines[i]);
            else if (result.Count > 0 && result[^1].Mark != "…") result.Add(new("", "", "…", ""));
        }
        if (result.Count > 0 && result[^1].Mark == "…") result.RemoveAt(result.Count - 1);
        return result;
    }
}

public sealed record CodeStartRequest(string Folder);
public sealed record CodeMessageRequest(string Text);
public sealed record CodeAcceptRequest(string[]? Files);

public static class CodingEndpoints
{
    public static void MapCoding(this WebApplication app)
    {
        app.MapPost("/code/sessions", (CodeStartRequest request, CodingAgent agent) =>
        {
            try
            {
                var s = agent.Start(request.Folder ?? "");
                return Results.Ok(new { id = s.Id, folder = CodingAgent.Display(s.Root), tests = s.TestCommand, sandbox = agent.Bwrap is not null });
            }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        // NDJSON: step events, then changes, message and done (or error).
        app.MapPost("/code/sessions/{id}/messages", async (string id, CodeMessageRequest request, HttpContext http, CodingAgent agent,
            InferenceActivity activity, CancellationToken ct) =>
        {
            CodingSession session;
            try { session = agent.Get(id); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
            if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 8000)
                return Results.BadRequest(new { error = "Ask for a change in 1-8000 characters." });
            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.CacheControl = "no-store";
            activity.BeginRequest();
            try
            {
                await foreach (var ev in agent.RunAsync(session, request.Text, ct))
                {
                    await http.Response.WriteAsync(JsonSerializer.Serialize(ev) + "\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
            finally { activity.EndRequest(); }
            return Results.Empty;
        }).RequireRateLimiting("ai");
        app.MapPost("/code/sessions/{id}/accept", async (string id, CodeAcceptRequest request, CodingAgent agent, CancellationToken ct) =>
        {
            try { return Results.Ok(await agent.AcceptAsync(agent.Get(id), request.Files, ct)); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        app.MapPost("/code/sessions/{id}/reject", (string id, CodingAgent agent) =>
        {
            try { return Results.Ok(agent.Reject(agent.Get(id))); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        app.MapPost("/code/sessions/{id}/undo", async (string id, CodingAgent agent, CancellationToken ct) =>
        {
            try { return Results.Ok(await agent.UndoAsync(agent.Get(id), ct)); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        // The person's Run on the agent's test card: runs that one offer, once, in the sandbox.
        app.MapPost("/code/sessions/{id}/runs/{run}/confirm", async (string id, string run, CodingAgent agent, CancellationToken ct) =>
        {
            try { return Results.Ok(await agent.ConfirmRunAsync(agent.Get(id), run, ct)); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        app.MapPost("/code/sessions/{id}/runs/{run}/dismiss", (string id, string run, CodingAgent agent) =>
        {
            try { return Results.Ok(agent.DismissRun(agent.Get(id), run)); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
        app.MapPost("/code/sessions/{id}/runs/stop", (string id, CodingAgent agent) =>
        {
            try { return Results.Ok(agent.StopRun(agent.Get(id))); }
            catch (CodingException ex) { return Results.Problem(ex.Message, statusCode: ex.Status); }
        });
    }
}
