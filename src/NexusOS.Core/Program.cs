using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5077"); // MVP: loopback only, no remote access.
builder.Services.AddHttpClient("ai", client => client.Timeout = TimeSpan.FromSeconds(180));
builder.Services.AddSingleton<NexusStatePaths>();
builder.Services.AddSingleton<ProviderStore>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<ModelRouter>();
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "NexusOS.Core", version = "0.2.0-dev" }));
app.MapGet("/providers", (ProviderStore store) => Results.Ok(new { selected = store.Selected, providers = store.Names }));
app.MapPost("/providers/select", (SelectRequest request, ProviderStore store) =>
{
    if (!store.Select(request.Name)) return Results.BadRequest(new { error = "Unknown provider" });
    return Results.Ok(new { selected = store.Selected });
});
app.MapGet("/history", async (ConversationStore history, CancellationToken ct) => Results.Ok(await history.RecentAsync(50, ct)));
app.MapPost("/chat", async (ChatRequest request, ModelRouter router, ConversationStore history, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32768)
        return Results.BadRequest(new { error = "Prompt must be between 1 and 32768 characters." });
    try
    {
        var (response, provider) = await router.AskAsync(request.Prompt, ct);
        await history.AppendAsync(new ConversationEntry(DateTimeOffset.UtcNow, provider, request.Prompt, response), ct);
        return Results.Ok(new { answer = response, provider });
    }
    catch (HttpRequestException)
    {
        return Results.Problem("The model endpoint is unavailable. Check the provider service and configuration.", statusCode: 502);
    }
    catch (TaskCanceledException) when (!ct.IsCancellationRequested)
    {
        return Results.Problem("The model request timed out.", statusCode: 504);
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
});
app.MapGet("/system", () => Results.Ok(new
{
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
    processorCount = Environment.ProcessorCount,
    memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
    machineName = Environment.MachineName
})); // Deliberately read-only, limited metadata.
app.Run();

public sealed record ChatRequest(string Prompt);
public sealed record SelectRequest(string Name);

public sealed class ModelRouter(IHttpClientFactory factory, ProviderStore providers, IConfiguration config)
{
    public string CurrentProvider => providers.Selected;
    public async Task<(string Answer, string Provider)> AskAsync(string prompt, CancellationToken ct)
    {
        var provider = CurrentProvider;
        var client = factory.CreateClient("ai");
        if (provider == "local")
        {
            var url = config["NEXUS_OLLAMA_URL"] ?? "http://127.0.0.1:11434";
            // This service is single-user, local-only for MVP. Endpoints must be configured by operator.
            var model = config["NEXUS_OLLAMA_MODEL"] ?? "llama3.2:3b";
            using var response = await client.PostAsJsonAsync(url.TrimEnd('/') + "/api/generate",
                new { model, prompt, stream = false }, ct);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return (doc.RootElement.GetProperty("response").GetString() ?? string.Empty, provider);
        }
        if (provider == "openai-compatible")
        {
            var endpoint = config["NEXUS_OPENAI_ENDPOINT"] ?? "https://api.openai.com/v1";
            var token = config["NEXUS_OPENAI_API_KEY"];
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("Set NEXUS_OPENAI_API_KEY before using openai-compatible.");
            var model = config["NEXUS_OPENAI_MODEL"] ?? "gpt-4.1-mini";
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/chat/completions");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model, messages = new[] { new { role = "user", content = prompt } }
            }), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(message, ct);
            response.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return (doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty, provider);
        }
        throw new InvalidOperationException("Unknown provider");
    }
}
