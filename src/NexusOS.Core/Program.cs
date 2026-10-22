using Microsoft.AspNetCore.Connections.Features;
using System.Threading.RateLimiting;

// Content root is the install directory, never the caller's working
// directory: Core must start however it is launched (systemd, runuser, a
// shell in an unreadable directory), and it serves no files from disk.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var apiMode = ApiModes.Resolve(builder.Configuration["NEXUS_API_MODE"]);
if (apiMode == ApiMode.Unix)
{
    var state = new NexusStatePaths(builder.Configuration);
    var socketPath = state.PathFor("nexus.sock");
    UnixSocketGuard.PrepareListenPath(socketPath);
    builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath, listen =>
    {
        listen.Use(next => connection =>
        {
            var socket = connection.Features.Get<IConnectionSocketFeature>()?.Socket;
            if (!LinuxPeerIdentity.IsAuthorized(LinuxPeerIdentity.ReadPeerUserId(socket),
                    LinuxPeerIdentity.ServerUserId()))
            {
                connection.Abort();
                return Task.CompletedTask;
            }
            return next(connection);
        });
    }));
}
else
{
    builder.WebHost.UseUrls("http://127.0.0.1:5077");
}
// Trace-level HttpClient logging writes request headers; redact every value so
// provider credentials can never reach logs, whatever the configured level.
builder.Services.AddHttpClient("ai", client => client.Timeout = TimeSpan.FromSeconds(180))
    .RedactLoggedHeaders(_ => true)
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddHttpClient("cloud", client => client.Timeout = TimeSpan.FromSeconds(180))
    .RedactLoggedHeaders(_ => true)
    .ConfigurePrimaryHttpMessageHandler(() => CloudHttp.CreateHandler(CloudConnector.Default));
builder.Services.AddSingleton<NexusStatePaths>();
builder.Services.AddSingleton<SqliteStateDatabase>();
builder.Services.AddSingleton<ProviderStore>(sp => new ProviderStore(sp.GetRequiredService<SqliteStateDatabase>()));
builder.Services.AddSingleton<ConversationStore>(sp => new ConversationStore(
    sp.GetRequiredService<SqliteStateDatabase>(), sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IProviderCredentialStore, LinuxSecretToolStore>();
builder.Services.AddSingleton<LlamaServerProcess>();
builder.Services.AddSingleton<IModelProvider, OllamaProvider>();
builder.Services.AddSingleton<IModelProvider>(sp => new LlamaCppProvider(
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>(),
    sp.GetRequiredService<LlamaServerProcess>()));
builder.Services.AddSingleton<IModelProvider, OpenAiCompatibleProvider>();
builder.Services.AddSingleton<ModelRouter>();
builder.Services.AddSingleton<InferenceActivity>();
builder.Services.AddSingleton<InferenceConcurrencyGate>();
builder.Services.AddSingleton<InferenceTokenSettings>();
builder.Services.AddHostedService<IdleModelEvictionService>();
builder.Services.AddSingleton<MemoryTelemetry>();
builder.Services.AddSingleton<GuardianBrokerClient>();
builder.Services.AddSingleton<GuardianProposer>();
builder.Services.AddSingleton<LocalApiToken>();
builder.Services.AddNexusSearch(); // Private search (M8): SearchEndpoints.cs
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
// Resolve configuration-dependent services now so invalid settings stop Core
// at startup with a clear message instead of failing individual requests.
_ = app.Services.GetRequiredService<LocalApiToken>();
_ = app.Services.GetRequiredService<ModelRouter>();
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
app.MapPost("/chat", async (ChatRequest request, ModelRouter router, ConversationStore history, InferenceActivity activity, InferenceConcurrencyGate concurrency, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32768)
        return Results.BadRequest(new { error = "Prompt must be between 1 and 32768 characters." });
    using var slot = await concurrency.TryEnterAsync(ct);
    if (slot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    try
    {
        activity.BeginRequest();
        (string response, string provider) answer;
        try { answer = await router.AskAsync(request.Prompt, ct); }
        finally { activity.EndRequest(); }
        var (response, provider) = answer;
        await history.AppendAsync(new ConversationEntry(DateTimeOffset.UtcNow, provider, request.Prompt, response), ct);
        return Results.Ok(new { answer = response, provider });
    }
    catch (ProviderException ex)
    {
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
}).RequireRateLimiting("ai");
app.MapPost("/guardian/preview", (GuardianPreviewRequest request) =>
{
    try { return Results.Ok(GuardianPlanner.Preview(request.Description, request.Changes)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Unsupported or invalid change plan." }); }
});
// The model may only *propose* a typed plan; the response is a preview the
// user must explicitly submit. Invalid model output is rejected outright.
app.MapPost("/guardian/propose", async (GuardianProposeRequest request, GuardianProposer proposer,
    InferenceConcurrencyGate concurrency, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Request) || request.Request.Length > GuardianProposer.MaxRequestLength)
        return Results.BadRequest(new { error = $"Request must be 1-{GuardianProposer.MaxRequestLength} characters." });
    using var slot = await concurrency.TryEnterAsync(ct);
    if (slot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    try
    {
        var proposal = await proposer.ProposeAsync(request.Request, ct);
        return proposal.Plan is null
            ? Results.Ok(new { proposed = false, refusal = proposal.Refusal, provider = proposal.Provider })
            : Results.Ok(new { proposed = true, plan = proposal.Plan, provider = proposal.Provider,
                               submitted = false, next = "Review the plan, then POST it to /guardian/submit." });
    }
    catch (ProposalRejectedException ex) { return Results.Problem(ex.Message, statusCode: 422); }
    catch (ProviderException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
}).RequireRateLimiting("ai");
// Guardian decisions belong to the root-owned broker (ADR-002). Core validates
// the typed plan locally (same shared schema) and forwards it; it never
// approves, records decisions or applies changes.
app.MapPost("/guardian/submit", async (GuardianPreviewRequest request, GuardianBrokerClient broker, CancellationToken ct) =>
{
    GuardianPlan plan;
    try { plan = GuardianPlanner.Preview(request.Description, request.Changes); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Unsupported or invalid change plan." }); }
    try
    {
        var response = await broker.SendAsync("submit", new
        {
            description = plan.Description,
            changes = plan.Changes.Select(x => new { option = x.Option, enabled = x.Enabled })
        }, ct);
        return Results.Ok(new { planId = plan.Id, broker = response });
    }
    catch (GuardianBrokerException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
});
app.MapGet("/guardian/candidates/{candidateId}", async (string candidateId, GuardianBrokerClient broker, CancellationToken ct) =>
{
    if (candidateId.Length != 24 || !candidateId.All(Uri.IsHexDigit))
        return Results.BadRequest(new { error = "Invalid candidate ID." });
    try { return Results.Ok(await broker.SendAsync("status", new { candidateId }, ct)); }
    catch (GuardianBrokerException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
});
app.MapPost("/guardian/review", async (GuardianReviewRequest request, GuardianBrokerClient broker, CancellationToken ct) =>
{
    if (request.CandidateId is not { Length: 24 } || !request.CandidateId.All(Uri.IsHexDigit) ||
        request.Attestation.ValueKind != System.Text.Json.JsonValueKind.Object)
        return Results.BadRequest(new { error = "A candidate ID and attestation object are required." });
    try
    {
        return Results.Ok(await broker.SendAsync("review",
            new { candidateId = request.CandidateId, attestation = request.Attestation }, ct));
    }
    catch (GuardianBrokerException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
});
app.MapGet("/inference/telemetry", async (MemoryTelemetry telemetry, CancellationToken ct) =>
    Results.Ok(await telemetry.ReadAsync(ct)));
app.MapGet("/inference/status", async (ModelRouter router, CancellationToken ct) =>
{
    var provider = router.Current;
    return Results.Ok(new { backend = provider.Name, available = await provider.IsAvailableAsync(ct),
                            unloadSupported = provider.SupportsUnload });
});
app.MapPost("/inference/unload", async (ModelRouter router, CancellationToken ct) =>
{
    try { await router.Current.UnloadAsync(ct); return Results.Ok(new { unloadRequested = true }); }
    catch (ProviderException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
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
app.MapNexusSearch();
app.Run();

public sealed record ChatRequest(string Prompt);
public sealed record SelectRequest(string Name);
public sealed record GuardianPreviewRequest(string Description, GuardianChange[] Changes);
public sealed record GuardianProposeRequest(string Request);
public sealed record GuardianReviewRequest(string CandidateId, System.Text.Json.JsonElement Attestation);

public partial class Program;
