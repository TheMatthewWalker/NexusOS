using System.Net.Http.Json;
using System.Text.Json;

// An embedding failure that is safe to show: never includes content.
public sealed class EmbeddingUnavailableException(string message) : Exception(message);

public interface IEmbedder
{
    // null when embeddings are switched off (NEXUS_EMBED_MODEL=off).
    string? Model { get; }
    Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct);
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);
}

// Embeddings come only from the local Ollama endpoint. This class never goes
// through ModelRouter, so selecting a cloud provider for chat can never send
// indexed content off the machine. The endpoint passes the same loopback
// guard as the chat provider and uses the "ai" client (no proxy, no redirects).
public sealed class OllamaEmbedder : IEmbedder
{
    public const int MaxBatch = 16;
    private readonly IHttpClientFactory _factory;
    public string Endpoint { get; }
    public string? Model { get; }

    public OllamaEmbedder(IHttpClientFactory factory, IConfiguration config)
    {
        _factory = factory;
        var endpoint = config["NEXUS_OLLAMA_URL"] ?? "http://127.0.0.1:11434";
        ProviderEndpointGuard.RequireLoopback(endpoint);
        Endpoint = endpoint.TrimEnd('/');
        var model = config["NEXUS_EMBED_MODEL"];
        if (string.IsNullOrWhiteSpace(model)) model = "nomic-embed-text";
        if (string.Equals(model, "off", StringComparison.OrdinalIgnoreCase)) model = null;
        else if (model.Length > 128 || !model.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '/'))
            throw new InvalidOperationException("NEXUS_EMBED_MODEL must be an Ollama model name or 'off'.");
        Model = model;
    }

    // nomic-embed-text is trained with task prefixes; other models get raw text.
    private string Prefix(string task) => Model?.Contains("nomic", StringComparison.OrdinalIgnoreCase) == true ? task + ": " : "";

    public Task<float[][]> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count is 0 or > MaxBatch) throw new ArgumentOutOfRangeException(nameof(texts));
        return EmbedAsync(texts.Select(x => Prefix("search_document") + x).ToArray(), ct);
    }

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) =>
        (await EmbedAsync([Prefix("search_query") + query], ct))[0];

    private async Task<float[][]> EmbedAsync(string[] input, CancellationToken ct)
    {
        if (Model is null) throw new EmbeddingUnavailableException("Meaning search is switched off (NEXUS_EMBED_MODEL=off).");
        HttpResponseMessage response;
        try
        {
            response = await _factory.CreateClient("ai").PostAsJsonAsync(Endpoint + "/api/embed",
                new { model = Model, input, truncate = true }, ct);
        }
        catch (HttpRequestException) { throw new EmbeddingUnavailableException("Local Ollama is unreachable."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new EmbeddingUnavailableException("Local Ollama did not answer in time.");
        }
        using (response)
        {
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new EmbeddingUnavailableException($"Embedding model '{Model}' is not installed. Run: ollama pull {Model}");
            if (!response.IsSuccessStatusCode)
                throw new EmbeddingUnavailableException($"Local Ollama returned HTTP {(int)response.StatusCode} for embeddings.");
            JsonDocument document;
            try { document = await ModelResponseGuard.ReadAsync(response.Content, ct); }
            catch (Exception ex) when (ex is JsonException or ResponseTooLargeException)
            {
                throw new EmbeddingUnavailableException("Local Ollama returned an unusable embeddings response.");
            }
            using (document)
            {
                try
                {
                    var vectors = document.RootElement.GetProperty("embeddings").EnumerateArray()
                        .Select(v => v.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
                    if (vectors.Length != input.Length || vectors.Any(v => v.Length is 0 or > 8192) ||
                        vectors.Any(v => v.Length != vectors[0].Length))
                        throw new InvalidOperationException();
                    return vectors;
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    throw new EmbeddingUnavailableException("Local Ollama returned an unusable embeddings response.");
                }
            }
        }
    }
}

public static class Vectors
{
    // Stored unit-length as float16: half the size of float32, and the
    // precision loss does not change a cosine ranking in practice.
    public static byte[] Pack(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm == 0 || float.IsNaN(norm) || float.IsInfinity(norm)) norm = 1;
        var bytes = new byte[vector.Length * 2];
        for (var i = 0; i < vector.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), (Half)(vector[i] / norm));
        return bytes;
    }

    public static float[] Normalise(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        return norm == 0 ? vector : vector.Select(x => x / norm).ToArray();
    }

    // Cosine similarity of a unit query against a packed unit vector.
    public static float Cosine(ReadOnlySpan<float> query, ReadOnlySpan<byte> packed)
    {
        if (packed.Length != query.Length * 2) return float.NaN;
        var sum = 0f;
        for (var i = 0; i < query.Length; i++)
            sum += query[i] * (float)BitConverter.ToHalf(packed.Slice(i * 2, 2));
        return sum;
    }
}
