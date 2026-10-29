using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The Nexus coding agent (design: "Coding agent A · Conversation and diff
// side by side"). The person picks a project folder in their home and asks
// for a change; the selected model works through it one action at a time:
//   list, read, search      look around the project (never outside it)
//   write, replace          stage an edit: nothing on disk changes yet
//   run_tests               run the project's own test command (detected
//                           here, never chosen by the model) on a copy that
//                           has the staged edits
//   done                    explain what it did
// The person sees every step, then the diff, and accepts or rejects it.
// Accepting writes the files and can be undone while the session is open.
// The agent never runs commands of its own choosing, never reads secrets
// (.env files, keys) and cannot reach past the project folder.
public sealed class CodingSession
{
    public required string Id { get; init; }
    public required string Root { get; init; }
    public string? TestCommand { get; init; }
    public List<ChatMessage> Turns { get; } = [];
    public Dictionary<string, string> Staged { get; } = new(StringComparer.Ordinal);       // relative path -> new content
    public Dictionary<string, string?> Original { get; } = new(StringComparer.Ordinal);    // before staging (null = new file)
    public Dictionary<string, string?> Undo { get; } = new(StringComparer.Ordinal);        // after accept
    public SemaphoreSlim Busy { get; } = new(1, 1);
    public DateTimeOffset LastUsed { get; set; } = DateTimeOffset.UtcNow;
}

public sealed partial class CodingAgent(ModelRouter router, ActivityJournal journal, ILogger<CodingAgent> log)
{
    public const int MaxSteps = 24, MaxFileBytes = 64 * 1024, MaxReadChars = 24000, MaxResultChars = 6000, MaxSessions = 8;
    public static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] Skipped = [".git", "node_modules", ".venv", "venv", "__pycache__", "target", "bin", "obj", "dist", "build", ".next", ".cache", "result"];
    private readonly ConcurrentDictionary<string, CodingSession> _sessions = new();

    // ---- sessions ----
    public CodingSession Start(string folder)
    {
        var home = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var expanded = folder.StartsWith("~/") ? Path.Combine(home, folder[2..]) : folder == "~" ? home : folder;
        var root = Path.GetFullPath(expanded);
        if (!(root + "/").StartsWith(home + "/", StringComparison.Ordinal) || root == home)
            throw new CodingException("Choose a project folder inside your home folder.");
        if (!Directory.Exists(root)) throw new CodingException("That folder does not exist.");
        if (new DirectoryInfo(root).LinkTarget is not null) throw new CodingException("The project folder cannot be a symbolic link.");
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
                var (tool, result, ev) = await ActAsync(session, action.Value, read, ct);
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

    private async Task<(string Tool, string Result, object? Event)> ActAsync(CodingSession session, JsonElement action,
        HashSet<string> read, CancellationToken ct)
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
                    var (passed, output) = await RunTestsAsync(session, ct);
                    var summary = TestSummary(output, passed);
                    return (tool, (passed ? "Tests passed." : "Tests failed.") + "\n" + Tail(output, MaxResultChars),
                        new { type = "step", kind = passed ? "pass" : "fail", title = passed ? "Tests pass" : "Tests fail", detail = summary, tests = new { passed, summary } });
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
        {"tool":"run_tests"}                                     run the project's tests on your staged changes
        {"tool":"done","summary":"two or three plain sentences on what you changed and why"}

        Rules:
        - Look before you change: read every file you edit. Keep changes small and in the project's style.
        - Prefer replace for edits; "old" must match the file exactly, once.
        - Your edits are staged: the person reviews the diff and accepts or rejects it. Never claim a change is saved.
        - If the project has tests, run them after editing, and fix failures you caused.
        - You cannot run other commands, install packages or change the system. Say so in your summary if the task needs it.
        - Paths are relative to the project. Files outside it, .git, .env files and keys are off limits.
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
        var name = parts[^1];
        if (SecretName().IsMatch(name)) throw new CodingException("Secret files (.env, keys) are off limits.");
        for (var p = full; p.Length > s.Root.Length; p = Path.GetDirectoryName(p)!)
            if ((File.Exists(p) || Directory.Exists(p)) && new FileInfo(p).LinkTarget is not null)
                throw new CodingException("Symbolic links are not followed.");
        if (mustExist && !File.Exists(full) && !Directory.Exists(full)) throw new CodingException("Not found: " + rel);
        return full;
    }

    [GeneratedRegex(@"^(\.env(\..*)?|id_(rsa|ed25519|ecdsa)(\.pub)?|.*\.(pem|key|p12|pfx|keystore)|\.netrc|\.npmrc|\.pypirc|credentials(\.json)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    private static string Rel(CodingSession s, string full) => full == s.Root ? "" : Path.GetRelativePath(s.Root, full);

    private static string ReadText(string path)
    {
        if (!File.Exists(path)) throw new CodingException("Not found. To create it, use write.");
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new CodingException($"That file is too large to read ({info.Length / 1024} KB).");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Contains((byte)0)) throw new CodingException("That is a binary file.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static void Stage(CodingSession s, string full, string content)
    {
        var rel = Rel(s, full);
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
                if (isDir && Skipped.Contains(name)) continue;
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
            if (SecretName().IsMatch(Path.GetFileName(file))) continue;
            string text;
            try { if (new FileInfo(file).Length > MaxFileBytes) continue; text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (text.Contains('\0')) continue;
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
                if (Directory.Exists(e)) { if (!Skipped.Contains(Path.GetFileName(e))) stack.Push(e); }
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

    // Tests run on a copy of the project with the staged edits, so the person's files are untouched until they accept.
    private async Task<(bool Passed, string Output)> RunTestsAsync(CodingSession s, CancellationToken ct)
    {
        var copy = Directory.CreateTempSubdirectory("nexus-code-").FullName;
        try
        {
            CopyTree(s.Root, copy);
            foreach (var (rel, content) in s.Staged)
            {
                var path = Path.Combine(copy, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, ct);
            }
            var start = new ProcessStartInfo("sh", ["-c", s.TestCommand!])
            {
                WorkingDirectory = copy, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TestTimeout);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(true);
                return (false, $"The tests did not finish within {TestTimeout.TotalMinutes} minutes.");
            }
            return (process.ExitCode == 0, await stdout + await stderr);
        }
        finally
        {
            try { Directory.Delete(copy, true); } catch (IOException ex) { log.LogWarning("Could not remove {Copy}: {Message}", copy, ex.Message); }
        }
    }

    // Copies the project for a test run; dependency folders (node_modules, .venv) come too, as the tests need them, but not .git.
    private static void CopyTree(string from, string to)
    {
        long bytes = 0;
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, dir);
            if (rel.Split('/').Contains(".git") || new DirectoryInfo(dir).LinkTarget is not null) continue;
            Directory.CreateDirectory(Path.Combine(to, rel));
        }
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(from, file);
            if (rel.Split('/').Contains(".git") || !Directory.Exists(Path.GetDirectoryName(Path.Combine(to, rel))!)) continue;
            var info = new FileInfo(file);
            if (info.LinkTarget is not null) continue;
            bytes += info.Length;
            if (bytes > 2L << 30) throw new CodingException("The project is too large to copy for a test run (over 2 GB).");
            File.Copy(file, Path.Combine(to, rel));
        }
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
                return Results.Ok(new { id = s.Id, folder = CodingAgent.Display(s.Root), tests = s.TestCommand });
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
    }
}
