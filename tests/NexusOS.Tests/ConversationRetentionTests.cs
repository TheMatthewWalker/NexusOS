using Microsoft.Extensions.Configuration;
using Xunit;

public class ConversationRetentionTests
{
    [Fact]
    public async Task MalformedLinesDoNotEvictValidHistoryDuringAppend()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-history-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NEXUS_STATE_DIR"] = root,
                ["NEXUS_HISTORY_LIMIT"] = "2"
            }).Build();
            var paths = new NexusStatePaths(config);
            var original = new ConversationEntry(DateTimeOffset.UtcNow, "local", "original", "reply");
            await File.WriteAllLinesAsync(paths.PathFor("history.jsonl"), new[]
            {
                System.Text.Json.JsonSerializer.Serialize(original),
                "{ malformed json"
            });
            var store = new ConversationStore(paths, config);
            await store.AppendAsync(new ConversationEntry(DateTimeOffset.UtcNow, "local", "new", "answer"), CancellationToken.None);
            var recent = await store.RecentAsync(10, CancellationToken.None);
            Assert.Equal(2, recent.Count);
            Assert.Equal("new", recent[0].Prompt);
            Assert.Equal("original", recent[1].Prompt);
        }
        finally { Directory.Delete(root, true); }
    }
}
