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
if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("NEXUS_API_MODE") == "unix")
{
    var socketPath = Path.Combine(root, "nexus.sock");
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
using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5077"), Timeout = TimeSpan.FromMinutes(4) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
if (args.Length == 0) { Usage(); return 2; }
try
{
    switch (args[0].ToLowerInvariant())
    {
        case "health":
            await PrintGet("/health"); return 0;
        case "system":
            await PrintGet("/system"); return 0;
        case "providers":
            await PrintGet("/providers"); return 0;
        case "history":
            await PrintGet("/history"); return 0;
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

async Task PrintGet(string path)
{
    using var response = await http.GetAsync(path);
    await PrintResponse(response);
}
static async Task PrintResponse(HttpResponseMessage response)
{
    var raw = await response.Content.ReadAsStringAsync();
    try
    {
        using var document = JsonDocument.Parse(raw);
        Console.WriteLine(JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (JsonException) { Console.WriteLine(raw); }
}
static void Usage() => Console.WriteLine("nexus [health|system|providers|history|clear-history|use <local|llama-cpp|openai-compatible>|ask <prompt>]");
