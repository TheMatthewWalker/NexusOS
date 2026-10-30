using Xunit;

public sealed class ConfigurationValidationTests
{
    [Theory]
    [InlineData(null, true, ApiMode.Unix)]
    [InlineData(null, false, ApiMode.Tcp)]
    [InlineData("TCP", true, ApiMode.Tcp)]
    [InlineData(" unix ", true, ApiMode.Unix)]
    public void ApiModeDefaultsToUnixOnLinux(string? configured, bool linux, ApiMode expected) =>
        Assert.Equal(expected, ApiModes.Resolve(configured, linux));

    [Theory]
    [InlineData("unix", false)]
    [InlineData("http", true)]
    public void ApiModeRejectsUnsupportedValues(string configured, bool linux) =>
        Assert.Throws<InvalidOperationException>(() => ApiModes.Resolve(configured, linux));

    [Theory]
    [InlineData("100", null)]
    [InlineData("4096", "4096")]
    [InlineData("lots", null)]
    [InlineData(null, "zero")]
    public void InvalidTokenBudgetFailsWhenConstructed(string? context, string? output) =>
        Assert.Throws<InvalidOperationException>(() => new InferenceTokenSettings(
            TestConfig.Of(("NEXUS_CONTEXT_TOKENS", context), ("NEXUS_MAX_OUTPUT_TOKENS", output))));

    [Fact]
    public void DefaultTokenBudget() =>
        Assert.Equal(new InferenceTokenBudget(8192, 1024, 7168), new InferenceTokenSettings(TestConfig.Of()).Budget);
}
