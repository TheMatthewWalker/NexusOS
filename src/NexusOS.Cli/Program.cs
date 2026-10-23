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
        case "ask" when args.Length > 2 && args[1] == "--stream":
            return await StreamAnswer(string.Join(' ', args.Skip(2)));
        case "bridge":
            return await Bridge();
        case "suggest" when args.Length > 1:
            using (var res = await http.PostAsJsonAsync("/suggest", new { prompt = string.Join(' ', args.Skip(1)) }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        case "ask" when args.Length > 1:
            using (var res = await http.PostAsJsonAsync("/chat", new { prompt = string.Join(' ', args.Skip(1)) }))
            { await PrintResponse(res); return res.IsSuccessStatusCode ? 0 : 1; }
        default: Usage(); return 2;
    }
}
catch (HttpRequestException ex) { Console.Error.WriteLine($"Nexus Core not reachable: {ex.Message}"); return 1; }
catch (TaskCanceledException) { Console.Error.WriteLine("Request timed out."); return 1; }

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
    }
    return ok ? 0 : 1;
}

// Streams /chat/stream events; an HTTP error becomes a single error event.
async IAsyncEnumerable<JsonElement> ChatEvents(string prompt)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, "/chat/stream") { Content = JsonContent.Create(new { prompt }) };
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
//   {"id":1,"op":"chat","prompt":"..."}     -> delta events, then done or error
//   {"id":2,"op":"suggest","prompt":"...","model":false}
//   {"id":3,"op":"apps"} | {"id":4,"op":"health"}
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
        try
        {
            switch (op)
            {
                case "chat":
                    await foreach (var ev in ChatEvents(prompt)) Emit(id, ev);
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

async Task<object> Call(HttpMethod method, string path, object? body, string type)
{
    using var request = new HttpRequestMessage(method, path);
    if (body is not null) request.Content = JsonContent.Create(body);
    using var response = await http.SendAsync(request);
    var raw = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) return new { type = "error", message = ErrorMessage(raw, response) };
    using var doc = JsonDocument.Parse(raw);
    return new { type, data = doc.RootElement.Clone() };
}

static void Usage() => Console.WriteLine("nexus [health|system|providers|status|unload|history|clear-history|use <local|llama-cpp|openai-compatible>|ask [--stream] <prompt>|suggest <prompt>|bridge]");
