using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record WorkspaceApp(string AppId, string? DesktopId, string Title, IReadOnlyList<string>? Files = null);
public sealed record WorkspaceTab(string Url, string Title);
// The Nexus conversation on screen, by its place in history: the answer is
// read back from history on Restore, so erasing history erases it here too.
public sealed record WorkspaceConversation(DateTimeOffset At, string Prompt);
public sealed record WorkspaceRecord(string Name, int Workspace, IReadOnlyList<WorkspaceApp> Apps, DateTimeOffset SavedAt,
    IReadOnlyList<WorkspaceTab>? Tabs = null, WorkspaceConversation? Conversation = null, IReadOnlyList<string>? Notes = null);
public sealed record WorkspaceAppRequest(string? AppId, string? DesktopId, string? Title);
// Conversation: the question shown in Nexus when the workspace was remembered, if any.
public sealed record WorkspaceSaveRequest(string? Name, int? Workspace, WorkspaceAppRequest?[]? Apps, string? Conversation = null);
// What Remember found besides the apps, and what it could not keep.
public sealed record WorkspaceExtras(IReadOnlyList<IReadOnlyList<string>> Files, IReadOnlyList<WorkspaceTab> Tabs,
    WorkspaceConversation? Conversation, IReadOnlyList<string> Notes);

// Workspaces that remember (feature 7): a named workspace, the apps that
// were open on it, the files open in them, Firefox's tabs and the Nexus
// conversation on screen, so the shell can bring them back later. Core keeps
// the record (in its settings table, like the search folders) and finds the
// files and tabs (WorkspaceContents.cs); it never launches anything. The
// shell relaunches the apps from their desktop entries when the person
// chooses Restore. Names and ids are checked here so a record can always be
// used safely in a Hyprland dispatch. What could not be kept or found again
// is said in plain words (Notes), never quietly dropped.
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
        if (request.Conversation is { Length: > 32768 })
            return "The conversation is too long to remember.";
        return null;
    }

    public (WorkspaceRecord Saved, bool Replaced)? Save(WorkspaceSaveRequest request, WorkspaceExtras? extras = null)
    {
        if (Validate(request) is not null) return null;
        var record = new WorkspaceRecord(request.Name!.Trim(), request.Workspace!.Value,
            request.Apps!.Select((a, i) => new WorkspaceApp(a!.AppId!, a.DesktopId, Clean(a.Title),
                extras is not null && i < extras.Files.Count && extras.Files[i].Count > 0 ? extras.Files[i] : null)).ToArray(),
            time.GetUtcNow(),
            extras is { Tabs.Count: > 0 } ? extras.Tabs : null, extras?.Conversation,
            extras is { Notes.Count: > 0 } ? extras.Notes : null);
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
    public static IServiceCollection AddWorkspaces(this IServiceCollection services) =>
        services.AddSingleton<WorkspaceStore>().AddSingleton<WorkspaceContents>();

    // What else is on the workspace: files from window titles (made full
    // paths through recent files), Firefox's tabs (its session store, matched
    // to its windows by title) and the conversation in Nexus (by its history entry).
    public static async Task<WorkspaceExtras> CaptureAsync(WorkspaceSaveRequest request, WorkspaceContents contents,
        ConversationStore history, CancellationToken ct)
    {
        var apps = request.Apps ?? [];
        var notes = new List<string>();
        var files = new List<IReadOnlyList<string>>();
        foreach (var app in apps)
        {
            var found = new List<string>();
            if (app is { AppId: { } id } && !WorkspaceContents.IsFirefox(id) && WorkspaceContents.FileNameFromTitle(app.Title) is { } name)
            {
                if (contents.ResolveFile(name) is { } path) found.Add(path);
                else notes.Add($"“{name}” ({id}): could not find where the file is, so only the app reopens.");
            }
            files.Add(found);
        }
        var tabs = new List<WorkspaceTab>();
        var firefox = apps.Where(a => a?.AppId is { } id && WorkspaceContents.IsFirefox(id)).ToArray();
        if (firefox.Length > 0)
        {
            var session = contents.ReadFirefoxSession();
            if (session is null) notes.Add("Firefox's tabs could not be read (no saved session in your profile), so only Firefox reopens.");
            else
                foreach (var window in firefox)
                {
                    var page = WorkspaceContents.FirefoxPageTitle(window!.Title ?? "");
                    var match = session.Windows.FirstOrDefault(w => w.SelectedTitle == page)
                                ?? (session.Windows.Count == 1 && firefox.Length == 1 ? session.Windows[0] : null);
                    if (match is null) notes.Add($"A Firefox window (“{WorkspaceContents.Clean(page)}”) did not match its saved session, so its tabs are not kept.");
                    else tabs.AddRange(match.Tabs.Where(t => tabs.All(x => x.Url != t.Url)));
                }
            if (tabs.Count > WorkspaceContents.MaxTabs)
            {
                notes.Add($"Only the first {WorkspaceContents.MaxTabs} of {tabs.Count} Firefox tabs are kept.");
                tabs = tabs.Take(WorkspaceContents.MaxTabs).ToList();
            }
        }
        WorkspaceConversation? conversation = null;
        if (request.Conversation?.Trim() is { Length: > 0 } prompt)
        {
            var entry = (await history.RecentAsync(50, ct)).FirstOrDefault(e => e.Prompt.Trim() == prompt);
            if (entry is not null) conversation = new WorkspaceConversation(entry.Timestamp, WorkspaceContents.Clean(prompt));
            else notes.Add("The Nexus conversation is not in Nexus's history (history may be off), so it is not kept.");
        }
        return new WorkspaceExtras(files, tabs, conversation, notes);
    }

    // What Restore can bring back now: files that are still there, the tabs,
    // and the conversation if history still has it; the rest in plain words.
    public static async Task<object> RestorePlanAsync(WorkspaceRecord record, ConversationStore history, CancellationToken ct)
    {
        var notes = new List<string>();
        var apps = record.Apps.Select(a =>
        {
            var there = (a.Files ?? []).Where(File.Exists).ToArray();
            foreach (var gone in (a.Files ?? []).Except(there))
                notes.Add($"“{Path.GetFileName(gone)}” is no longer at {gone}, so {a.AppId} opens without it.");
            return new { appId = a.AppId, desktopId = a.DesktopId, title = a.Title, files = there };
        }).ToArray();
        object? conversation = null;
        if (record.Conversation is { } c)
        {
            var entry = (await history.RecentAsync(100, ct)).FirstOrDefault(e =>
                Math.Abs((e.Timestamp - c.At).TotalSeconds) < 1 && WorkspaceContents.Clean(e.Prompt.Trim()) == c.Prompt);
            if (entry is not null) conversation = new { prompt = entry.Prompt, answer = entry.Answer };
            else notes.Add("The Nexus conversation is no longer in history (it was erased or is too old).");
        }
        return new
        {
            name = record.Name, workspace = record.Workspace, apps,
            tabs = (record.Tabs ?? []).Where(t => WorkspaceContents.IsWebUrl(t.Url)).ToArray(),
            conversation,
            // What could not be kept when it was remembered, then what is gone since.
            notes = (record.Notes ?? []).Concat(notes).ToArray()
        };
    }

    // GET /workspaces, POST /workspaces (save or replace by name), DELETE /workspaces?name=,
    // GET /workspaces/restore?name= (what Restore brings back now)
    public static void MapWorkspaces(this WebApplication app)
    {
        app.MapGet("/workspaces", (WorkspaceStore store) => Results.Ok(new { workspaces = store.List() }));
        app.MapPost("/workspaces", async (WorkspaceSaveRequest request, WorkspaceStore store, WorkspaceContents contents,
            ConversationStore history, CancellationToken ct) =>
        {
            if (WorkspaceStore.Validate(request) is { } error) return Results.BadRequest(new { error });
            var extras = await CaptureAsync(request, contents, history, ct);
            if (store.Save(request, extras) is not { } saved)
                return Results.BadRequest(new { error = $"At most {WorkspaceStore.MaxWorkspaces} workspaces can be remembered. Forget one first." });
            return Results.Ok(new { workspace = saved.Saved, replaced = saved.Replaced, workspaces = store.List() });
        });
        app.MapGet("/workspaces/restore", async (string? name, WorkspaceStore store, ConversationStore history, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new { error = "Give the name of the workspace to restore." });
            return store.Find(name) is { } record
                ? Results.Ok(await RestorePlanAsync(record, history, ct))
                : Results.NotFound(new { error = "No workspace has that name." });
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
