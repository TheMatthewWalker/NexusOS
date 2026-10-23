public sealed record TryKeepRequest(string Attr);

// "Try before you install" (docs/ARCHITECTURE.md). Trials run in the user's
// own bubblewrap sandbox through nexus-try and never involve Core. Keeping an
// app is a normal Guardian install proposal: Core builds the same typed plan
// as any "install <attr>" request and submits it to the root broker, which
// still needs a root-console approval, a signed build attestation and a trial
// boot. Core never installs anything itself.
public static class TryApps
{
    public static void MapTryApps(this WebApplication app) =>
        app.MapPost("/try/keep", async (TryKeepRequest request, GuardianBrokerClient broker, CancellationToken ct) =>
        {
            GuardianPlan plan;
            try { plan = GuardianPlanner.Install(request.Attr, $"Install {request.Attr} (kept after a trial)"); }
            catch (ArgumentException) { return Results.BadRequest(new { error = "Not a valid nixpkgs package name." }); }
            try
            {
                var response = await broker.SendAsync("submit", new
                {
                    description = plan.Description,
                    changes = GuardianPlanner.WireChanges(plan)
                }, ct);
                return Results.Ok(new
                {
                    planId = plan.Id, plan, broker = response, installed = false,
                    next = "Approve the candidate at a root console (nexus-guardian approve); it installs on the next trial boot."
                });
            }
            catch (GuardianBrokerException ex) { return Results.Problem(ex.Message, statusCode: ex.StatusCode); }
        });
}
