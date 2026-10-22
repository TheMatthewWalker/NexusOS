using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

public sealed class GuardianBrokerException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

// Client for the root-owned Guardian broker (ADR-002). Core never approves or
// records decisions itself: it forwards typed requests over the broker's Unix
// socket, where the kernel-reported UID of this process identifies the caller.
// Frames are a 4-byte big-endian length followed by UTF-8 JSON.
public sealed class GuardianBrokerClient(IConfiguration config)
{
    public const int MaxMessage = 65536;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    public string SocketPath { get; } = config["NEXUS_GUARDIAN_SOCKET"] ?? "/run/nexus-guardian/review.sock";

    public async Task<JsonElement> SendAsync(string operation, object payload, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new GuardianBrokerException("The Guardian broker requires Linux.", 503);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { operation, payload });
        if (body.Length > MaxMessage)
            throw new GuardianBrokerException("Guardian request is too large.", 413);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), deadline.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            throw new GuardianBrokerException("This user is not permitted to use the Guardian broker.", 403);
        }
        catch (SocketException)
        {
            throw new GuardianBrokerException("The Guardian broker is not available on this system.", 503);
        }
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
            await socket.SendAsync(header, SocketFlags.None, deadline.Token);
            await socket.SendAsync(body, SocketFlags.None, deadline.Token);
            await ReadExactAsync(socket, header, deadline.Token);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is 0 or > MaxMessage)
                throw new GuardianBrokerException("The Guardian broker sent an invalid response.", 502);
            var response = new byte[length];
            await ReadExactAsync(socket, response, deadline.Token);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement.Clone();
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok))
                throw new GuardianBrokerException("The Guardian broker sent an invalid response.", 502);
            if (ok.ValueKind != JsonValueKind.True)
            {
                var error = root.TryGetProperty("error", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString()! : "Request refused.";
                throw new GuardianBrokerException("Guardian refused: " + error, 422);
            }
            return root;
        }
        catch (Exception ex) when (ex is EndOfStreamException ||
                                   ex is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.Shutdown })
        {
            // The broker closes without replying to callers outside its allowlist
            // (a reset when our request bytes were still unread).
            throw new GuardianBrokerException("The Guardian broker does not accept requests from this user.", 403);
        }
        catch (SocketException)
        {
            throw new GuardianBrokerException("The Guardian broker connection failed.", 502);
        }
        catch (JsonException)
        {
            throw new GuardianBrokerException("The Guardian broker sent an invalid response.", 502);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GuardianBrokerException("The Guardian broker did not respond in time.", 504);
        }
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(read), SocketFlags.None, ct);
            if (count == 0) throw new EndOfStreamException();
            read += count;
        }
    }
}
