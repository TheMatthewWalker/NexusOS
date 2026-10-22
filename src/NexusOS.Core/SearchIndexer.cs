using System.Collections.Concurrent;
using System.Threading.Channels;

public sealed record SearchIndexIssue(string Path, string Reason, DateTimeOffset At);
public sealed record SearchScanReport(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Forced, int Indexed,
    int Updated, int Unchanged, int Removed, int Embedded, bool Complete,
    IReadOnlyDictionary<string, int> Skipped, IReadOnlyList<SearchIndexIssue> SkippedExamples);
public sealed record SearchSourceStatus(string Path, bool Watching);
public sealed record SearchEmbeddingStatus(string? Model, bool? Available, string? Reason);
public sealed record SearchStatus(IReadOnlyList<SearchSourceStatus> Sources, long Files, long Chunks, long EmbeddedChunks,
    bool Running, SearchEmbeddingStatus Embeddings, SearchScanReport? LastRun, IReadOnlyList<SearchIndexIssue> Errors);

// Keeps the index in step with the chosen folders. A scan compares each
// file's mtime and size with the index, so it is the single path for adds,
// changes and deletes; FileSystemWatcher (inotify) only asks for an early
// scan, and the timer covers anything a watcher misses or cannot watch.
public sealed class SearchIndexer(
    SearchIndexStore store,
    SearchSourceStore sources,
    TextExtractorRegistry extractors,
    IEmbedder embedder,
    SearchSettings settings,
    ILogger<SearchIndexer> logger) : BackgroundService
{
    private const int MaxDepth = 32;
    private const int MaxIssues = 20;
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly Channel<bool> _trigger = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Dictionary<string, FileSystemWatcher?> _watchers = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<SearchIndexIssue> _errors = new();
    private CancellationTokenSource? _scanCts;
    private volatile bool _force;
    private volatile bool _running;
    private volatile SearchScanReport? _lastRun;
    private volatile SearchEmbeddingStatus _embedding = new(embedder.Model, null,
        embedder.Model is null ? "Meaning search is switched off (NEXUS_EMBED_MODEL=off)." : "Not checked yet.");

    public void RequestScan(bool force = false)
    {
        if (force) _force = true;
        _trigger.Writer.TryWrite(true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!first)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(settings.ScanInterval);
                try
                {
                    await _trigger.Reader.WaitToReadAsync(wait.Token);
                    await Task.Delay(Debounce, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* Timer scan. */ }
            }
            first = false;
            _trigger.Reader.TryRead(out _);
            try { await RunScanAsync(stoppingToken); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { /* Erase or removal took over. */ }
            // An exception escaping a BackgroundService stops Core; search must never do that.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never log paths or content; the type is enough to diagnose.
                logger.LogWarning("Search scan failed ({Type}).", ex.GetType().Name);
                AddError(_errors, new("", "Scan failed: " + ex.GetType().Name, DateTimeOffset.UtcNow));
            }
        }
    }

    // Runs work that must not overlap a scan (erase, removing a folder). A
    // running scan is cancelled first, so the person never waits for it and
    // it cannot re-add what was just removed.
    public async Task<T> ExclusiveAsync<T>(Func<T> action, CancellationToken ct)
    {
        try { _scanCts?.Cancel(); } catch (ObjectDisposedException) { }
        await _scanLock.WaitAsync(ct);
        try { return action(); }
        finally { _scanLock.Release(); }
    }

    public SearchStatus Status()
    {
        var counts = store.Counts(embedder.Model);
        SearchSourceStatus[] list;
        lock (_watchers)
            list = sources.List().Select(s => new SearchSourceStatus(s, _watchers.GetValueOrDefault(s) is not null)).ToArray();
        return new SearchStatus(list, counts.Files, counts.Chunks, counts.EmbeddedChunks, _running, _embedding,
            _lastRun, _errors.ToArray());
    }

    public void ResetStatus()
    {
        _errors.Clear();
        _lastRun = null;
    }

    private sealed record Candidate(string Path, string Source, ITextExtractor Extractor, long Mtime, long Size);

    private sealed class ScanTally
    {
        public int Indexed, Updated, Unchanged, Removed, Embedded;
        public long ChunkBudget, FileBudget;
        public bool LimitReached;
        public readonly ConcurrentDictionary<string, int> Skipped = new();
        public readonly ConcurrentQueue<SearchIndexIssue> Examples = new();
        public readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        public readonly HashSet<string> Incomplete = new(StringComparer.Ordinal);
        public int Candidates;
    }

    public async Task<SearchScanReport> RunScanAsync(CancellationToken ct)
    {
        await _scanLock.WaitAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _scanCts = cts;
        _running = true;
        try
        {
            var started = DateTimeOffset.UtcNow;
            var force = _force;
            _force = false;
            var chosen = sources.List();
            SyncWatchers(chosen);
            var tally = new ScanTally();
            tally.Removed += store.RemoveOutsideSources(chosen);
            var stamps = store.LoadStamps();
            var counts = store.Counts(null);
            tally.ChunkBudget = settings.MaxChunks - counts.Chunks;
            tally.FileBudget = settings.MaxFiles - counts.Files;

            await Parallel.ForEachAsync(Walk(chosen, tally, cts.Token),
                new ParallelOptions { MaxDegreeOfParallelism = settings.Workers, CancellationToken = cts.Token },
                (candidate, _) => { Process(candidate, stamps, force, tally); return ValueTask.CompletedTask; });

            // Deleted files: indexed before, not seen now, under a folder that was walked completely.
            var gone = stamps.Keys.Where(p => !tally.Seen.Contains(p) &&
                !tally.Incomplete.Any(s => p.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.Ordinal))).ToArray();
            tally.Removed += store.RemoveFiles(gone);
            if (tally.Updated > 0) store.Optimize();
            await EmbedMissingAsync(tally, cts.Token);

            var report = new SearchScanReport(started, DateTimeOffset.UtcNow, force, tally.Indexed, tally.Updated,
                tally.Unchanged, tally.Removed, tally.Embedded, tally.Incomplete.Count == 0,
                new Dictionary<string, int>(tally.Skipped), tally.Examples.ToArray());
            _lastRun = report;
            return report;
        }
        finally
        {
            _running = false;
            _scanCts = null;
            _scanLock.Release();
        }
    }

    // Walks the chosen folders lazily, so memory holds one directory listing
    // and the set of paths seen, never the whole tree. Never follows a
    // symbolic link, never enters a hidden or credential folder, skips hidden
    // files, and refuses a chosen folder that no longer passes validation.
    private IEnumerable<Candidate> Walk(IReadOnlyList<string> chosen, ScanTally tally, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        foreach (var source in chosen)
        {
            var (_, refusal) = sources.Validate(source);
            if (refusal is not null)
            {
                // The folder vanished or became a link: drop what it held.
                AddError(_errors, new(source, refusal, DateTimeOffset.UtcNow));
                continue;
            }
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((source, 0));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, depth) = stack.Pop();
                FileSystemInfo[] entries;
                try { entries = new DirectoryInfo(dir).GetFileSystemInfos("*", options); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    tally.Incomplete.Add(dir);
                    AddError(_errors, new(dir, "Folder could not be read.", DateTimeOffset.UtcNow));
                    continue;
                }
                foreach (var entry in entries)
                {
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if (entry is DirectoryInfo sub)
                    {
                        if (SearchSourceStore.RefusedFolderName(sub.Name) is null && sub.FullName != sources.StateRoot)
                        {
                            if (depth < MaxDepth) stack.Push((sub.FullName, depth + 1));
                            else tally.Incomplete.Add(sub.FullName);
                        }
                        continue;
                    }
                    if (entry is not FileInfo file || file.Name.StartsWith('.') || !tally.Seen.Add(file.FullName)) continue;
                    var extractor = extractors.For(file.FullName);
                    if (extractor is null)
                    {
                        if (TextExtractorRegistry.UnsupportedReason(file.FullName) is { } reason) Skip(tally, file.FullName, reason);
                        continue;
                    }
                    if (++tally.Candidates > settings.MaxFiles)
                    {
                        if (!tally.LimitReached)
                            AddError(_errors, new(source, $"Stopped at the {settings.MaxFiles}-file limit (NEXUS_SEARCH_MAX_FILES).", DateTimeOffset.UtcNow));
                        tally.LimitReached = true;
                        // Nothing unseen may be treated as deleted after a partial walk.
                        foreach (var s in chosen) tally.Incomplete.Add(s);
                        yield break;
                    }
                    yield return new Candidate(file.FullName, source, extractor,
                        new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeMilliseconds(), file.Length);
                }
            }
        }
    }

    private void Process(Candidate file, Dictionary<string, FileStamp> stamps, bool force, ScanTally tally)
    {
        var known = stamps.TryGetValue(file.Path, out var stamp);
        if (!force && known && stamp!.Mtime == file.Mtime && stamp.Size == file.Size)
        {
            Interlocked.Increment(ref tally.Unchanged);
            return;
        }
        string? skipped = null;
        try
        {
            if (file.Size > settings.MaxFileBytes) skipped = "too large";
            else if (Interlocked.Read(ref tally.ChunkBudget) <= 0 ||
                     (!known && Interlocked.Decrement(ref tally.FileBudget) < 0)) skipped = "index full";
            else
            {
                using var stream = SearchFileReader.OpenRegularFile(file.Path);
                if (stream is null) skipped = "not a regular file";
                else
                {
                    // ACTIVITY JOURNAL HOOK (feature 8): when the journal lands,
                    // record "indexed <path>" here as a file Nexus read. Not merged yet.
                    var extracted = file.Extractor.Extract(stream, settings.MaxFileBytes);
                    if (extracted.Text is null) skipped = extracted.Skipped ?? "unreadable";
                    else
                    {
                        var chunks = TextChunker.Split(extracted.Text, SearchSettings.MaxChunksPerFile);
                        store.ReplaceFile(new IndexedFile(file.Path, file.Source, file.Extractor.Kind,
                            Path.GetFileName(file.Path), file.Mtime, file.Size, chunks));
                        Interlocked.Add(ref tally.ChunkBudget, -chunks.Count);
                        Interlocked.Increment(ref known ? ref tally.Updated : ref tally.Indexed);
                        if (extracted.Truncated) Skip(tally, file.Path, "partly indexed (size limit)");
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            skipped = "could not be read";
            AddError(_errors, new(file.Path, "File could not be read.", DateTimeOffset.UtcNow));
        }
        Skip(tally, file.Path, skipped);
        // A file that can no longer be indexed must not keep its old passages.
        if (known && store.RemoveFiles([file.Path]) > 0) Interlocked.Increment(ref tally.Removed);
    }

    private async Task EmbedMissingAsync(ScanTally tally, CancellationToken ct)
    {
        if (embedder.Model is not { } model) return;
        var budget = settings.EmbedPerScan;
        while (budget > 0)
        {
            var batch = store.ChunksMissingEmbeddings(model, Math.Min(OllamaEmbedder.MaxBatch, budget));
            if (batch.Count == 0) break;
            float[][] vectors;
            try { vectors = await embedder.EmbedDocumentsAsync(batch.Select(x => x.Text).ToArray(), ct); }
            catch (EmbeddingUnavailableException ex)
            {
                // Search keeps working on words alone; the next scan retries.
                _embedding = new(model, false, ex.Message);
                return;
            }
            store.StoreEmbeddings(model, batch.Select((x, i) => (x.Id, vectors[i])).ToArray());
            budget -= batch.Count;
            tally.Embedded += batch.Count;
            _embedding = new(model, true, null);
        }
    }

    private static void Skip(ScanTally tally, string path, string reason)
    {
        tally.Skipped.AddOrUpdate(reason, 1, (_, n) => n + 1);
        if (tally.Examples.Count < MaxIssues) tally.Examples.Enqueue(new(path, reason, DateTimeOffset.UtcNow));
    }

    private static void AddError(ConcurrentQueue<SearchIndexIssue> errors, SearchIndexIssue issue)
    {
        errors.Enqueue(issue);
        while (errors.Count > MaxIssues) errors.TryDequeue(out _);
    }

    public void SyncWatchers(IReadOnlyList<string> chosen)
    {
        lock (_watchers)
        {
            foreach (var stale in _watchers.Keys.Except(chosen).ToArray())
            {
                _watchers[stale]?.Dispose();
                _watchers.Remove(stale);
            }
            foreach (var source in chosen.Where(s => !_watchers.ContainsKey(s)))
                _watchers[source] = TryWatch(source);
        }
    }

    private FileSystemWatcher? TryWatch(string source)
    {
        try
        {
            var watcher = new FileSystemWatcher(source)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, e) => OnChange(source, e.FullPath);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, e) => OnChange(source, e.FullPath);
            // inotify limits or a removed folder: the timer still covers it.
            watcher.Error += (_, _) => { lock (_watchers) if (_watchers.GetValueOrDefault(source) == watcher) _watchers[source] = null; watcher.Dispose(); };
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OnChange(string source, string path)
    {
        // Ignore churn inside hidden folders (.git, caches), which are never indexed.
        var relative = Path.GetRelativePath(source, path);
        if (relative.Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.'))) return;
        RequestScan();
    }

    public override void Dispose()
    {
        lock (_watchers)
        {
            foreach (var watcher in _watchers.Values) watcher?.Dispose();
            _watchers.Clear();
        }
        base.Dispose();
    }
}
