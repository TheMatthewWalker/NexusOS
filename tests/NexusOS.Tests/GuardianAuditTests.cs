using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class GuardianAuditTests
{
    [Fact]
    public async Task ApprovalCreatesHashLinkedEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["NEXUS_STATE_DIR"] = root }).Build();
            var store = new GuardianAuditStore(new NexusStatePaths(config));
            var plan = GuardianPlanner.Preview("Enable printing", [new GuardianChange("services.printing.enable", true)]);
            var a = await store.RecordApprovalAsync(plan, CancellationToken.None);
            var b = await store.RecordApprovalAsync(plan, CancellationToken.None);
            Assert.Equal("GENESIS", a.PreviousHash);
            Assert.Equal(a.Hash, b.PreviousHash);
            Assert.Equal("approved-preview-only", b.Event);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
