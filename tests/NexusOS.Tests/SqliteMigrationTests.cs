using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;

[Collection("SqliteFaultInjection")]
public sealed class SqliteMigrationTests
{
    private static IConfiguration Config(string root) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string,string?> { ["NEXUS_STATE_DIR"] = root })
        .Build();

    [Fact]
    public async Task ImportsLegacyProviderAndChatOnceAndPersistsSelection()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "provider.json"), "{\"Selected\":\"llama-cpp\"}");
            var chat = new ConversationEntry(DateTimeOffset.UtcNow, "local", "legacy prompt", "legacy reply");
            File.WriteAllText(Path.Combine(root, "history.jsonl"), JsonSerializer.Serialize(chat) + "\n");
            var config = Config(root);
            var paths = new NexusStatePaths(config);
            var store = new ConversationStore(paths, config);
            Assert.Equal("llama-cpp", new ProviderStore(paths).Selected);
            Assert.Equal("legacy prompt", Assert.Single(await store.RecentAsync(50, CancellationToken.None)).Prompt);
            Assert.True(new ProviderStore(paths).Select("openai-compatible"));
            Assert.Equal("openai-compatible", new ProviderStore(paths).Selected);
            // A second startup must not duplicate the imported history.
            var again = new ConversationStore(new NexusStatePaths(config), config);
            Assert.Single(await again.RecentAsync(50, CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(root, "history.jsonl")));
            Assert.True(File.Exists(Path.Combine(root, "provider.json")));
            Assert.True(File.Exists(Path.Combine(root, "state.sqlite3")));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(Path.Combine(root, "state.sqlite3")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CorruptLegacyLinesDoNotBlockValidRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-migration-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "provider.json"), "{bad-json");
            File.WriteAllLines(Path.Combine(root, "history.jsonl"), new[]
            {
                "not json",
                JsonSerializer.Serialize(new ConversationEntry(DateTimeOffset.UtcNow, "local", "valid", "yes"))
            });
            var config = Config(root);
            var paths = new NexusStatePaths(config);
            Assert.Equal("local", new ProviderStore(paths).Selected);
            Assert.Equal("valid", Assert.Single(await new ConversationStore(paths, config)
                .RecentAsync(50, CancellationToken.None)).Prompt);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SqliteHistoryRetainsBoundsAcrossRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexus-sqlite-bounds-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["NEXUS_STATE_DIR"] = root, ["NEXUS_HISTORY_LIMIT"] = "2" }).Build();
            var store = new ConversationStore(new NexusStatePaths(config), config);
            for (var i = 0; i < 4; i++)
                await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", $"chat-{i}", "ok"), CancellationToken.None);
            var items = await new ConversationStore(new NexusStatePaths(config), config)
                .RecentAsync(50, CancellationToken.None);
            Assert.Equal(new[] { "chat-3", "chat-2" }, items.Select(x => x.Prompt));
            await store.ClearAsync(CancellationToken.None);
            Assert.Empty(await new ConversationStore(new NexusStatePaths(config), config)
                .RecentAsync(50, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static long Scalar(string root, string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "state.sqlite3")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public void FailedJournalMigrationRollsBackAndRetriesOnce()
    {
        using var state = new TempState();
        SqliteStateDatabase.MigrationFault = marker => marker == "schema_journal_v1";
        try { Assert.Throws<IOException>(() => new SqliteStateDatabase(state.Paths())); }
        finally { SqliteStateDatabase.MigrationFault = null; }

        // The table and its marker commit together or not at all.
        Assert.Equal(0, Scalar(state.Root, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('journal','journal_timestamp')"));
        Assert.Equal(0, Scalar(state.Root, "SELECT COUNT(*) FROM metadata WHERE key='schema_journal_v1'"));

        _ = new SqliteStateDatabase(state.Paths());
        _ = new SqliteStateDatabase(state.Paths());
        Assert.Equal(2, Scalar(state.Root, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('journal','journal_timestamp')"));
        Assert.Equal(1, Scalar(state.Root, "SELECT COUNT(*) FROM metadata WHERE key='schema_journal_v1'"));
    }

    [Fact]
    public async Task DatabaseFromBeforeTheJournalIsUpgradedInPlace()
    {
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        using (var db = new SqliteConnection($"Data Source={Path.Combine(state.Root, "state.sqlite3")};Pooling=False"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE history(id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp TEXT NOT NULL, provider TEXT NOT NULL, prompt TEXT NOT NULL, answer TEXT NOT NULL);
                CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata VALUES('legacy_import_v1','1');
                INSERT INTO settings VALUES('provider','llama-cpp');
                INSERT INTO history(timestamp,provider,prompt,answer) VALUES('{DateTimeOffset.UtcNow:O}','local','kept','yes');
                """;
            cmd.ExecuteNonQuery();
        }
        var database = new SqliteStateDatabase(state.Paths());
        Assert.Equal("llama-cpp", new ProviderStore(database).Selected);
        Assert.Equal("kept", Assert.Single(await new ConversationStore(database, state.Config()).RecentAsync(50, CancellationToken.None)).Prompt);
        var journal = new ActivityJournal(database, state.Config());
        await journal.AppendAsync(new JournalRecord(JournalKinds.Setting, "user", "Night light on"), CancellationToken.None);
        Assert.Single(await journal.QueryAsync(null, null, 10, CancellationToken.None));
    }
}
