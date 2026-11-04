using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class JournalKinds
{
    public const string Setting = "setting", App = "app", System = "system", AiRead = "ai-read", Egress = "egress", Device = "device";
    public static readonly string[] All = [Setting, App, System, AiRead, Egress, Device];
    // The shell records only what it alone observes; Core records the rest itself.
    public static readonly string[] FromShell = [Setting, Device];
}

public sealed record JournalRecord(string Kind, string Actor, string Summary, JsonElement? Before = null,
    JsonElement? After = null, bool? Reversible = null, string? Ref = null);

public sealed record JournalEntry(long Id, DateTimeOffset Time, string Kind, string Actor, string Summary,
    JsonElement? Before, JsonElement? After, bool? Reversible, string? Ref, DateTimeOffset? UndoneAt);

public sealed record JournalDaySummary(string Day, long LeftTheComputer, long AiReads, long DeviceUses);

// Per-user, append-only log of what happened (docs/design/features.md). It
// shares the history database, age limit and erasure rules, and never holds
// prompt or answer text. Days are the machine's local calendar days.
public sealed partial class ActivityJournal
{
    public const int MaxSummaryLength = 200, MaxJsonBytes = 4096, MaxRefLength = 512, MaxPage = 500;
    // A hard row cap so a chatty shell cannot grow the database without bound.
    public const int MaxEntries = 10000;
    private const string Columns = "id,timestamp,kind,actor,summary,before,after,reversible,ref,undone_at";
    private readonly SqliteStateDatabase _database;
    private readonly TimeSpan _maxAge;

    public ActivityJournal(SqliteStateDatabase database, IConfiguration config)
    {
        _database = database;
        _maxAge = HistoryRetention.MaxAge(config);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ActorPattern();

    public static string? Validate(JournalRecord record)
    {
        if (!JournalKinds.All.Contains(record.Kind)) return "Unknown journal kind.";
        if (record.Actor is null || !ActorPattern().IsMatch(record.Actor)) return "Actor must be 'user', 'nexus' or an app name.";
        if (string.IsNullOrWhiteSpace(record.Summary) || record.Summary.Trim().Length > MaxSummaryLength || record.Summary.Any(char.IsControl))
            return $"Summary must be 1-{MaxSummaryLength} characters without control characters.";
        if (record.Ref is not null && (record.Ref.Length is 0 or > MaxRefLength || record.Ref.Any(char.IsControl)))
            return $"Ref must be 1-{MaxRefLength} characters without control characters.";
        if (Json(record.Before) is { } before && Encoding.UTF8.GetByteCount(before) > MaxJsonBytes ||
            Json(record.After) is { } after && Encoding.UTF8.GetByteCount(after) > MaxJsonBytes)
            return $"Before and after values must each be at most {MaxJsonBytes} bytes of JSON.";
        return null;
    }

    public Task<long> AppendAsync(JournalRecord record, CancellationToken ct, DateTimeOffset? at = null)
    {
        ct.ThrowIfCancellationRequested();
        if (Validate(record) is { } error) throw new ArgumentException(error);
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        using var put = db.CreateCommand();
        put.Transaction = tx;
        put.CommandText = """
            INSERT INTO journal(timestamp,kind,actor,summary,before,after,reversible,ref)
            VALUES($time,$kind,$actor,$summary,$before,$after,$reversible,$ref) RETURNING id
            """;
        put.Parameters.AddWithValue("$time", Stamp(at ?? DateTimeOffset.UtcNow));
        put.Parameters.AddWithValue("$kind", record.Kind);
        put.Parameters.AddWithValue("$actor", record.Actor);
        put.Parameters.AddWithValue("$summary", record.Summary.Trim());
        put.Parameters.AddWithValue("$before", (object?)Json(record.Before) ?? DBNull.Value);
        put.Parameters.AddWithValue("$after", (object?)Json(record.After) ?? DBNull.Value);
        put.Parameters.AddWithValue("$reversible", record.Reversible is { } r ? r ? 1 : 0 : DBNull.Value);
        put.Parameters.AddWithValue("$ref", (object?)record.Ref ?? DBNull.Value);
        var id = Convert.ToInt64(put.ExecuteScalar());
        PruneExpired(db, tx, _maxAge);
        tx.Commit();
        return Task.FromResult(id);
    }

    // Records that a prompt is about to leave the machine: who received it and
    // when the person consented. The prompt itself is never recorded.
    public Task RecordEgressAsync(string provider, string host, DateTimeOffset? consentAt, CancellationToken ct) =>
        AppendAsync(new JournalRecord(JournalKinds.Egress, "nexus", $"Sent a request to {host} ({provider})",
            After: JsonSerializer.SerializeToElement(new { provider, host, consentAt }), Ref: host), ct);

    // Shared with history retention: drops expired and future-dated rows, then
    // the oldest rows beyond the hard cap.
    internal static void PruneExpired(SqliteConnection db, SqliteTransaction tx, TimeSpan maxAge)
    {
        using var trim = db.CreateCommand();
        trim.Transaction = tx;
        trim.CommandText = """
            DELETE FROM journal
            WHERE timestamp < $cutoff OR timestamp > $future OR
              id NOT IN (SELECT id FROM journal ORDER BY id DESC LIMIT $limit)
            """;
        trim.Parameters.AddWithValue("$cutoff", Stamp(DateTimeOffset.UtcNow.Subtract(maxAge)));
        trim.Parameters.AddWithValue("$future", Stamp(DateTimeOffset.UtcNow.AddMinutes(5)));
        trim.Parameters.AddWithValue("$limit", MaxEntries);
        trim.ExecuteNonQuery();
    }

    public Task<IReadOnlyList<JournalEntry>> QueryAsync(DateTimeOffset? since, string? kind, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > MaxPage) throw new ArgumentOutOfRangeException(nameof(limit));
        if (kind is not null && !JournalKinds.All.Contains(kind)) throw new ArgumentException("Unknown journal kind.");
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT {Columns} FROM journal
            WHERE timestamp >= $cutoff AND timestamp <= $future AND ($kind IS NULL OR kind=$kind)
            ORDER BY id DESC LIMIT $limit
            """;
        var cutoff = DateTimeOffset.UtcNow.Subtract(_maxAge);
        cmd.Parameters.AddWithValue("$cutoff", Stamp(since > cutoff ? since.Value : cutoff));
        cmd.Parameters.AddWithValue("$future", Stamp(DateTimeOffset.UtcNow.AddMinutes(5)));
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);
        return Task.FromResult<IReadOnlyList<JournalEntry>>(ReadAll(cmd));
    }

    public Task<JournalDaySummary> SummaryAsync(DateOnly day, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (start, end) = DayBounds(day);
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(kind='egress'),0), COALESCE(SUM(kind='ai-read'),0), COALESCE(SUM(kind='device'),0)
            FROM journal WHERE timestamp >= $start AND timestamp < $end AND timestamp >= $cutoff
            """;
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$end", end);
        cmd.Parameters.AddWithValue("$cutoff", Stamp(DateTimeOffset.UtcNow.Subtract(_maxAge)));
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return Task.FromResult(new JournalDaySummary(day.ToString("yyyy-MM-dd"), reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)));
    }

    // Core cannot change shell settings, so undo hands back the old value for
    // the shell to write and marks the entry undone. Null means no such entry.
    public Task<(JournalEntry? Entry, string? Conflict)> UndoAsync(long id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        using var find = db.CreateCommand();
        find.Transaction = tx;
        find.CommandText = $"SELECT {Columns} FROM journal WHERE id=$id AND timestamp >= $cutoff AND timestamp <= $future";
        find.Parameters.AddWithValue("$id", id);
        find.Parameters.AddWithValue("$cutoff", Stamp(DateTimeOffset.UtcNow.Subtract(_maxAge)));
        find.Parameters.AddWithValue("$future", Stamp(DateTimeOffset.UtcNow.AddMinutes(5)));
        var entry = ReadAll(find).SingleOrDefault();
        if (entry is null) return Task.FromResult<(JournalEntry?, string?)>((null, null));
        var conflict = entry.Kind != JournalKinds.Setting || entry.Before is null || entry.Reversible == false
            ? "Only reversible setting entries with a previous value can be undone."
            : entry.UndoneAt is not null ? "This entry was already undone." : null;
        if (conflict is not null) return Task.FromResult<(JournalEntry?, string?)>((entry, conflict));
        var now = DateTimeOffset.UtcNow;
        using var mark = db.CreateCommand();
        mark.Transaction = tx;
        mark.CommandText = "UPDATE journal SET undone_at=$now WHERE id=$id AND undone_at IS NULL";
        mark.Parameters.AddWithValue("$now", Stamp(now));
        mark.Parameters.AddWithValue("$id", id);
        mark.ExecuteNonQuery();
        tx.Commit();
        return Task.FromResult<(JournalEntry?, string?)>((entry with { UndoneAt = now }, null));
    }

    // One entry within the age limit, or null.
    public Task<JournalEntry?> GetAsync(long id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var find = db.CreateCommand();
        find.CommandText = $"SELECT {Columns} FROM journal WHERE id=$id AND timestamp >= $cutoff AND timestamp <= $future";
        find.Parameters.AddWithValue("$id", id);
        find.Parameters.AddWithValue("$cutoff", Stamp(DateTimeOffset.UtcNow.Subtract(_maxAge)));
        find.Parameters.AddWithValue("$future", Stamp(DateTimeOffset.UtcNow.AddMinutes(5)));
        return Task.FromResult(ReadAll(find).SingleOrDefault());
    }

    // The newest entry of a kind about the same thing (an app in one place), so
    // an Undo can refuse when a later change followed.
    public Task<long?> LatestRefAsync(string kind, string reference, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var find = db.CreateCommand();
        find.CommandText = "SELECT MAX(id) FROM journal WHERE kind=$kind AND ref=$ref AND timestamp >= $cutoff";
        find.Parameters.AddWithValue("$kind", kind);
        find.Parameters.AddWithValue("$ref", reference);
        find.Parameters.AddWithValue("$cutoff", Stamp(DateTimeOffset.UtcNow.Subtract(_maxAge)));
        return Task.FromResult(find.ExecuteScalar() is long id ? (long?)id : null);
    }

    // App entries are marked undone once the opposite change has been reported
    // (AppChanges), not when Undo is asked for. False if it already was.
    public Task<bool> MarkUndoneAsync(long id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var db = _database.Open();
        using var mark = db.CreateCommand();
        mark.CommandText = "UPDATE journal SET undone_at=$now WHERE id=$id AND undone_at IS NULL";
        mark.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
        mark.Parameters.AddWithValue("$id", id);
        return Task.FromResult(mark.ExecuteNonQuery() == 1);
    }

    // "Forget today": deletes the day's entries with secure_delete on (set by
    // SqliteStateDatabase.Open), and checks none remain before committing.
    public Task<long> ForgetDayAsync(DateOnly day, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (start, end) = DayBounds(day);
        using var db = _database.Open();
        using var tx = db.BeginTransaction();
        using var erase = db.CreateCommand();
        erase.Transaction = tx;
        erase.CommandText = "DELETE FROM journal WHERE timestamp >= $start AND timestamp < $end";
        erase.Parameters.AddWithValue("$start", start);
        erase.Parameters.AddWithValue("$end", end);
        long erased = erase.ExecuteNonQuery();
        erase.CommandText = "SELECT COUNT(*) FROM journal WHERE timestamp >= $start AND timestamp < $end";
        if (Convert.ToInt64(erase.ExecuteScalar()) != 0)
            throw new InvalidOperationException("Journal erasure could not be verified.");
        tx.Commit();
        return Task.FromResult(erased);
    }

    public static (string Start, string End) DayBounds(DateOnly day)
    {
        var start = DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local);
        return (Stamp(start), Stamp(start.AddDays(1)));
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private static string Stamp(DateTimeOffset time) => time.ToUniversalTime().ToString("O");

    private static string? Json(JsonElement? value) =>
        value is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } element ? element.GetRawText() : null;

    private static List<JournalEntry> ReadAll(SqliteCommand cmd)
    {
        var items = new List<JournalEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            items.Add(new JournalEntry(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), Element(reader, 5), Element(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7) == 1, reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9))));
        return items;
    }

    private static JsonElement? Element(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        using var document = JsonDocument.Parse(reader.GetString(ordinal));
        return document.RootElement.Clone();
    }
}
