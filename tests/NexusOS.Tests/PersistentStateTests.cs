using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class PersistentStateTests
{
    [Fact]
    public void SelectedProviderSurvivesRecreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "selected.json");
            Assert.True(new ProviderStore(file).Select("OPENAI-COMPATIBLE"));
            Assert.Equal("openai-compatible", new ProviderStore(file).Selected);
            Assert.False(new ProviderStore(file).Select("invalid"));
            Assert.Equal("openai-compatible", new ProviderStore(file).Selected);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CorruptProviderFileDefaultsToLocal()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "{not json");
            Assert.Equal("local", new ProviderStore(file).Selected);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task HistorySurvivesRecreationAndReturnsMostRecentFirst()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["NEXUS_STATE_DIR"] = root })
                .Build();
            var paths = new NexusStatePaths(config);
            var store = new ConversationStore(paths);
            await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", "first", "one"), CancellationToken.None);
            await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", "second", "two"), CancellationToken.None);
            var restored = new ConversationStore(new NexusStatePaths(config));
            var entries = await restored.RecentAsync(1, CancellationToken.None);
            Assert.Single(entries);
            Assert.Equal("second", entries[0].Prompt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
