using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Sockets;

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
        case "unload":
            using (var res = await http.PostAsync("/inference/unload", null))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "clear-history":
            using (var res = await http.DeleteAsync("/history"))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "use" when args.Length == 2:
            using (var res = await http.PostAsJsonAsync("/providers/select", new { name = args[1] }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "ask" when args.Length > 1:
            using (var res = await http.PostAsJsonAsync("/chat", new { prompt = string.Join(' ', args.Skip(1)) }))
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
                return value.GetString()!;
    }
    catch (JsonException) { }
    return response.StatusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => "Authentication failed; restart Core or check the api-token file.",
        System.Net.HttpStatusCode.TooManyRequests => "Nexus Core is busy or rate limiting requests; try again shortly.",
        _ => "Request failed."
    };
}
static void Usage() => Console.WriteLine("nexus [health [report]|fixit <problem>|fixit apply <fix id> <token>|system|providers|status|unload|history|clear-history|use <local|llama-cpp|openai-compatible>|ask <prompt>]");
