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
            else if (File.ResolveLinkTarget(path, false) is not null)
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

public sealed class ProviderStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private string _selected = "local";
    public string[] Names => ["local", "llama-cpp", "openai-compatible"];

    public ProviderStore() : this(Path.Combine(Path.GetTempPath(), "nexusos-provider-no-persistence-" + Guid.NewGuid().ToString("N"))) { }

    public ProviderStore(NexusStatePaths paths) : this(paths.PathFor("provider.json")) { }

    public ProviderStore(string path)
    {
        _path = path;
        if (!File.Exists(path)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<ProviderSelection>(File.ReadAllText(path));
            if (saved is not null && IsValid(saved.Selected))
                _selected = saved.Selected.ToLowerInvariant();
        }
        catch (JsonException) { /* Corrupt preferences safely revert to local. */ }
    }

    public string Selected { get { lock (_gate) return _selected; } }

    public bool Select(string? name)
    {
        if (!IsValid(name)) return false;
        lock (_gate)
        {
            var next = name!.ToLowerInvariant();
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, ".provider-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(temp, JsonSerializer.Serialize(new ProviderSelection(next)));
                NexusStatePaths.RestrictPermissions(temp, false);
                File.Move(temp, _path, true);
                _selected = next;
                return true;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
    }

    private static bool IsValid(string? name) =>
        name is not null && (name.Equals("local", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("llama-cpp", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase));

    private sealed record ProviderSelection(string Selected);
}

public sealed record ConversationEntry(DateTimeOffset Timestamp, string Provider, string Prompt, string Answer);

public sealed class ConversationStore
{
    private readonly string _file;
    private readonly int _maxEntries;
    private readonly TimeSpan _maxAge;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversationStore(NexusStatePaths paths) : this(paths, new ConfigurationBuilder().Build()) { }

    public ConversationStore(NexusStatePaths paths, IConfiguration config)
    {
        _file = paths.PathFor("history.jsonl");
        _maxEntries = int.TryParse(config["NEXUS_HISTORY_LIMIT"], out var count)
            ? Math.Clamp(count, 0, 1000) : 200;
        var days = int.TryParse(config["NEXUS_HISTORY_MAX_DAYS"], out var parsed)
            ? Math.Clamp(parsed, 1, 365) : 30;
        _maxAge = TimeSpan.FromDays(days);
    }

    public async Task AppendAsync(ConversationEntry entry, CancellationToken ct)
    {
        if (_maxEntries == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            var lines = File.Exists(_file) ? await File.ReadAllLinesAsync(_file, ct) : [];
            var cutoff = DateTimeOffset.UtcNow - _maxAge;
            var kept = lines.Where(x => IsRecentEntry(x, cutoff))
                .TakeLast(_maxEntries - 1)
                .Append(JsonSerializer.Serialize(entry)).ToArray();
            await WriteAtomicAsync(kept, ct);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await WriteAtomicAsync([], ct); }
        finally { _gate.Release(); }
    }

    private async Task WriteAtomicAsync(string[] lines, CancellationToken ct)
    {
        var temp = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(temp, lines, ct);
            NexusStatePaths.RestrictPermissions(temp, false);
            File.Move(temp, _file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static bool IsRecentEntry(string line, DateTimeOffset cutoff)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<ConversationEntry>(line);
            return entry is not null && entry.Timestamp >= cutoff && entry.Timestamp <= DateTimeOffset.UtcNow.AddMinutes(5);
        }
        catch (JsonException) { return false; }
    }

    public async Task<IReadOnlyList<ConversationEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_file)) return [];
            var entries = new Queue<ConversationEntry>();
            var cutoff = DateTimeOffset.UtcNow - _maxAge;
            foreach (var line in await File.ReadAllLinesAsync(_file, ct))
            {
                if (!IsRecentEntry(line, cutoff)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<ConversationEntry>(line);
                    if (entry is null) continue;
                    entries.Enqueue(entry);
                    if (entries.Count > limit) entries.Dequeue();
                }
                catch (JsonException) { }
            }
            return entries.Reverse().ToArray();
        }
        finally { _gate.Release(); }
    }
}
