using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// What Nexus can do in the chat: a fixed list of typed actions (tools), each
// with a JSON schema, a plain description for the model and a safety class.
//  - read: looks things up (system state, health, search, apps, automations,
//    today's journal, settings). Runs at once; the result goes back to the model.
//  - personal: changes something of the person's own (open an app, a setting,
//    a Flathub app, an automation...). Never runs on the model's say-so: it is
//    offered as a card and runs only when the person clicks "Do it"
//    (POST /actions/{id}/confirm). Desktop actions come back as an instruction
//    for the shell to perform, as automations do.
//  - system: never run by Core. It becomes a typed Guardian plan (the same
//    allowlist and validation as /guardian/propose) for the person to review;
//    approval stays with the root-owned broker.
// Safety rules:
//  - the model never runs a command: only these actions, with arguments
//    validated against each schema (unknown keys, wrong types, control
//    characters, anything outside a pattern or list is refused);
//  - arguments and results are untrusted data. What a card says comes from the
//    validated arguments, never from the model's own words;
//  - private look-ups (your files, the journal) are not offered to a cloud model.
public enum ActionSafety { Read, Personal, System }

// Type is "string", "integer", "boolean" or "value" (a string, number or true/false).
public sealed record ActionParam(string Name, string Type, string Description, bool Required = true,
    string[]? Choices = null, int MaxLength = 200, string? Pattern = null, int Min = 0, int Max = 0);

public sealed record NexusAction(string Name, ActionSafety Safety, string Description, IReadOnlyList<ActionParam> Params,
    bool Private = false)
{
    public string SafetyName => Safety switch { ActionSafety.Read => "read", ActionSafety.Personal => "personal", _ => "system" };

    public ToolSpec Tool => new(Name, Description, JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = Params.ToDictionary(p => p.Name, p => Schema(p)),
        required = Params.Where(p => p.Required).Select(p => p.Name).ToArray()
    }));

    private static object Schema(ActionParam p)
    {
        var schema = new Dictionary<string, object> { ["description"] = p.Description };
        schema["type"] = p.Type == "value" ? new[] { "string", "number", "boolean" } : p.Type;
        if (p.Choices is not null) schema["enum"] = p.Choices;
        if (p.Type == "integer") { schema["minimum"] = p.Min; schema["maximum"] = p.Max; }
        if (p.Type == "string" && p.Choices is null) schema["maxLength"] = p.MaxLength;
        return schema;
    }
}

public sealed class ActionRejectedException(string message) : Exception(message);

public static class NexusActions
{
    public static readonly string[] SettingsPages =
        ["network", "bluetooth", "sound", "display", "power", "desktop", "privacy", "search", "timeline", "automations",
         "accessibility", "health", "about"];
    public static readonly string[] PersonalSettings =
        ["volume", "night_light", "scale", "windows", "blur", "animations", "high_contrast"];
    public static readonly string[] Roles = ["files", "browser", "terminal", "settings", "studio"];

    public static readonly NexusAction[] All =
    [
        // ---- read: run at once ----
        new("get_system_state", ActionSafety.Read,
            "What this computer is: system, processor, memory, the AI model answering, and the date and time.", []),
        new("get_health_report", ActionSafety.Read,
            "A health check from safe, read-only checks: disk space, updates, network, sound, battery and problems found.", []),
        new("diagnose_problem", ActionSafety.Read,
            "Run Fix it's safe, read-only checks for a problem the person describes. Returns the likely cause and the fixes on offer.",
            [new("problem", "string", "The problem in the person's words, for example 'no sound'.", MaxLength: 300)]),
        new("search_files", ActionSafety.Read,
            "Search the person's own files (the folders they chose for search) by words or meaning.",
            [new("query", "string", "What to look for.", MaxLength: 200),
             new("limit", "integer", "How many results, 1 to 10.", Required: false, Min: 1, Max: 10)], Private: true),
        new("list_installed_apps", ActionSafety.Read, "The apps installed on this computer, with the id to open each.", []),
        new("list_automations", ActionSafety.Read, "The person's automations, and whether each is on.", []),
        new("get_today_summary", ActionSafety.Read,
            "What happened on this computer today, from the activity journal: changes, installs, what left the computer.", [],
            Private: true),
        new("get_settings", ActionSafety.Read,
            "The person's current desktop settings (windows, blur, animations, scale, night light, high contrast) and the AI model in use.", []),
        new("get_system_updates", ActionSafety.Read,
            "Security updates for the system (kernel, Firefox, Chromium...): when Guardian last checked, and whether updates are ready to approve.", []),
        new("get_move_in_report", ActionSafety.Read,
            "What Move in found on the old drive the person opened: folders and sizes, browser bookmarks per person, and its programs matched to apps here.", [],
            Private: true),

        // ---- personal: offered to the person, run on "Do it" ----
        new("open_app", ActionSafety.Personal,
            "Open an app: an id or name from list_installed_apps, or one of files, browser, terminal, settings, studio.",
            [new("app", "string", "The app's id or name.", MaxLength: 100, Pattern: @"[A-Za-z0-9][A-Za-z0-9 ._+-]{0,99}")]),
        new("open_settings", ActionSafety.Personal, "Open a page of Settings.",
            [new("page", "string", "The page.", Choices: SettingsPages),
             new("item", "string", "Optional item to highlight, for example wifi, volume, nightlight, scale, windows.",
                 Required: false, MaxLength: 20, Pattern: "[a-z]{1,20}")]),
        new("change_setting", ActionSafety.Personal,
            "Change one of the person's own settings. volume: 0-100. night_light, blur, animations, high_contrast: on or off. " +
            "scale: 1, 1.25, 1.5, 1.75 or 2. windows: floating or tiled.",
            [new("setting", "string", "Which setting.", Choices: PersonalSettings),
             new("value", "value", "The new value.", MaxLength: 16)]),
        new("install_app", ActionSafety.Personal,
            "Install a desktop app from Flathub for this person only (no password). Use the Flathub id, for example com.spotify.Client.",
            [new("app_id", "string", "The Flathub app id.", MaxLength: 128, Pattern: FlathubId)]),
        new("remove_app", ActionSafety.Personal, "Remove a Flathub app this person installed.",
            [new("app_id", "string", "The Flathub app id.", MaxLength: 128, Pattern: FlathubId)]),
        new("apt_install", ActionSafety.Personal,
            "Install an Ubuntu package with apt in the Ubuntu container (command-line tools, Ubuntu software). Opens a terminal so the person sees it.",
            [new("package", "string", "The apt package name, for example htop.", MaxLength: 100, Pattern: @"[a-z0-9][a-z0-9+.-]{0,99}")]),
        new("create_automation", ActionSafety.Personal,
            "Create an automation from one sentence, for example 'At 22:00 every day, turn on night light'.",
            [new("sentence", "string", "The automation in one sentence.", MaxLength: RuleParser.MaxSentenceLength)]),
        new("make_image", ActionSafety.Personal, "Make a picture from a description with the local image model (opens Images).",
            [new("description", "string", "What the picture shows.", MaxLength: 500)]),
        new("start_coding_task", ActionSafety.Personal,
            "Open Code, the coding agent, on a project folder in the home folder, with a task filled in for the person to send.",
            [new("folder", "string", "The project folder, for example ~/projects/app.", MaxLength: 200),
             new("task", "string", "What to change, in plain words.", MaxLength: 1000)]),
        new("open_fix_it", ActionSafety.Personal, "Open the Fix it window to check a problem and offer fixes.",
            [new("problem", "string", "The problem in the person's words.", MaxLength: 300)]),
        new("remember_workspace", ActionSafety.Personal,
            "Remember the current workspace's open apps under a name, so they can be brought back later.",
            [new("name", "string", "A short name, for example Dissertation.", MaxLength: 40, Pattern: @"[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}")]),
        new("restore_workspace", ActionSafety.Personal, "Bring back a remembered workspace: reopen its apps on it.",
            [new("name", "string", "The workspace's name.", MaxLength: 40, Pattern: @"[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}")]),
        new("try_app", ActionSafety.Personal,
            "Try a program from nixpkgs without installing it, in a sandbox that keeps nothing (nexus try).",
            [new("package", "string", "The nixpkgs package name, for example gimp.", MaxLength: 100, Pattern: @"[A-Za-z0-9][A-Za-z0-9._+-]{0,99}")]),
        new("open_move_in", ActionSafety.Personal, "Open Move in, to bring files, bookmarks and apps over from an old Windows, Mac or Linux drive.", []),
        new("copy_old_files", ActionSafety.Personal, "Copy the folders ticked in Move in from the old drive into the home folder (never replaces files).", []),
        new("import_old_bookmarks", ActionSafety.Personal,
            "Save one person's browser bookmarks from the old drive as a file to import into Firefox (adds, never replaces).",
            [new("user", "string", "Whose bookmarks, from get_move_in_report; leave out if only one person has any.", Required: false,
                 MaxLength: 100, Pattern: @"[^/.][^/]{0,99}")]),
        new("show_old_apps", ActionSafety.Personal, "Show the apps matched to the old computer's programs in Apps, to install with a click.", []),
        new("open_welcome", ActionSafety.Personal, "Open the welcome setup, which suggests apps and settings for what the person does.", []),
        new("check_for_updates", ActionSafety.Personal,
            "Ask Guardian to check for system security updates now (it also checks weekly). Found updates are built and tested, then shown for approval.", []),
        new("apply_fix", ActionSafety.Personal, "Apply one personal fix that diagnose_problem offered.",
            [new("fix_id", "string", "The fix's id from diagnose_problem.", MaxLength: 64, Pattern: @"[a-z0-9][a-z0-9._-]{0,63}")]),

        // ---- system: a Guardian proposal, never run by Core ----
        new("install_system_software", ActionSafety.System,
            "Install software for every account (a NixOS package, for example htop). Goes to Guardian for the person to approve.",
            [new("package", "string", "The nixpkgs package name.", MaxLength: 128)]),
        new("change_system_option", ActionSafety.System,
            "Turn a system service or option on or off. Goes to Guardian for the person to review and approve; " +
            "explain a risky change before offering it. Options: " +
            string.Join("; ", GuardianSchema.Options.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.Key + " (" + x.Value.Summary.TrimEnd('.') +
                             (x.Value.AlsoSetWhenEnabled is null ? "" : ". " + x.Value.RiskNote?.TrimEnd('.')) + ")")) + ".",
            [new("option", "string", "The option.", Choices: GuardianSchema.Options.Keys.Order(StringComparer.Ordinal).ToArray()),
             new("enabled", "boolean", "On (true) or off (false).")]),
    ];

    private const string FlathubId = @"[A-Za-z][A-Za-z0-9_-]{0,63}(\.[A-Za-z0-9_-]{1,64}){2,6}";

    public static readonly IReadOnlyDictionary<string, NexusAction> ByName = All.ToDictionary(x => x.Name, StringComparer.Ordinal);

    // Validates the model's arguments against the action's parameters and
    // returns them normalised. Anything unexpected is refused, not repaired.
    public static JsonObject Validate(NexusAction action, string arguments)
    {
        if (arguments.Length > ModelTools.MaxArgumentChars) throw new ActionRejectedException("The arguments are too long.");
        var options = new JsonDocumentOptions { MaxDepth = 4 };
        JsonNode? node;
        try
        {
            node = string.IsNullOrWhiteSpace(arguments) ? new JsonObject() : JsonNode.Parse(arguments, documentOptions: options);
            // Some models send the object as a JSON string.
            if (node is JsonValue value && value.TryGetValue<string>(out var inner))
                node = string.IsNullOrWhiteSpace(inner) ? new JsonObject() : JsonNode.Parse(inner, documentOptions: options);
        }
        catch (JsonException) { throw new ActionRejectedException("The arguments were not a JSON object."); }
        if (node is not JsonObject given) throw new ActionRejectedException("The arguments were not a JSON object.");
        foreach (var (key, _) in given)
            if (!action.Params.Any(p => p.Name == key))
                throw new ActionRejectedException($"'{Clean(key, 40)}' is not an argument of {action.Name}.");
        var result = new JsonObject();
        foreach (var p in action.Params)
        {
            if (!given.TryGetPropertyValue(p.Name, out var raw) || raw is null)
            {
                if (p.Required) throw new ActionRejectedException($"{action.Name} needs '{p.Name}'.");
                continue;
            }
            if (raw is not JsonValue v) throw new ActionRejectedException($"'{p.Name}' must be a single value.");
            result[p.Name] = p.Type switch
            {
                "string" => JsonValue.Create(Text(p, v)),
                "integer" => JsonValue.Create(Integer(p, v)),
                "boolean" => JsonValue.Create(Flag(p, v)),
                _ => Scalar(p, v),
            };
        }
        return result;
    }

    private static string Text(ActionParam p, JsonValue v)
    {
        if (!v.TryGetValue<string>(out var text)) throw new ActionRejectedException($"'{p.Name}' must be text.");
        if (text.Any(char.IsControl)) throw new ActionRejectedException($"'{p.Name}' contains control characters.");
        text = text.Trim();
        if (text.Length == 0 || text.Length > p.MaxLength)
            throw new ActionRejectedException($"'{p.Name}' must be 1-{p.MaxLength} characters.");
        if (p.Choices is not null)
            return p.Choices.FirstOrDefault(c => string.Equals(c, text, StringComparison.OrdinalIgnoreCase))
                ?? throw new ActionRejectedException($"'{p.Name}' must be one of: {string.Join(", ", p.Choices)}.");
        if (p.Pattern is not null &&
            !Regex.IsMatch(text, @"\A(?:" + p.Pattern + @")\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ActionRejectedException($"'{p.Name}' is not in the expected form.");
        return text;
    }

    private static int Integer(ActionParam p, JsonValue v)
    {
        int number;
        if (v.TryGetValue<int>(out var n)) number = n;
        else if (v.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 1e6) number = (int)d;
        else if (v.TryGetValue<string>(out var s) && int.TryParse(s.Trim(), System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out var parsed)) number = parsed;
        else throw new ActionRejectedException($"'{p.Name}' must be a whole number.");
        if (number < p.Min || number > p.Max) throw new ActionRejectedException($"'{p.Name}' must be {p.Min}-{p.Max}.");
        return number;
    }

    private static bool Flag(ActionParam p, JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s))
            switch (s.Trim().ToLowerInvariant())
            {
                case "true" or "on" or "yes": return true;
                case "false" or "off" or "no": return false;
            }
        throw new ActionRejectedException($"'{p.Name}' must be true or false.");
    }

    private static JsonNode Scalar(ActionParam p, JsonValue v)
    {
        if (v.TryGetValue<bool>(out var b)) return JsonValue.Create(b);
        if (v.TryGetValue<double>(out var d)) return JsonValue.Create(d);
        return JsonValue.Create(Text(p, v));
    }

    internal static string Clean(string value, int max)
    {
        var text = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}

// An action waiting for the person. Perform is the shell's instruction for
// desktop actions; Rule, FixToken and Plan hold what Core itself will use.
public sealed record PendingAction(string Id, NexusAction Action, JsonObject Args, string Summary, DateTimeOffset Expires,
    JsonObject? Perform = null, RuleDefinition? Rule = null, string? FixToken = null, GuardianPlan? Plan = null)
{
    // actionId repeats id for `nexus bridge`, which tags every event with its own request id.
    public object Event() => new
    {
        type = "action", id = Id, actionId = Id, action = Action.Name, args = Args, safety = Action.SafetyName, summary = Summary,
        plan = Plan is null ? null : new { id = Plan.Id, description = Plan.Description, changes = Plan.Changes, packages = Plan.Packages,
                                           nixPreview = Plan.NixPreview, risk = Plan.Risk, riskLevel = Plan.RiskLevel }
    };
}

// In memory only: a pending action lasts 15 minutes, is used once, and at
// most 32 wait at a time (the oldest go first).
public sealed class PendingActions(TimeProvider time)
{
    public const int MaxPending = 32;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<string, PendingAction> _items = new(StringComparer.Ordinal);

    public static bool IsValidId(string? id) => id is { Length: 24 } && id.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    public PendingAction Add(NexusAction action, JsonObject args, string summary, JsonObject? perform = null,
        RuleDefinition? rule = null, string? fixToken = null, GuardianPlan? plan = null)
    {
        var now = time.GetUtcNow();
        foreach (var old in _items.Values.Where(x => x.Expires <= now)) _items.TryRemove(old.Id, out _);
        while (_items.Count >= MaxPending && _items.Values.MinBy(x => x.Expires) is { } oldest) _items.TryRemove(oldest.Id, out _);
        var item = new PendingAction(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), action, args,
            summary, now + Lifetime, perform, rule, fixToken, plan);
        _items[item.Id] = item;
        return item;
    }

    public PendingAction? Get(string id) =>
        _items.TryGetValue(id, out var item) && item.Expires > time.GetUtcNow() ? item : null;

    // Removes and returns it: an action runs at most once.
    public PendingAction? Take(string id) =>
        _items.TryRemove(id, out var item) && item.Expires > time.GetUtcNow() ? item : null;
}

// The apps installed for this person: desktop entries in the XDG data
// folders (system, per-user and Flatpak exports), or NEXUS_APPLICATION_DIRS.
public sealed class InstalledApps(IConfiguration config)
{
    public const int MaxFiles = 2000;
    public sealed record App(string Id, string Name);

    public IReadOnlyList<App> List()
    {
        var apps = new Dictionary<string, App>(StringComparer.Ordinal);
        foreach (var dir in Folders())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.desktop").Take(MaxFiles))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (apps.ContainsKey(id) || new FileInfo(file).Length > 65536) continue;
                    if (Read(file) is { } name) apps[id] = new App(id, name);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return apps.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private IEnumerable<string> Folders()
    {
        if (config["NEXUS_APPLICATION_DIRS"] is { Length: > 0 } configured) return configured.Split(':');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h : Path.Combine(home, ".local", "share");
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share").Split(':');
        return new[] { dataHome }.Concat(dataDirs)
            .Concat([Path.Combine(dataHome, "flatpak", "exports", "share"), "/var/lib/flatpak/exports/share", "/run/current-system/sw/share"])
            .Select(x => Path.Combine(x, "applications")).Distinct(StringComparer.Ordinal);
    }

    private static string? Read(string file)
    {
        string? name = null;
        var inEntry = false;
        foreach (var line in File.ReadLines(file))
        {
            if (line.StartsWith('[')) { if (inEntry) break; inEntry = line.Trim() == "[Desktop Entry]"; continue; }
            if (!inEntry) continue;
            if (line.StartsWith("NoDisplay=true", StringComparison.Ordinal) || line.StartsWith("Hidden=true", StringComparison.Ordinal)) return null;
            if (line.StartsWith("Type=", StringComparison.Ordinal) && line.Trim() != "Type=Application") return null;
            if (name is null && line.StartsWith("Name=", StringComparison.Ordinal)) name = NexusActions.Clean(line[5..], 80);
        }
        return string.IsNullOrEmpty(name) ? null : name;
    }
}
