using System.Net.Http.Json;

public sealed record InferenceBudget(long TotalVramBytes, long ReserveBytes, long AiBudgetBytes);

public static class InferenceBudgetPlanner
{
    public static InferenceBudget Calculate(long totalVramBytes, int reservePercent)
    {
        if (totalVramBytes < 0 || reservePercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(reservePercent));
        var reserve = totalVramBytes / 100 * reservePercent;
        return new(totalVramBytes, reserve, totalVramBytes - reserve);
    }
}

public sealed class InferenceLifecycle(IHttpClientFactory factory, IConfiguration config)
{
    private (string Endpoint, string Model) Settings()
    {
        var endpoint = config["NEXUS_OLLAMA_URL"] ?? "http://127.0.0.1:11434";
        ProviderEndpointGuard.RequireLoopback(endpoint);
        return (endpoint.TrimEnd('/'), config["NEXUS_OLLAMA_MODEL"] ?? "llama3.2:3b");
    }

    public async Task<bool> IsBackendAvailableAsync(CancellationToken ct)
    {
        var (endpoint, _) = Settings();
        try
        {
            using var response = await factory.CreateClient("ai").GetAsync(endpoint + "/api/tags", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    // Ollama unload request: keep_alive=0 asks Ollama to unload this model.
    // Memory reclamation is backend dependent and must be measured, not assumed.
    public async Task UnloadAsync(CancellationToken ct)
    {
        var (endpoint, model) = Settings();
        using var response = await factory.CreateClient("ai")
            .PostAsJsonAsync(endpoint + "/api/generate", new { model, keep_alive = 0 }, ct);
        response.EnsureSuccessStatusCode();
    }
}
