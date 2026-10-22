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
static void Usage() => Console.WriteLine("nexus [health|system|providers|status|unload|history|clear-history|use <local|llama-cpp|openai-compatible>|ask <prompt>|try <app> [-- args]|try keep <app>]");
