using System.Net;
using Xunit;

public class CloudAddressPolicyTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.100")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("2001:db8::1")]
    [InlineData("2001::1")]
    public void RejectsNonPublicAddresses(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => CloudAddressPolicy.Validate(IPAddress.Parse(raw)));
    }

    [Fact]
    public void RejectsMixedPublicAndPrivateDnsAnswersRegardlessOfOrder()
    {
        var publicIp = IPAddress.Parse("8.8.8.8");
        var privateIp = IPAddress.Parse("10.0.0.1");
        Assert.Throws<InvalidOperationException>(() => CloudAddressPolicy.SelectValidatedAddress(new[] { publicIp, privateIp }));
        Assert.Throws<InvalidOperationException>(() => CloudAddressPolicy.SelectValidatedAddress(new[] { privateIp, publicIp }));
    }

    [Fact]
    public void RejectsEmptyDnsAnswers()
    {
        Assert.Throws<InvalidOperationException>(() => CloudAddressPolicy.SelectValidatedAddress(Array.Empty<IPAddress>()));
    }

    [Fact]
    public void SelectsValidatedPublicDnsAddress()
    {
        var address = IPAddress.Parse("1.1.1.1");
        Assert.Equal(address, CloudAddressPolicy.SelectValidatedAddress(new[] { address }));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void AcceptsPublicAddresses(string raw)
    {
        Assert.Equal(IPAddress.Parse(raw), CloudAddressPolicy.Validate(IPAddress.Parse(raw)));
    }
}
