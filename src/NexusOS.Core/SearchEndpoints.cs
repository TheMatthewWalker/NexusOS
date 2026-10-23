using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

public sealed record SearchSourcesRequest(string?[]? Paths);

// Private search (feature 8, M8). Kept in its own files so Program.cs only
// gains two lines: AddNexusSearch() and MapNexusSearch().
public static class SearchEndpoints
{
    public const int MaxConcurrentSearches = 2;

    public static IServiceCollection AddNexusSearch(this IServiceCollection services)
    {
        services.AddSingleton<SearchSettings>();
        services.AddSingleton<SearchIndexStore>();
        services.AddSingleton<SearchSourceStore>();
        services.AddSingleton(_ => TextExtractorRegistry.Default());
        services.AddSingleton<IEmbedder, OllamaEmbedder>();
        services.AddSingleton<SearchQuery>();
        services.AddSingleton<SearchIndexer>();
        services.AddHostedService(sp => sp.GetRequiredService<SearchIndexer>());
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy("search", _ => RateLimitPartition.GetFixedWindowLimiter("single-user-search", __ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
            })));
        return services;
    }

    public static WebApplication MapNexusSearch(this WebApplication app)
    {
        // Fail at startup on a bad embedding model name or a non-loopback Ollama URL.
        _ = app.Services.GetRequiredService<IEmbedder>();
        _ = app.Services.GetRequiredService<SearchIndexStore>();
        var searches = new SemaphoreSlim(MaxConcurrentSearches, MaxConcurrentSearches);

        app.MapGet("/search/sources", (SearchSourceStore sources) => Results.Ok(new { sources = sources.List() }));
        app.MapPost("/search/sources", (SearchSourcesRequest request, SearchSourceStore sources, SearchIndexer indexer) =>
        {
            if (request.Paths is not { Length: > 0 } paths || paths.Length > SearchSourceStore.MaxSources)
                return Results.BadRequest(new { error = $"Give between 1 and {SearchSourceStore.MaxSources} folder paths." });
            var (list, refused) = sources.Add(paths);
            if (refused.Count > 0)
                return Results.BadRequest(new { error = "Some folders cannot be searched; nothing was added.", refused });
            indexer.RequestScan();
            return Results.Ok(new { sources = list, indexing = true });
        });
        app.MapDelete("/search/sources", async (string? path, SearchSourceStore sources, SearchIndexer indexer,
            SearchIndexStore store, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "Give the folder path to remove." });
            var result = await indexer.ExclusiveAsync(() =>
            {
                if (!sources.Remove(path, out var remaining)) return ((IReadOnlyList<string>?)null, 0);
                var removed = store.RemoveSource(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
                indexer.SyncWatchers(remaining);
                return (remaining, removed);
            }, ct);
            return result.Item1 is null
                ? Results.NotFound(new { error = "That folder is not being searched." })
                : Results.Ok(new { sources = result.Item1, removedFiles = result.Item2 });
        });
        app.MapGet("/search/status", (SearchIndexer indexer) => Results.Ok(indexer.Status()));
        app.MapPost("/search/reindex", (SearchIndexer indexer, SearchSourceStore sources) =>
        {
            indexer.RequestScan(force: true);
            return Results.Json(new { queued = true, sources = sources.List().Count }, statusCode: StatusCodes.Status202Accepted);
        });
        // Erases the whole index and forgets the chosen folders, so nothing is
        // re-indexed until the person chooses again.
        app.MapDelete("/search/index", async (SearchIndexer indexer, SearchSourceStore sources, SearchIndexStore store,
            CancellationToken ct) =>
        {
            var (cleared, verified) = await indexer.ExclusiveAsync(() =>
            {
                var count = sources.Clear();
                indexer.SyncWatchers([]);
                var ok = store.Erase();
                indexer.ResetStatus();
                return (count, ok);
            }, ct);
            return verified
                ? Results.Ok(new { erased = true, verified, sourcesCleared = cleared })
                : Results.Problem("The search index could not be verified as empty.", statusCode: 500);
        });
        app.MapPost("/search", async (SearchRequest request, SearchQuery query, CancellationToken ct) =>
        {
            if (SearchQuery.Validate(request) is { } error) return Results.BadRequest(new { error });
            if (!await searches.WaitAsync(0, ct)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            try
            {
                // ACTIVITY JOURNAL HOOK (feature 8): when the journal lands, record
                // the search and the result paths shown here. Not merged yet.
                return Results.Ok(await query.SearchAsync(request, ct));
            }
            finally { searches.Release(); }
        }).RequireRateLimiting("search");
        return app;
    }
}
