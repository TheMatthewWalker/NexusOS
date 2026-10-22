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
        case "health":
            return await PrintGet("/health");
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
        case "search" when args.Length > 1:
            return await Search(args[1..]);
        case "ask" when args.Length > 1:
            using (var res = await http.PostAsJsonAsync("/chat", new { prompt = string.Join(' ', args.Skip(1)) }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        default: Usage(); return 2;
    }
}
catch (HttpRequestException ex) { Console.Error.WriteLine($"Nexus Core not reachable: {ex.Message}"); return 1; }
catch (TaskCanceledException) { Console.Error.WriteLine("Request timed out."); return 1; }

// nexus search <query> | status | reindex | erase | sources [list|add <dir>...|rm <dir>]
// A query that starts with a subcommand word can be given after "--".
async Task<int> Search(string[] rest)
{
    switch (rest)
    {
        case ["status"]: return await PrintGet("/search/status");
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
static void Usage()
{
    Console.WriteLine("nexus [health|system|providers|status|unload|history|clear-history|use <local|llama-cpp|openai-compatible>|ask <prompt>]");
    Console.WriteLine("nexus search <query> | search status | search reindex | search erase");
    Console.WriteLine("nexus search sources [list | add <folder>... | rm <folder>]   (search -- <query> to search for a subcommand word)");
}
