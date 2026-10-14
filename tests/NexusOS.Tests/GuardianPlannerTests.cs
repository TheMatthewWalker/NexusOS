using Xunit;

public sealed class GuardianPlannerTests
{
    [Fact]
    public void PreviewIsDeterministicAndDoesNotExecute()
    {
        var a = GuardianPlanner.Preview("Enable printing", [new GuardianChange("services.printing.enable", true)]);
        var b = GuardianPlanner.Preview("Another description", [new GuardianChange("services.printing.enable", true)]);
        Assert.Equal(a.Id, b.Id);
        Assert.Contains("services.printing.enable = true;", a.NixPreview);
    }

    [Theory]
    [InlineData("environment.systemPackages")]
    [InlineData("imports")]
    [InlineData("services.openssh.enable; builtins.exec")]
    [InlineData("../../etc/passwd")]
    public void RejectsUnsupportedOptions(string option) =>
        Assert.Throws<ArgumentException>(() => GuardianPlanner.Preview("unsafe", [new GuardianChange(option, true)]));

    [Fact]
    public void RejectsDuplicates() =>
        Assert.Throws<ArgumentException>(() => GuardianPlanner.Preview("duplicate",
            [new GuardianChange("services.printing.enable", true), new GuardianChange("services.printing.enable", false)]));

    [Fact]
    public void WarnsForSshEnable() =>
        Assert.Contains("Elevated", GuardianPlanner.Preview("SSH",
            [new GuardianChange("services.openssh.enable", true)]).Risk);
}
