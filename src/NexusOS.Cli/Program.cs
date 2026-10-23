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
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5077"), Timeout = TimeSpan.FromMinutes(4) };
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
        System.Net.HttpStatusCode.TooManyRequests => "Nexus Core is busy or rate limiting requests; try again shortly.",
        _ => "Request failed."
    };
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
