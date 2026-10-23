using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

public sealed class SearchSettings
{
    public const int MaxChunksPerFile = 2000;
    public const int MaxQueryLength = 512;
    public const int MaxLimit = 50;
    public const int MaxQueryTerms = 16;
    public const int CandidatePool = 100;
    public TimeSpan ScanInterval { get; }
    public long MaxFileBytes { get; }
    public int MaxFiles { get; }
    public int MaxChunks { get; }
    public int Workers { get; }
    public int EmbedPerScan { get; }
    public float MinSimilarity { get; }
    public TimeSpan ExtractTimeout { get; }

    public SearchSettings(IConfiguration config)
    {
        static int Int(IConfiguration c, string key, int fallback, int min, int max) =>
            int.TryParse(c[key], out var value) ? Math.Clamp(value, min, max) : fallback;
        ScanInterval = TimeSpan.FromSeconds(Int(config, "NEXUS_SEARCH_SCAN_SECONDS", 300, 10, 86400));
        MaxFileBytes = Int(config, "NEXUS_SEARCH_MAX_FILE_BYTES", 4 * 1024 * 1024, 1024, 64 * 1024 * 1024);
        MaxFiles = Int(config, "NEXUS_SEARCH_MAX_FILES", 100_000, 1, 1_000_000);
        MaxChunks = Int(config, "NEXUS_SEARCH_MAX_CHUNKS", 100_000, 1, 1_000_000);
        Workers = Int(config, "NEXUS_SEARCH_WORKERS", 2, 1, 8);
        ExtractTimeout = TimeSpan.FromSeconds(Int(config, "NEXUS_SEARCH_EXTRACT_SECONDS", 30, 1, 600));
        EmbedPerScan = Int(config, "NEXUS_SEARCH_EMBED_PER_SCAN", 5000, 0, 1_000_000);
        MinSimilarity = float.TryParse(config["NEXUS_SEARCH_MIN_SIMILARITY"], System.Globalization.CultureInfo.InvariantCulture,
            out var similarity) ? Math.Clamp(similarity, -1f, 1f) : 0.5f;
    }
}

public sealed record FileStamp(long Mtime, long Size);
public sealed record SearchCounts(long Files, long Chunks, long EmbeddedChunks);
public sealed record IndexedFile(string Path, string Source, string Kind, string Title, long Mtime, long Size, IReadOnlyList<TextChunk> Chunks);

// The index lives in its own database (search.sqlite3, 0600 in the 0700
// state directory) so it can be erased without touching history, and so a
// damaged index never blocks Core. FTS5 holds the passage text; embeddings
// are float16 unit vectors keyed by passage. secure_delete zeroes freed pages.
public sealed class SearchIndexStore
{
    private readonly string _path;
    private readonly object _writeGate = new();
    public string FilePath => _path;

    public SearchIndexStore(NexusStatePaths paths)
    {
        _path = paths.PathFor("search.sqlite3");
        NexusStatePaths.RejectSymbolicLinkFile(_path);
        using var db = Open();
        CreateSchema(db);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        connection.Open();
        NexusStatePaths.RestrictPermissions(_path, false);
        Exec(connection, "PRAGMA busy_timeout=5000; PRAGMA secure_delete=ON;");
        return connection;
    }

    private static void CreateSchema(SqliteConnection db) => Exec(db, """
        CREATE TABLE IF NOT EXISTS files(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE, source TEXT NOT NULL,
            kind TEXT NOT NULL, title TEXT NOT NULL, mtime INTEGER NOT NULL, size INTEGER NOT NULL, indexed_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS files_source ON files(source);
        CREATE TABLE IF NOT EXISTS chunks(id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, ordinal INTEGER NOT NULL,
            start INTEGER NOT NULL, length INTEGER NOT NULL, first_line INTEGER NOT NULL, last_line INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS chunks_file ON chunks(file_id);
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(title, text, tokenize='porter unicode61 remove_diacritics 2');
        CREATE TABLE IF NOT EXISTS embeddings(chunk_id INTEGER PRIMARY KEY, model TEXT NOT NULL, vector BLOB NOT NULL);
        """);

    private static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection db, string sql, params (string, object)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public Dictionary<string, FileStamp> LoadStamps()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT path, mtime, size FROM files";
        var stamps = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) stamps[reader.GetString(0)] = new(reader.GetInt64(1), reader.GetInt64(2));
        return stamps;
    }

    public SearchCounts Counts(string? model)
    {
        using var db = Open();
        return new(Scalar(db, "SELECT COUNT(*) FROM files"), Scalar(db, "SELECT COUNT(*) FROM chunks"),
            model is null ? 0 : Scalar(db, "SELECT COUNT(*) FROM embeddings WHERE model=$m", ("$m", model)));
    }

    // Replaces one file's passages in a single transaction, so a search never
    // sees half of an update.
    public void ReplaceFile(IndexedFile file)
    {
        lock (_writeGate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            DeleteFiles(db, tx, "path", file.Path);
            long fileId;
            using (var put = db.CreateCommand())
            {
                put.Transaction = tx;
                put.CommandText = """
                    INSERT INTO files(path,source,kind,title,mtime,size,indexed_at) VALUES($p,$s,$k,$t,$m,$z,$at);
                    SELECT last_insert_rowid();
                    """;
                put.Parameters.AddWithValue("$p", file.Path);
                put.Parameters.AddWithValue("$s", file.Source);
                put.Parameters.AddWithValue("$k", file.Kind);
                put.Parameters.AddWithValue("$t", file.Title);
                put.Parameters.AddWithValue("$m", file.Mtime);
                put.Parameters.AddWithValue("$z", file.Size);
                put.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
                fileId = Convert.ToInt64(put.ExecuteScalar());
            }
            using var chunk = db.CreateCommand();
            chunk.Transaction = tx;
            chunk.CommandText = """
                INSERT INTO chunks(file_id,ordinal,start,length,first_line,last_line) VALUES($f,$o,$s,$l,$a,$b);
                INSERT INTO chunks_fts(rowid,title,text) VALUES(last_insert_rowid(),$t,$x);
                """;
            var p = new[] { "$f", "$o", "$s", "$l", "$a", "$b", "$t", "$x" }.Select(n => chunk.Parameters.Add(n, SqliteType.Text)).ToArray();
            foreach (var c in file.Chunks)
            {
                p[0].Value = fileId; p[1].Value = c.Ordinal; p[2].Value = c.Start; p[3].Value = c.Length;
                p[4].Value = c.FirstLine; p[5].Value = c.LastLine; p[6].Value = file.Title; p[7].Value = c.Text;
                chunk.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // Deletes files by exact path or by source, child rows first, so every
    // passage leaves FTS and the embeddings table too. Returns files removed.
    private static int DeleteFiles(SqliteConnection db, SqliteTransaction tx, string column, string value)
    {
        if (column is not ("path" or "source")) throw new ArgumentOutOfRangeException(nameof(column));
        var files = $"SELECT id FROM files WHERE {column}=$v";
        var chunks = $"SELECT id FROM chunks WHERE file_id IN ({files})";
        var removed = 0;
        foreach (var sql in new[]
                 {
                     $"DELETE FROM embeddings WHERE chunk_id IN ({chunks})",
                     $"DELETE FROM chunks_fts WHERE rowid IN ({chunks})",
                     $"DELETE FROM chunks WHERE file_id IN ({files})",
                     $"DELETE FROM files WHERE {column}=$v"
                 })
        {
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$v", value);
            removed = cmd.ExecuteNonQuery();
        }
        return removed;
    }

    public int RemoveFiles(IEnumerable<string> paths)
    {
        var removed = 0;
        lock (_writeGate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            foreach (var path in paths) removed += DeleteFiles(db, tx, "path", path);
            tx.Commit();
            if (removed > 0) Compact(db);
        }
        return removed;
    }

    public int RemoveSource(string source)
    {
        lock (_writeGate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var removed = DeleteFiles(db, tx, "source", source);
            tx.Commit();
            Compact(db);
            return removed;
        }
    }

    public int RemoveOutsideSources(IReadOnlyList<string> sources)
    {
        lock (_writeGate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var removed = 0;
            using (var list = db.CreateCommand())
            {
                list.Transaction = tx;
                list.CommandText = "SELECT DISTINCT source FROM files";
                var stale = new List<string>();
                using (var reader = list.ExecuteReader())
                    while (reader.Read()) if (!sources.Contains(reader.GetString(0))) stale.Add(reader.GetString(0));
                foreach (var source in stale) removed += DeleteFiles(db, tx, "source", source);
            }
            tx.Commit();
            if (removed > 0) Compact(db);
            return removed;
        }
    }

    // FTS5 marks deleted rows instead of rewriting its segments; 'optimize'
    // merges them so removed words leave the index, and secure_delete zeroes
    // the pages that frees.
    public void Optimize()
    {
        lock (_writeGate)
        {
            using var db = Open();
            Compact(db);
        }
    }

    private static void Compact(SqliteConnection db) =>
        Exec(db, "INSERT INTO chunks_fts(chunks_fts) VALUES('optimize');");

    public void ForceReindex()
    {
        lock (_writeGate)
        {
            using var db = Open();
            Exec(db, "UPDATE files SET mtime=-1");
        }
    }

    public IReadOnlyList<(long Id, string Text)> ChunksMissingEmbeddings(string model, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, t.text FROM chunks c JOIN chunks_fts t ON t.rowid=c.id
            WHERE NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.chunk_id=c.id AND e.model=$m)
            ORDER BY c.id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$limit", limit);
        var items = new List<(long, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) items.Add((reader.GetInt64(0), reader.GetString(1)));
        return items;
    }

    public void StoreEmbeddings(string model, IReadOnlyList<(long Id, float[] Vector)> vectors)
    {
        lock (_writeGate)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using (var stale = db.CreateCommand())
            {
                stale.Transaction = tx;
                stale.CommandText = "DELETE FROM embeddings WHERE model<>$m";
                stale.Parameters.AddWithValue("$m", model);
                stale.ExecuteNonQuery();
            }
            using var put = db.CreateCommand();
            put.Transaction = tx;
            // The passage may have been replaced while it was being embedded.
            put.CommandText = """
                INSERT OR REPLACE INTO embeddings(chunk_id,model,vector)
                SELECT $id,$m,$v WHERE EXISTS (SELECT 1 FROM chunks WHERE id=$id)
                """;
            var id = put.Parameters.Add("$id", SqliteType.Integer);
            put.Parameters.AddWithValue("$m", model);
            var vector = put.Parameters.Add("$v", SqliteType.Blob);
            foreach (var (chunkId, v) in vectors)
            {
                id.Value = chunkId;
                vector.Value = Vectors.Pack(v);
                put.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // Drops every table and rebuilds the file, then checks nothing is left.
    public bool Erase()
    {
        lock (_writeGate)
        {
            using var db = Open();
            Exec(db, "DROP TABLE IF EXISTS chunks_fts; DROP TABLE IF EXISTS embeddings; DROP TABLE IF EXISTS chunks; DROP TABLE IF EXISTS files;");
            Exec(db, "VACUUM;");
            CreateSchema(db);
            return Scalar(db, "SELECT COUNT(*) FROM files") == 0 && Scalar(db, "SELECT COUNT(*) FROM chunks") == 0 &&
                   Scalar(db, "SELECT COUNT(*) FROM chunks_fts") == 0 && Scalar(db, "SELECT COUNT(*) FROM embeddings") == 0 &&
                   Scalar(db, "PRAGMA freelist_count") == 0;
        }
    }

    private static string KindFilter(SqliteCommand cmd, IReadOnlyList<string> kinds)
    {
        if (kinds.Count == 0) return "";
        var names = kinds.Select((k, i) => { cmd.Parameters.AddWithValue($"$k{i}", k); return $"$k{i}"; });
        return $" AND f.kind IN ({string.Join(',', names)})";
    }

    public IReadOnlyList<(long Id, double Rank, string Snippet)> TextMatches(string ftsQuery, IReadOnlyList<string> kinds, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT chunks_fts.rowid, bm25(chunks_fts, 3.0, 1.0) AS rank,
                   snippet(chunks_fts, 1, '«', '»', '…', 24)
            FROM chunks_fts JOIN chunks c ON c.id=chunks_fts.rowid JOIN files f ON f.id=c.file_id
            WHERE chunks_fts MATCH $q{KindFilter(cmd, kinds)}
            ORDER BY rank LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$q", ftsQuery);
        cmd.Parameters.AddWithValue("$limit", limit);
        var items = new List<(long, double, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) items.Add((reader.GetInt64(0), reader.GetDouble(1), reader.GetString(2)));
        return items;
    }

    // Brute-force cosine over the stored vectors, streamed so memory stays at
    // one row plus the top-k heap. Bounded by NEXUS_SEARCH_MAX_CHUNKS.
    public IReadOnlyList<(long Id, float Similarity)> VectorMatches(string model, float[] query, IReadOnlyList<string> kinds, int limit, float min)
    {
        var unit = Vectors.Normalise(query);
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT e.chunk_id, e.vector FROM embeddings e JOIN chunks c ON c.id=e.chunk_id JOIN files f ON f.id=c.file_id
            WHERE e.model=$m{KindFilter(cmd, kinds)}
            """;
        cmd.Parameters.AddWithValue("$m", model);
        var top = new PriorityQueue<(long, float), float>();
        var buffer = new byte[unit.Length * 2];
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetBytes(1, 0, null, 0, 0) != buffer.Length) continue;
            reader.GetBytes(1, 0, buffer, 0, buffer.Length);
            var similarity = Vectors.Cosine(unit, buffer);
            if (float.IsNaN(similarity) || similarity < min) continue;
            top.Enqueue((reader.GetInt64(0), similarity), similarity);
            if (top.Count > limit) top.Dequeue();
        }
        var items = new List<(long, float)>();
        while (top.TryDequeue(out var item, out _)) items.Add(item);
        items.Reverse();
        return items;
    }

    public (string Path, string Title, string Kind, long Mtime, int FirstLine, int LastLine, string Text)? Chunk(long id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT f.path, f.title, f.kind, f.mtime, c.first_line, c.last_line, t.text
            FROM chunks c JOIN files f ON f.id=c.file_id JOIN chunks_fts t ON t.rowid=c.id WHERE c.id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6));
    }

    // Which of the person's own words this passage contains, decided by FTS
    // itself (same stemming as the search), never by a model.
    public IReadOnlyList<string> MatchedTerms(long id, IReadOnlyList<string> terms)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks_fts WHERE chunks_fts MATCH $q AND rowid=$id";
        var q = cmd.Parameters.Add("$q", SqliteType.Text);
        cmd.Parameters.AddWithValue("$id", id);
        var matched = new List<string>();
        foreach (var term in terms)
        {
            q.Value = SearchQuery.Quote(term);
            if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) matched.Add(term);
        }
        return matched;
    }
}

public sealed record SearchRequest(string? Query, int? Limit, string[]? Kinds);
public sealed record SearchHit(string Path, string Title, string Kind, string Snippet, DateTimeOffset Modified,
    int FirstLine, int LastLine, double Score, string Why, IReadOnlyList<string> MatchedTerms, float? Similarity);
public sealed record SearchResponse(string Query, string Mode, bool MeaningSearch, string? Notice, IReadOnlyList<SearchHit> Results);

public sealed partial class SearchQuery(SearchIndexStore store, IEmbedder embedder, SearchSettings settings)
{
    // Reciprocal-rank fusion constant: blends word and meaning rankings
    // without having to calibrate bm25 against cosine.
    private const double Fusion = 60;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Word();

    public static IReadOnlyList<string> Terms(string query) =>
        Word().Matches(query.ToLowerInvariant()).Select(m => m.Value).Distinct().Take(SearchSettings.MaxQueryTerms).ToArray();

    public static string Quote(string term) => "\"" + term.Replace("\"", "\"\"") + "\"";

    public static string? Validate(SearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Query) || request.Query.Length > SearchSettings.MaxQueryLength)
            return $"Query must be between 1 and {SearchSettings.MaxQueryLength} characters.";
        if (request.Limit is < 1 or > SearchSettings.MaxLimit)
            return $"Limit must be between 1 and {SearchSettings.MaxLimit}.";
        if (request.Kinds is { } kinds && (kinds.Length > SearchKinds.All.Length || kinds.Any(k => !SearchKinds.All.Contains(k))))
            return "Kinds must be some of: " + string.Join(", ", SearchKinds.All) + ".";
        return null;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken ct)
    {
        var query = request.Query!.Trim();
        var limit = request.Limit ?? 10;
        var kinds = request.Kinds?.Distinct().ToArray() ?? [];
        var terms = Terms(query);

        var text = terms.Count == 0 ? [] :
            store.TextMatches(string.Join(" OR ", terms.Select(Quote)), kinds, SearchSettings.CandidatePool);

        IReadOnlyList<(long Id, float Similarity)> meaning = [];
        string? notice = null;
        var meaningUsed = false;
        if (embedder.Model is null)
            notice = "Meaning search is switched off; results match your words only.";
        else if (store.Counts(embedder.Model).EmbeddedChunks == 0)
            notice = "Meaning search is not ready yet (no passages have embeddings); results match your words only.";
        else
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                var vector = await embedder.EmbedQueryAsync(query, timeout.Token);
                meaning = store.VectorMatches(embedder.Model, vector, kinds, SearchSettings.CandidatePool, settings.MinSimilarity);
                meaningUsed = true;
            }
            catch (EmbeddingUnavailableException ex) { notice = ex.Message + " Results match your words only."; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                notice = "Meaning search timed out. Results match your words only.";
            }
        }

        var scores = new Dictionary<long, (double Score, string? Snippet, float? Similarity)>();
        for (var i = 0; i < text.Count; i++)
            scores[text[i].Id] = (1 / (Fusion + i + 1), text[i].Snippet, null);
        for (var i = 0; i < meaning.Count; i++)
        {
            var (id, similarity) = meaning[i];
            var current = scores.GetValueOrDefault(id);
            scores[id] = (current.Score + 1 / (Fusion + i + 1), current.Snippet, similarity);
        }

        // One result per file: its best passage.
        var hits = new List<SearchHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, (score, snippet, similarity)) in scores.OrderByDescending(x => x.Value.Score))
        {
            if (hits.Count >= limit) break;
            if (store.Chunk(id) is not { } chunk || !seen.Add(chunk.Path)) continue;
            var matched = snippet is null ? [] : store.MatchedTerms(id, terms);
            hits.Add(new SearchHit(chunk.Path, chunk.Title, chunk.Kind, snippet ?? Excerpt(chunk.Text),
                DateTimeOffset.FromUnixTimeMilliseconds(chunk.Mtime), chunk.FirstLine, chunk.LastLine,
                Math.Round(score, 6), Why(matched, similarity, chunk.FirstLine, chunk.LastLine), matched,
                similarity is null ? null : MathF.Round(similarity.Value, 3)));
        }
        return new SearchResponse(query, meaningUsed ? "hybrid" : "text", meaningUsed, notice, hits);
    }

    private static string Excerpt(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    // Built only from what the index actually matched.
    public static string Why(IReadOnlyList<string> matched, float? similarity, int firstLine, int lastLine)
    {
        var lines = firstLine == lastLine ? $"line {firstLine}" : $"lines {firstLine}–{lastLine}";
        var parts = new List<string>();
        if (matched.Count > 0)
            parts.Add("contains " + string.Join(", ", matched.Select(t => $"\"{t}\"")));
        if (similarity is { } s)
            parts.Add($"close in meaning to your search (similarity {s:0.00})");
        if (parts.Count == 0) parts.Add("matched your search");
        var sentence = string.Join(" and ", parts);
        return char.ToUpperInvariant(sentence[0]) + sentence[1..] + $", in {lines}.";
    }
}
