using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class HistoryControlsTests
{
    [Fact]
    public async Task HistoryIsBoundedAndCanBeCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-retention-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["NEXUS_STATE_DIR"] = root,
                ["NEXUS_HISTORY_LIMIT"] = "2"
            }).Build();
            var store = new ConversationStore(new NexusStatePaths(config), config);
            foreach (var prompt in new[] { "one", "two", "three" })
                await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", prompt, "ok"), CancellationToken.None);
            var history = await store.RecentAsync(50, CancellationToken.None);
            Assert.Equal(new[] { "three", "two" }, history.Select(x => x.Prompt));
            await store.ClearAsync(CancellationToken.None);
            Assert.Empty(await store.RecentAsync(50, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DisabledHistoryDoesNotWritePrompts()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-disabled-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["NEXUS_STATE_DIR"] = root,
                ["NEXUS_HISTORY_LIMIT"] = "0"
            }).Build();
            var store = new ConversationStore(new NexusStatePaths(config), config);
            await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", "private", "reply"), CancellationToken.None);
            Assert.Empty(await store.RecentAsync(50, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
