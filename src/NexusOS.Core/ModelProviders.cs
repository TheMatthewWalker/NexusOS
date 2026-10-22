using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

public static class ProviderNames
{
    public const string Local = "local";
    public const string LlamaCpp = "llama-cpp";
    public const string OpenAiCompatible = "openai-compatible";
    public static readonly string[] All = [Local, LlamaCpp, OpenAiCompatible];

    public static string? Normalise(string? name) =>
        All.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
}

// A provider failure that is safe to show to the API caller. Messages never
// include upstream bodies, prompts or credentials.
public sealed class ProviderException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IModelProvider
{
    string Name { get; }
    bool SupportsUnload { get; }
    Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct);
    // null means the provider is deliberately not probed (for example, cloud egress).
    Task<bool?> IsAvailableAsync(CancellationToken ct);
    Task UnloadAsync(CancellationToken ct);
}

public static class ProviderHttp
{
    public static async Task<string> SendAsync(string label, Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<JsonElement, string?> extract, CancellationToken ct, string? notFoundHint = null)
    {
        HttpResponseMessage response;
        try { response = await send(ct); }
        catch (HttpRequestException)
        {
            throw new ProviderException($"{label} is unreachable. Check that it is running at the configured endpoint.", 502);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException($"{label} did not respond before the timeout.", 504);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw StatusError(label, response.StatusCode, notFoundHint);
            using var document = await ReadJsonAsync(label, response.Content, ct);
            string? answer;
            try { answer = extract(document.RootElement); }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new ProviderException($"{label} returned an unexpected response format.", 502);
            }
            if (string.IsNullOrWhiteSpace(answer))
                throw new ProviderException($"{label} returned an empty answer.", 502);
            return answer;
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(string label, HttpContent content, CancellationToken ct)
    {
        try { return await ModelResponseGuard.ReadAsync(content, ct); }
        catch (JsonException) { throw new ProviderException($"{label} returned malformed JSON.", 502); }
        catch (ResponseTooLargeException) { throw new ProviderException($"{label} response exceeded the allowed size.", 502); }
    }

    public static ProviderException StatusError(string label, HttpStatusCode status, string? notFoundHint) => status switch
    {
        HttpStatusCode.NotFound when notFoundHint is not null => new ProviderException(notFoundHint, 502),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new ProviderException($"{label} rejected the credential.", 502),
        HttpStatusCode.TooManyRequests => new ProviderException($"{label} is rate limiting requests.", 503),
        _ => new ProviderException($"{label} returned HTTP {(int)status}.", 502)
    };

    public static async Task<bool> ProbeAsync(HttpClient client, string url, CancellationToken ct)
    {
        try
        {
            using var response = await client.GetAsync(url, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }
}

public sealed class OllamaProvider : IModelProvider
{
    private readonly IHttpClientFactory _factory;
    public string Endpoint { get; }
    public string Model { get; }
    public string Name => ProviderNames.Local;
    public bool SupportsUnload => true;

    public OllamaProvider(IHttpClientFactory factory, IConfiguration config)
    {
        _factory = factory;
        var endpoint = config["NEXUS_OLLAMA_URL"] ?? "http://127.0.0.1:11434";
        ProviderEndpointGuard.RequireLoopback(endpoint);
        Endpoint = endpoint.TrimEnd('/');
        Model = config["NEXUS_OLLAMA_MODEL"] ?? "llama3.2:3b";
    }

    public Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct) =>
        ProviderHttp.SendAsync("Local Ollama", token => _factory.CreateClient("ai").PostAsJsonAsync(Endpoint + "/api/generate",
                new { model = Model, prompt, stream = false,
                      options = new { num_ctx = budget.ContextTokens, num_predict = budget.ReservedOutputTokens } }, token),
            root => root.GetProperty("response").GetString(), ct,
            $"Model '{Model}' is not installed in Ollama. Run: ollama pull {Model}");

    public async Task<bool?> IsAvailableAsync(CancellationToken ct) =>
        await ProviderHttp.ProbeAsync(_factory.CreateClient("ai"), Endpoint + "/api/tags", ct);

    // keep_alive=0 asks Ollama to unload this model. Memory reclamation is
    // backend dependent and must be measured, not assumed.
    public async Task UnloadAsync(CancellationToken ct)
    {
        using var response = await _factory.CreateClient("ai")
            .PostAsJsonAsync(Endpoint + "/api/generate", new { model = Model, keep_alive = 0 }, ct);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class LlamaCppProvider : IModelProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly LlamaServerProcess? _process;
    public string Endpoint { get; }
    public string Model { get; }
    public string Name => ProviderNames.LlamaCpp;
    public bool SupportsUnload => _process is not null;

    public LlamaCppProvider(IHttpClientFactory factory, IConfiguration config, LlamaServerProcess? process = null)
    {
        _factory = factory;
        _process = process?.IsConfigured == true ? process : null;
        var endpoint = _process?.Endpoint ?? config["NEXUS_LLAMA_ENDPOINT"] ?? "http://127.0.0.1:8080";
        ProviderEndpointGuard.RequireLoopback(endpoint);
        Endpoint = endpoint.TrimEnd('/');
        Model = config["NEXUS_LLAMA_MODEL"] ?? "default";
    }

    public async Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct)
    {
        if (_process is not null) await _process.EnsureRunningAsync(ct);
        return await ProviderHttp.SendAsync("Local llama.cpp server", token => _factory.CreateClient("ai").PostAsJsonAsync(
                Endpoint + "/v1/chat/completions",
                new { model = Model, messages = new[] { new { role = "user", content = prompt } }, stream = false,
                      max_tokens = budget.ReservedOutputTokens }, token),
            root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), ct);
    }

    public async Task<bool?> IsAvailableAsync(CancellationToken ct) =>
        await ProviderHttp.ProbeAsync(_factory.CreateClient("ai"), Endpoint + "/health", ct);

    public async Task UnloadAsync(CancellationToken ct)
    {
        if (_process is null)
            throw new ProviderException("Unload requires a Core-managed llama.cpp server (NEXUS_LLAMA_SERVER_BIN).", 409);
        await _process.StopAsync(ct);
    }
}

public sealed class OpenAiCompatibleProvider : IModelProvider
{
    private readonly IHttpClientFactory _factory;
    private readonly IConfiguration _config;
    private readonly IProviderCredentialStore _credentials;
    public string Endpoint { get; }
    public string Model { get; }
    public string Name => ProviderNames.OpenAiCompatible;
    public bool SupportsUnload => false;

    public OpenAiCompatibleProvider(IHttpClientFactory factory, IConfiguration config, IProviderCredentialStore credentials)
    {
        _factory = factory;
        _config = config;
        _credentials = credentials;
        var endpoint = config["NEXUS_OPENAI_ENDPOINT"] ?? "https://api.openai.com/v1";
        ProviderEndpointGuard.RequireCloudEndpoint(endpoint, config["NEXUS_CLOUD_HOSTS"]);
        Endpoint = endpoint.TrimEnd('/');
        Model = config["NEXUS_OPENAI_MODEL"] ?? "gpt-4.1-mini";
    }

    public async Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct)
    {
        if (!CloudEgressPolicy.IsExplicitlyAllowed(_config))
            throw new ProviderException("Cloud prompt transmission is disabled. Set NEXUS_CLOUD_EGRESS_ALLOWED=true explicitly to enable.", 503);
        string? token;
        try { token = await _credentials.GetAsync(Name, ct); }
        catch (InvalidOperationException ex) { throw new ProviderException(ex.Message, 503); }
        if (string.IsNullOrWhiteSpace(token))
            throw new ProviderException("Cloud credential missing from the OS keyring.", 503);
        return await ProviderHttp.SendAsync("Cloud provider", async innerCt =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint + "/chat/completions");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                message.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = Model, messages = new[] { new { role = "user", content = prompt } },
                    max_tokens = budget.ReservedOutputTokens
                }), Encoding.UTF8, "application/json");
                return await _factory.CreateClient("cloud").SendAsync(message, innerCt);
            },
            root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), ct);
    }

    public Task<bool?> IsAvailableAsync(CancellationToken ct) => Task.FromResult<bool?>(null);

    public Task UnloadAsync(CancellationToken ct) =>
        throw new ProviderException("Cloud providers have no local model to unload.", 409);
}

public sealed class ModelRouter
{
    private readonly ProviderStore _store;
    private readonly InferenceTokenSettings _tokens;
    private readonly Dictionary<string, IModelProvider> _providers;

    public ModelRouter(IEnumerable<IModelProvider> providers, ProviderStore store, InferenceTokenSettings tokens)
    {
        _store = store;
        _tokens = tokens;
        _providers = providers.ToDictionary(x => x.Name, StringComparer.Ordinal);
        if (!ProviderNames.All.All(_providers.ContainsKey) || _providers.Count != ProviderNames.All.Length)
            throw new InvalidOperationException("Provider registry does not match the supported provider names.");
    }

    public IModelProvider Current => _providers[_store.Selected];

    public async Task<(string Answer, string Provider)> AskAsync(string prompt, CancellationToken ct)
    {
        var provider = Current;
        return (await provider.AskAsync(prompt, _tokens.Budget, ct), provider.Name);
    }
}
