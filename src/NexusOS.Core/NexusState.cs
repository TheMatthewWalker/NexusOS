using System.Text.Json;

public sealed class NexusStatePaths
{
    public string Root { get; }

    public NexusStatePaths(IConfiguration configuration)
    {
        var explicitRoot = configuration["NEXUS_STATE_DIR"];
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var basePath = !string.IsNullOrWhiteSpace(explicitRoot)
            ? explicitRoot
            : !string.IsNullOrWhiteSpace(xdgState)
                ? Path.Combine(xdgState, "nexusos")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "nexusos");
        Root = Path.GetFullPath(basePath);
        RejectSymbolicLinkComponents(Root);
        Directory.CreateDirectory(Root);
        RejectSymbolicLinkComponents(Root);
        RestrictPermissions(Root, true);
    }

    // Reject symlinked ancestors as well as a symlink at the state directory
    // itself. This is defense in depth, not a race-free openat2 boundary.
    internal static void RejectSymbolicLinkComponents(string fullPath)
    {
        var path = Path.GetFullPath(fullPath);
        while (!string.IsNullOrEmpty(path))
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Nexus state path cannot contain symbolic links.");
            }
            else if (new FileInfo(path).LinkTarget is not null)
                throw new IOException("Nexus state path cannot contain symbolic links.");
            var parent = Path.GetDirectoryName(path);
            if (parent is null || parent == path) break;
            path = parent;
        }
    }

    public string PathFor(string name) => Path.Combine(Root, name);

    public static void RestrictPermissions(string path, bool isDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, isDirectory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

public sealed class ConversationStore
{
    private readonly string _dbPath;
    private readonly string _legacyPath;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversationStore(NexusStatePaths paths) : this(paths, new ConfigurationBuilder().Build()) { }

    public ConversationStore(NexusStatePaths paths, IConfiguration config)
    {
        _dbPath = paths.PathFor("history.sqlite3");
        _legacyPath = paths.PathFor("history.jsonl");
        _maxEntries = int.TryParse(config["NEXUS_HISTORY_LIMIT"], out var count)
            ? Math.Clamp(count, 0, 1000) : 200;
        var days = int.TryParse(config["NEXUS_HISTORY_MAX_DAYS"], out var parsed)
            ? Math.Clamp(parsed, 1, 365) : 30;
        _maxAge = TimeSpan.FromDays(days);
    }

    private async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
        await connection.OpenAsync(ct);
        try
        {
            NexusStatePaths.RestrictPermissions(_dbPath, false);
            await using var schema = connection.CreateCommand();
            schema.CommandText = "CREATE TABLE IF NOT EXISTS history (id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp TEXT NOT NULL, provider TEXT NOT NULL, prompt TEXT NOT NULL, answer TEXT NOT NULL); CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            await schema.ExecuteNonQueryAsync(ct);
            // Legacy file is retained as a recovery source; migration is idempotent.
            await using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM metadata WHERE key='legacy_migrated'";
            var migrated = Convert.ToInt64(await check.ExecuteScalarAsync(ct)) != 0;
            if (!migrated)
            {
                using var tx = connection.BeginTransaction();
                if (File.Exists(_legacyPath))
                {
                    foreach (var line in await File.ReadAllLinesAsync(_legacyPath, ct))
                    {
                        try
                        {
                            var entry = JsonSerializer.Deserialize<ConversationEntry>(line);
                            if (entry is not null) await InsertAsync(connection, tx, entry, ct);
                        }
                        catch (JsonException) { /* Legacy malformed records are skipped. */ }
                    }
                }
                await using var marker = connection.CreateCommand();
                marker.Transaction = tx;
                marker.CommandText = "INSERT INTO metadata(key,value) VALUES('legacy_migrated','1')";
                await marker.ExecuteNonQueryAsync(ct);
                tx.Commit();
            }
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static async Task InsertAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction tx, ConversationEntry entry, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "INSERT INTO history(timestamp,provider,prompt,answer) VALUES($ts,$provider,$prompt,$answer)";
        command.Parameters.AddWithValue("$ts", entry.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$provider", entry.Provider);
        command.Parameters.AddWithValue("$prompt", entry.Prompt);
        command.Parameters.AddWithValue("$answer", entry.Answer);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task PruneAsync(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction tx, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "DELETE FROM history WHERE timestamp < $cutoff OR timestamp > $future OR id NOT IN (SELECT id FROM history ORDER BY id DESC LIMIT $max)";
        command.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - _maxAge).ToString("O"));
        command.Parameters.AddWithValue("$future", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
        command.Parameters.AddWithValue("$max", _maxEntries);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AppendAsync(ConversationEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            using var tx = connection.BeginTransaction();
            if (_maxEntries > 0) await InsertAsync(connection, tx, entry, ct);
            await PruneAsync(connection, tx, ct);
            tx.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM history";
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ConversationEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(ct);
        try
        {
            await using var connection = await OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT timestamp,provider,prompt,answer FROM history WHERE timestamp >= $cutoff AND timestamp <= $future ORDER BY id DESC LIMIT $limit";
            command.Parameters.AddWithValue("$cutoff", (DateTimeOffset.UtcNow - _maxAge).ToString("O"));
            command.Parameters.AddWithValue("$future", DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
            command.Parameters.AddWithValue("$limit", limit);
            var entries = new List<ConversationEntry>();
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                entries.Add(new ConversationEntry(DateTimeOffset.Parse(reader.GetString(0)),
                    reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return entries;
        }
        finally { _gate.Release(); }
    }
}
