using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

public sealed record RuleHistorySummary(int Runs, DateTimeOffset? LastRun);

public sealed record Rule(string Id, string Name, bool Enabled, RuleTrigger Trigger, IReadOnlyList<RuleCondition> Conditions,
    IReadOnlyList<RuleAction> Actions, string CreatedFrom, string Sentence, DateTimeOffset CreatedAt, RuleHistorySummary History)
{
    [JsonIgnore] public RuleDefinition Definition => new(Trigger, Conditions, Actions);
}

// One run of a rule: when it fired, what fired it, and the actions handed to
// the shell (in the /events response, or queued for a time trigger).
public sealed record RuleRun(long Id, DateTimeOffset Time, string Trigger, IReadOnlyList<RuleAction> Actions, string Delivery);

public sealed class RuleLimitException(string message) : Exception(message);

// Rules live in Core's per-user state.sqlite3 (0600, secure_delete). Their run
// history follows chat history's rules: NEXUS_HISTORY_MAX_DAYS hides and
// prunes old runs, NEXUS_HISTORY_LIMIT caps runs per rule (0 records none),
// and erasing history (DELETE /history) erases every run.
public sealed class RuleStore
{
    public const string MigrationMarker = "schema_rules_v1";
    public const string Schema = """
        CREATE TABLE rules(id TEXT PRIMARY KEY, name TEXT NOT NULL, enabled INTEGER NOT NULL CHECK(enabled IN (0,1)),
            definition TEXT NOT NULL, created_from TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE rule_runs(id INTEGER PRIMARY KEY AUTOINCREMENT, rule_id TEXT NOT NULL, timestamp TEXT NOT NULL,
            trigger TEXT NOT NULL, actions TEXT NOT NULL, delivery TEXT NOT NULL CHECK(delivery IN ('events-response','queued')));
        CREATE INDEX rule_runs_rule ON rule_runs(rule_id, id);
        CREATE INDEX rule_runs_timestamp ON rule_runs(timestamp);
        """;
    public const int MaxRules = 100;
    public const int MaxRunsTotal = 10_000;
    public const int MaxHistoryPage = 100;

    private readonly SqliteStateDatabase _database;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    public int MaxRunsPerRule { get; }
    public TimeSpan MaxAge { get; }

    public RuleStore(SqliteStateDatabase database, IConfiguration config, TimeProvider time)
    {
        _database = database;
        _time = time;
        MaxRunsPerRule = int.TryParse(config["NEXUS_HISTORY_LIMIT"], out var count) ? Math.Clamp(count, 0, 1000) : 200;
        MaxAge = TimeSpan.FromDays(int.TryParse(config["NEXUS_HISTORY_MAX_DAYS"], out var days) ? Math.Clamp(days, 1, 365) : 30);
    }

    public static bool IsValidId(string? id) => id is { Length: 16 } && id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    public Task<Rule> AddAsync(RuleDefinition definition, string name, string createdFrom, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RuleSchema.Validate(definition);
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            using var db = _database.Open();
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM rules";
            if (Convert.ToInt64(cmd.ExecuteScalar()) >= MaxRules)
                throw new RuleLimitException($"You can keep up to {MaxRules} rules. Remove one first.");
            cmd.CommandText = """
                INSERT INTO rules(id,name,enabled,definition,created_from,created_at,updated_at)
                VALUES($id,$name,$enabled,$definition,$from,$now,$now)
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$definition", RuleSchema.Serialize(definition));
            cmd.Parameters.AddWithValue("$from", createdFrom);
            cmd.Parameters.AddWithValue("$now", now.ToString("O"));
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        return Task.FromResult(new Rule(id, name, enabled, definition.Trigger, definition.Conditions, definition.Actions,
            createdFrom, RuleSchema.Describe(definition), now, new RuleHistorySummary(0, null)));
    }

    public Task<IReadOnlyList<Rule>> ListAsync(CancellationToken ct) => Task.FromResult(Load(null, ct));

    public Task<Rule?> GetAsync(string id, CancellationToken ct) =>
        Task.FromResult(IsValidId(id) ? Load(id, ct).SingleOrDefault() : null);

    public async Task<Rule?> SetEnabledAsync(string id, bool enabled, CancellationToken ct)
    {
        if (!IsValidId(id)) return null;
        using (var db = _database.Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rules SET enabled=$enabled, updated_at=$now WHERE id=$id";
            cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", _time.GetUtcNow().ToString("O"));
            cmd.Parameters.AddWithValue("$id", id);
            if (cmd.ExecuteNonQuery() == 0) return null;
        }
        return await GetAsync(id, ct);
    }

    // Removes the rule and its whole run history in one transaction.
    public Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsValidId(id)) return Task.FromResult(false);
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.CommandText = "DELETE FROM rule_runs WHERE rule_id=$id";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "DELETE FROM rules WHERE id=$id";
        var removed = cmd.ExecuteNonQuery() > 0;
        tx.Commit();
        return Task.FromResult(removed);
    }

    public Task<IReadOnlyList<RuleRun>?> HistoryAsync(string id, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > MaxHistoryPage) throw new ArgumentOutOfRangeException(nameof(limit));
        ct.ThrowIfCancellationRequested();
        if (!IsValidId(id)) return Task.FromResult<IReadOnlyList<RuleRun>?>(null);
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rules WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt64(cmd.ExecuteScalar()) == 0) return Task.FromResult<IReadOnlyList<RuleRun>?>(null);
        cmd.CommandText = """
            SELECT id,timestamp,trigger,actions,delivery FROM rule_runs
            WHERE rule_id=$id AND timestamp >= $cutoff AND timestamp <= $future
            ORDER BY id DESC LIMIT $limit
            """;
        AddWindow(cmd);
        cmd.Parameters.AddWithValue("$limit", MaxRunsPerRule == 0 ? 0 : limit);
        var runs = new List<RuleRun>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var actions = JsonSerializer.Deserialize<List<RuleAction>>(reader.GetString(3), RuleSchema.Json) ?? [];
            runs.Add(new RuleRun(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2),
                actions, reader.GetString(4)));
        }
        return Task.FromResult<IReadOnlyList<RuleRun>?>(runs);
    }

    // Returns the run id, or null when run history is turned off.
    public Task<long?> RecordRunAsync(string ruleId, DateTimeOffset at, string trigger, IReadOnlyList<RuleAction> actions,
        string delivery, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (MaxRunsPerRule == 0) return Task.FromResult<long?>(null);
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO rule_runs(rule_id,timestamp,trigger,actions,delivery)
            SELECT $rule,$time,$trigger,$actions,$delivery WHERE EXISTS(SELECT 1 FROM rules WHERE id=$rule);
            SELECT CASE WHEN changes() > 0 THEN last_insert_rowid() END;
            """;
        cmd.Parameters.AddWithValue("$rule", ruleId);
        cmd.Parameters.AddWithValue("$time", at.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$trigger", trigger);
        cmd.Parameters.AddWithValue("$actions", JsonSerializer.Serialize(actions, RuleSchema.Json));
        cmd.Parameters.AddWithValue("$delivery", delivery);
        var id = cmd.ExecuteScalar() is long value ? value : (long?)null;
        using (var trim = db.CreateCommand())
        {
            trim.Transaction = tx;
            trim.CommandText = """
                DELETE FROM rule_runs WHERE timestamp < $cutoff OR timestamp > $future OR
                  (rule_id=$rule AND id NOT IN (SELECT id FROM rule_runs WHERE rule_id=$rule ORDER BY id DESC LIMIT $limit)) OR
                  id NOT IN (SELECT id FROM rule_runs ORDER BY id DESC LIMIT $total);
                """;
            AddWindow(trim);
            trim.Parameters.AddWithValue("$rule", ruleId);
            trim.Parameters.AddWithValue("$limit", MaxRunsPerRule);
            trim.Parameters.AddWithValue("$total", MaxRunsTotal);
            trim.ExecuteNonQuery();
        }
        tx.Commit();
        return Task.FromResult(id);
    }

    // Part of erasing history: every rule's runs go, the rules stay.
    public Task ClearRunsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM rule_runs";
        cmd.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    private IReadOnlyList<Rule> Load(string? id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        var summaries = new Dictionary<string, RuleHistorySummary>(StringComparer.Ordinal);
        using (var runs = db.CreateCommand())
        {
            runs.CommandText = """
                SELECT rule_id, COUNT(*), MAX(timestamp) FROM rule_runs
                WHERE timestamp >= $cutoff AND timestamp <= $future GROUP BY rule_id
                """;
            AddWindow(runs);
            using var reader = runs.ExecuteReader();
            while (reader.Read())
                summaries[reader.GetString(0)] = new RuleHistorySummary(reader.GetInt32(1), DateTimeOffset.Parse(reader.GetString(2)));
        }
        using var cmd = db.CreateCommand();
        cmd.CommandText = id is null
            ? "SELECT id,name,enabled,definition,created_from,created_at FROM rules ORDER BY created_at, id"
            : "SELECT id,name,enabled,definition,created_from,created_at FROM rules WHERE id=$id";
        if (id is not null) cmd.Parameters.AddWithValue("$id", id);
        var rules = new List<Rule>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                RuleDefinition definition;
                // Stored rows are re-validated on every read: a row edited outside
                // Core that no longer fits the schema never fires.
                try { definition = RuleSchema.Deserialize(reader.GetString(3)); }
                catch (Exception ex) when (ex is RuleRejectedException or JsonException) { continue; }
                var ruleId = reader.GetString(0);
                rules.Add(new Rule(ruleId, reader.GetString(1), reader.GetInt64(2) == 1, definition.Trigger,
                    definition.Conditions, definition.Actions, reader.GetString(4), RuleSchema.Describe(definition),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    summaries.TryGetValue(ruleId, out var summary) ? summary : new RuleHistorySummary(0, null)));
            }
        }
        return rules;
    }

    private void AddWindow(SqliteCommand cmd)
    {
        var now = _time.GetUtcNow();
        cmd.Parameters.AddWithValue("$cutoff", now.Subtract(MaxAge).ToString("O"));
        cmd.Parameters.AddWithValue("$future", now.AddMinutes(5).ToString("O"));
    }
}
