using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;

public sealed record RuleParseRequest(string? Sentence);

// Wiring for the rules engine, kept apart from Program.cs: two calls there
// (AddRules, MapRules) bring in everything below.
public static class RuleEndpoints
{
    public const int EventsPerMinute = 120;
    public const int WritesPerMinute = 60;

    public static IServiceCollection AddRules(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RuleStore>();
        services.AddSingleton<RuleEngine>();
        services.AddSingleton<RuleParser>();
        services.AddHostedService<RuleTimerService>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("rules-events", _ => RateLimitPartition.GetFixedWindowLimiter("single-user", __ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = EventsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
            options.AddPolicy("rules-write", _ => RateLimitPartition.GetFixedWindowLimiter("single-user", __ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = WritesPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            }));
        });
        return services;
    }

    public static void MapRules(this WebApplication app)
    {
        // A draft only: nothing is saved and nothing runs until the person
        // confirms it with POST /rules.
        app.MapPost("/rules/parse", async (RuleParseRequest request, RuleParser parser, InferenceConcurrencyGate concurrency,
            CancellationToken ct) =>
        {
            var sentence = RuleSchema.Clean(request.Sentence ?? "");
            if (sentence.Length is 0 || sentence.Length > RuleParser.MaxSentenceLength)
                return Results.BadRequest(new { error = $"Sentence must be 1-{RuleParser.MaxSentenceLength} characters." });
            using var slot = await concurrency.TryEnterAsync(ct);
            if (slot is null) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            var result = await parser.ParseAsync(sentence, ct);
            return Results.Ok(result.Parsed
                ? new { parsed = true, saved = false, source = result.Source, provider = result.Provider, rule = (object?)result.Rule,
                        next = "Review the rule, then POST it to /rules to save it." }
                : (object)new { parsed = false, saved = false, source = result.Source, systemChange = result.SystemChange,
                                message = result.Message, suggestions = result.Suggestions });
        }).RequireRateLimiting("ai");

        app.MapPost("/rules", async ([FromBody] JsonElement body, RuleStore store, CancellationToken ct) =>
        {
            RuleDefinition definition;
            string name, createdFrom;
            bool enabled;
            try
            {
                definition = RuleSchema.ReadDefinition(body, "name", "enabled", "createdFrom");
                createdFrom = OptionalText(body, "createdFrom", RuleSchema.MaxCreatedFromLength) ?? "";
                name = OptionalText(body, "name", RuleSchema.MaxNameLength) ?? Shorten(createdFrom.Length > 0 ? createdFrom : RuleSchema.Describe(definition));
                enabled = !body.TryGetProperty("enabled", out var flag) || flag.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new RuleRejectedException("'enabled' must be true or false."),
                };
            }
            catch (RuleRejectedException ex) { return Results.BadRequest(new { error = ex.Message }); }
            try
            {
                var rule = await store.AddAsync(definition, name, createdFrom, enabled, ct);
                return Results.Created($"/rules/{rule.Id}", rule);
            }
            catch (RuleLimitException ex) { return Results.Conflict(new { error = ex.Message }); }
        }).RequireRateLimiting("rules-write");

        app.MapGet("/rules", async (RuleStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));

        app.MapPost("/rules/{id}/pause", (string id, RuleStore store, RuleEngine engine, CancellationToken ct) =>
            SetEnabled(id, false, store, engine, ct)).RequireRateLimiting("rules-write");
        app.MapPost("/rules/{id}/resume", (string id, RuleStore store, RuleEngine engine, CancellationToken ct) =>
            SetEnabled(id, true, store, engine, ct)).RequireRateLimiting("rules-write");

        app.MapDelete("/rules/{id}", async (string id, RuleStore store, RuleEngine engine, CancellationToken ct) =>
        {
            if (!RuleStore.IsValidId(id)) return Results.BadRequest(new { error = "Invalid rule ID." });
            if (!await store.DeleteAsync(id, ct)) return Results.NotFound(new { error = "No such rule." });
            engine.Forget(id);
            return Results.NoContent();
        }).RequireRateLimiting("rules-write");

        app.MapGet("/rules/{id}/history", async (string id, int? limit, RuleStore store, CancellationToken ct) =>
        {
            if (!RuleStore.IsValidId(id)) return Results.BadRequest(new { error = "Invalid rule ID." });
            if (limit is < 1 or > RuleStore.MaxHistoryPage)
                return Results.BadRequest(new { error = $"limit must be 1-{RuleStore.MaxHistoryPage}." });
            var runs = await store.HistoryAsync(id, limit ?? 50, ct);
            return runs is null ? Results.NotFound(new { error = "No such rule." }) : Results.Ok(runs);
        });

        // Actions queued by time triggers, after the shell's cursor. Event
        // triggers return their actions in the /events response instead.
        app.MapGet("/rules/pending-actions", (long? after, RuleEngine engine) =>
        {
            if (after is < 0) return Results.BadRequest(new { error = "after must be 0 or more." });
            var (items, next) = engine.Pending(after ?? 0);
            return Results.Ok(new { next, items });
        });

        // The shell reports what it observes; Core answers with the actions of
        // any rules that fired, for the shell to perform.
        app.MapPost("/events", async ([FromBody] JsonElement body, RuleEngine engine, CancellationToken ct) =>
        {
            RuleEvent e;
            try { e = RuleSchema.ReadEvent(body); }
            catch (RuleRejectedException ex) { return Results.BadRequest(new { error = ex.Message }); }
            return Results.Ok(new { accepted = true, fired = await engine.HandleEventAsync(e, ct) });
        }).RequireRateLimiting("rules-events");
    }

    private static async Task<IResult> SetEnabled(string id, bool enabled, RuleStore store, RuleEngine engine, CancellationToken ct)
    {
        if (!RuleStore.IsValidId(id)) return Results.BadRequest(new { error = "Invalid rule ID." });
        var rule = await store.SetEnabledAsync(id, enabled, ct);
        if (rule is null) return Results.NotFound(new { error = "No such rule." });
        if (!enabled) engine.Forget(id);
        return Results.Ok(rule);
    }

    private static string? OptionalText(JsonElement body, string key, int max)
    {
        if (!body.TryGetProperty(key, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new RuleRejectedException($"'{key}' must be text.");
        var text = RuleSchema.Clean(value.GetString()!);
        if (text.Length > max) throw new RuleRejectedException($"'{key}' must be at most {max} characters.");
        return text.Length == 0 ? null : text;
    }

    private static string Shorten(string text) =>
        text.Length <= RuleSchema.MaxNameLength ? text : text[..(RuleSchema.MaxNameLength - 1)] + "…";
}
