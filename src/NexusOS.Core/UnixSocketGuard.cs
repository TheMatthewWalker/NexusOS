using System.Net.Sockets;

public static class UnixSocketGuard
{
    // A crash leaves the socket file behind; without this a supervised restart
    // would fail forever. Remove the path only when it is a socket nobody is
    // listening on; refuse when another Core is live or it is anything else.
    public static void PrepareListenPath(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
            throw new InvalidOperationException("The Nexus socket path is a symbolic link; inspect the state directory.");
        if (!info.Exists) return;
        if (!IsSocket(path))
            throw new InvalidOperationException("The Nexus socket path exists and is not a socket; inspect the state directory.");
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(path); // Stale: the previous Core is gone.
            return;
        }
        throw new InvalidOperationException("Another Nexus Core is already serving this user's socket.");
    }

    // .NET exposes no S_ISSOCK; opening a socket as a file fails with ENXIO,
    // while a regular file opens normally.
    private static bool IsSocket(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read);
            return false;
        }
        catch (IOException) { return true; }
    }
}
