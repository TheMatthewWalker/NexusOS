using System.Text.Json;

// Folders the person chose for private search (feature 8). Nothing is indexed
// until a folder is chosen here. The list is configuration, kept in Core's
// settings table; the index itself lives in search.sqlite3.
public sealed class SearchSourceStore
{
    public const int MaxSources = 32;
    public const int MaxPathLength = 4096;
    private const string SettingsKey = "search_sources";

    // Folders that hold credentials or browser profiles. Hidden folders are
    // refused as a whole; these names are refused even when not hidden.
    private static readonly HashSet<string> CredentialFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh", ".gnupg", "keyrings", ".password-store", ".pki", ".aws", ".kube", ".docker",
        ".mozilla", ".thunderbird", "google-chrome", "chromium", "BraveSoftware", "vivaldi",
        "microsoft-edge", "Cookies"
    };

    private readonly SqliteStateDatabase _database;
    private readonly object _gate = new();
    public string Home { get; }
    public string StateRoot { get; }

    public SearchSourceStore(SqliteStateDatabase database, NexusStatePaths paths, IConfiguration config)
    {
        _database = database;
        // NEXUS_SEARCH_HOME exists for tests; in production the boundary is the user's home.
        var home = config["NEXUS_SEARCH_HOME"];
        Home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(string.IsNullOrWhiteSpace(home)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : home));
        StateRoot = Path.TrimEndingDirectorySeparator(paths.Root);
    }

    public IReadOnlyList<string> List()
    {
        lock (_gate)
        {
            using var db = _database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", SettingsKey);
            if (cmd.ExecuteScalar() is not string raw) return [];
            try { return JsonSerializer.Deserialize<string[]>(raw) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    // Validates every path first; nothing is added unless all of them pass.
    public (IReadOnlyList<string> Sources, IReadOnlyList<SearchSourceRefusal> Refused) Add(IEnumerable<string?> paths)
    {
        var accepted = new List<string>();
        var refused = new List<SearchSourceRefusal>();
        foreach (var path in paths)
        {
            var (full, reason) = Validate(path);
            if (reason is not null) refused.Add(new(path ?? "", reason));
            else accepted.Add(full!);
        }
        lock (_gate)
        {
            var current = List();
            if (refused.Count > 0 || accepted.Count == 0) return (current, refused);
            var next = current.Concat(accepted).Distinct(StringComparer.Ordinal).ToArray();
            if (next.Length > MaxSources)
                return (current, [new("", $"At most {MaxSources} folders can be searched.")]);
            Save(next);
            return (next, refused);
        }
    }

    public bool Remove(string path, out IReadOnlyList<string> remaining)
    {
        var target = Normalise(path);
        lock (_gate)
        {
            var current = List();
            remaining = current;
            if (target is null || !current.Contains(target, StringComparer.Ordinal)) return false;
            remaining = current.Where(x => x != target).ToArray();
            Save(remaining);
            return true;
        }
    }

    public int Clear()
    {
        lock (_gate)
        {
            var count = List().Count;
            Save([]);
            return count;
        }
    }

    private void Save(IReadOnlyList<string> sources)
    {
        using var db = _database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key,value) VALUES($key,$value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", SettingsKey);
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(sources));
        cmd.ExecuteNonQuery();
    }

    private static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || path.Contains('\0') ||
            !Path.IsPathFullyQualified(path)) return null;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public (string? FullPath, string? Reason) Validate(string? path)
    {
        var full = Normalise(path);
        if (full is null) return (null, "Use an absolute folder path.");
        if (full != Home && !full.StartsWith(Home + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return (null, "Only folders inside your home folder can be searched.");
        if (full == StateRoot || full.StartsWith(StateRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return (null, "Nexus's own state folder cannot be searched.");
        var below = full == Home ? [] : full[(Home.Length + 1)..].Split(Path.DirectorySeparatorChar);
        foreach (var part in below)
        {
            var refusal = RefusedFolderName(part);
            if (refusal is not null) return (null, refusal);
        }
        if (!Directory.Exists(full)) return (null, "That folder does not exist.");
        // A symlink anywhere below home could point the index at a refused folder.
        var walk = Home;
        foreach (var part in below)
        {
            walk = Path.Combine(walk, part);
            if (new DirectoryInfo(walk).LinkTarget is not null)
                return (null, "Folders reached through a symbolic link cannot be searched.");
        }
        return (full, null);
    }

    // Shared with the indexer, which applies the same rule to every folder it walks.
    public static string? RefusedFolderName(string name)
    {
        if (CredentialFolders.Contains(name)) return "Credential and browser-profile folders are never searched.";
        if (name.StartsWith('.')) return "Hidden folders are never searched.";
        return null;
    }
}

public sealed record SearchSourceRefusal(string Path, string Reason);
