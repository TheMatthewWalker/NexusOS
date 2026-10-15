using System.Net;
using System.Net.Sockets;
using Xunit;

public class CloudConnectionPolicyTests
{
    [Fact]
    public async Task RejectsLiteralIpHostBeforeOpeningSocket()
    {
        var context = new SocketsHttpConnectionContext(
            new DnsEndPoint("127.0.0.1", 443), null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CloudAddressPolicy.ConnectAsync(context, CancellationToken.None));
    }

    [Theory]
    [InlineData("8.8.8.8", "127.0.0.1")]
    [InlineData("1.1.1.1", "169.254.169.254")]
    [InlineData("2606:4700:4700::1111", "fc00::1")]
    public void RejectsUnsafeDnsAnswerEvenWhenPublicAddressAppearsFirst(string safe, string unsafeAddress)
    {
        Assert.Throws<InvalidOperationException>(() =>
            CloudAddressPolicy.SelectValidatedAddress(new[] { IPAddress.Parse(safe), IPAddress.Parse(unsafeAddress) }));
    }
}
