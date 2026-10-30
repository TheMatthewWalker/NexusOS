using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record WorkspaceApp(string AppId, string? DesktopId, string Title);
public sealed record WorkspaceRecord(string Name, int Workspace, IReadOnlyList<WorkspaceApp> Apps, DateTimeOffset SavedAt);
public sealed record WorkspaceAppRequest(string? AppId, string? DesktopId, string? Title);
public sealed record WorkspaceSaveRequest(string? Name, int? Workspace, WorkspaceAppRequest?[]? Apps);

// Workspaces that remember (feature 7): a named workspace and the apps that
// were open on it, so the shell can bring them back later. Core only keeps
// the record (in its settings table, like the search folders); it never
// launches anything. The shell relaunches the apps from their desktop
// entries when the person chooses Restore. Names and ids are checked here so
// a record can always be used safely in a Hyprland dispatch.
public sealed partial class WorkspaceStore(SqliteStateDatabase database, TimeProvider time)
{
    public const int MaxWorkspaces = 30;
    public const int MaxApps = 24;
    public const int MaxTitleLength = 200;
    private const string SettingsKey = "workspaces";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} '._&()-]{0,39}$")]
    private static partial Regex NamePattern();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex IdPattern();

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public IReadOnlyList<WorkspaceRecord> List()
    {
        lock (_gate)
        {
            using var db = database.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$key";
            cmd.Parameters.AddWithValue("$key", SettingsKey);
            if (cmd.ExecuteScalar() is not string raw) return [];
            try { return JsonSerializer.Deserialize<WorkspaceRecord[]>(raw, Json) ?? []; }
            catch (JsonException) { return []; }
        }
    }

    public WorkspaceRecord? Find(string name) =>
        List().FirstOrDefault(w => string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    // Returns the problem in plain words, or null when the request is usable.
    public static string? Validate(WorkspaceSaveRequest request)
    {
        var name = request.Name?.Trim();
        if (!IsValidName(name))
            return "Give the workspace a name of up to 40 letters, numbers and spaces.";
        if (request.Workspace is not (>= 1 and <= 99))
            return "Workspace must be a number from 1 to 99.";
        if (request.Apps is not { } apps || apps.Length > MaxApps)
            return $"Give between 0 and {MaxApps} apps.";
        foreach (var app in apps)
        {
            if (app is null || !IsValidId(app.AppId))
                return "Each app needs its window class (letters, numbers, dots, dashes).";
            if (app.DesktopId is not null && !IsValidId(app.DesktopId))
                return "A desktop entry id may only hold letters, numbers, dots and dashes.";
        }
        return null;
    }

    public (WorkspaceRecord Saved, bool Replaced)? Save(WorkspaceSaveRequest request)
    {
        if (Validate(request) is not null) return null;
        var record = new WorkspaceRecord(request.Name!.Trim(), request.Workspace!.Value,
            request.Apps!.Select(a => new WorkspaceApp(a!.AppId!, a.DesktopId, Clean(a.Title))).ToArray(),
            time.GetUtcNow());
        lock (_gate)
        {
            var current = List();
            var replaced = current.Any(w => string.Equals(w.Name, record.Name, StringComparison.OrdinalIgnoreCase));
            if (!replaced && current.Count >= MaxWorkspaces) return null;
            Write(current.Where(w => !string.Equals(w.Name, record.Name, StringComparison.OrdinalIgnoreCase))
                .Append(record).OrderBy(w => w.Workspace).ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToArray());
            return (record, replaced);
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            var current = List();
            var remaining = current.Where(w => !string.Equals(w.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (remaining.Length == current.Count) return false;
            Write(remaining);
            return true;
        }
    }

    // Window titles come from apps: one line, no control characters, bounded.
    private static string Clean(string? title)
    {
        var text = new string((title ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length <= MaxTitleLength ? text : text[..(MaxTitleLength - 1)] + "…";
    }

    private void Write(IReadOnlyList<WorkspaceRecord> records)
    {
        using var db = database.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key,value) VALUES($key,$value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$key", SettingsKey);
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(records, Json));
        cmd.ExecuteNonQuery();
    }
}

public static class WorkspaceEndpoints
{
    public static IServiceCollection AddWorkspaces(this IServiceCollection services) => services.AddSingleton<WorkspaceStore>();

    // GET /workspaces, POST /workspaces (save or replace by name), DELETE /workspaces?name=
    public static void MapWorkspaces(this WebApplication app)
    {
        app.MapGet("/workspaces", (WorkspaceStore store) => Results.Ok(new { workspaces = store.List() }));
        app.MapPost("/workspaces", (WorkspaceSaveRequest request, WorkspaceStore store) =>
        {
            if (WorkspaceStore.Validate(request) is { } error) return Results.BadRequest(new { error });
            if (store.Save(request) is not { } saved)
                return Results.BadRequest(new { error = $"At most {WorkspaceStore.MaxWorkspaces} workspaces can be remembered. Forget one first." });
            return Results.Ok(new { workspace = saved.Saved, replaced = saved.Replaced, workspaces = store.List() });
        });
        app.MapDelete("/workspaces", (string? name, WorkspaceStore store) =>
        {
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Give the name of the workspace to forget." });
            return store.Remove(name)
                ? Results.Ok(new { forgotten = name.Trim(), workspaces = store.List() })
                : Results.NotFound(new { error = "No workspace has that name." });
        });
    }
}
