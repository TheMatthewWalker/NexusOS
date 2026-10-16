using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class HistoryAgeRetentionTests
{
    [Fact]
    public async Task OldMessagesAreHiddenAndPrunedOnNextSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-age-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["NEXUS_STATE_DIR"] = root,
                ["NEXUS_HISTORY_MAX_DAYS"] = "7"
            }).Build();
            var store = new ConversationStore(new NexusStatePaths(config), config);
            await store.AppendAsync(new(DateTimeOffset.UtcNow.AddDays(-15), "local", "old", "expired"), CancellationToken.None);
            await store.AppendAsync(new(DateTimeOffset.UtcNow.AddDays(-1), "local", "new", "fresh"), CancellationToken.None);
            var recent = await store.RecentAsync(50, CancellationToken.None);
            Assert.Single(recent);
            Assert.Equal("new", recent[0].Prompt);
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "state.sqlite3") }.ToString());
            db.Open();
            using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM history WHERE prompt='old'";
            Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OldEntriesCannotBeReadEvenBeforeNextSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-age-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["NEXUS_STATE_DIR"] = root, ["NEXUS_HISTORY_MAX_DAYS"] = "1"
            }).Build();
            var paths = new NexusStatePaths(config);
            await File.WriteAllTextAsync(paths.PathFor("history.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(new ConversationEntry(DateTimeOffset.UtcNow.AddDays(-2), "local", "expired", "reply")) + "\n");
            var store = new ConversationStore(paths, config);
            Assert.Empty(await store.RecentAsync(50, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
