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
                (bytes[0] == 0x20 && bytes[1] == 0x01 && (bytes[2] & 0xf0) == 0x00)) // IETF special-use
                throw new InvalidOperationException("Cloud endpoint resolves to a disallowed address.");
        }
        else throw new InvalidOperationException("Unsupported cloud address family.");
        return address;
    }

    public static IPAddress SelectValidatedAddress(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0)
            throw new InvalidOperationException("Cloud endpoint has no DNS addresses.");
        foreach (var address in addresses) Validate(address);
        return addresses[0];
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        if (IPAddress.TryParse(host, out _))
            throw new InvalidOperationException("Cloud endpoint must use DNS.");
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        // Fail closed for mixed answers. The connected address is exactly the validated one.
        var selected = SelectValidatedAddress(addresses);
        var socket = new Socket(selected.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(selected, context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
