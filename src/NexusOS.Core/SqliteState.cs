using Microsoft.Data.Sqlite;
using System.Text.Json;

public sealed class SqliteStateDatabase
{
    private readonly string _path;
    // Test-only fault injection: returns true to abort the legacy import after N rows.
    internal static Func<int, bool>? ImportFault;
    // Test-only fault injection: returns true to abort the named schema step before it is marked done.
    internal static Func<string, bool>? MigrationFault;

    public SqliteStateDatabase(NexusStatePaths paths)
    {
        _path = paths.PathFor("state.sqlite3");
        NexusStatePaths.RejectSymbolicLinkFile(_path);
        using var connection = Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL, provider TEXT NOT NULL, prompt TEXT NOT NULL, answer TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS history_timestamp ON history(timestamp);
            CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        schema.ExecuteNonQuery();
        SchemaMigration.Apply(connection, RuleStore.MigrationMarker, RuleStore.Schema);
        MigrateLegacy(connection, paths);
        Migrate(connection, "schema_journal_v1", """
            CREATE TABLE journal(id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('setting','app','system','ai-read','egress','device')),
                actor TEXT NOT NULL, summary TEXT NOT NULL, before TEXT, after TEXT,
                reversible INTEGER, ref TEXT, undone_at TEXT);
            CREATE INDEX journal_timestamp ON journal(timestamp);
            """);
    }

    // Each schema step runs once, in one transaction with its completion
    // marker, so a failure part-way leaves the previous schema untouched.
    private static void Migrate(SqliteConnection connection, string marker, string ddl)
    {
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM metadata WHERE key=$marker";
        check.Parameters.AddWithValue("$marker", marker);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) { transaction.Commit(); return; }
        using (var apply = connection.CreateCommand())
        {
            apply.Transaction = transaction;
            apply.CommandText = ddl;
            apply.ExecuteNonQuery();
        }
        if (MigrationFault?.Invoke(marker) == true)
            throw new IOException("Injected schema migration fault.");
        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO metadata(key,value) VALUES($marker,'1')";
        mark.Parameters.AddWithValue("$marker", marker);
        mark.ExecuteNonQuery();
        transaction.Commit();
    }

    public SqliteConnection Open()
    {
        // Pooling disabled so the app does not retain a private-file handle after close.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        connection.Open();
        NexusStatePaths.RestrictPermissions(_path, false);
        using var setup = connection.CreateCommand();
        setup.CommandText = "PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;";
        setup.ExecuteNonQuery();
        return connection;
    }

    private static void MigrateLegacy(SqliteConnection connection, NexusStatePaths paths)
    {
        using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM metadata WHERE key='legacy_import_v1'";
        if (Convert.ToInt64(check.ExecuteScalar()) > 0) { transaction.Commit(); return; }

        var providerPath = paths.PathFor("provider.json");
        if (File.Exists(providerPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(providerPath));
                var name = ProviderNames.Normalise(doc.RootElement.GetProperty("Selected").GetString());
                if (name is not null)
                {
                    using var put = connection.CreateCommand();
                    put.Transaction = transaction;
                    put.CommandText = "INSERT OR IGNORE INTO settings(key,value) VALUES('provider',$value)";
                    put.Parameters.AddWithValue("$value", name);
                    put.ExecuteNonQuery();
                }
            }
            catch (JsonException) { /* Ignore corrupt legacy preferences. */ }
            catch (KeyNotFoundException) { }
            catch (InvalidOperationException) { /* "Selected" was not a string, or the root was not an object. */ }
        }

        var historyPath = paths.PathFor("history.jsonl");
        if (File.Exists(historyPath))
        {
            var imported = 0;
            foreach (var line in File.ReadLines(historyPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ConversationEntry>(line);
                    // Valid JSON with missing fields would violate NOT NULL and
                    // abort the whole import on every startup; treat it as malformed.
                    if (entry is null || entry.Provider is null || entry.Prompt is null || entry.Answer is null) continue;
                    using var put = connection.CreateCommand();
                    put.Transaction = transaction;
                    put.CommandText = """
                        INSERT INTO history(timestamp,provider,prompt,answer)
                        VALUES ($timestamp,$provider,$prompt,$answer)
                        """;
                    put.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToUniversalTime().ToString("O"));
                    put.Parameters.AddWithValue("$provider", entry.Provider);
                    put.Parameters.AddWithValue("$prompt", entry.Prompt);
                    put.Parameters.AddWithValue("$answer", entry.Answer);
                    put.ExecuteNonQuery();
                    if (ImportFault?.Invoke(++imported) == true)
                        throw new IOException("Injected legacy import fault.");
                }
                catch (JsonException) { /* Skip malformed lines only. */ }
            }
        }
        using var mark = connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO metadata(key,value) VALUES('legacy_import_v1','1')";
        mark.ExecuteNonQuery();
        transaction.Commit();
        // Intentionally retain legacy files as untouched migration backups.
    }
}

public sealed class ProviderStore
{
    private readonly SqliteStateDatabase _database;
    private readonly object _gate = new();
    private string _selected = ProviderNames.Local;
    private DateTimeOffset? _selectedAt;
    public string[] Names => ProviderNames.All;

    public ProviderStore(SqliteStateDatabase database)
    {
        _database = database;
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT value FROM settings WHERE key='provider'";
        _selected = ProviderNames.Normalise(query.ExecuteScalar() as string) ?? ProviderNames.Local;
        query.CommandText = "SELECT value FROM settings WHERE key='provider_selected_at'";
        _selectedAt = DateTimeOffset.TryParse(query.ExecuteScalar() as string, out var at) ? at : null;
    }
    public ProviderStore(NexusStatePaths paths) : this(new SqliteStateDatabase(paths)) { }

    public string Selected { get { lock (_gate) return _selected; } }
    // When the person last chose a provider; choosing the cloud provider is
    // their consent to send prompts. Null if chosen before this was recorded.
    public DateTimeOffset? SelectedAt { get { lock (_gate) return _selectedAt; } }
    public bool Select(string? name)
    {
        var next = ProviderNames.Normalise(name);
        if (next is null) return false;
        lock (_gate)
        {
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings(key,value) VALUES('provider',$value),('provider_selected_at',$at)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            var now = DateTimeOffset.UtcNow;
            cmd.Parameters.AddWithValue("$value", next);
            cmd.Parameters.AddWithValue("$at", now.ToString("O"));
            cmd.ExecuteNonQuery();
            _selected = next;
            _selectedAt = now;
        }
        return true;
    }
}

public static class HistoryRetention
{
    // One age limit (NEXUS_HISTORY_MAX_DAYS) for chat history and the activity journal.
    public static TimeSpan MaxAge(IConfiguration config) =>
        TimeSpan.FromDays(int.TryParse(config["NEXUS_HISTORY_MAX_DAYS"], out var days) ? Math.Clamp(days, 1, 365) : 30);
}

public sealed record ConversationEntry(DateTimeOffset Timestamp, string Provider, string Prompt, string Answer);

public sealed class ConversationStore
{
    private readonly SqliteStateDatabase _database;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;
    public int MaxEntries => _maxEntries;

    public ConversationStore(SqliteStateDatabase database, IConfiguration config)
    {
        _database = database;
        _maxEntries = int.TryParse(config["NEXUS_HISTORY_LIMIT"], out var count)
            ? Math.Clamp(count, 0, 1000) : 200;
        _maxAge = HistoryRetention.MaxAge(config);
    }
    public ConversationStore(NexusStatePaths paths, IConfiguration config) : this(new SqliteStateDatabase(paths), config) { }
    public ConversationStore(NexusStatePaths paths) : this(paths, new ConfigurationBuilder().Build()) { }

    public Task AppendAsync(ConversationEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_maxEntries == 0) return Task.CompletedTask;
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        using (var put = db.CreateCommand())
        {
            put.Transaction = tx;
            put.CommandText = "INSERT INTO history(timestamp,provider,prompt,answer) VALUES($time,$provider,$prompt,$answer)";
            put.Parameters.AddWithValue("$time", entry.Timestamp.ToUniversalTime().ToString("O"));
            put.Parameters.AddWithValue("$provider", entry.Provider);
            put.Parameters.AddWithValue("$prompt", entry.Prompt);
            put.Parameters.AddWithValue("$answer", entry.Answer);
            put.ExecuteNonQuery();
        }
        using (var trim = db.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = """
                DELETE FROM history
                WHERE timestamp < $cutoff OR timestamp > $future OR
                  id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT $limit)
                """;
            trim.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(_maxAge).ToString("O"));
            trim.Parameters.AddWithValue("$future", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
            trim.Parameters.AddWithValue("$limit", _maxEntries);
            trim.ExecuteNonQuery();
        }
        ActivityJournal.PruneExpired(db, tx, _maxAge);
        tx.Commit();
        return Task.CompletedTask;
    }
    public Task ClearAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM history";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<ConversationEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp,provider,prompt,answer FROM history
            WHERE timestamp >= $cutoff AND timestamp <= $future
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(_maxAge).ToString("O"));
        cmd.Parameters.AddWithValue("$future", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        cmd.Parameters.AddWithValue("$limit", _maxEntries == 0 ? 0 : limit);
        var items = new List<ConversationEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new ConversationEntry(DateTimeOffset.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        return Task.FromResult<IReadOnlyList<ConversationEntry>>(items);
    }
}
