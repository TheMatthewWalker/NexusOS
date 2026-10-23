using Microsoft.Data.Sqlite;
using Xunit;

public sealed class FakeTime(DateTimeOffset now, TimeZoneInfo? zone = null) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => zone ?? TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => Now += by;
}

public static class RuleSamples
{
    public static readonly RuleAction OpenSpotify = new(RuleActions.AppOpen, AppId: "spotify");
    public static RuleDefinition Headphones() =>
        new(new RuleTrigger(RuleTriggers.DeviceConnected, "audio"), [], [OpenSpotify]);
    public static RuleDefinition Daily(string time, params string[] days) =>
        new(new RuleTrigger(RuleTriggers.TimeDaily, Time: time, Days: days.Length == 0 ? null : days), [], [OpenSpotify]);
}

[Collection("SchemaMigrationFault")]
public sealed class RuleStoreTests
{
    private static SqliteConnection Raw(TempState state)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(state.Root, "state.sqlite3") }.ToString());
        db.Open();
        return db;
    }

    private static long Count(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task MigrationCreatesTheTablesOnceAndKeepsRulesAcrossRestarts()
    {
        using var state = new TempState();
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), state.Config(), time);
        var rule = await store.AddAsync(RuleSamples.Headphones(), "Headphones", "When I plug in my headphones, open Spotify", true, default);
        // A second and third start do not re-run the migration or lose data.
        _ = new SqliteStateDatabase(state.Paths());
        var again = new RuleStore(new SqliteStateDatabase(state.Paths()), state.Config(), time);
        var loaded = Assert.Single(await again.ListAsync(default));
        Assert.Equal(rule.Id, loaded.Id);
        Assert.Equal("When I plug in my headphones, open Spotify", loaded.CreatedFrom);
        using var db = Raw(state);
        Assert.Equal(1, Count(db, $"SELECT COUNT(*) FROM metadata WHERE key='{RuleStore.MigrationMarker}'"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(state.Root, "state.sqlite3")));
    }

    [Fact]
    public void FailedMigrationLeavesNoTablesAndRetriesNextStart()
    {
        using var state = new TempState();
        SchemaMigration.Fault = marker => marker == RuleStore.MigrationMarker;
        try { Assert.Throws<IOException>(() => new SqliteStateDatabase(state.Paths())); }
        finally { SchemaMigration.Fault = null; }
        using (var db = Raw(state))
        {
            Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('rules','rule_runs')"));
            Assert.Equal(0, Count(db, $"SELECT COUNT(*) FROM metadata WHERE key='{RuleStore.MigrationMarker}'"));
        }
        _ = new SqliteStateDatabase(state.Paths());
        using var after = Raw(state);
        Assert.Equal(2, Count(after, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('rules','rule_runs')"));
    }

    [Fact]
    public async Task ExistingDatabaseFromBeforeRulesIsUpgradedWithoutLosingHistory()
    {
        using var state = new TempState();
        Directory.CreateDirectory(state.Root);
        using (var db = Raw(state))
        using (var cmd = db.CreateCommand())
        {
            // The schema as it was before the rules migration existed.
            cmd.CommandText = """
                CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE history(id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL, provider TEXT NOT NULL, prompt TEXT NOT NULL, answer TEXT NOT NULL);
                CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO metadata VALUES('legacy_import_v1','1');
                INSERT INTO history(timestamp,provider,prompt,answer) VALUES($t,'local','kept','yes');
                """;
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        var config = state.Config();
        var history = new ConversationStore(state.Paths(), config);
        Assert.Equal("kept", Assert.Single(await history.RecentAsync(10, default)).Prompt);
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), config, TimeProvider.System);
        await store.AddAsync(RuleSamples.Headphones(), "x", "", true, default);
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task RunHistoryFollowsHistoryAgeAndCountLimits()
    {
        using var state = new TempState();
        var time = new FakeTime(DateTimeOffset.UtcNow);
        var config = state.Config(("NEXUS_HISTORY_MAX_DAYS", "7"), ("NEXUS_HISTORY_LIMIT", "3"));
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), config, time);
        var rule = await store.AddAsync(RuleSamples.Headphones(), "x", "", true, default);
        await store.RecordRunAsync(rule.Id, time.Now.AddDays(-10), "old", rule.Actions, "events-response", default);
        // Expired runs are hidden even before the next write prunes them.
        Assert.Empty((await store.HistoryAsync(rule.Id, 50, default))!);
        for (var i = 0; i < 5; i++)
            await store.RecordRunAsync(rule.Id, time.Now.AddMinutes(i), $"run-{i}", rule.Actions, "events-response", default);
        var runs = (await store.HistoryAsync(rule.Id, 50, default))!;
        Assert.Equal(["run-4", "run-3", "run-2"], runs.Select(r => r.Trigger));
        using var db = Raw(state);
        Assert.Equal(3, Count(db, "SELECT COUNT(*) FROM rule_runs"));
        var listed = Assert.Single(await store.ListAsync(default));
        Assert.Equal(3, listed.History.Runs);
    }

    [Fact]
    public async Task HistoryLimitZeroRecordsNoRuns()
    {
        using var state = new TempState();
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), state.Config(("NEXUS_HISTORY_LIMIT", "0")), TimeProvider.System);
        var rule = await store.AddAsync(RuleSamples.Headphones(), "x", "", true, default);
        Assert.Null(await store.RecordRunAsync(rule.Id, DateTimeOffset.UtcNow, "t", rule.Actions, "queued", default));
        using var db = Raw(state);
        Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM rule_runs"));
    }

    [Fact]
    public async Task ErasingHistoryErasesRunsAndDeletingARuleErasesItsRuns()
    {
        using var state = new TempState();
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), state.Config(), TimeProvider.System);
        var a = await store.AddAsync(RuleSamples.Headphones(), "a", "", true, default);
        var b = await store.AddAsync(RuleSamples.Daily("08:00"), "b", "", true, default);
        await store.RecordRunAsync(a.Id, DateTimeOffset.UtcNow, "t", a.Actions, "events-response", default);
        await store.RecordRunAsync(b.Id, DateTimeOffset.UtcNow, "t", b.Actions, "queued", default);
        Assert.True(await store.DeleteAsync(a.Id, default));
        using (var db = Raw(state))
        {
            Assert.Equal(0, Count(db, $"SELECT COUNT(*) FROM rule_runs WHERE rule_id='{a.Id}'"));
            Assert.Equal(1, Count(db, "SELECT COUNT(*) FROM rule_runs"));
        }
        await store.ClearRunsAsync(default);
        using (var db = Raw(state)) Assert.Equal(0, Count(db, "SELECT COUNT(*) FROM rule_runs"));
        Assert.Single(await store.ListAsync(default)); // Rules themselves stay.
        // Runs for a rule that no longer exists are not recorded.
        Assert.Null(await store.RecordRunAsync(a.Id, DateTimeOffset.UtcNow, "t", a.Actions, "queued", default));
        Assert.Null(await store.HistoryAsync(a.Id, 10, default));
    }

    [Fact]
    public async Task TamperedRowsNeverLoadOrFire()
    {
        using var state = new TempState();
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), state.Config(), TimeProvider.System);
        var rule = await store.AddAsync(RuleSamples.Headphones(), "a", "", true, default);
        using (var db = Raw(state))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """UPDATE rules SET definition='{"trigger":{"type":"power.plugged"},"actions":[{"type":"shell.run","command":"reboot"}]}'""";
            cmd.ExecuteNonQuery();
        }
        Assert.Empty(await store.ListAsync(default));
        Assert.Null(await store.GetAsync(rule.Id, default));
    }

    [Fact]
    public async Task RuleCountIsBounded()
    {
        using var state = new TempState();
        var store = new RuleStore(new SqliteStateDatabase(state.Paths()), state.Config(), TimeProvider.System);
        for (var i = 0; i < RuleStore.MaxRules; i++) await store.AddAsync(RuleSamples.Headphones(), "r", "", true, default);
        await Assert.ThrowsAsync<RuleLimitException>(() => store.AddAsync(RuleSamples.Headphones(), "r", "", true, default));
    }
}

[CollectionDefinition("SchemaMigrationFault", DisableParallelization = true)]
public sealed class SchemaMigrationFaultCollection;
