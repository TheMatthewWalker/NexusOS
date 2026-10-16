using System.Net.Sockets;
using System.Runtime.InteropServices;

public static class LinuxPeerIdentity
{
    // Linux-only SO_PEERCRED = 17 at SOL_SOCKET. Struct ucred: pid_t, uid_t, gid_t.
    private const int SoPeerCred = 17;
    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEffectiveUserId();

    public static uint ServerUserId()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        return GetEffectiveUserId();
    }

    public static bool IsAuthorized(uint? peerUid, uint serverUid) =>
        peerUid.HasValue && peerUid.Value == serverUid;

    public static uint? ReadPeerUserId(Socket? socket)
    {
        if (!OperatingSystem.IsLinux() || socket is null || socket.AddressFamily != AddressFamily.Unix)
            return null;
        try
        {
            var data = socket.GetSocketOption(SocketOptionLevel.Socket,
                (SocketOptionName)SoPeerCred, 12);
            if (data is null || data.Length != 12) return null;
            return BitConverter.ToUInt32(data, 4);
        }
        catch (SocketException) { return null; }
        catch (ArgumentException) { return null; }
    }
}
