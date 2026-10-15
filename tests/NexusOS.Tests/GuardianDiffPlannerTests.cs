using Xunit;

public sealed class GuardianDiffPlannerTests
{
    [Fact]
    public void ShowsOnlyEffectiveChanges()
    {
        var changes = new[]
        {
            new GuardianChange("services.printing.enable", true),
            new GuardianChange("services.openssh.enable", false)
        };
        var baseline = new Dictionary<string, bool>
        {
            ["services.printing.enable"] = false,
            ["services.openssh.enable"] = false
        };
        var diff = GuardianDiffPlanner.Compare("Plan", changes, baseline);
        Assert.Single(diff.Differences);
        Assert.Equal("services.printing.enable", diff.Differences[0].Option);
        Assert.False(diff.Differences[0].Before);
        Assert.True(diff.Differences[0].After);
        Assert.False(diff.UsesObservedBaseline);
    }

    [Fact]
    public void RejectsUnspecifiedBaseline()
    {
        Assert.Throws<ArgumentException>(() => GuardianDiffPlanner.Compare("Change",
            [new GuardianChange("services.printing.enable", true)],
            new Dictionary<string, bool>()));
    }

    [Fact]
    public void RejectsArbitraryOptionAsBeforeAndAfter()
    {
        Assert.Throws<ArgumentException>(() => GuardianDiffPlanner.Compare("Change",
            [new GuardianChange("imports", true)],
            new Dictionary<string, bool> { ["imports"] = false }));
    }
}
