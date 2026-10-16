using System.Net.Sockets;
using System.Runtime.InteropServices;

public static class LinuxPeerIdentity
{
    // Linux SOL_SOCKET=1 and SO_PEERCRED=17; struct ucred(pid_t,uid_t,gid_t).
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct UCred
    {
        public int ProcessId;
        public uint UserId;
        public uint GroupId;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int GetSocketOption(nint fd, int level, int name,
        out UCred value, ref uint length);

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

        // Hold an owned managed Socket alive while calling getsockopt on its descriptor.
        uint length = (uint)Marshal.SizeOf<UCred>();
        try
        {
            var fd = socket.SafeHandle.DangerousGetHandle();
            if (GetSocketOption(fd, SolSocket, SoPeerCred, out var credentials, ref length) != 0 ||
                length != Marshal.SizeOf<UCred>())
                return null;
            return credentials.UserId;
        }
        catch (ObjectDisposedException) { return null; }
    }
}
