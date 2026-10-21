using Microsoft.Data.Sqlite;
using System.Text.Json;

public sealed class SqliteStateDatabase
{
    private readonly string _path;
    public SqliteStateDatabase(NexusStatePaths paths)
    {
        _path = paths.PathFor("state.sqlite3");
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
        MigrateLegacy(connection, paths);
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
            foreach (var line in File.ReadLines(historyPath))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ConversationEntry>(line);
                    if (entry is null) continue;
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
    public string[] Names => ProviderNames.All;

    public ProviderStore(SqliteStateDatabase database)
    {
        _database = database;
        using var db = database.Open();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT value FROM settings WHERE key='provider'";
        _selected = ProviderNames.Normalise(query.ExecuteScalar() as string) ?? ProviderNames.Local;
    }
    public ProviderStore(NexusStatePaths paths) : this(new SqliteStateDatabase(paths)) { }

    public string Selected { get { lock (_gate) return _selected; } }
    public bool Select(string? name)
    {
        var next = ProviderNames.Normalise(name);
        if (next is null) return false;
        lock (_gate)
        {
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO settings(key,value) VALUES('provider',$value)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            cmd.Parameters.AddWithValue("$value", next);
            cmd.ExecuteNonQuery();
            _selected = next;
        }
        return true;
    }
}

public sealed record ConversationEntry(DateTimeOffset Timestamp, string Provider, string Prompt, string Answer);

public sealed class ConversationStore
{
    private readonly SqliteStateDatabase _database;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;

    public ConversationStore(SqliteStateDatabase database, IConfiguration config)
    {
        _database = database;
        _maxEntries = int.TryParse(config["NEXUS_HISTORY_LIMIT"], out var count)
            ? Math.Clamp(count, 0, 1000) : 200;
        _maxAge = TimeSpan.FromDays(int.TryParse(config["NEXUS_HISTORY_MAX_DAYS"], out var days)
            ? Math.Clamp(days, 1, 365) : 30);
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
