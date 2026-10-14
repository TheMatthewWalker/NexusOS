using System.Text.Json;

public static class ModelResponseGuard
{
    public const long MaxResponseBytes = 4 * 1024 * 1024;

    public static async Task<JsonDocument> ReadAsync(HttpContent content, CancellationToken ct)
    {
        // Prevent a provider from streaming an arbitrarily large JSON body into RAM.
        await content.LoadIntoBufferAsync(MaxResponseBytes, ct);
        using var stream = await content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }
}
