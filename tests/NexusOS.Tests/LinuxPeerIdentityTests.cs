using System.Net.Sockets;
using Xunit;

public sealed class LinuxPeerIdentityTests
{
    [Fact]
    public void MissingOrMismatchedPeerIdentityFailsClosed()
    {
        Assert.False(LinuxPeerIdentity.IsAuthorized(null, 1000));
        Assert.False(LinuxPeerIdentity.IsAuthorized(1001, 1000));
        Assert.True(LinuxPeerIdentity.IsAuthorized(1000, 1000));
    }

    [Fact]
    public async Task UnixSocketReportsKernelPeerUserId()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), "nexus-peer-" + Guid.NewGuid().ToString("N") + ".sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path));
            using var accepted = await listener.AcceptAsync();
            var uid = LinuxPeerIdentity.ReadPeerUserId(accepted);
            Assert.NotNull(uid);
            Assert.Equal(LinuxPeerIdentity.ServerUserId(), uid.Value);
            Assert.True(LinuxPeerIdentity.IsAuthorized(uid, LinuxPeerIdentity.ServerUserId()));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NullSocketReturnsNoIdentity() =>
        Assert.Null(LinuxPeerIdentity.ReadPeerUserId(null));
}
