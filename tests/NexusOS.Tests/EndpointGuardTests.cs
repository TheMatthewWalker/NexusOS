using Xunit;

public sealed class EndpointGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://localhost:11434")]
    public void AcceptsLoopbackModels(string endpoint) => ProviderEndpointGuard.RequireLoopback(endpoint);

    [Theory]
    [InlineData("http://192.168.1.10:11434")]
    [InlineData("https://example.org")]
    [InlineData("http://user:pass@localhost")]
    public void RejectsNonLoopbackModels(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => ProviderEndpointGuard.RequireLoopback(endpoint));

    [Fact]
    public void CloudOnlyAllowsExplicitHosts()
    {
        ProviderEndpointGuard.RequireCloudEndpoint("https://api.openai.com/v1", null);
        Assert.Throws<InvalidOperationException>(() => ProviderEndpointGuard.RequireCloudEndpoint("https://127.0.0.1/v1", null));
        Assert.Throws<InvalidOperationException>(() => ProviderEndpointGuard.RequireCloudEndpoint("http://api.openai.com/v1", null));
        ProviderEndpointGuard.RequireCloudEndpoint("https://example.net/v1", "example.net");
    }
}
