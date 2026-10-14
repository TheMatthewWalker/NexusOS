using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5077"); // MVP: loopback only, no remote access.
builder.Services.AddHttpClient("ai", client => client.Timeout = TimeSpan.FromSeconds(180))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddSingleton<NexusStatePaths>();
builder.Services.AddSingleton<ProviderStore>();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddSingleton<IProviderCredentialStore, LinuxSecretToolStore>();
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<InferenceLifecycle>();
builder.Services.AddSingleton<GuardianAuditStore>();
builder.Services.AddSingleton<LocalApiToken>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ai", _ => RateLimitPartition.GetFixedWindowLimiter("single-user", __ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
var app = builder.Build();
app.Use(async (context, next) =>
{
    var token = context.RequestServices.GetRequiredService<LocalApiToken>();
    var auth = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !token.Verify(auth[prefix.Length..]))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsJsonAsync(new { error = "Authentication required" });
        return;
    }
    await next(context);
});
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "NexusOS.Core", version = "0.2.0-dev" }));
app.MapGet("/providers", (ProviderStore store) => Results.Ok(new { selected = store.Selected, providers = store.Names }));
app.MapPost("/providers/select", (SelectRequest request, ProviderStore store) =>
{
    if (!store.Select(request.Name)) return Results.BadRequest(new { error = "Unknown provider" });
    return Results.Ok(new { selected = store.Selected });
});
app.MapDelete("/history", async (ConversationStore history, CancellationToken ct) => { await history.ClearAsync(ct); return Results.NoContent(); });
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
}).RequireRateLimiting("ai");
app.MapPost("/guardian/preview", (GuardianPreviewRequest request) =>
{
    try { return Results.Ok(GuardianPlanner.Preview(request.Description, request.Changes)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Unsupported or invalid change plan." }); }
});
app.MapPost("/guardian/acknowledge", async (GuardianApprovalRequest request, GuardianAuditStore audit, CancellationToken ct) =>
{
    if (request.Acknowledgement != "I APPROVE")
        return Results.BadRequest(new { error = "Explicit acknowledgement required." });
    try
    {
        var plan = GuardianPlanner.Preview(request.Description, request.Changes);
        if (plan.Id != request.ExpectedPlanId)
            return Results.BadRequest(new { error = "Plan ID does not match the reviewed preview." });
        var entry = await audit.RecordApprovalAsync(plan, ct);
        return Results.Ok(new { accepted = true, executed = false, auditHash = entry.Hash });
    }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Invalid plan." }); }
    catch (InvalidDataException) { return Results.Problem("Audit verification failed; no action recorded.", statusCode: 409); }
});
app.MapGet("/inference/status", async (InferenceLifecycle life, CancellationToken ct) =>
    Results.Ok(new { backend = "ollama", available = await life.IsBackendAvailableAsync(ct) }));
app.MapPost("/inference/unload", async (InferenceLifecycle life, CancellationToken ct) =>
{
    try { await life.UnloadAsync(ct); return Results.Ok(new { unloadRequested = true }); }
    catch (HttpRequestException) { return Results.Problem("Local inference backend unavailable", statusCode: 502); }
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
public sealed record GuardianPreviewRequest(string Description, GuardianChange[] Changes);
public sealed record GuardianApprovalRequest(string Description, GuardianChange[] Changes, string ExpectedPlanId, string Acknowledgement);

public sealed class ModelRouter(IHttpClientFactory factory, ProviderStore providers, IConfiguration config, IProviderCredentialStore credentials)
{
    public string CurrentProvider => providers.Selected;
    public async Task<(string Answer, string Provider)> AskAsync(string prompt, CancellationToken ct)
    {
        var provider = CurrentProvider;
        var client = factory.CreateClient("ai");
        if (provider == "local")
        {
            var url = config["NEXUS_OLLAMA_URL"] ?? "http://127.0.0.1:11434";
            ProviderEndpointGuard.RequireLoopback(url);
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
            ProviderEndpointGuard.RequireCloudEndpoint(endpoint, config["NEXUS_CLOUD_HOSTS"]);
            var token = await credentials.GetAsync(provider, ct);
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("OpenAI credential missing from OS keyring.");
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
