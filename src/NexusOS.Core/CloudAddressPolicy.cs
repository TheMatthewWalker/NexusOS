using System.Net;
using System.Net.Sockets;

public static class CloudAddressPolicy
{
    public static IPAddress Validate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            if (a == 0 || a == 10 || a == 127 || a >= 224 ||
                (a == 100 && b >= 64 && b <= 127) ||
                (a == 169 && b == 254) || (a == 172 && b >= 16 && b <= 31) ||
                (a == 192 && (b == 168 || b == 0)) ||
                (a == 198 && (b == 18 || b == 19)) ||
                (a == 192 && b == 88) ||
                (a == 198 && b == 51 && bytes[2] == 100) ||
                (a == 203 && b == 0 && bytes[2] == 113))
                throw new InvalidOperationException("Cloud endpoint resolves to a disallowed address.");
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Allow only globally routable IPv6 unicast (2000::/3).
            if ((bytes[0] & 0xe0) != 0x20 ||
                (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8) || // documentation range
                (bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xf0) == 0x00) || // IETF special-use
                (bytes[0] == 0x20 && bytes[1] == 0x02) || // 6to4: IPv4 can be embedded in IPv6 destination
                (bytes[0] == 0x3f && bytes[1] == 0xff && (bytes[2] & 0xf0) == 0x00)) // 3fff::/20 documentation
                throw new InvalidOperationException("Cloud endpoint resolves to a disallowed address.");
        }
        else throw new InvalidOperationException("Unsupported cloud address family.");
        return address;
    }

    // Every answer must be safe: a mixed public/private answer fails closed
    // rather than "choosing" the public address.
    public static IReadOnlyList<IPAddress> ValidateAll(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException("Cloud endpoint has no DNS addresses.");
        return addresses.Select(Validate).ToArray();
    }

    public static IPAddress SelectValidatedAddress(IReadOnlyList<IPAddress> addresses) => ValidateAll(addresses)[0];

    public static void RequireDnsHostname(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || IPAddress.TryParse(host, out _))
            throw new InvalidOperationException("Cloud endpoint must use DNS.");
    }

    public static ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken) =>
        CloudConnector.Default.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);
}

// Resolves once per connection, validates every answer, then connects only to
// those validated addresses in order. Trying each address keeps cloud access
// working on hosts with broken IPv6 without ever re-resolving between
// validation and connect, so DNS rebinding cannot swap in another address.
public sealed class CloudConnector(
    Func<string, CancellationToken, Task<IPAddress[]>> resolve,
    Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect,
    TimeSpan attemptTimeout)
{
    public static readonly CloudConnector Default = new(Dns.GetHostAddressesAsync, ConnectSocketAsync, TimeSpan.FromSeconds(10));

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        CloudAddressPolicy.RequireDnsHostname(host);
        var addresses = CloudAddressPolicy.ValidateAll(await resolve(host, ct));
        Exception? last = null;
        foreach (var address in addresses)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(attemptTimeout);
            try { return await connect(new IPEndPoint(address, port), attempt.Token); }
            catch (Exception ex) when (ex is SocketException or IOException ||
                                       (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                last = ex;
            }
        }
        throw new HttpRequestException("Could not connect to any validated cloud endpoint address.", last);
    }

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}

public static class CloudHttp
{
    // No redirects (each target would bypass host allowlisting) and no proxy
    // (a proxy would resolve and connect on our behalf, bypassing validation).
    public static SocketsHttpHandler CreateHandler(CloudConnector connector) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = (context, ct) => connector.ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct)
    };
}
