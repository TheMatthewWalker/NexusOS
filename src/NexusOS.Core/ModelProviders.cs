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
    string Model { get; }
    bool SupportsUnload { get; }
    // context, when given, is the system prompt and earlier exchanges
    // (AssistantContext.cs); without it the prompt is sent on its own.
    Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct, ChatContext? context = null);
    // Yields the answer in pieces as the engine produces it. Same bounds and
    // error rules as AskAsync; an empty stream is an error.
    IAsyncEnumerable<string> StreamAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct, ChatContext? context = null);
    // One chat turn that may ask for actions (NexusActions.cs): text pieces as
    // they come, then any tool calls. With no tools, or a backend that refuses
    // them, the turn is text only.
    IAsyncEnumerable<TurnPiece> StreamTurnAsync(IReadOnlyList<TurnMessage> messages, IReadOnlyList<ToolSpec> tools,
        InferenceTokenBudget budget, CancellationToken ct);
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

    public static readonly TimeSpan StreamTimeout = TimeSpan.FromSeconds(180);
    public const int MaxStreamLineBytes = 256 * 1024;

    // Streams newline-delimited events (Ollama NDJSON or OpenAI-style SSE).
    // parse turns one line into a text piece (null to skip) and says whether
    // the stream is finished. The whole stream is bounded like a full response.
    public static async IAsyncEnumerable<string> StreamAsync(string label,
        Func<CancellationToken, Task<HttpResponseMessage>> send, Func<string, (string? Text, bool Done)> parse,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string? notFoundHint = null)
    {
        var any = false;
        await foreach (var line in StreamLinesAsync(label, send, ct, notFoundHint, withTools: false))
        {
            var (text, done) = ParseLine(label, parse, line);
            if (!string.IsNullOrEmpty(text)) { any = true; yield return text; }
            if (done) break;
        }
        if (!any) throw new ProviderException($"{label} returned an empty answer.", 502);
    }

    // A turn that may call tools: text as it streams, then the calls the
    // parser collected. A turn with only tool calls is not empty.
    public static async IAsyncEnumerable<TurnPiece> StreamTurnAsync(string label,
        Func<CancellationToken, Task<HttpResponseMessage>> send, ITurnParser parser, bool withTools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string? notFoundHint = null)
    {
        var any = false;
        await foreach (var line in StreamLinesAsync(label, send, ct, notFoundHint, withTools))
        {
            var (text, done) = ParseLine(label, parser.Feed, line);
            if (!string.IsNullOrEmpty(text)) { any = true; yield return new TurnPiece(text, null); }
            if (done) break;
        }
        foreach (var call in parser.Calls) { any = true; yield return new TurnPiece(null, call); }
        if (!any) throw new ProviderException($"{label} returned an empty answer.", 502);
    }

    // Starts a turn with tools; if the backend refuses them before answering
    // (a model without tool support, llama-server without --jinja), the same
    // turn is asked again without tools.
    // refused, if given, is told, so the provider can stop sending tools for a while.
    public static async IAsyncEnumerable<TurnPiece> WithToolFallback(Func<bool, IAsyncEnumerable<TurnPiece>> start, bool tools,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, Action? refused = null)
    {
        var pieces = start(tools).GetAsyncEnumerator(ct);
        bool more;
        try { more = await pieces.MoveNextAsync(); }
        catch (ToolsRefusedException)
        {
            refused?.Invoke();
            await pieces.DisposeAsync();
            pieces = start(false).GetAsyncEnumerator(ct);
            more = await pieces.MoveNextAsync();
        }
        try
        {
            while (more) { yield return pieces.Current; more = await pieces.MoveNextAsync(); }
        }
        finally { await pieces.DisposeAsync(); }
    }

    // The bounded line reader under both: one timeout for the whole answer and
    // the same size limit as a full response.
    private static async IAsyncEnumerable<string> StreamLinesAsync(string label,
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, string? notFoundHint, bool withTools)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StreamTimeout);
        var response = await SendForStreamAsync(label, send, ct, timeout.Token);
        using (response)
        {
            if (withTools && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.InternalServerError
                    or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotImplemented)
                throw new ToolsRefusedException();
            if (!response.IsSuccessStatusCode)
                throw StatusError(label, response.StatusCode, notFoundHint);
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);
            long total = 0;
            while (true)
            {
                var line = await ReadLineAsync(label, reader, ct, timeout.Token);
                if (line is null) break;
                total += Encoding.UTF8.GetByteCount(line) + 1;
                if (total > ModelResponseGuard.MaxResponseBytes || line.Length > MaxStreamLineBytes)
                    throw new ProviderException($"{label} response exceeded the allowed size.", 502);
                if (line.Length == 0) continue;
                yield return line;
            }
        }
    }

    private static async Task<HttpResponseMessage> SendForStreamAsync(string label,
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken caller, CancellationToken token)
    {
        try { return await send(token); }
        catch (HttpRequestException)
        {
            throw new ProviderException($"{label} is unreachable. Check that it is running at the configured endpoint.", 502);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new ProviderException($"{label} did not respond before the timeout.", 504);
        }
    }

    private static async Task<string?> ReadLineAsync(string label, StreamReader reader, CancellationToken caller, CancellationToken token)
    {
        try { return await reader.ReadLineAsync(token); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new ProviderException($"{label} did not finish before the timeout.", 504);
        }
        catch (IOException)
        {
            throw new ProviderException($"{label} closed the connection mid-answer.", 502);
        }
    }

    private static (string? Text, bool Done) ParseLine(string label, Func<string, (string? Text, bool Done)> parse, string line)
    {
        try { return parse(line); }
        catch (JsonException) { throw new ProviderException($"{label} returned malformed JSON.", 502); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new ProviderException($"{label} returned an unexpected response format.", 502);
        }
    }

    // Ollama streams one JSON object per line: {"response": "...", "done": false}.
    public static (string? Text, bool Done) ParseOllamaLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
        return (root.TryGetProperty("response", out var r) ? r.GetString() : null, done);
    }

    // Ollama's /api/chat streams {"message": {"content": "..."}, "done": false}.
    public static (string? Text, bool Done) ParseOllamaChatLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
        return (root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) ? c.GetString() : null, done);
    }

    public static object[] Messages(string prompt, ChatContext? context) =>
        (context?.Messages(prompt) ?? [new ChatMessage("user", prompt)])
            .Select(m => (object)new { role = m.Role, content = m.Content }).ToArray();

    // OpenAI-compatible servers stream Server-Sent Events: "data: {json}" lines, ending with "data: [DONE]".
    public static (string? Text, bool Done) ParseSseLine(string line)
    {
        if (line.StartsWith(':')) return (null, false);
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return (null, false);
        var data = line[5..].Trim();
        if (data == "[DONE]") return (null, true);
        using var doc = JsonDocument.Parse(data);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return (null, false);
        var choice = choices[0];
        var text = choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c)
            && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var done = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String;
        return (text, done);
    }

    public static Task<HttpResponseMessage> PostStreamAsync(HttpClient client, string url, object body, CancellationToken ct,
        AuthenticationHeaderValue? auth = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        if (auth is not null) message.Headers.Authorization = auth;
        return client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
    }

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
        Model = config["NEXUS_OLLAMA_MODEL"] ?? "qwen3:8b";
        Think = string.Equals(config["NEXUS_OLLAMA_THINK"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public bool Think { get; }

    // A chat (with context) goes to /api/chat, so the model's own template
    // separates the system prompt and earlier turns; a bare prompt (the JSON
    // tasks) to /api/generate. Thinking models (qwen3 and others) are asked
    // not to think first unless NEXUS_OLLAMA_THINK=true: on a laptop GPU the
    // hidden reasoning costs seconds before the first word appears.
    public Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct, ChatContext? context = null) =>
        context is null
            ? ProviderHttp.SendAsync("Local Ollama", token => _factory.CreateClient("ai").PostAsJsonAsync(Endpoint + "/api/generate",
                    new { model = Model, prompt, stream = false, think = Think, options = Options(budget) }, token),
                root => root.GetProperty("response").GetString(), ct, Missing)
            : ProviderHttp.SendAsync("Local Ollama", token => _factory.CreateClient("ai").PostAsJsonAsync(Endpoint + "/api/chat",
                    ChatBody(prompt, context, budget, stream: false), token),
                root => root.GetProperty("message").GetProperty("content").GetString(), ct, Missing);

    public IAsyncEnumerable<string> StreamAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct, ChatContext? context = null) =>
        context is null
            ? ProviderHttp.StreamAsync("Local Ollama", token => ProviderHttp.PostStreamAsync(_factory.CreateClient("ai"),
                    Endpoint + "/api/generate",
                    new { model = Model, prompt, stream = true, think = Think, options = Options(budget) }, token),
                ProviderHttp.ParseOllamaLine, ct, Missing)
            : ProviderHttp.StreamAsync("Local Ollama", token => ProviderHttp.PostStreamAsync(_factory.CreateClient("ai"),
                    Endpoint + "/api/chat", ChatBody(prompt, context, budget, stream: true), token),
                ProviderHttp.ParseOllamaChatLine, ct, Missing);

    // qwen3 and other tool-capable models take "tools" on /api/chat and answer
    // with message.tool_calls; a model without tool support gets text only.
    public IAsyncEnumerable<TurnPiece> StreamTurnAsync(IReadOnlyList<TurnMessage> messages, IReadOnlyList<ToolSpec> tools,
        InferenceTokenBudget budget, CancellationToken ct) =>
        ProviderHttp.WithToolFallback(withTools => ProviderHttp.StreamTurnAsync("Local Ollama",
            token => ProviderHttp.PostStreamAsync(_factory.CreateClient("ai"), Endpoint + "/api/chat",
                TurnBody(messages, withTools ? tools : [], budget), token),
            new OllamaTurnParser(), withTools, ct, Missing), tools.Count > 0, ct);

    private Dictionary<string, object> TurnBody(IReadOnlyList<TurnMessage> messages, IReadOnlyList<ToolSpec> tools, InferenceTokenBudget budget)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = Model, ["messages"] = ModelTools.OllamaMessages(messages), ["stream"] = true,
            ["think"] = Think, ["options"] = Options(budget)
        };
        if (tools.Count > 0) body["tools"] = ModelTools.Wire(tools);
        return body;
    }

    private Dictionary<string, object> ChatBody(string prompt, ChatContext context, InferenceTokenBudget budget, bool stream)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = Model, ["messages"] = ProviderHttp.Messages(prompt, context), ["stream"] = stream,
            ["think"] = Think, ["options"] = Options(budget)
        };
        if (context.Json) body["format"] = "json";
        return body;
    }

    private static object Options(InferenceTokenBudget budget) =>
        new { num_ctx = budget.ContextTokens, num_predict = budget.ReservedOutputTokens };
    private string Missing => $"Model '{Model}' is not installed in Ollama. Run: ollama pull {Model}";

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

    public async Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct, ChatContext? context = null)
    {
        if (_process is not null) await _process.EnsureRunningAsync(ct);
        return await ProviderHttp.SendAsync("Local llama.cpp server", token => _factory.CreateClient("ai").PostAsJsonAsync(
                Endpoint + "/v1/chat/completions",
                new { model = Model, messages = ProviderHttp.Messages(prompt, context), stream = false,
                      max_tokens = budget.ReservedOutputTokens }, token),
            root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), ct);
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, InferenceTokenBudget budget,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, ChatContext? context = null)
    {
        if (_process is not null) await _process.EnsureRunningAsync(ct);
        await foreach (var piece in ProviderHttp.StreamAsync("Local llama.cpp server",
                           token => ProviderHttp.PostStreamAsync(_factory.CreateClient("ai"), Endpoint + "/v1/chat/completions",
                               new { model = Model, messages = ProviderHttp.Messages(prompt, context), stream = true,
                                     max_tokens = budget.ReservedOutputTokens }, token),
                           ProviderHttp.ParseSseLine, ct))
            yield return piece;
    }

    // llama-server takes OpenAI-style tools when started with --jinja (Core's
    // managed server is, LlamaServerProcess). Without it the request is refused
    // and the turn falls back to text only; tools are then left out for a while
    // (ToolsRetry), so an older server is not asked twice for every question.
    // A managed server that could not take --jinja is never sent tools.
    public static readonly TimeSpan ToolsRetry = TimeSpan.FromMinutes(10);
    private DateTimeOffset _toolsRefusedAt = DateTimeOffset.MinValue;

    public bool ToolsLikely => (_process is null || _process.ToolCalling) && DateTimeOffset.UtcNow - _toolsRefusedAt > ToolsRetry;

    public async IAsyncEnumerable<TurnPiece> StreamTurnAsync(IReadOnlyList<TurnMessage> messages, IReadOnlyList<ToolSpec> tools,
        InferenceTokenBudget budget, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (_process is not null) await _process.EnsureRunningAsync(ct);
        if (!ToolsLikely) tools = [];
        await foreach (var piece in ProviderHttp.WithToolFallback(withTools => ProviderHttp.StreamTurnAsync("Local llama.cpp server",
                           token => ProviderHttp.PostStreamAsync(_factory.CreateClient("ai"), Endpoint + "/v1/chat/completions",
                               OpenAiTurnBody(Model, messages, withTools ? tools : [], budget), token),
                           new OpenAiTurnParser(), withTools, ct), tools.Count > 0, ct,
                           refused: () => _toolsRefusedAt = DateTimeOffset.UtcNow))
            yield return piece;
    }

    internal static Dictionary<string, object> OpenAiTurnBody(string model, IReadOnlyList<TurnMessage> messages,
        IReadOnlyList<ToolSpec> tools, InferenceTokenBudget budget)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = model, ["messages"] = ModelTools.OpenAiMessages(messages), ["stream"] = true,
            ["max_tokens"] = budget.ReservedOutputTokens
        };
        if (tools.Count > 0) body["tools"] = ModelTools.Wire(tools);
        return body;
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
    private readonly ActivityJournal? _journal;
    private readonly ProviderStore? _store;
    public string Endpoint { get; }
    public string Model { get; }
    public string Name => ProviderNames.OpenAiCompatible;
    public bool SupportsUnload => false;

    public OpenAiCompatibleProvider(IHttpClientFactory factory, IConfiguration config, IProviderCredentialStore credentials,
        ActivityJournal? journal = null, ProviderStore? store = null)
    {
        _factory = factory;
        _config = config;
        _credentials = credentials;
        _journal = journal;
        _store = store;
        var endpoint = config["NEXUS_OPENAI_ENDPOINT"] ?? "https://api.openai.com/v1";
        ProviderEndpointGuard.RequireCloudEndpoint(endpoint, config["NEXUS_CLOUD_HOSTS"]);
        Endpoint = endpoint.TrimEnd('/');
        Model = config["NEXUS_OPENAI_MODEL"] ?? "gpt-4.1-mini";
    }

    public async Task<string> AskAsync(string prompt, InferenceTokenBudget budget, CancellationToken ct, ChatContext? context = null)
    {
        var token = await RequireCredentialAsync(ct);
        return await ProviderHttp.SendAsync("Cloud provider", async innerCt =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint + "/chat/completions");
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                message.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = Model, messages = ProviderHttp.Messages(prompt, context),
                    max_tokens = budget.ReservedOutputTokens
                }), Encoding.UTF8, "application/json");
                // Journal before sending, so a request is never sent unrecorded.
                if (_journal is not null)
                    await _journal.RecordEgressAsync(Name, message.RequestUri!.Host, _store?.SelectedAt, innerCt);
                return await _factory.CreateClient("cloud").SendAsync(message, innerCt);
            },
            root => root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString(), ct);
    }

    public async IAsyncEnumerable<string> StreamAsync(string prompt, InferenceTokenBudget budget,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, ChatContext? context = null)
    {
        var token = await RequireCredentialAsync(ct);
        await foreach (var piece in ProviderHttp.StreamAsync("Cloud provider",
                           innerCt => ProviderHttp.PostStreamAsync(_factory.CreateClient("cloud"), Endpoint + "/chat/completions",
                               new { model = Model, messages = ProviderHttp.Messages(prompt, context), stream = true,
                                     max_tokens = budget.ReservedOutputTokens }, innerCt,
                               new AuthenticationHeaderValue("Bearer", token)),
                           ProviderHttp.ParseSseLine, ct))
            yield return piece;
    }

    // Each request of a turn is journalled before it is sent, like AskAsync.
    // The chat never offers private look-ups (files, the journal) to a cloud
    // provider (NexusActions.ToolsFor).
    public async IAsyncEnumerable<TurnPiece> StreamTurnAsync(IReadOnlyList<TurnMessage> messages, IReadOnlyList<ToolSpec> tools,
        InferenceTokenBudget budget, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var token = await RequireCredentialAsync(ct);
        await foreach (var piece in ProviderHttp.WithToolFallback(withTools => ProviderHttp.StreamTurnAsync("Cloud provider",
                           async innerCt =>
                           {
                               if (_journal is not null)
                                   await _journal.RecordEgressAsync(Name, new Uri(Endpoint).Host, _store?.SelectedAt, innerCt);
                               return await ProviderHttp.PostStreamAsync(_factory.CreateClient("cloud"), Endpoint + "/chat/completions",
                                   LlamaCppProvider.OpenAiTurnBody(Model, messages, withTools ? tools : [], budget), innerCt,
                                   new AuthenticationHeaderValue("Bearer", token));
                           },
                           new OpenAiTurnParser(), withTools, ct), tools.Count > 0, ct))
            yield return piece;
    }

    // Consent first, then the keyring: no credential is read for a prompt that may not leave the machine.
    private async Task<string> RequireCredentialAsync(CancellationToken ct)
    {
        if (!CloudEgressPolicy.IsExplicitlyAllowed(_config))
            throw new ProviderException("Cloud prompt transmission is disabled. Set NEXUS_CLOUD_EGRESS_ALLOWED=true explicitly to enable.", 503);
        string? token;
        try { token = await _credentials.GetAsync(Name, ct); }
        catch (InvalidOperationException ex) { throw new ProviderException(ex.Message, 503); }
        if (string.IsNullOrWhiteSpace(token))
            throw new ProviderException("Cloud credential missing from the OS keyring.", 503);
        return token;
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

    public async Task<(string Answer, string Provider)> AskAsync(string prompt, CancellationToken ct, ChatContext? context = null)
    {
        var provider = Current;
        return (await provider.AskAsync(prompt, _tokens.Budget, ct, context), provider.Name);
    }

    public (IAsyncEnumerable<string> Pieces, string Provider) Stream(string prompt, CancellationToken ct, ChatContext? context = null)
    {
        var provider = Current;
        return (provider.StreamAsync(prompt, _tokens.Budget, ct, context), provider.Name);
    }

    public (IAsyncEnumerable<TurnPiece> Pieces, string Provider) StreamTurn(IReadOnlyList<TurnMessage> messages,
        IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        var provider = Current;
        return (provider.StreamTurnAsync(messages, tools, _tokens.Budget, ct), provider.Name);
    }
}
