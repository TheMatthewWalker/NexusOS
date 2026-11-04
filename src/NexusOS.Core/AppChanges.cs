using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// Installs and removals of apps, with an Undo (docs/INSTALL.md, "Adding software").
// The desktop (Apps, for Flathub) and the apt command (nix/packages/nexus-apt.sh)
// report each change once it has happened; Core records it in the activity
// journal (kind "app") with what an Undo needs: for Flathub the scope (--user
// or --system), the remote, the branch and the commit, so a reinstall can go
// back to the same version; for apt the version and the container.
// Undo never runs here. Core hands back the opposite change for the desktop to
// run where the person can see it (Apps, or a terminal for apt), and the entry
// is marked undone when that change is reported with "undoes". The person's
// own data (~/.var/app/<id>) is never deleted by a removal or an Undo; Apps
// offers that separately and it is journalled as "delete-data", which has no Undo.
public sealed record AppChange(string Source, string Op, string Id, string? Name = null, string? Scope = null,
    string? Origin = null, string? Branch = null, string? Commit = null, string? Version = null, string? Box = null,
    bool Isolated = false);

public sealed record AppChangeRequest(string? Source, string? Op, string? Id, string? Name, string? Scope, string? Origin,
    string? Branch, string? Commit, string? Version, string? Box, bool? Isolated, long? Undoes);

public sealed record AppUndoPlan(JournalEntry? Entry, AppChange? Change, JsonObject? Perform, string? Description, string? Conflict);

public static partial class AppChanges
{
    public const string Flathub = "flathub", Apt = "apt";
    public const string Install = "install", Remove = "remove", DeleteData = "delete-data";
    public const int MaxNameLength = 80;
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    // The same forms the shell, the apt command and Nexus's actions accept.
    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,64}){2,6}$")]
    public static partial Regex FlathubId();
    [GeneratedRegex(@"^[a-z0-9][a-z0-9+.-]{0,99}$")]
    public static partial Regex AptPackage();
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex Word();
    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Commit();
    // apt versions go back into `apt install name=version`, so no spaces or shell characters.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9.+~:_-]{0,99}$")]
    private static partial Regex AptVersion();
    // A Flathub version is only shown (the commit pins the reinstall).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 .+~:_()-]{0,63}$")]
    private static partial Regex ShownVersion();

    public static (AppChange? Change, string? Error) FromRequest(AppChangeRequest r)
    {
        var change = new AppChange(r.Source ?? "", r.Op ?? "", r.Id ?? "", Blank(r.Name), Blank(r.Scope), Blank(r.Origin),
            Blank(r.Branch), Blank(r.Commit), Blank(r.Version), Blank(r.Box), r.Isolated ?? false);
        return Validate(change) is { } error ? (null, error) : (change with { Name = change.Name?.Trim() }, null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public static string? Validate(AppChange c)
    {
        if (c.Source is not (Flathub or Apt)) return "source must be flathub or apt.";
        if (c.Op is not (Install or Remove or DeleteData)) return "op must be install, remove or delete-data.";
        if (c.Name is { } name && (name.Trim().Length is 0 or > MaxNameLength || name.Any(char.IsControl)))
            return $"name must be 1-{MaxNameLength} characters without control characters.";
        if (c.Origin is { } origin && !Word().IsMatch(origin) || c.Branch is { } branch && !Word().IsMatch(branch))
            return "origin and branch must be plain names.";
        if (c.Source == Flathub)
        {
            if (!FlathubId().IsMatch(c.Id)) return "Not a Flathub app id.";
            if (c.Scope is not ("user" or "system")) return "scope must be user or system.";
            if (c.Commit is { } commit && !Commit().IsMatch(commit)) return "commit must be 64 hexadecimal characters.";
            if (c.Version is { } version && !ShownVersion().IsMatch(version)) return "Not a version.";
            if (c.Box is not null || c.Isolated) return "box and isolated are for apt.";
            return null;
        }
        if (!AptPackage().IsMatch(c.Id)) return "Not an apt package name.";
        if (c.Op == DeleteData) return "delete-data is for Flathub apps.";
        if (c.Box is null || !Word().IsMatch(c.Box)) return "box must name the Ubuntu container.";
        if (c.Version is { } v && !AptVersion().IsMatch(v)) return "Not an apt version.";
        if (c.Scope is not null || c.Commit is not null || c.Origin is not null || c.Branch is not null)
            return "scope, commit, origin and branch are for Flathub.";
        return null;
    }

    // Changes to the same app in the same place share a ref, so Undo can tell whether a later one followed.
    public static string Ref(AppChange c) => c.Source == Flathub ? $"flathub:{c.Scope}:{c.Id}" : $"apt:{c.Box}:{c.Id}";

    public static string Label(AppChange c) => c.Name ?? c.Id;

    public static string Summary(AppChange c, bool undo = false)
    {
        var text = (c.Source, c.Op) switch
        {
            (Flathub, Install) => $"Installed {Label(c)} from Flathub, " + (c.Scope == "user" ? "just for you" : "for everyone"),
            (Flathub, Remove) => $"Removed {Label(c)} (Flathub" + (c.Scope == "user" ? ")" : ", for everyone)"),
            (Flathub, _) => $"Deleted {Label(c)}'s data and settings (~/.var/app/{c.Id})",
            (_, Install) => $"Installed {c.Id}" + (c.Version is null ? "" : " " + c.Version) + " with apt" + Where(c),
            _ => $"Removed {c.Id} with apt" + Where(c),
        };
        return NexusActions.Clean(undo ? text + " (undo)" : text, ActivityJournal.MaxSummaryLength);
    }

    private static string Where(AppChange c) => c.Isolated ? " (isolated Ubuntu container)" : " (Ubuntu container)";

    public static JournalRecord Record(AppChange c, string actor = "user", bool undo = false) =>
        new(JournalKinds.App, actor, Summary(c, undo), After: JsonSerializer.SerializeToElement(c, Wire),
            Reversible: c.Op != DeleteData, Ref: Ref(c));

    public static AppChange? Read(JournalEntry entry)
    {
        if (entry.Kind != JournalKinds.App || entry.After is not { ValueKind: JsonValueKind.Object } after) return null;
        try
        {
            var change = after.Deserialize<AppChange>(Wire);
            return change is not null && Validate(change) is null ? change : null;
        }
        catch (JsonException) { return null; }
    }

    // The opposite change, as an instruction for the desktop. A removal is
    // undone by reinstalling in the same place, from the same remote and
    // branch, and then going back to the same commit or version if that is
    // still offered.
    public static JsonObject Inverse(AppChange c, long undoes)
    {
        var perform = c.Source == Flathub
            ? c.Op == Install
                ? new JsonObject { ["op"] = "flathub.remove", ["appId"] = c.Id, ["scope"] = c.Scope }
                : new JsonObject { ["op"] = "flathub.install", ["appId"] = c.Id, ["scope"] = c.Scope, ["origin"] = c.Origin ?? Flathub,
                                   ["branch"] = c.Branch, ["commit"] = c.Commit }
            : c.Op == Install
                ? new JsonObject { ["op"] = "apt.remove", ["package"] = c.Id, ["box"] = c.Box, ["isolated"] = c.Isolated }
                : new JsonObject { ["op"] = "apt.install", ["package"] = c.Id, ["version"] = c.Version, ["box"] = c.Box, ["isolated"] = c.Isolated };
        perform["name"] = Label(c);
        perform["undoes"] = undoes;
        return perform;
    }

    public static string UndoDescription(AppChange c) => NexusActions.Clean((c.Source, c.Op) switch
    {
        (Flathub, Install) => $"Remove {Label(c)} again. Its data and settings stay unless you delete them",
        (Flathub, _) => $"Reinstall {Label(c)} from Flathub " + (c.Scope == "user" ? "just for you" : "for everyone") +
                        (c.Commit is null ? "" : ", at the same version if Flathub still offers it"),
        (_, Install) => $"Remove {c.Id} with apt" + Where(c),
        _ => $"Reinstall {c.Id}" + (c.Version is null ? "" : " " + c.Version) + " with apt" + Where(c),
    }, 200);

    // Whether the change reported as undoing `original` really is its opposite.
    public static bool Undoes(AppChange change, AppChange original) =>
        change.Source == original.Source && change.Id == original.Id && change.Scope == original.Scope &&
        change.Box == original.Box && change.Isolated == original.Isolated &&
        (change.Op, original.Op) is (Install, Remove) or (Remove, Install);

    public static async Task<AppUndoPlan> PlanUndoAsync(ActivityJournal journal, long id, CancellationToken ct)
    {
        var entry = await journal.GetAsync(id, ct);
        if (entry is null) return new(null, null, null, null, null);
        var change = Read(entry);
        if (change is null || entry.Reversible != true || change.Op == DeleteData)
            return new(entry, null, null, null, "This entry cannot be undone.");
        if (entry.UndoneAt is not null) return new(entry, change, null, null, "This entry was already undone.");
        if (await journal.LatestRefAsync(JournalKinds.App, Ref(change), ct) is { } latest && latest != id)
            return new(entry, change, null, null, $"{Label(change)} changed again later. Undo the later change in the timeline instead.");
        return new(entry, change, Inverse(change, id), UndoDescription(change), null);
    }

    public static void MapAppChanges(this WebApplication app)
    {
        // Reported by the desktop and the apt command after a change has happened.
        app.MapPost("/apps/changes", async (AppChangeRequest request, ActivityJournal journal, CancellationToken ct) =>
        {
            var (change, error) = FromRequest(request);
            if (change is null) return Results.BadRequest(new { error });
            AppChange? original = null;
            if (request.Undoes is { } undoes && await journal.GetAsync(undoes, ct) is { UndoneAt: null } entry &&
                Read(entry) is { } before && Undoes(change, before))
                original = before;
            var id = await journal.AppendAsync(Record(change, undo: original is not null), ct);
            var undid = original is not null && await journal.MarkUndoneAsync(request.Undoes!.Value, ct);
            return Results.Ok(new { id, undid, summary = Summary(change, original is not null) });
        }).RequireRateLimiting("journal");
        // What installing a Flathub app means: publisher, licence, size and permissions in plain words.
        app.MapGet("/apps/flathub/{id}", async (string id, FlathubDetails details, CancellationToken ct) =>
            FlathubId().IsMatch(id)
                ? Results.Ok(await details.GetAsync(id, ct))
                : Results.BadRequest(new { error = "Not a Flathub app id." }));
    }
}
