using Microsoft.Extensions.Configuration;
using Xunit;

public class SqliteConversationTests
{
    [Fact]
    public async Task MigratesLegacyOnlyOnceAndRetainsOriginalFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-sqlite-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NEXUS_STATE_DIR"] = root,
                ["NEXUS_HISTORY_LIMIT"] = "2"
            }).Build();
            var paths = new NexusStatePaths(config);
            var old = new ConversationEntry(DateTimeOffset.UtcNow, "local", "legacy", "answer");
            await File.WriteAllLinesAsync(paths.PathFor("history.jsonl"), new[]
            {
                System.Text.Json.JsonSerializer.Serialize(old), "{ invalid"
            });
            var store = new ConversationStore(paths, config);
            Assert.Single(await store.RecentAsync(10, CancellationToken.None));
            await store.AppendAsync(new ConversationEntry(DateTimeOffset.UtcNow, "local", "new", "reply"), CancellationToken.None);
            var reopened = new ConversationStore(paths, config);
            var entries = await reopened.RecentAsync(10, CancellationToken.None);
            Assert.Equal(2, entries.Count);
            Assert.Equal("new", entries[0].Prompt);
            Assert.Equal("legacy", entries[1].Prompt);
            Assert.True(File.Exists(paths.PathFor("history.jsonl")));
            Assert.True(File.Exists(paths.PathFor("history.sqlite3")));
            await reopened.ClearAsync(CancellationToken.None);
            Assert.Empty(await reopened.RecentAsync(10, CancellationToken.None));
            Assert.Empty(await new ConversationStore(paths, config).RecentAsync(10, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
