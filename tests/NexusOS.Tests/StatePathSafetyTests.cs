using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class StatePathSafetyTests
{
    [Fact]
    public void OrdinaryPrivateStateDirectoryWorks()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-state-safe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["NEXUS_STATE_DIR"] = root }).Build();
            Assert.Equal(Path.GetFullPath(root), new NexusStatePaths(config).Root);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SymlinkStateRootAndAncestorAreRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var scratch = Path.Combine(Path.GetTempPath(), "nexus-state-links-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var target = Path.Combine(scratch, "real");
            Directory.CreateDirectory(target);
            var link = Path.Combine(scratch, "link");
            Directory.CreateSymbolicLink(link, target);
            var direct = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["NEXUS_STATE_DIR"] = link }).Build();
            Assert.Throws<IOException>(() => new NexusStatePaths(direct));
            var nested = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string,string?> { ["NEXUS_STATE_DIR"] = Path.Combine(link, "nested") }).Build();
            Assert.Throws<IOException>(() => new NexusStatePaths(nested));
            Assert.False(Directory.Exists(Path.Combine(target, "nested")));
        }
        finally { Directory.Delete(scratch, true); }
    }
}
