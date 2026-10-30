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
        Assert.Equal(24, a.Id.Length);
        Assert.EndsWith("}\n", a.NixPreview);
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

    // SSH turned on through NexusOS is always key-only; the settings come from the schema, not the proposal.
    [Fact]
    public void SshIsKeyOnlyWhenTurnedOn()
    {
        var plan = GuardianPlanner.Preview("SSH", [new GuardianChange("services.printing.enable", false),
                                                   new GuardianChange("services.openssh.enable", true)]);
        Assert.Equal("{\n" +
                     "  services.openssh.enable = true;\n" +
                     "  services.openssh.settings.KbdInteractiveAuthentication = false;\n" +
                     "  services.openssh.settings.PasswordAuthentication = false;\n" +
                     "  services.openssh.settings.PermitRootLogin = \"no\";\n" +
                     "  services.printing.enable = false;\n" +
                     "}\n", plan.NixPreview);
        Assert.Contains("Key-only", plan.Risk);
        // The plan's changes are still only what was proposed.
        Assert.Equal(["services.openssh.enable", "services.printing.enable"], plan.Changes.Select(x => x.Option));
        Assert.Equal("{\n  services.openssh.enable = false;\n}\n",
            GuardianPlanner.Preview("SSH off", [new GuardianChange("services.openssh.enable", false)]).NixPreview);
    }

    [Theory]
    [InlineData("services.openssh.settings.PasswordAuthentication", true)]
    [InlineData("services.openssh.settings.PasswordAuthentication", false)]
    [InlineData("services.openssh.settings.KbdInteractiveAuthentication", true)]
    [InlineData("services.openssh.settings.PermitRootLogin", true)]
    [InlineData("services.openssh.openFirewall", true)]
    public void SshAuthenticationCannotBeProposed(string option, bool enabled)
    {
        Assert.False(GuardianSchema.Options.ContainsKey(option));
        Assert.Throws<ArgumentException>(() => GuardianPlanner.Preview("SSH with passwords",
            [new GuardianChange("services.openssh.enable", true), new GuardianChange(option, enabled)]));
        Assert.Throws<ArgumentException>(() => GuardianPlanner.Preview("SSH with passwords", [new GuardianChange(option, enabled)]));
    }

    [Fact]
    public void EveryProposableOptionIsAPlainSwitch() =>
        Assert.All(GuardianSchema.Options.Values, spec => Assert.Equal("bool", spec.Type));
}
