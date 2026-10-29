using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

// System changes waiting for the person, and how each one is going, for the
// desktop's Guardian dialog (shell/qml/Guardian.qml). A proposal is a typed,
// validated plan (GuardianPlanner) plus where it is in Guardian's flow:
//
//   waiting   -> the person has not decided. Nothing has been sent anywhere.
//   checking  -> the person chose Approve: submitted to the broker and being
//                built and tested by this computer's builder
//   ready     -> checks passed; waiting for the person's password (polkit)
//   approved  -> the broker reviewed it with the builder's attestation: eligible
//   staged    -> set up as a one-time trial for the next restart
//   applied | rolled-back           -> what the trial boot did
//   declined | failed | dismissed   -> finished; nothing changed
//
// Safety rules: Core never approves, decides or stages. "check" is an explicit
// request from the person; approval happens outside Core (polkit runs
// nexus-guardian-desktop-approve after the password); the review goes through
// the broker (/guardian/review) and staging through Guardian's staging
// service, which accepts only this user's permitted decisions. Everything
// here is a record of what those root services reported.
public sealed class GuardianProposalRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset Updated { get; set; }
    public string Source { get; set; } = "user";
    public string? Request { get; set; }
    public GuardianPlan Plan { get; set; } = null!;
    public string State { get; set; } = GuardianProposals.Waiting;
    public string? Message { get; set; }
    public string? CandidateId { get; set; }
    public Dictionary<string, string> Checks { get; set; } = GuardianProposals.PendingChecks();
    public JsonElement? Before { get; set; }
    public JsonElement? Attestation { get; set; }
    public string? Decision { get; set; }
}

public sealed record GuardianProposalRequest(string? Description, GuardianChange[]? Changes, string[]? Packages = null,
    string? Source = null, string? Request = null);
public sealed record GuardianStageRequest(string? Decision);

public sealed class GuardianProposals(NexusStatePaths paths, GuardianBrokerClient broker, GuardianBuilderClient builder,
    GuardianStageClient stager, ActivityJournal journal, TimeProvider time)
{
    public const string Waiting = "waiting", Checking = "checking", Ready = "ready", Approved = "approved", Staged = "staged",
        Applied = "applied", RolledBack = "rolled-back", Declined = "declined", Failed = "failed", Dismissed = "dismissed";
    public static readonly string[] Sources = ["nexus", "fixit", "try", "user"];
    public const int MaxKept = 30, MaxOpen = 5, MaxRequestLength = 300;
    private static readonly string[] Open = [Waiting, Checking, Ready, Approved];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string FilePath => paths.PathFor("guardian-proposals.json");

    public static Dictionary<string, string> PendingChecks() =>
        new() { ["evaluate"] = "pending", ["build"] = "pending", ["vm-test"] = "pending" };

    public static bool IsValidId(string? id) => id is { Length: 16 } && id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    // ---- storage: one small JSON file in the private state directory ----

    private List<GuardianProposalRecord> Load()
    {
        NexusStatePaths.RejectSymbolicLinkFile(FilePath);
        if (!File.Exists(FilePath)) return [];
        try { return JsonSerializer.Deserialize<List<GuardianProposalRecord>>(File.ReadAllText(FilePath), Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private void Save(List<GuardianProposalRecord> items)
    {
        var keep = items.OrderByDescending(x => x.Created).Take(MaxKept).ToList();
        var temporary = FilePath + ".new";
        NexusStatePaths.RejectSymbolicLinkFile(temporary);
        File.WriteAllText(temporary, JsonSerializer.Serialize(keep, Json));
        NexusStatePaths.RestrictPermissions(temporary, false);
        File.Move(temporary, FilePath, true);
    }

    private async Task<T> Locked<T>(Func<List<GuardianProposalRecord>, Task<T>> work, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var items = Load();
            var result = await work(items);
            Save(items);
            return result;
        }
        finally { _lock.Release(); }
    }

    private void Touch(GuardianProposalRecord p, string state, string? message = null)
    {
        p.State = state;
        p.Message = message;
        p.Updated = time.GetUtcNow();
    }

    // ---- what the person and Nexus can do ----

    // Records a plan for the person to decide on. The same plan still open is returned, not repeated.
    public Task<GuardianProposalRecord> AddAsync(GuardianPlan plan, string source, string? request, CancellationToken ct) =>
        Locked(items =>
        {
            if (items.FirstOrDefault(x => x.Plan.Id == plan.Id && Open.Contains(x.State)) is { } same) return Task.FromResult(same);
            if (items.Count(x => x.State == Waiting) >= MaxOpen)
                throw new ArgumentException($"{MaxOpen} system changes are already waiting. Decide on those first.");
            var now = time.GetUtcNow();
            var proposal = new GuardianProposalRecord
            {
                Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                Created = now, Updated = now, Source = Sources.Contains(source) ? source : "user",
                Request = request is null ? null : NexusActions.Clean(request, MaxRequestLength), Plan = plan
            };
            items.Add(proposal);
            return Task.FromResult(proposal);  // Callers journal where it came from (for example "Nexus proposed…").
        }, ct);

    public Task<GuardianProposalRecord?> GetAsync(string id, CancellationToken ct) =>
        Locked(async items =>
        {
            var p = items.FirstOrDefault(x => x.Id == id);
            if (p is not null) await RefreshAsync(p, ct);
            return p;
        }, ct);

    // Newest first, with anything in progress brought up to date from the root services.
    public Task<List<GuardianProposalRecord>> ListAsync(CancellationToken ct) =>
        Locked(async items =>
        {
            foreach (var p in items) await RefreshAsync(p, ct);
            return items.Where(x => x.State != Dismissed).OrderByDescending(x => x.Created).ToList();
        }, ct);

    // The person chose Approve: submit to the broker and ask this computer's builder to check it.
    public Task<GuardianProposalRecord?> CheckAsync(string id, CancellationToken ct) =>
        Locked<GuardianProposalRecord?>(async items =>
        {
            var p = items.FirstOrDefault(x => x.Id == id);
            if (p is null || p.State is not (Waiting or Failed)) return p;
            var changes = GuardianPlanner.WireChanges(p.Plan);
            try
            {
                var submitted = await broker.SendAsync("submit", new { description = p.Plan.Description, changes }, ct);
                var candidateId = CandidateIdOf(submitted);
                var built = await builder.SendAsync("build", new { changes }, ct);
                if (candidateId is null || CandidateIdOf(built) != candidateId)
                {
                    Touch(p, Failed, "Guardian and its builder did not agree on this change, so it was stopped. Nothing changed.");
                    return p;
                }
                p.CandidateId = candidateId;
                p.Checks = PendingChecks();
                p.Attestation = null;
                p.Decision = null;
                Touch(p, Checking, "Guardian is building and testing the change on this computer.");
                await Record("user", "Sent to Guardian for approval: ", p.Plan, ct, "candidate:" + candidateId);
                if (built.TryGetProperty("job", out var job)) ApplyJob(p, job);
            }
            catch (GuardianBrokerException ex) { Touch(p, Failed, Plain(ex)); }
            return p;
        }, ct);

    public Task<GuardianProposalRecord?> DeclineAsync(string id, CancellationToken ct) =>
        Locked<GuardianProposalRecord?>(async items =>
        {
            var p = items.FirstOrDefault(x => x.Id == id);
            if (p is null || p.State is not (Waiting or Ready or Failed)) return p;
            Touch(p, Declined, "Declined. Nothing on this computer changed.");
            await Record("user", "You declined a system change: ", p.Plan, ct);
            return p;
        }, ct);

    public Task<GuardianProposalRecord?> DismissAsync(string id, CancellationToken ct) =>
        Locked<GuardianProposalRecord?>(items =>
        {
            var p = items.FirstOrDefault(x => x.Id == id);
            if (p is not null && p.State is Applied or RolledBack or Declined or Failed) Touch(p, Dismissed, p.Message);
            return Task.FromResult(p);
        }, ct);

    // Called after /guardian/review: the broker's decision, recorded as reported.
    public Task RecordReviewAsync(string candidateId, bool eligible, string? decision, string? refusal, CancellationToken ct) =>
        Locked(items =>
        {
            foreach (var p in items.Where(x => x.CandidateId == candidateId && x.State == Ready))
            {
                if (eligible && decision is { Length: 64 }) { p.Decision = decision; Touch(p, Approved, "Approved. Guardian is setting it up for the next restart."); }
                else Touch(p, refusal is null ? Failed : Ready, refusal ?? "Guardian did not accept this change after checking it. Nothing changed.");
            }
            return Task.FromResult(true);
        }, ct);

    // Asks Guardian's staging service to set an approved decision up for one trial boot.
    public Task<(GuardianProposalRecord? Proposal, string? Error, int Status)> StageAsync(string decision, CancellationToken ct) =>
        Locked<(GuardianProposalRecord?, string?, int)>(async items =>
        {
            var p = items.FirstOrDefault(x => x.Decision == decision);
            if (p is not null && p.State != Approved) return (p, "This change has already been staged.", 409);
            try
            {
                await stager.SendAsync("stage", new { decision }, ct);
                if (p is not null)
                {
                    Touch(p, Staged, "Ready. It is used from the next restart; if the computer does not start properly, it goes back to today's version by itself.");
                    await Record("guardian", "Staged for the next restart: ", p.Plan, ct, "candidate:" + p.CandidateId);
                }
                return (p, null, 200);
            }
            catch (GuardianBrokerException ex)
            {
                // A refused repeat (already staged) leaves the proposal as it is.
                if (p is not null && p.State == Approved && ex.StatusCode == 422) Touch(p, Failed, Plain(ex));
                return (p, ex.Message, ex.StatusCode);
            }
        }, ct);

    // ---- keeping in-progress proposals up to date ----

    private async Task RefreshAsync(GuardianProposalRecord p, CancellationToken ct)
    {
        try
        {
            if (p.State == Checking && p.CandidateId is { } candidateId)
            {
                var job = await builder.SendAsync("status", new { candidateId }, ct);
                ApplyJob(p, job);
            }
            else if (p.State == Staged && p.Decision is { } decision)
            {
                var outcome = await stager.SendAsync("outcome", new { decision }, ct);
                var state = outcome.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (state == "applied")
                {
                    Touch(p, Applied, "In use. If you want it gone, ask for the opposite change; earlier versions are also in the start-up menu.");
                    await Record("guardian", "A system change is now in use: ", p.Plan, ct, "candidate:" + p.CandidateId);
                }
                else if (state is "rolled-back" or "failed")
                {
                    Touch(p, RolledBack, "The changed system did not start properly, so the computer went back to the previous version. Nothing else changed.");
                    await Record("guardian", "A system change was rolled back: ", p.Plan, ct, "candidate:" + p.CandidateId);
                }
            }
        }
        catch (GuardianBrokerException ex) when (ex.StatusCode == 422)
        {
            // The service no longer knows it (for example the builder restarted mid-check).
            if (p.State == Checking) Touch(p, Failed, "Checking stopped before it finished. Try again.");
        }
        catch (GuardianBrokerException) { }  // Not reachable just now: keep the last known state.
    }

    private void ApplyJob(GuardianProposalRecord p, JsonElement job)
    {
        if (job.ValueKind != JsonValueKind.Object) return;
        if (job.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Object)
            foreach (var name in PendingChecks().Keys)
                if (checks.TryGetProperty(name, out var value) && value.GetString() is "pending" or "running" or "pass" or "fail")
                    p.Checks[name] = value.GetString()!;
        if (job.TryGetProperty("before", out var before) && before.ValueKind == JsonValueKind.Object) p.Before = before.Clone();
        var state = job.TryGetProperty("state", out var s) ? s.GetString() : null;
        var message = job.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (state == "passed" && job.TryGetProperty("attestation", out var attestation) && attestation.ValueKind == JsonValueKind.Object)
        {
            p.Attestation = attestation.Clone();
            Touch(p, Ready, "Built and tested on this computer. Enter your password to approve it.");
        }
        else if (state == "failed") Touch(p, Failed, message is { Length: > 0 } ? NexusActions.Clean(message, 200) : "Guardian's checks did not pass. Nothing changed.");
        else p.Updated = time.GetUtcNow();
    }

    private static string? CandidateIdOf(JsonElement response) =>
        response.ValueKind == JsonValueKind.Object && response.TryGetProperty("candidate", out var c) && c.ValueKind == JsonValueKind.Object &&
        c.TryGetProperty("candidateId", out var cid) && cid.ValueKind == JsonValueKind.String &&
        cid.GetString() is { Length: 24 } id && id.All(Uri.IsHexDigit) ? id : null;

    private static string Plain(GuardianBrokerException ex) => ex.StatusCode switch
    {
        503 => "Guardian is not running on this computer, so system changes cannot be made here.",
        403 => "This account is not allowed to make system changes.",
        _ => NexusActions.Clean(ex.Message.Replace("Guardian refused: ", "Guardian stopped this change: "), 200)
    };

    private Task Record(string actor, string prefix, GuardianPlan plan, CancellationToken ct, string? reference = null) =>
        journal.AppendAsync(new JournalRecord(JournalKinds.System, actor,
            NexusActions.Clean(prefix + plan.Description, ActivityJournal.MaxSummaryLength), Ref: reference ?? "plan:" + plan.Id), ct);

    // ---- what the desktop shows ----

    // Every item changed with before and after. "before" is today's value as
    // this computer's builder evaluated it (null until the check has started).
    public static object View(GuardianProposalRecord p)
    {
        JsonElement? section(string name) =>
            p.Before is { } b && b.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.Object ? s : null;
        bool? was(JsonElement? values, string key) =>
            values is { } v && v.TryGetProperty(key, out var x) && x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : null;
        var options = section("options");
        var packages = section("packages");
        var items = p.Plan.Changes.Select(c => (object)new
            {
                kind = "setting", name = c.Option, label = GuardianSchema.Options[c.Option].Summary,
                before = was(options, c.Option), after = c.Enabled
            })
            .Concat((p.Plan.Packages ?? []).Select(attr => (object)new
            {
                kind = "package", name = attr, label = attr, before = was(packages, attr), after = true
            })).ToArray();
        var notes = p.Plan.Changes
            .Where(c => GuardianSchema.Options[c.Option] is { Risk: "elevated" } spec && spec.ElevatedWhen == c.Enabled)
            .Select(c => GuardianSchema.Options[c.Option].RiskNote).ToArray();
        return new
        {
            id = p.Id, created = p.Created, updated = p.Updated, source = p.Source, request = p.Request,
            state = p.State, message = p.Message, title = p.Plan.Description, plan = p.Plan, items,
            risk = new { level = p.Plan.RiskLevel, notes },
            checks = p.Checks, candidateId = p.CandidateId,
            attestation = p.State == Ready ? p.Attestation : null, decision = p.Decision
        };
    }
}

// GET/POST /guardian/proposals, /guardian/proposals/{id}[/check|/decline|/dismiss], POST /guardian/stage.
public static class GuardianProposalEndpoints
{
    public static IServiceCollection AddGuardianProposals(this IServiceCollection services)
    {
        services.AddSingleton<GuardianBuilderClient>();
        services.AddSingleton<GuardianStageClient>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<GuardianProposals>();
        return services;
    }

    public static void MapGuardianProposals(this WebApplication app)
    {
        app.MapGet("/guardian/proposals", async (GuardianProposals proposals, CancellationToken ct) =>
        {
            var list = await proposals.ListAsync(ct);
            return Results.Ok(new
            {
                proposals = list.Select(GuardianProposals.View),
                // The top bar: something needs the person, or something is under way.
                attention = list.Any(x => x.State is GuardianProposals.Waiting or GuardianProposals.Ready),
                busy = list.Any(x => x.State is GuardianProposals.Checking or GuardianProposals.Approved),
                restartNeeded = list.Any(x => x.State == GuardianProposals.Staged)
            });
        });
        app.MapPost("/guardian/proposals", async (GuardianProposalRequest request, GuardianProposals proposals, CancellationToken ct) =>
        {
            GuardianPlan plan;
            try { plan = GuardianPlanner.Preview(request.Description, request.Changes, request.Packages); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Unsupported or invalid change plan." }); }
            if (request.Request is { Length: > GuardianProposals.MaxRequestLength })
                return Results.BadRequest(new { error = $"request must be at most {GuardianProposals.MaxRequestLength} characters." });
            try { return Results.Ok(GuardianProposals.View(await proposals.AddAsync(plan, request.Source ?? "user", request.Request, ct))); }
            catch (ArgumentException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireRateLimiting("journal");
        app.MapGet("/guardian/proposals/{id}", async (string id, GuardianProposals proposals, CancellationToken ct) =>
            !GuardianProposals.IsValidId(id) ? Results.BadRequest(new { error = "Invalid proposal ID." })
            : await proposals.GetAsync(id, ct) is { } p ? Results.Ok(GuardianProposals.View(p)) : NotFound());
        Verb(app, "check", (p, id, ct) => p.CheckAsync(id, ct));
        Verb(app, "decline", (p, id, ct) => p.DeclineAsync(id, ct));
        Verb(app, "dismiss", (p, id, ct) => p.DismissAsync(id, ct));
        app.MapPost("/guardian/stage", async (GuardianStageRequest request, GuardianProposals proposals, CancellationToken ct) =>
        {
            if (request.Decision is not { Length: 64 } decision || !decision.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return Results.BadRequest(new { error = "A decision hash is required." });
            var (proposal, error, status) = await proposals.StageAsync(decision, ct);
            if (error is not null) return Results.Problem(error, statusCode: status);
            return Results.Ok(new { staged = true, proposal = proposal is null ? null : GuardianProposals.View(proposal) });
        }).RequireRateLimiting("journal");
    }

    private static void Verb(WebApplication app, string verb, Func<GuardianProposals, string, CancellationToken, Task<GuardianProposalRecord?>> act) =>
        app.MapPost("/guardian/proposals/{id}/" + verb, async (string id, GuardianProposals proposals, CancellationToken ct) =>
            !GuardianProposals.IsValidId(id) ? Results.BadRequest(new { error = "Invalid proposal ID." })
            : await act(proposals, id, ct) is { } p ? Results.Ok(GuardianProposals.View(p)) : NotFound()).RequireRateLimiting("journal");

    private static IResult NotFound() => Results.NotFound(new { error = "No such system change." });
}
