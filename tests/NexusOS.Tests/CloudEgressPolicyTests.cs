using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class CloudEgressPolicyTests
{
    private static IConfiguration Settings(string? value) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?> { ["NEXUS_CLOUD_EGRESS_ALLOWED"] = value }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    public void DefaultsToDeny(string? setting) =>
        Assert.Throws<InvalidOperationException>(() => CloudEgressPolicy.RequireConsent(Settings(setting)));

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void ExplicitTruePermitsCloud(string setting) =>
        CloudEgressPolicy.RequireConsent(Settings(setting));
}
