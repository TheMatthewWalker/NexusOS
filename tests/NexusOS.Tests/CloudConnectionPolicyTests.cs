using System.Net;
using Xunit;

public class CloudConnectionPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("169.254.169.254")]
    [InlineData("")]
    public void RejectsLiteralIpOrEmptyCloudHost(string host)
    {
        Assert.Throws<InvalidOperationException>(() => CloudAddressPolicy.RequireDnsHostname(host));
    }

    [Fact]
    public void AcceptsDnsCloudHostname()
    {
        CloudAddressPolicy.RequireDnsHostname("api.openai.com");
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
