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
builder.Services.AddSingleton<ActivityJournal>();
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
// Automations in plain words: rule store, engine, parser and timer (RuleEndpoints.cs).
builder.Services.AddRules();
builder.Services.AddHostedService<IdleModelEvictionService>();
builder.Services.AddSingleton<MemoryTelemetry>();
builder.Services.AddSingleton<GuardianBrokerClient>();
builder.Services.AddSingleton<GuardianProposer>();
builder.Services.AddSingleton<AppSuggester>();
builder.Services.AddSingleton<LocalApiToken>();
// Fix it and the health report: read-only probes and the fix library (FixIt.cs).
builder.Services.AddFixIt();
builder.Services.AddNexusSearch(); // Private search (M8): SearchEndpoints.cs
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("ai", _ => RateLimitPartition.GetFixedWindowLimiter("single-user", __ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));
    options.AddPolicy("journal", _ => RateLimitPartition.GetFixedWindowLimiter("single-user", __ => new FixedWindowRateLimiterOptions
    {
        PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
    }));
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
var app = builder.Build();
// Resolve configuration-dependent services now so invalid settings stop Core
// at startup with a clear message instead of failing individual requests.
_ = app.Services.GetRequiredService<LocalApiToken>();
_ = app.Services.GetRequiredService<ModelRouter>();
_ = app.Services.GetRequiredService<FixLibrary>();
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
app.MapDelete("/history", async (ConversationStore history, RuleStore rules, CancellationToken ct) =>
    { await history.ClearAsync(ct); await rules.ClearRunsAsync(ct); return Results.NoContent(); });
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
// Streams the answer as newline-delimited JSON events:
//   {"type":"delta","text":"..."}  repeated, then {"type":"done","provider":"..."}
// An error before the first piece is an ordinary HTTP problem response; an
// error mid-answer becomes {"type":"error","message":"..."}. Only completed
// answers are written to history.
app.MapPost("/chat/stream", async (ChatRequest request, HttpContext http, ModelRouter router, ConversationStore history,
    InferenceActivity activity, InferenceConcurrencyGate concurrency, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 32768)
        return Results.BadRequest(new { error = "Prompt must be between 1 and 32768 characters." });
    using var slot = await concurrency.TryEnterAsync(ct);
    if (slot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    var answer = new System.Text.StringBuilder();
    var started = false;
    async Task WriteEvent(object value)
    {
        if (!started)
        {
            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.CacheControl = "no-store";
            started = true;
        }
        await http.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(value) + "\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
    activity.BeginRequest();
    try
    {
        var (pieces, provider) = router.Stream(request.Prompt, ct);
        await foreach (var piece in pieces)
        {
            answer.Append(piece);
            await WriteEvent(new { type = "delta", text = piece });
        }
        await history.AppendAsync(new ConversationEntry(DateTimeOffset.UtcNow, provider, request.Prompt, answer.ToString()), ct);
        await WriteEvent(new { type = "done", provider });
        return Results.Empty;
    }
    catch (ProviderException ex)
    {
        if (!started) return Results.Problem(ex.Message, statusCode: ex.StatusCode);
        await WriteEvent(new { type = "error", message = ex.Message });
        return Results.Empty;
    }
    finally { activity.EndRequest(); }
}).RequireRateLimiting("ai");
// App suggestions for the desktop. /suggest uses the catalogue's rules: it is
// instant and runs while the user types. /suggest/model asks the selected
// model to choose from the catalogue and, if it cannot, falls back to the
// rules, labelled as such.
app.MapPost("/suggest", async (SuggestRequest request, AppSuggester suggester, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > AppSuggester.MaxPromptLength)
        return Results.BadRequest(new { error = $"Prompt must be 1-{AppSuggester.MaxPromptLength} characters." });
    return Results.Ok(await suggester.SuggestAsync(request.Prompt, false, ct));
});
app.MapPost("/suggest/model", async (SuggestRequest request, AppSuggester suggester, InferenceConcurrencyGate concurrency, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > AppSuggester.MaxPromptLength)
        return Results.BadRequest(new { error = $"Prompt must be 1-{AppSuggester.MaxPromptLength} characters." });
    using var slot = await concurrency.TryEnterAsync(ct);
    if (slot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    return Results.Ok(await suggester.SuggestAsync(request.Prompt, true, ct));
}).RequireRateLimiting("ai");
app.MapGet("/apps", () => Results.Ok(AppCatalogue.Roles.Select(r => new { id = r.Id, label = r.Label, detail = r.Detail })));
app.MapPost("/guardian/preview", (GuardianPreviewRequest request) =>
{
    try { return Results.Ok(GuardianPlanner.Preview(request.Description, request.Changes)); }
    catch (ArgumentException) { return Results.BadRequest(new { error = "Unsupported or invalid change plan." }); }
});
// The model may only *propose* a typed plan; the response is a preview the
// user must explicitly submit. Invalid model output is rejected outright.
app.MapPost("/guardian/propose", async (GuardianProposeRequest request, GuardianProposer proposer,
    InferenceConcurrencyGate concurrency, ActivityJournal journal, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Request) || request.Request.Length > GuardianProposer.MaxRequestLength)
        return Results.BadRequest(new { error = $"Request must be 1-{GuardianProposer.MaxRequestLength} characters." });
    using var slot = await concurrency.TryEnterAsync(ct);
    if (slot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    try
    {
        var proposal = await proposer.ProposeAsync(request.Request, ct);
        if (proposal.Plan is { } proposed)
            await journal.AppendAsync(new JournalRecord(JournalKinds.System, "nexus",
                Summary("Nexus proposed a system change: ", proposed.Description), Ref: "plan:" + proposed.Id), ct);
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
// approves or applies changes, and only journals what the broker reports.
app.MapPost("/guardian/submit", async (GuardianPreviewRequest request, GuardianBrokerClient broker, ActivityJournal journal,
    CancellationToken ct) =>
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
        var candidate = response.TryGetProperty("candidate", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Object &&
            c.TryGetProperty("candidateId", out var cid) && cid.ValueKind == System.Text.Json.JsonValueKind.String &&
            cid.GetString() is { Length: 24 } id && id.All(Uri.IsHexDigit) ? id : null;
        await journal.AppendAsync(new JournalRecord(JournalKinds.System, "user",
            Summary("Sent to Guardian for approval: ", plan.Description),
            Ref: candidate is null ? "plan:" + plan.Id : "candidate:" + candidate), ct);
        return Results.Ok(new { planId = plan.Id, broker = response });
    }
    catch (GuardianBrokerException ex)
    {
        if (ex.StatusCode == 422)
            await journal.AppendAsync(new JournalRecord(JournalKinds.System, "guardian",
                Summary("Guardian refused a change: ", plan.Description), Ref: "plan:" + plan.Id), ct);
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
});
app.MapGet("/guardian/candidates/{candidateId}", async (string candidateId, GuardianBrokerClient broker, CancellationToken ct) =>
{
    if (candidateId.Length != 24 || !candidateId.All(Uri.IsHexDigit))
        return Results.BadRequest(new { error = "Invalid candidate ID." });
    try { return Results.Ok(await broker.SendAsync("status", new { candidateId }, ct)); }
    catch (GuardianBrokerException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
});
app.MapPost("/guardian/review", async (GuardianReviewRequest request, GuardianBrokerClient broker, ActivityJournal journal,
    CancellationToken ct) =>
{
    if (request.CandidateId is not { Length: 24 } || !request.CandidateId.All(Uri.IsHexDigit) ||
        request.Attestation.ValueKind != System.Text.Json.JsonValueKind.Object)
        return Results.BadRequest(new { error = "A candidate ID and attestation object are required." });
    // Guardian's decision is recorded as reported; Core does not judge it.
    try
    {
        var decision = await broker.SendAsync("review",
            new { candidateId = request.CandidateId, attestation = request.Attestation }, ct);
        var eligible = decision.TryGetProperty("eligible", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.True;
        await journal.AppendAsync(new JournalRecord(JournalKinds.System, "guardian",
            eligible ? "Guardian approved a change for staging" : "Guardian found a change not eligible for staging",
            Ref: "candidate:" + request.CandidateId), ct);
        return Results.Ok(decision);
    }
    catch (GuardianBrokerException ex)
    {
        if (ex.StatusCode == 422)
            await journal.AppendAsync(new JournalRecord(JournalKinds.System, "guardian", "Guardian refused a review",
                Ref: "candidate:" + request.CandidateId), ct);
        return Results.Problem(ex.Message, statusCode: ex.StatusCode);
    }
});
// Activity journal. The shell records the setting and device events only it
// can observe; Core records egress and Guardian events itself. Writes are
// rate limited; bounded reads are not, so a dashboard can poll.
app.MapPost("/journal", async (JournalPostRequest request, ActivityJournal journal, CancellationToken ct) =>
{
    if (request.Kind is null || !JournalKinds.FromShell.Contains(request.Kind))
        return Results.BadRequest(new { error = "Only setting and device entries can be recorded." });
    var record = new JournalRecord(request.Kind, request.Actor ?? "user", request.Summary ?? "",
        request.Before, request.After, request.Reversible, request.Ref);
    if (record.Actor == "nexus") return Results.BadRequest(new { error = "Only Core records entries as 'nexus'." });
    if (ActivityJournal.Validate(record) is { } error) return Results.BadRequest(new { error });
    return Results.Ok(new { id = await journal.AppendAsync(record, ct) });
}).RequireRateLimiting("journal");
app.MapGet("/journal", async (string? since, string? kind, int? limit, ActivityJournal journal, CancellationToken ct) =>
{
    DateTimeOffset? from = null;
    if (since is not null)
    {
        if (!DateTimeOffset.TryParse(since, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            return Results.BadRequest(new { error = "since must be an ISO 8601 time." });
        from = parsed;
    }
    if (kind is not null && !JournalKinds.All.Contains(kind)) return Results.BadRequest(new { error = "Unknown journal kind." });
    if (limit is < 1 or > ActivityJournal.MaxPage)
        return Results.BadRequest(new { error = $"limit must be 1-{ActivityJournal.MaxPage}." });
    return Results.Ok(await journal.QueryAsync(from, kind, limit ?? 100, ct));
});
app.MapGet("/journal/today/summary", async (ActivityJournal journal, CancellationToken ct) =>
    Results.Ok(await journal.SummaryAsync(ActivityJournal.Today, ct)));
app.MapPost("/journal/{id:long}/undo", async (long id, ActivityJournal journal, CancellationToken ct) =>
{
    var (entry, conflict) = await journal.UndoAsync(id, ct);
    if (entry is null) return Results.NotFound(new { error = "No such journal entry." });
    if (conflict is not null) return Results.Conflict(new { error = conflict });
    return Results.Ok(new { id, undone = true, restore = entry.Before, @ref = entry.Ref,
                            instruction = "Write 'restore' back to the setting named by 'ref'. Core does not change shell settings." });
}).RequireRateLimiting("journal");
app.MapDelete("/journal", async (string? day, ActivityJournal journal, CancellationToken ct) =>
{
    if (!DateOnly.TryParseExact(day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date))
        return Results.BadRequest(new { error = "day must be YYYY-MM-DD." });
    return Results.Ok(new { day, erased = await journal.ForgetDayAsync(date, ct) });
}).RequireRateLimiting("journal");
app.MapGet("/inference/telemetry", async (MemoryTelemetry telemetry, CancellationToken ct) =>
    Results.Ok(await telemetry.ReadAsync(ct)));
// /rules, /rules/parse, /events and /rules/pending-actions (RuleEndpoints.cs).
app.MapRules();
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
// POST /fixit, POST /fixit/apply, GET /health/report (FixIt.cs).
app.MapFixIt();
app.MapNexusSearch();
app.Run();

// Journal summaries are one short line: drop control characters and truncate.
static string Summary(string prefix, string detail)
{
    var text = prefix + new string(detail.Where(ch => !char.IsControl(ch)).ToArray());
    return text.Length <= ActivityJournal.MaxSummaryLength ? text : text[..(ActivityJournal.MaxSummaryLength - 1)] + "…";
}

public sealed record ChatRequest(string Prompt);
public sealed record SuggestRequest(string Prompt);
public sealed record SelectRequest(string Name);
public sealed record GuardianPreviewRequest(string Description, GuardianChange[] Changes);
public sealed record GuardianProposeRequest(string Request);
public sealed record JournalPostRequest(string? Kind, string? Actor, string? Summary, System.Text.Json.JsonElement? Before,
    System.Text.Json.JsonElement? After, bool? Reversible, string? Ref);
public sealed record GuardianReviewRequest(string CandidateId, System.Text.Json.JsonElement Attestation);

public partial class Program;
