using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Sockets;

// `nexus try <app>` runs the sandbox directly and needs no Core; only
// `nexus try keep <app>` talks to Core, which turns it into a Guardian proposal.
if (args.Length > 1 && args[0].Equals("try", StringComparison.OrdinalIgnoreCase) && args[1] != "keep")
    return RunTrial(args[1..]);
var root = Environment.GetEnvironmentVariable("NEXUS_STATE_DIR");
if (string.IsNullOrWhiteSpace(root))
{
    var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
    root = string.IsNullOrWhiteSpace(xdg)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "nexusos")
        : Path.Combine(xdg, "nexusos");
}
var tokenPath = Path.Combine(root, "api-token");
if (!File.Exists(tokenPath))
{
    Console.Error.WriteLine("Nexus Core token missing. Start Nexus Core first.");
    return 1;
}
var token = (await File.ReadAllTextAsync(tokenPath)).Trim();
HttpMessageHandler handler;
var mode = Environment.GetEnvironmentVariable("NEXUS_API_MODE")?.Trim().ToLowerInvariant();
if (string.IsNullOrEmpty(mode)) mode = OperatingSystem.IsLinux() ? "unix" : "tcp";
if (mode is not ("unix" or "tcp") || (mode == "unix" && !OperatingSystem.IsLinux()))
{
    Console.Error.WriteLine("NEXUS_API_MODE must be 'unix' (Linux only) or 'tcp'.");
    return 2;
}
var socketPath = Path.Combine(root, "nexus.sock");
if (mode == "unix")
{
    if (!File.Exists(socketPath))
    {
        Console.Error.WriteLine($"Nexus Core socket not found at {socketPath}. Start Core, or set NEXUS_API_MODE=tcp if Core uses TCP.");
        return 1;
    }
    handler = new SocketsHttpHandler
    {
        ConnectCallback = async (_, ct) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };
}
else handler = new SocketsHttpHandler();
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5077"), Timeout = TimeSpan.FromMinutes(15) };   // images and the coding agent can take minutes
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
if (args.Length == 0) { Usage(); return 2; }
try
{
    switch (args[0].ToLowerInvariant())
    {
        case "health" when args.Length == 2 && args[1].Equals("report", StringComparison.OrdinalIgnoreCase):
            return await HealthReport();
        case "health":
            return await PrintGet("/health");
        case "fixit" when args.Length == 4 && args[1].Equals("apply", StringComparison.OrdinalIgnoreCase):
            return await ApplyFix(args[2], args[3]);
        case "fixit" when args.Length > 1:
            return await FixIt(string.Join(' ', args.Skip(1)));
        case "system":
            return await PrintGet("/system");
        case "providers":
            return await PrintGet("/providers");
        case "history":
            return await PrintGet("/history");
        case "status":
            return await PrintGet("/inference/status");
        case "rules":
            return await RulesCommand.RunAsync(http, args[1..], Console.In, Console.Out, Console.Error);
        case "unload":
            using (var res = await http.PostAsync("/inference/unload", null))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "clear-history":
            using (var res = await http.DeleteAsync("/history"))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "journal" when args.Length == 2 && args[1] == "forget-today":
            using (var res = await http.DeleteAsync($"/journal?day={DateTime.Now:yyyy-MM-dd}"))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "journal":
            var query = new List<string>();
            for (var i = 1; i < args.Length; i++)
            {
                if (args[i] == "--today")
                    query.Add("since=" + Uri.EscapeDataString(new DateTimeOffset(DateTime.Today).ToUniversalTime().ToString("O")));
                else if (args[i] == "--kind" && i + 1 < args.Length)
                    query.Add("kind=" + Uri.EscapeDataString(args[++i]));
                else { Usage(); return 2; }
            }
            return await PrintGet("/journal" + (query.Count > 0 ? "?" + string.Join('&', query) : ""));
        case "use" when args.Length == 2:
            using (var res = await http.PostAsJsonAsync("/providers/select", new { name = args[1] }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "ask" when args.Length > 2 && args[1] == "--stream":
            return await StreamAnswer(string.Join(' ', args.Skip(2)));
        case "bridge":
            return await Bridge();
        case "suggest" when args.Length > 1:
            using (var res = await http.PostAsJsonAsync("/suggest", new { prompt = string.Join(' ', args.Skip(1)) }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "search" when args.Length > 1:
            return await Search(args[1..]);
        case "ask" when args.Length > 1:
            using (var res = await http.PostAsJsonAsync("/chat", new { prompt = string.Join(' ', args.Skip(1)) }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "try" when args.Length == 3 && args[1] == "keep":
            using (var res = await http.PostAsJsonAsync("/try/keep", new { attr = args[2] }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        default: Usage(); return 2;
    }
}
catch (HttpRequestException ex) { Console.Error.WriteLine($"Nexus Core not reachable: {ex.Message}"); return 1; }
catch (TaskCanceledException) { Console.Error.WriteLine("Request timed out."); return 1; }

// Fix it: show the cause, the evidence and the fixes; the person chooses.
async Task<int> FixIt(string problem)
{
    using var response = await http.PostAsJsonAsync("/fixit", new { problem });
    if (!response.IsSuccessStatusCode) { await PrintResponse(response); return 1; }
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    var cause = Text(body, "cause");
    if (cause is null)
    {
        Console.WriteLine(Text(body, "message") ?? "No known problem matched.");
        foreach (var probe in body.GetProperty("probes").EnumerateArray())
            Console.WriteLine($"  checked {Text(probe, "id")}: {Text(probe, "status")}");
        return 0;
    }
    Console.WriteLine(cause);
    foreach (var item in body.GetProperty("evidence").EnumerateArray())
        Console.WriteLine($"  evidence ({Text(item, "probe")}): {Text(item, "excerpt")}");
    var fixes = body.GetProperty("fixes").EnumerateArray().ToArray();
    var token = Text(body, "evidenceToken")!;
    for (var i = 0; i < fixes.Length; i++)
        Console.WriteLine($"  [{i + 1}] {Text(fixes[i], "label")}" +
            (Text(fixes[i], "kind") == "system" ? " (system change: needs approval through Guardian)" : ""));
    if (fixes.Length == 0) return 0;
    if (Console.IsInputRedirected)
    {
        Console.WriteLine($"Apply with: nexus fixit apply <fix id> {token}");
        foreach (var fix in fixes) Console.WriteLine($"  {Text(fix, "id")}");
        return 0;
    }
    Console.Write("Apply a fix? Enter its number, or press Enter to skip: ");
    if (!int.TryParse(Console.ReadLine(), out var choice) || choice < 1 || choice > fixes.Length) return 0;
    return await ApplyFix(Text(fixes[choice - 1], "id")!, token);
}
async Task<int> ApplyFix(string fixId, string token)
{
    using var response = await http.PostAsJsonAsync("/fixit/apply", new { fixId, evidenceToken = token });
    var raw = await response.Content.ReadAsStringAsync();
    if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        using var document = JsonDocument.Parse(raw);
        Console.WriteLine($"This is a system change and needs your approval through Guardian: {Text(document.RootElement, "proposal")}");
        return 1;
    }
    if (!response.IsSuccessStatusCode) { Console.Error.WriteLine($"nexus: {ErrorMessage(raw, response)} (HTTP {(int)response.StatusCode})"); return 1; }
    using var result = JsonDocument.Parse(raw);
    var applied = result.RootElement.GetProperty("applied").GetBoolean();
    Console.WriteLine(applied ? "Fix applied." : $"The fix did not complete ({Text(result.RootElement, "status")}).");
    return applied ? 0 : 1;
}
async Task<int> HealthReport()
{
    using var response = await http.GetAsync("/health/report");
    if (!response.IsSuccessStatusCode) { await PrintResponse(response); return 1; }
    var body = await response.Content.ReadFromJsonAsync<JsonElement>();
    foreach (var fact in body.GetProperty("facts").EnumerateArray()) Console.WriteLine(Text(fact, "text"));
    var problems = body.GetProperty("problems").EnumerateArray().ToArray();
    if (problems.Length > 0) Console.WriteLine("\nProblems (run `nexus fixit` to fix):");
    foreach (var problem in problems) Console.WriteLine($"  {Text(problem, "cause")}");
    return 0;
}
static string? Text(JsonElement element, string name) =>
    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        ? new string(value.GetString()!.Where(ch => !char.IsControl(ch)).ToArray()) : null;
// nexus search <query> | status | formats | reindex | erase | sources [list|add <dir>...|rm <dir>]
// A query that starts with a subcommand word can be given after "--".
async Task<int> Search(string[] rest)
{
    switch (rest)
    {
        case ["status"]: return await PrintGet("/search/status");
        case ["formats"]:
        {
            using var res = await http.GetAsync("/search/status");
            if (!res.IsSuccessStatusCode) { await PrintResponse(res); return 1; }
            using var document = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var formats = document.RootElement.GetProperty("formats");
            Console.WriteLine("Indexed file types:");
            foreach (var kind in formats.GetProperty("indexed").EnumerateObject())
                Console.WriteLine($"  {kind.Name,-9} {string.Join(' ', kind.Value.EnumerateArray().Select(x => x.GetString()))}");
            Console.WriteLine("Reported as not indexed: " +
                string.Join(' ', formats.GetProperty("reportedNotIndexed").EnumerateArray().Select(x => x.GetString())));
            return 0;
        }
        case ["sources"] or ["sources", "list"]: return await PrintGet("/search/sources");
        case ["sources", "add", .. var dirs] when dirs.Length > 0:
            using (var res = await http.PostAsJsonAsync("/search/sources", new { paths = dirs.Select(FullPath).ToArray() }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case ["sources", "rm", var dir]:
            using (var res = await http.DeleteAsync("/search/sources?path=" + Uri.EscapeDataString(FullPath(dir))))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case ["reindex"]:
            using (var res = await http.PostAsync("/search/reindex", null))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case ["erase"]:
            using (var res = await http.DeleteAsync("/search/index"))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case ["sources", ..]: Usage(); return 2;
    }
    var query = string.Join(' ', rest[0] == "--" ? rest[1..] : rest);
    if (string.IsNullOrWhiteSpace(query)) { Usage(); return 2; }
    using var response = await http.PostAsJsonAsync("/search", new { query });
    if (!response.IsSuccessStatusCode) { await PrintResponse(response); return 1; }
    var raw = await response.Content.ReadAsStringAsync();
    try
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("notice", out var notice) && notice.ValueKind == JsonValueKind.String)
            Console.WriteLine($"({notice.GetString()})");
        var results = root.GetProperty("results");
        if (results.GetArrayLength() == 0) Console.WriteLine("No matches.");
        var n = 0;
        foreach (var hit in results.EnumerateArray())
        {
            Console.WriteLine($"{++n}. {hit.GetProperty("path").GetString()}  ({hit.GetProperty("modified").GetDateTimeOffset().ToLocalTime():yyyy-MM-dd HH:mm})");
            Console.WriteLine($"   {hit.GetProperty("why").GetString()}");
            Console.WriteLine($"   {hit.GetProperty("snippet").GetString()?.ReplaceLineEndings(" ")}");
        }
    }
    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { Console.WriteLine(raw); }
    return 0;
}

static string FullPath(string path) => Path.GetFullPath(path == "~" || path.StartsWith("~/")
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
    : path);

async Task<int> PrintGet(string path)
{
    using var response = await http.GetAsync(path);
    await PrintResponse(response);
    return response.IsSuccessStatusCode ? 0 : 1;
}
static async Task PrintResponse(HttpResponseMessage response)
{
    var raw = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"nexus: {ErrorMessage(raw, response)} (HTTP {(int)response.StatusCode})");
        return;
    }
    try
    {
        using var document = JsonDocument.Parse(raw);
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (JsonException) { Console.WriteLine(raw); }
}
static string ErrorMessage(string raw, HttpResponseMessage response)
{
    try
    {
        using var document = JsonDocument.Parse(raw);
        foreach (var key in new[] { "detail", "error", "title" })
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                // Search source refusals carry a reason per path.
                if (!document.RootElement.TryGetProperty("refused", out var refused) || refused.ValueKind != JsonValueKind.Array)
                    return value.GetString()!;
                return value.GetString() + string.Concat(refused.EnumerateArray().Select(x =>
                    $"\n  {x.GetProperty("path").GetString()}: {x.GetProperty("reason").GetString()}"));
            }
    }
    catch (JsonException) { }
    return response.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => "Authentication failed; restart Core or check the api-token file.",
        System.Net.HttpStatusCode.TooManyRequests => "Nexus Core stopped an unusually fast run of requests (more than a person makes); it accepts them again within a minute.",
        _ => "Request failed."
    };
}
// Prints the answer as Core streams it.
async Task<int> StreamAnswer(string prompt)
{
    var ok = true;
    await foreach (var ev in ChatEvents(prompt))
    {
        var type = ev.GetProperty("type").GetString();
        if (type == "delta") { Console.Write(ev.GetProperty("text").GetString()); Console.Out.Flush(); }
        else if (type == "error") { Console.Error.WriteLine($"\nnexus: {ev.GetProperty("message").GetString()}"); ok = false; }
        else if (type == "done") Console.WriteLine();
        // Actions wait for the person's click in the Nexus window; the command line only says what was offered.
        else if (type == "action")
            Console.Error.WriteLine($"\nNexus offers to: {ev.GetProperty("summary").GetString()}. Ask in Nexus (Super+Space) to do it.");
    }
    return ok ? 0 : 1;
}

// Streams /chat/stream events; an HTTP error becomes a single error event.
IAsyncEnumerable<JsonElement> ChatEvents(string prompt) => StreamEvents("/chat/stream", new { prompt });

async IAsyncEnumerable<JsonElement> StreamEvents(string path, object body)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (!response.IsSuccessStatusCode)
    {
        var raw = await response.Content.ReadAsStringAsync();
        yield return JsonSerializer.SerializeToElement(new { type = "error", message = ErrorMessage(raw, response) });
        yield break;
    }
    using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
    while (await reader.ReadLineAsync() is { } line)
    {
        if (line.Length == 0) continue;
        using var doc = JsonDocument.Parse(line);
        yield return doc.RootElement.Clone();
    }
}

// A long-lived channel for the desktop shell: one JSON request per stdin line,
// one JSON event per stdout line, each tagged with the request's id. The shell
// never holds the API token itself; this process reads it like any CLI call.
//   {"id":1,"op":"chat","prompt":"..."}     -> delta, tool and action events, then done or error
//   (an action card's "Do it": {"op":"api","method":"POST","path":"/actions/<id>/confirm"})
//   {"id":2,"op":"suggest","prompt":"...","model":false}
//   {"id":3,"op":"apps"} | {"id":4,"op":"health"}
//   {"id":5,"op":"unload"}                  -> {"type":"unloaded","data":{"vramFreedBytes":N|null}}
// For Settings, Privacy and AI:
//   {"id":6,"op":"providers"}               -> {"type":"providers","data":{"selected":"local","providers":[...]}}
//   {"id":7,"op":"select","name":"local"}   -> {"type":"providers","data":{"selected":"local"}}
//   {"id":8,"op":"status"}                  -> {"type":"status","data":{"backend":..,"model":..,"available":..}}
//   {"id":9,"op":"clear-history"}           -> {"type":"cleared"}
// The coding agent (CodingAgent.cs):
//   {"id":10,"op":"code-start","folder":"~/projects/app"} -> {"type":"code-session","data":{"id":..,"folder":..,"tests":..}}
//   {"id":11,"op":"code-send","session":"..","text":"fix the bug"} -> step events, changes, message, done
//   {"id":12,"op":"code-accept","session":"..","files":["a.py"]|null} | code-reject | code-undo
//   {"id":13,"op":"code-run","session":"..","run":".."}  the person's Run on the agent's test card -> {"type":"code-result","data":{"type":"test-result",..}}
//   {"id":14,"op":"code-dismiss","session":"..","run":".."} | {"id":15,"op":"code-stop","session":".."}
// Images (ImageStudio.cs):
//   {"op":"images-status"} | {"op":"images-list"} | {"op":"images-delete","name":".."}
//   {"op":"images-generate","prompt":"..","shape":"landscape","count":4,"style":"painterly","seed":-1}
// Any other Core request the desktop's pages need, limited to these areas:
//   {"op":"api","method":"GET","path":"/journal?limit=50"}  -> {"type":"api","data":...}
//   {"op":"api","method":"POST","path":"/fixit","body":{"problem":"no sound"}}
// Streaming requests (chat, code-send), test runs and image generation run alongside the others, so a long
// answer never holds up a health check or a click.
async Task<int> Bridge()
{
    var output = Console.Out;
    void Emit(JsonElement? id, object payload)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(payload))!.AsObject();
        if (id is { } value) node["id"] = System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText());
        lock (output) { output.WriteLine(node.ToJsonString()); output.Flush(); }
    }
    Emit(null, new { type = "ready" });
    while (await Console.In.ReadLineAsync() is { } line)
    {
        if (line.Length == 0) continue;
        JsonElement req;
        try { using var doc = JsonDocument.Parse(line); req = doc.RootElement.Clone(); }
        catch (JsonException) { Emit(null, new { type = "error", message = "Bridge requests must be one JSON object per line." }); continue; }
        JsonElement? id = req.TryGetProperty("id", out var i) ? i : null;
        var op = req.TryGetProperty("op", out var o) ? o.GetString() : null;
        var prompt = req.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        string Arg(string key) => req.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (op == "api")
        {
            var method = Arg("method").ToUpperInvariant();
            var apiPath = Arg("path");
            object? apiBody = req.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? b : null;
            if (!BridgeApi.Allowed(method, apiPath))
            {
                Emit(id, new { type = "error", message = $"The desktop may not call {method} {apiPath}." });
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { Emit(id, await Call(new HttpMethod(method), apiPath, apiBody, "api")); }
                catch (HttpRequestException) { Emit(id, new { type = "error", message = "Nexus Core is not reachable." }); }
                catch (TaskCanceledException) { Emit(id, new { type = "error", message = "Nexus Core did not answer in time." }); }
            });
            continue;
        }
        if (op == "images-generate")
        {
            var body = JsonSerializer.Deserialize<JsonElement>(line);
            _ = Task.Run(async () =>
            {
                try { Emit(id, await Call(HttpMethod.Post, "/images/generate", body, "images")); }
                catch (HttpRequestException) { Emit(id, new { type = "error", message = "Nexus Core is not reachable." }); }
                catch (TaskCanceledException) { Emit(id, new { type = "error", message = "The image took too long." }); }
            });
            continue;
        }
        if (op == "code-run")
        {
            var runPath = $"/code/sessions/{Uri.EscapeDataString(Arg("session"))}/runs/{Uri.EscapeDataString(Arg("run"))}/confirm";
            _ = Task.Run(async () =>
            {
                try { Emit(id, await Call(HttpMethod.Post, runPath, new { }, "code-result")); }
                catch (HttpRequestException) { Emit(id, new { type = "error", message = "Nexus Core is not reachable." }); }
                catch (TaskCanceledException) { Emit(id, new { type = "error", message = "The tests took too long." }); }
            });
            continue;
        }
        if (op is "chat" or "code-send")
        {
            var path = op == "chat" ? "/chat/stream" : $"/code/sessions/{Uri.EscapeDataString(Arg("session"))}/messages";
            object body = op == "chat" ? new { prompt } : new { text = Arg("text") };
            _ = Task.Run(async () =>
            {
                try { await foreach (var ev in StreamEvents(path, body)) Emit(id, ev); }
                catch (HttpRequestException) { Emit(id, new { type = "error", message = "Nexus Core is not reachable." }); }
                catch (Exception ex) when (ex is TaskCanceledException or IOException) { Emit(id, new { type = "error", message = "Nexus Core did not answer in time." }); }
            });
            continue;
        }
        try
        {
            switch (op)
            {
                case "images-status":
                    Emit(id, await Call(HttpMethod.Get, "/images/status", null, "images-status"));
                    break;
                case "images-list":
                    Emit(id, await Call(HttpMethod.Get, "/images", null, "images"));
                    break;
                case "images-delete":
                    Emit(id, await Call(HttpMethod.Delete, $"/images/{Uri.EscapeDataString(Arg("name"))}", null, "images-deleted"));
                    break;
                case "code-start":
                    Emit(id, await Call(HttpMethod.Post, "/code/sessions", new { folder = Arg("folder") }, "code-session"));
                    break;
                case "code-accept":
                    string[]? files = req.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array
                        ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : null;
                    Emit(id, await Call(HttpMethod.Post, $"/code/sessions/{Uri.EscapeDataString(Arg("session"))}/accept", new { files }, "code-result"));
                    break;
                case "code-reject":
                case "code-undo":
                    Emit(id, await Call(HttpMethod.Post, $"/code/sessions/{Uri.EscapeDataString(Arg("session"))}/{op[5..]}", new { }, "code-result"));
                    break;
                case "code-dismiss":
                    Emit(id, await Call(HttpMethod.Post, $"/code/sessions/{Uri.EscapeDataString(Arg("session"))}/runs/{Uri.EscapeDataString(Arg("run"))}/dismiss", new { }, "code-result"));
                    break;
                case "code-stop":
                    Emit(id, await Call(HttpMethod.Post, $"/code/sessions/{Uri.EscapeDataString(Arg("session"))}/runs/stop", new { }, "code-result"));
                    break;
                case "suggest":
                    var model = req.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.True;
                    Emit(id, await Call(HttpMethod.Post, model ? "/suggest/model" : "/suggest", new { prompt }, "suggestions"));
                    break;
                case "apps":
                    Emit(id, await Call(HttpMethod.Get, "/apps", null, "apps"));
                    break;
                case "health":
                    Emit(id, await Call(HttpMethod.Get, "/health", null, "health"));
                    break;
                case "unload":
                    Emit(id, await Unload());
                    break;
                case "providers":
                    Emit(id, await Call(HttpMethod.Get, "/providers", null, "providers"));
                    break;
                case "select":
                    var name = req.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                    Emit(id, await Call(HttpMethod.Post, "/providers/select", new { name }, "providers"));
                    break;
                case "status":
                    Emit(id, await Call(HttpMethod.Get, "/inference/status", null, "status"));
                    break;
                case "clear-history":
                    Emit(id, await Call(HttpMethod.Delete, "/history", null, "cleared"));
                    break;
                default:
                    Emit(id, new { type = "error", message = $"Unknown bridge op '{op}'." });
                    break;
            }
        }
        catch (HttpRequestException) { Emit(id, new { type = "error", message = "Nexus Core is not reachable." }); }
        catch (TaskCanceledException) { Emit(id, new { type = "error", message = "Nexus Core did not answer in time." }); }
    }
    return 0;
}

// Frees the local model's memory and reports how much GPU memory came back,
// measured by Core's telemetry. vramFreedBytes is null when the GPU cannot be
// read or nothing measurable was freed; it is never estimated.
async Task<object> Unload()
{
    var before = await FreeVram();
    using (var response = await http.PostAsync("/inference/unload", null))
    {
        if (!response.IsSuccessStatusCode)
            return new { type = "error", message = ErrorMessage(await response.Content.ReadAsStringAsync(), response) };
    }
    long? freed = null;
    // The backend can take a moment to hand memory back to the driver.
    for (var attempt = 0; before is not null && attempt < 4; attempt++)
    {
        if (attempt > 0) await Task.Delay(500);
        if (await FreeVram() is { } after && after > before) { freed = after - before; break; }
    }
    return new { type = "unloaded", data = new { vramFreedBytes = freed } };
}

async Task<long?> FreeVram()
{
    try
    {
        var telemetry = await http.GetFromJsonAsync<JsonElement>("/inference/telemetry");
        return telemetry.TryGetProperty("vramFreeBytes", out var free) && free.ValueKind == JsonValueKind.Number
            ? free.GetInt64() : null;
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException) { return null; }
}

async Task<object> Call(HttpMethod method, string path, object? body, string type)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body);
    using var response = await http.SendAsync(request);
    var raw = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) return new { type = "error", message = ErrorMessage(raw, response) };
    if (string.IsNullOrWhiteSpace(raw)) return new { type, data = (JsonElement?)null };
    using var doc = JsonDocument.Parse(raw);
    return new { type, data = doc.RootElement.Clone() };
}

static int RunTrial(string[] trialArgs)
{
    var start = new System.Diagnostics.ProcessStartInfo("nexus-try") { UseShellExecute = false };
    foreach (var arg in trialArgs) start.ArgumentList.Add(arg);
    try
    {
        using var trial = System.Diagnostics.Process.Start(start)!;
        trial.WaitForExit();
        return trial.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception)
    {
        Console.Error.WriteLine("nexus: nexus-try is not installed (services.nexus-desktop.tryApps.enable).");
        return 1;
    }
}
static void Usage()
{
    Console.WriteLine("nexus [health [report]|fixit <problem>|fixit apply <fix id> <token>|system|providers|status|unload|history|clear-history|use <local|llama-cpp|openai-compatible>|ask [--stream] <prompt>|suggest <prompt>|bridge|journal [--today] [--kind K]|journal forget-today|try <app> [-- args]|try keep <app>]");
    Console.WriteLine(RulesCommand.Usage);
    Console.WriteLine("nexus search <query> | search status | search formats | search reindex | search erase");
    Console.WriteLine("nexus search sources [list | add <folder>... | rm <folder>]   (search -- <query> to search for a subcommand word)");
}

// The Core areas the desktop's pages may call through the bridge's "api" op.
static class BridgeApi
{
    private static readonly string[] Prefixes =
        ["/journal", "/fixit", "/health/report", "/search", "/rules", "/events", "/guardian/propose", "/guardian/preview",
         "/guardian/proposals", "/guardian/review", "/guardian/stage",
         "/try/keep", "/settings/plan", "/workspaces", "/history", "/system", "/actions", "/images", "/explain/keep"];

    public static bool Allowed(string method, string path) =>
        method is "GET" or "POST" or "DELETE" && path.StartsWith('/') && !path.Contains("..") &&
        Prefixes.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal) || path.StartsWith(p + "?", StringComparison.Ordinal));
}
