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
        Directory.CreateDirectory(Root);
        RestrictPermissions(Root, true);
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
    public string[] Names => ["local", "openai-compatible"];

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
                            name.Equals("openai-compatible", StringComparison.OrdinalIgnoreCase));

    private sealed record ProviderSelection(string Selected);
}

public sealed record ConversationEntry(DateTimeOffset Timestamp, string Provider, string Prompt, string Answer);

public sealed class ConversationStore(NexusStatePaths paths)
{
    private readonly string _file = paths.PathFor("history.jsonl");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task AppendAsync(ConversationEntry entry, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(entry) + "\n";
        await _gate.WaitAsync(ct);
        try
        {
            await File.AppendAllTextAsync(_file, line, ct);
            NexusStatePaths.RestrictPermissions(_file, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ConversationEntry>> RecentAsync(int limit, CancellationToken ct)
    {
        if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_file)) return [];
            var entries = new Queue<ConversationEntry>();
            foreach (var line in await File.ReadAllLinesAsync(_file, ct))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ConversationEntry>(line);
                    if (entry is null) continue;
                    entries.Enqueue(entry);
                    if (entries.Count > limit) entries.Dequeue();
                }
                catch (JsonException) { /* Ignore an incomplete tail record. */ }
            }
            return entries.Reverse().ToArray();
        }
        finally { _gate.Release(); }
    }
}
