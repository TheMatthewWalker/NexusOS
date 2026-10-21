using System.Net.Sockets;
using Xunit;

public sealed class UnixSocketGuardTests
{
    [Fact]
    public void StaleSocketIsRemovedAndLiveSocketRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        var path = Path.Combine(state.Root, "nexus.sock");

        using (var live = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            live.Bind(new UnixDomainSocketEndPoint(path));
            live.Listen(1);
            Assert.Throws<InvalidOperationException>(() => UnixSocketGuard.PrepareListenPath(path));
            Assert.True(File.Exists(path));
        }
        // .NET unlinks sockets it disposes; a process that dies without cleanup
        // (as a crash does) leaves the file behind.
        using (var crashed = System.Diagnostics.Process.Start("python3",
                   ["-c", "import socket,os,sys; s=socket.socket(socket.AF_UNIX); s.bind(sys.argv[1]); s.listen(1); os._exit(0)", path]))
            crashed.WaitForExit();
        Assert.True(File.Exists(path));
        UnixSocketGuard.PrepareListenPath(path);
        Assert.False(File.Exists(path));
        UnixSocketGuard.PrepareListenPath(path); // Absent is fine.
    }

    [Fact]
    public void RegularFilesAndSymlinksAreNeverDeleted()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        var path = Path.Combine(state.Root, "nexus.sock");
        File.WriteAllText(path, "not a socket");
        Assert.Throws<InvalidOperationException>(() => UnixSocketGuard.PrepareListenPath(path));
        Assert.Equal("not a socket", File.ReadAllText(path));
        File.Delete(path);
        var target = Path.Combine(state.Root, "elsewhere");
        File.WriteAllText(target, "x");
        File.CreateSymbolicLink(path, target);
        Assert.Throws<InvalidOperationException>(() => UnixSocketGuard.PrepareListenPath(path));
        Assert.True(File.Exists(target));
    }
}
