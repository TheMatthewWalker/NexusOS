using System.Buffers;
using System.Text.Json;

public sealed class ResponseTooLargeException() : Exception("AI provider response exceeded the allowed size.");

public static class ModelResponseGuard
{
    public const long MaxResponseBytes = 4 * 1024 * 1024;

    public static async Task<JsonDocument> ReadAsync(HttpContent content, CancellationToken ct)
    {
        using var source = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int count;
            while ((count = await source.ReadAsync(chunk.AsMemory(), ct)) > 0)
            {
                if (buffer.Length + count > MaxResponseBytes)
                    throw new ResponseTooLargeException();
                await buffer.WriteAsync(chunk.AsMemory(0, count), ct);
            }
            buffer.Position = 0;
            return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
        }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }
}
