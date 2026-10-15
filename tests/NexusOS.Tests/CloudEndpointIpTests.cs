using Xunit;

public class CloudEndpointIpTests
{
    [Theory]
    [InlineData("https://127.0.0.1/v1", "127.0.0.1")]
    [InlineData("https://169.254.169.254/v1", "169.254.169.254")]
    [InlineData("https://[::1]/v1", "::1")]
    [InlineData("https://[::ffff:127.0.0.1]/v1", "::ffff:127.0.0.1")]
    public void LiteralIpIsRejectedEvenWhenAllowlisted(string endpoint, string allowed)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProviderEndpointGuard.RequireCloudEndpoint(endpoint, allowed));
    }

    [Fact]
    public void DefaultCloudHostnameIsAllowed()
    {
        ProviderEndpointGuard.RequireCloudEndpoint("https://api.openai.com/v1", null);
    }

    [Fact]
    public void UnlistedHostnameIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProviderEndpointGuard.RequireCloudEndpoint("https://example.com/v1", null));
    }
}
