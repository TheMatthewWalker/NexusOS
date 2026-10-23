using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

// M2-3 (#52): migration atomicity, retention edges, permissions and erasure.
[Collection("SqliteFaultInjection")]
public sealed class SqlitePersistenceSecurityTests
{
    private static string Line(string prompt, DateTimeOffset? at = null) =>
        JsonSerializer.Serialize(new ConversationEntry(at ?? DateTimeOffset.UtcNow, "local", prompt, "reply")) + "\n";

    private static long Count(string root, string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "state.sqlite3")};Pooling=False");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task LegacyRowsWithNullFieldsAreSkippedInsteadOfBlockingStartup()
    {
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        File.WriteAllText(Path.Combine(state.Root, "history.jsonl"),
            Line("good one") +
            """{"Timestamp":"2026-10-20T10:00:00+00:00","Provider":null,"Prompt":"null provider","Answer":"x"}""" + "\n" +
            """{"Timestamp":"2026-10-20T10:00:00+00:00","Provider":"local","Prompt":null,"Answer":"x"}""" + "\n" +
            "\"just a string\"\n" + "[]\n" + Line("good two"));
        var store = new ConversationStore(state.Paths(), state.Config());
        var prompts = (await store.RecentAsync(50, CancellationToken.None)).Select(x => x.Prompt).ToArray();
        Assert.Equal(["good two", "good one"], prompts);
    }

    [Fact]
    public void FailedImportRollsBackCompletelyAndRetriesExactlyOnce()
    {
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        File.WriteAllText(Path.Combine(state.Root, "provider.json"), "{\"Selected\":\"llama-cpp\"}");
        File.WriteAllText(Path.Combine(state.Root, "history.jsonl"), Line("one") + Line("two") + Line("three"));

        SqliteStateDatabase.ImportFault = rows => rows == 2;
        try
        {
            Assert.Throws<IOException>(() => new SqliteStateDatabase(state.Paths()));
        }
        finally { SqliteStateDatabase.ImportFault = null; }

        // Nothing from the failed attempt is visible: no rows, no provider, no completion marker.
        // (Schema steps commit separately, each with its own marker.)
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM history"));
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM settings"));
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM metadata WHERE key NOT LIKE 'schema_%'"));

        var database = new SqliteStateDatabase(state.Paths());
        Assert.Equal(3, Count(state.Root, "SELECT COUNT(*) FROM history"));
        Assert.Equal("llama-cpp", new ProviderStore(database).Selected);
        _ = new SqliteStateDatabase(state.Paths());
        Assert.Equal(3, Count(state.Root, "SELECT COUNT(*) FROM history"));
    }

    [Fact]
    public void LegacyContentAddedAfterImportIsNotReimported()
    {
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        var legacy = Path.Combine(state.Root, "history.jsonl");
        File.WriteAllText(legacy, Line("before"));
        _ = new SqliteStateDatabase(state.Paths());
        File.AppendAllText(legacy, Line("after"));
        _ = new SqliteStateDatabase(state.Paths());
        Assert.Equal(1, Count(state.Root, "SELECT COUNT(*) FROM history"));
        Assert.Contains("after", File.ReadAllText(legacy)); // Backup retained untouched.
    }

    [Fact]
    public async Task FutureDatedRowsAreHiddenAndPruned()
    {
        using var state = new TempState();
        var store = new ConversationStore(state.Paths(), state.Config());
        await store.AppendAsync(new(DateTimeOffset.UtcNow.AddDays(3), "local", "from the future", "x"), CancellationToken.None);
        Assert.Empty(await store.RecentAsync(50, CancellationToken.None));
        Assert.Equal(0, Count(state.Root, "SELECT COUNT(*) FROM history"));
    }

    [Fact]
    public async Task ClearHistoryLeavesNoPromptBytesInTheDatabaseFile()
    {
        using var state = new TempState();
        var store = new ConversationStore(state.Paths(), state.Config());
        var marker = "erase-me-" + Guid.NewGuid().ToString("N");
        for (var i = 0; i < 20; i++)
            await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", marker + i, marker + "answer"), CancellationToken.None);
        Assert.Contains(marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(state.Root, "state.sqlite3"))));
        await store.ClearAsync(CancellationToken.None);
        Assert.Empty(await store.RecentAsync(50, CancellationToken.None));
        foreach (var file in Directory.GetFiles(state.Root, "state.sqlite3*"))
            Assert.DoesNotContain(marker, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
    }

    [Fact]
    public void LoosenedDatabasePermissionsAreTightenedOnOpen()
    {
        if (OperatingSystem.IsWindows()) return;
        using var state = new TempState();
        var database = new SqliteStateDatabase(state.Paths());
        var file = Path.Combine(state.Root, "state.sqlite3");
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using (database.Open()) { }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Theory]
    [InlineData("state.sqlite3")]
    [InlineData("api-token")]
    public void SymlinkedStateFilesAreRejected(string name)
    {
        if (OperatingSystem.IsWindows()) return;
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        var elsewhere = Path.Combine(Path.GetTempPath(), "nexus-redirect-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(elsewhere, "");
        try
        {
            File.CreateSymbolicLink(Path.Combine(state.Root, name), elsewhere);
            var paths = state.Paths();
            Assert.Throws<IOException>(() =>
            {
                if (name == "api-token") _ = new LocalApiToken(paths);
                else _ = new SqliteStateDatabase(paths);
            });
            Assert.Equal("", File.ReadAllText(elsewhere));
        }
        finally { File.Delete(elsewhere); }
    }

    [Fact]
    public async Task RetentionLimitsHoldAcrossManyAppendsAndRestarts()
    {
        using var state = new TempState();
        var config = state.Config(("NEXUS_HISTORY_LIMIT", "5"));
        for (var round = 0; round < 3; round++)
        {
            var store = new ConversationStore(new NexusStatePaths(config), config);
            for (var i = 0; i < 4; i++)
                await store.AppendAsync(new(DateTimeOffset.UtcNow, "local", $"r{round}-{i}", "x"), CancellationToken.None);
        }
        Assert.Equal(5, Count(state.Root, "SELECT COUNT(*) FROM history"));
        var latest = await new ConversationStore(new NexusStatePaths(config), config).RecentAsync(50, CancellationToken.None);
        Assert.Equal(["r2-3", "r2-2", "r2-1", "r2-0", "r1-3"], latest.Select(x => x.Prompt));
    }

    [Theory]
    [InlineData("-5", 0)]
    [InlineData("5000", 1000)]
    [InlineData("abc", 200)]
    public void HistoryLimitIsClamped(string configured, int expected)
    {
        using var state = new TempState();
        var store = new ConversationStore(state.Paths(), state.Config(("NEXUS_HISTORY_LIMIT", configured)));
        Assert.Equal(expected, store.MaxEntries);
    }
}

[CollectionDefinition("SqliteFaultInjection", DisableParallelization = true)]
public sealed class SqliteFaultInjectionCollection { }
